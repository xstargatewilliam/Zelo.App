using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using Zelo.Core;

namespace Zelo.Service;

/// <summary>
/// Canal local (named pipe) por onde o painel conversa com o serviço.
/// Só aceita conexões desta máquina; usuários comuns podem consultar e disparar rotinas.
/// </summary>
public sealed class ServidorPipe : BackgroundService
{
    private readonly Motor _motor;
    private readonly IHostApplicationLifetime _vida;

    public ServidorPipe(Motor motor, IHostApplicationLifetime vida)
    {
        _motor = motor;
        _vida = vida;
    }

    private static PipeSecurity Seguranca()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Bloqueia acesso pela rede.
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return s;
    }

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        var seguranca = Seguranca();
        while (!parar.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    Caminhos.NomePipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, seguranca);
                await pipe.WaitForConnectionAsync(parar);
                var conectado = pipe;
                pipe = null;
                _ = Task.Run(() => AtenderAsync(conectado, parar), parar);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Registro.Escrever("Erro no canal do painel: " + ex.Message);
                try { await Task.Delay(1000, parar); } catch { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task AtenderAsync(NamedPipeServerStream pipe, CancellationToken parar)
    {
        await using var _ = pipe;
        try
        {
            var utf8 = new UTF8Encoding(false);
            using var leitor = new StreamReader(pipe, utf8, false, 65536, leaveOpen: true);
            using var escritor = new StreamWriter(pipe, utf8, 65536, leaveOpen: true) { AutoFlush = true };

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(parar);
            limite.CancelAfter(TimeSpan.FromSeconds(10));
            var linha = await leitor.ReadLineAsync(limite.Token);
            if (string.IsNullOrWhiteSpace(linha) || linha.Length > 1_000_000) return;

            Resposta resposta;
            try
            {
                var pedido = Json.Desserializar<Pedido>(linha) ?? new Pedido();
                resposta = pedido.Comando.StartsWith("otimizar-")
                    ? await OtimizarAsync(pedido, pipe, parar)
                    : Processar(pedido, () => ClienteEhAdministrador(pipe));
            }
            catch (Exception ex)
            {
                resposta = new Resposta { Ok = false, Mensagem = ex.Message };
            }
            await escritor.WriteLineAsync(Json.Serializar(resposta));
        }
        catch { /* cliente desconectou */ }
    }

    /// <summary>
    /// Verifica, pelo token de quem conectou no pipe, se a pessoa é administradora do PC.
    /// O painel roda sem elevação: num administrador com UAC, o grupo Administradores aparece
    /// como "deny-only" no token, e isso também conta. Na dúvida, nega.
    /// </summary>
    private static bool ClienteEhAdministrador(NamedPipeServerStream pipe)
    {
        const string admins = "S-1-5-32-544";
        bool resultado = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                // ifImpersonating: true → null se a personificação falhou (nunca cai no token do SYSTEM).
                using var id = WindowsIdentity.GetCurrent(true);
                if (id is null) return;
                bool ativo = id.Groups?.Any(g => g.Value == admins) == true;
                bool filtradoPeloUac = id.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == admins);
                resultado = ativo || filtradoPeloUac;
            });
        }
        catch (Exception ex)
        {
            Registro.Escrever("Não foi possível identificar quem conectou no painel: " + ex.Message);
            return false;
        }
        return resultado;
    }

    /// <summary>Quem está no painel (para os ajustes que valem só para essa pessoa).</summary>
    private static string? SidDoCliente(NamedPipeServerStream pipe)
    {
        string? sid = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(true);
                sid = id?.User?.Value;
            });
        }
        catch { }
        return sid;
    }

    private async Task<Resposta> OtimizarAsync(Pedido p, NamedPipeServerStream pipe, CancellationToken parar)
    {
        switch (p.Comando)
        {
            case "otimizar-estado":
                return new Resposta
                {
                    Ajustes = await Otimizador.ListarAsync(SidDoCliente(pipe), parar),
                    Otimizacao = Otimizador.Andamento,
                    PerfisUso = Otimizador.Carregar().Perfis,
                };

            case "otimizar-andamento":
                return new Resposta { Otimizacao = Otimizador.Andamento };

            case "otimizar-aplicar":
            case "otimizar-desfazer":
                // Muda o Windows como SYSTEM: só administradores.
                if (!ClienteEhAdministrador(pipe))
                    return new Resposta { Ok = false, Mensagem = "Só administradores do PC podem otimizar o Windows." };
                if (_motor.Ocupado)
                    return new Resposta { Ok = false, Mensagem = "Espere a vistoria em andamento terminar." };
                if (Atualizador.EmAndamento)
                    return new Resposta { Ok = false, Mensagem = "O Zelo está se atualizando. Tente de novo em alguns minutos." };
                var desfazer = p.Comando == "otimizar-desfazer";
                var erro = Otimizador.Iniciar(p.Ids ?? new(), p.PerfisUso ?? new(), SidDoCliente(pipe), desfazer, _vida.ApplicationStopping);
                return erro is null
                    ? new Resposta { Mensagem = desfazer ? "Desfazendo..." : "Aplicando...", Otimizacao = Otimizador.Andamento }
                    : new Resposta { Ok = false, Mensagem = erro };
        }
        return new Resposta { Ok = false, Mensagem = $"Comando desconhecido: {p.Comando}" };
    }

    private Resposta Processar(Pedido p, Func<bool> ehAdministrador)
    {
        switch (p.Comando)
        {
            case "estado":
                return new Resposta { Estado = MontarEstado() };

            case "historico":
                return new Resposta { Historico = Historico.Carregar(Math.Clamp(p.Quantidade, 1, 600)) };

            case "executar":
                if (p.Rotina is null) return new Resposta { Ok = false, Mensagem = "Rotina não informada." };
                if (_motor.Ocupado) return new Resposta { Ok = false, Mensagem = "Já existe uma rotina em andamento." };
                if (Atualizador.EmAndamento) return new Resposta { Ok = false, Mensagem = "O Zelo está se atualizando. Tente de novo em alguns minutos." };
                var rotina = p.Rotina.Value;
                var sim = p.Simulacao;
                _ = Task.Run(async () =>
                {
                    try { await _motor.ExecutarAsync(rotina, sim, "manual", _vida.ApplicationStopping); }
                    catch (Exception ex) { Registro.Escrever("Erro na execução manual: " + ex); }
                });
                return new Resposta { Mensagem = $"Rotina {rotina.Nome()} iniciada{(sim ? " em modo simulação" : "")}." };

            case "atualizar":
                // Instala código novo como SYSTEM: só administradores.
                if (!ehAdministrador())
                    return new Resposta { Ok = false, Mensagem = "Só administradores do PC podem atualizar o Zelo." };
                if (_motor.Ocupado)
                    return new Resposta { Ok = false, Mensagem = "Espere a rotina em andamento terminar para atualizar." };
                if (Atualizador.EmAndamento)
                    return new Resposta { Mensagem = "A atualização já está em andamento." };
                _ = Task.Run(async () =>
                {
                    try { Registro.Escrever("Atualização manual: " + await Atualizador.VerificarEAplicarAsync(true, _vida.ApplicationStopping)); }
                    catch (Exception ex) { Registro.Escrever("Erro na atualização manual: " + ex.Message); }
                });
                return new Resposta { Mensagem = "Procurando atualização no GitHub..." };

            case "dispensar":
                if (string.IsNullOrWhiteSpace(p.TarefaId) || Catalogo.Todas.All(t => t.Id != p.TarefaId))
                    return new Resposta { Ok = false, Mensagem = "Verificação desconhecida." };
                // Dispensar alertas de segurança esconderia um problema real: só administradores.
                if (!ehAdministrador())
                    return new Resposta { Ok = false, Mensagem = "Só administradores do PC podem dispensar alertas." };
                Dispensas.Dispensar(p.TarefaId);
                Registro.Escrever($"Alerta de {p.TarefaId} dispensado pelo usuário" + (string.IsNullOrWhiteSpace(p.Motivo) ? "." : $": {p.Motivo}"));
                return new Resposta { Mensagem = "Anotado. O alerta sai da sua nota até a próxima verificação." };

            case "executar-tarefa":
                var tarefa = Catalogo.Todas.FirstOrDefault(t => t.Id == p.TarefaId);
                if (tarefa is null) return new Resposta { Ok = false, Mensagem = "Verificação desconhecida." };
                if (_motor.Ocupado) return new Resposta { Ok = false, Mensagem = "Já existe uma rotina em andamento." };
                if (Atualizador.EmAndamento) return new Resposta { Ok = false, Mensagem = "O Zelo está se atualizando. Tente de novo em alguns minutos." };
                var rotinaDaTarefa = tarefa.Rotinas[0];
                _ = Task.Run(async () =>
                {
                    try { await _motor.ExecutarAsync(rotinaDaTarefa, false, "tarefa", _vida.ApplicationStopping, tarefa.Id); }
                    catch (Exception ex) { Registro.Escrever("Erro ao repetir tarefa: " + ex); }
                });
                return new Resposta { Mensagem = $"Tentando de novo: {tarefa.Nome}." };

            case "pausar":
                var minutos = Math.Clamp(p.Minutos, 0, 24 * 60);
                Pausa.Definir(TimeSpan.FromMinutes(minutos));
                Registro.Escrever(minutos > 0 ? $"Rotinas automáticas pausadas por {minutos} min." : "Pausa encerrada.");
                return new Resposta { Mensagem = minutos > 0 ? $"Pausado até {DateTime.Now.AddMinutes(minutos):HH:mm}." : "O Zelo voltou a cuidar normalmente." };

            case "cancelar":
                _motor.Cancelar();
                // Sem isso, a rotina interrompida (que não conta como feita) recomeçaria minutos depois.
                Pausa.Definir(TimeSpan.FromHours(2));
                Registro.Escrever("Rotina interrompida pelo usuário; vistorias automáticas pausadas por 2 horas.");
                return new Resposta { Mensagem = "Parando. As vistorias automáticas ficam pausadas por 2 horas." };

            case "config-obter":
                return new Resposta { Config = ArmazemConfig.Carregar() };

            case "config-salvar":
                if (p.Config is null) return new Resposta { Ok = false, Mensagem = "Configuração vazia." };
                // A configuração decide o que o serviço (SYSTEM) apaga e copia: só administradores alteram.
                if (!ehAdministrador())
                {
                    Registro.Escrever("Alteração de configuração recusada: usuário sem permissão de administrador.");
                    return new Resposta { Ok = false, Mensagem = "Só administradores do PC podem alterar as configurações do Zelo." };
                }
                var erro = ValidarDestino(p.Config);
                if (erro is not null) return new Resposta { Ok = false, Mensagem = erro };
                ArmazemConfig.Salvar(p.Config);
                Registro.Escrever("Configuração alterada pelo painel.");
                return new Resposta { Mensagem = "Configurações salvas.", Config = ArmazemConfig.Carregar() };

            default:
                return new Resposta { Ok = false, Mensagem = $"Comando desconhecido: {p.Comando}" };
        }
    }

    /// <summary>O serviço roda como SYSTEM: não deixamos o backup escrever em pastas do Windows.</summary>
    private static string? ValidarDestino(Configuracao c)
    {
        if (string.IsNullOrWhiteSpace(c.DestinoBackup)) return null;
        string destino;
        try { destino = Path.GetFullPath(c.DestinoBackup.Trim().Trim('"')); }
        catch { return "Destino do backup inválido."; }

        var proibidas = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        }.Where(s => !string.IsNullOrEmpty(s));

        foreach (var pasta in proibidas)
            if (destino.StartsWith(pasta, StringComparison.OrdinalIgnoreCase))
                return $"O destino do backup não pode ficar dentro de {pasta}.";

        foreach (var origem in c.PastasBackup)
        {
            try
            {
                var o = Path.GetFullPath(origem.Trim().Trim('"')).TrimEnd('\\') + "\\";
                if ((destino.TrimEnd('\\') + "\\").StartsWith(o, StringComparison.OrdinalIgnoreCase))
                    return $"O destino do backup não pode ficar dentro de uma pasta que está sendo copiada ({origem}).";
            }
            catch { return $"Pasta de backup inválida: {origem}"; }
        }
        return null;
    }

    private EstadoServico MontarEstado()
    {
        var historico = Historico.Carregar();
        var ultimas = Agenda.UltimasExecucoes(historico);
        var e = new EstadoServico
        {
            Versao = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            RotinaAtual = _motor.RotinaAtual,
            SimulacaoAtual = _motor.SimulacaoAtual,
            TarefaAtual = _motor.TarefaAtual,
            Indice = _motor.IndiceAtual,
            Total = _motor.TotalAtual,
            Saude = Saude.Calcular(historico, ArmazemConfig.Carregar().TarefasDesativadas),
            NaTomada = Nativo.NaTomada(),
            UltimaExecucaoId = historico.LastOrDefault()?.Id,
        };
        var atu = Atualizador.LerEstado();
        e.VersaoDisponivel = atu.VersaoDisponivel;
        e.UltimaVerificacaoAtualizacao = atu.UltimaVerificacao;
        e.ErroAtualizacao = atu.UltimoErro;
        e.Atualizando = Atualizador.EmAndamento;
        e.Etapas = _motor.Etapas;
        e.PausadoAte = Pausa.Ate;
        var cfgAtual = ArmazemConfig.Carregar();
        e.Perfil = cfgAtual.Perfil;
        e.DentroDaJanela = cfgAtual.DentroDaJanela(DateTime.Now);
        foreach (var (r, quando) in ultimas)
        {
            e.Ultima[r.ToString()] = quando;
            e.Proxima[r.ToString()] = Agenda.Proxima(r, quando);
        }
        return e;
    }
}
