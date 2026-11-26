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
        // Se o PC desligou no meio de uma otimização, desfaz o ajuste que ficou pela metade.
        try { await Otimizador.RecuperarAsync(parar); }
        catch (Exception ex) { Registro.Escrever("Otimizar: falha ao recuperar ajustes interrompidos: " + ex.Message); }
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
                bool Elevado() => Identificar(pipe).Elevado;
                resposta = pedido.Comando.StartsWith("otimizar-")
                    ? await OtimizarAsync(pedido, pipe, Elevado, parar)
                    : Processar(pedido, Elevado);
            }
            catch (Exception ex)
            {
                resposta = new Resposta { Ok = false, Mensagem = ex.Message };
            }
            await escritor.WriteLineAsync(Json.Serializar(resposta));
        }
        catch { /* cliente desconectou */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint acesso, bool herdar, uint pid);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processo, uint acesso, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    /// <summary>
    /// Descobre quem está do outro lado do pipe pelo token do processo do painel, lido pelo próprio
    /// serviço (SYSTEM). Antes isso era feito "personificando" o painel em nível de identificação,
    /// e nesse nível o Windows recusa até a consulta do nome da conta, então ninguém era reconhecido.
    /// </summary>
    private static WindowsIdentity? IdentidadeDoCliente(NamedPipeServerStream pipe)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x8, TOKEN_DUPLICATE = 0x2;
        try
        {
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            {
                var proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (proc != IntPtr.Zero)
                {
                    try
                    {
                        if (OpenProcessToken(proc, TOKEN_QUERY | TOKEN_DUPLICATE, out var token))
                        {
                            try { return new WindowsIdentity(token); }
                            finally { CloseHandle(token); }
                        }
                    }
                    finally { CloseHandle(proc); }
                }
            }
        }
        catch (Exception ex) { Registro.Escrever("Identificar pelo processo falhou: " + ex.Message); }

        // Plano B: copia o token durante a personificação e lê tudo depois, já como SYSTEM.
        WindowsIdentity? copia = null;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(true);
                if (id is not null) copia = new WindowsIdentity(id.AccessToken.DangerousGetHandle());
            });
        }
        catch (Exception ex) { Registro.Escrever("Identificar pela personificação falhou: " + ex.Message); }
        return copia;
    }

    /// <summary>
    /// O painel tem a confirmação de administrador do Windows (UAC)? Vale só o grupo Administradores
    /// ATIVO no token, ou seja, um processo elevado. Uma conta administradora com o painel comum
    /// (token filtrado pelo UAC) não basta: para ações de alto impacto, o painel pede a confirmação.
    /// </summary>
    private static (bool Elevado, string Conta, string? Sid) Identificar(NamedPipeServerStream pipe)
    {
        const string admins = "S-1-5-32-544";
        using var id = IdentidadeDoCliente(pipe);
        if (id is null) return (false, "?", null);
        string sid = id.User?.Value ?? "";
        string conta;
        try { conta = id.Name; } catch { conta = sid; }
        bool elevado = id.Groups?.Any(g => g.Value == admins) == true;
        return (elevado, conta, sid);
    }

    /// <summary>Resposta que faz o painel pedir a confirmação do Windows e repetir o pedido.</summary>
    private static Resposta PedirElevacao(string acao) => new()
    {
        Ok = false, PrecisaElevar = true,
        Mensagem = $"Para {acao}, o Windows precisa da confirmação de um administrador do PC.",
    };

    /// <summary>Quem está no painel (para os ajustes que valem só para essa pessoa).</summary>
    private static string? SidDoCliente(NamedPipeServerStream pipe)
    {
        using var id = IdentidadeDoCliente(pipe);
        return id?.User?.Value;
    }

    private async Task<Resposta> OtimizarAsync(Pedido p, NamedPipeServerStream pipe, Func<bool> elevado, CancellationToken parar)
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
                // Muda o Windows como SYSTEM: precisa da confirmação de administrador (UAC) a cada vez.
                var quem = Identificar(pipe);
                if (!quem.Elevado) return PedirElevacao("otimizar o Windows");
                var desfazer = p.Comando == "otimizar-desfazer";
                // O painel elevado informa a conta de quem pediu (na Proteção de administrador do Windows 11,
                // o processo elevado roda com outra conta); sem isso, vale a conta do próprio processo.
                var sid = Otimizador.SidValido(p.SidUsuario) ? p.SidUsuario : quem.Sid;
                var erro = Otimizador.Iniciar(p.Ids ?? new(), p.PerfisUso ?? new(), sid, desfazer, p.SemPontoRestauracao, _vida.ApplicationStopping);
                return erro is null
                    ? new Resposta { Mensagem = desfazer ? "Desfazendo..." : "Aplicando...", Otimizacao = Otimizador.Andamento }
                    : new Resposta { Ok = false, Mensagem = erro };
        }
        return new Resposta { Ok = false, Mensagem = $"Comando desconhecido: {p.Comando}" };
    }

    /// <summary>
    /// Níveis de permissão:
    /// • livre (qualquer pessoa no painel): ver estado e histórico, rodar e parar vistorias, pausar,
    ///   silenciar avisos, atualizar o Zelo pelo canal oficial e a maioria dos ajustes;
    /// • com confirmação do Windows (UAC): otimizar, backup, limpeza de Downloads, instalações automáticas,
    ///   desligar verificações de segurança e reconhecer alertas de segurança.
    /// </summary>
    private Resposta Processar(Pedido p, Func<bool> elevado)
    {
        switch (p.Comando)
        {
            case "estado":
                return new Resposta { Estado = MontarEstado() };

            case "historico":
                return new Resposta { Historico = Historico.Carregar(Math.Clamp(p.Quantidade, 1, 600)) };

            case "executar":
                if (p.Rotina is null) return new Resposta { Ok = false, Mensagem = "Rotina não informada." };
                if (_motor.Ocupado || Coordenador.Atual is not null) return new Resposta { Ok = false, Mensagem = Coordenador.Explicar() };
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
                // Só instala o pacote oficial do GitHub, conferido pelo SHA-256 (o mesmo da atualização automática).
                if (_motor.Ocupado)
                    return new Resposta { Ok = false, Mensagem = "Espere a rotina em andamento terminar para atualizar." };
                if (Atualizador.EmAndamento)
                    return new Resposta { Mensagem = "A atualização já está em andamento." };
                _ = Task.Run(async () =>
                {
                    try { Registro.Escrever("Atualização manual: " + await Atualizador.VerificarEAplicarAsync(true, _vida.ApplicationStopping)); }
                    catch (Exception ex) { Registro.Escrever("Erro na atualização manual: " + ex.Message); }
                });
                return new Resposta { Mensagem = "Procurando atualização no GitHub… Se houver versão nova, o Zelo instala e reinicia sozinho em seguida." };

            case "dispensar":
                if (string.IsNullOrWhiteSpace(p.TarefaId) || Catalogo.Todas.All(t => t.Id != p.TarefaId))
                    return new Resposta { Ok = false, Mensagem = "Verificação desconhecida." };
                // Sem motivo: só silencia (a nota continua contando o problema).
                // Com motivo: a pessoa reconhece o achado; em segurança, isso pede confirmação do Windows.
                var reconhece = !string.IsNullOrWhiteSpace(p.Motivo);
                var categoria = Catalogo.Todas.First(t => t.Id == p.TarefaId).Categoria;
                if (reconhece && categoria == Categoria.Seguranca && !elevado())
                    return PedirElevacao("confirmar um alerta de segurança");
                Dispensas.Dispensar(p.TarefaId, p.Motivo);
                Registro.Escrever($"Alerta de {p.TarefaId} " + (reconhece ? $"reconhecido pelo usuário: {p.Motivo}." : "silenciado pelo usuário."));
                return new Resposta
                {
                    Mensagem = reconhece
                        ? "Anotado. Conta como resolvido até a próxima verificação."
                        : "Aviso silenciado. Ele continua contando na nota até uma nova verificação não encontrar mais o problema.",
                };

            case "executar-tarefa":
                var tarefa = Catalogo.Todas.FirstOrDefault(t => t.Id == p.TarefaId);
                if (tarefa is null) return new Resposta { Ok = false, Mensagem = "Verificação desconhecida." };
                if (_motor.Ocupado || Coordenador.Atual is not null) return new Resposta { Ok = false, Mensagem = Coordenador.Explicar() };
                if (Atualizador.EmAndamento) return new Resposta { Ok = false, Mensagem = "O Zelo está se atualizando. Tente de novo em alguns minutos." };
                var rotinaDaTarefa = tarefa.Rotinas[0];
                _ = Task.Run(async () =>
                {
                    try { await _motor.ExecutarAsync(rotinaDaTarefa, false, "tarefa", _vida.ApplicationStopping, tarefa.Id); }
                    catch (Exception ex) { Registro.Escrever("Erro ao repetir tarefa: " + ex); }
                });
                return new Resposta { Mensagem = $"Tentando de novo: {tarefa.Nome}." };

            case "registrar":
                // O painel não escreve na pasta de dados (protegida): manda as linhas para o serviço gravar.
                var texto = (p.Motivo ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (texto.Length > 0) Registro.Escrever("Painel: " + (texto.Length > 400 ? texto[..400] + "…" : texto));
                return new Resposta();

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
                // Normaliza antes de comparar: listas nulas, espaços e números fora do limite não passam.
                p.Config.PastasBackup ??= new();
                p.Config.TarefasDesativadas ??= new();
                p.Config.Validar();
                // Horários, limpeza e avisos qualquer pessoa ajusta. O que decide o que o serviço (SYSTEM)
                // copia ou apaga fora das pastas temporárias, ou o que ele instala, só administradores.
                var sensivel = MudancasSensiveis(ArmazemConfig.Carregar(), p.Config);
                if (sensivel.Count > 0 && !elevado())
                    return PedirElevacao("mudar " + string.Join(", ", sensivel));
                var erro = ValidarDestino(p.Config);
                if (erro is not null) return new Resposta { Ok = false, Mensagem = erro };
                ArmazemConfig.Salvar(p.Config);
                Registro.Escrever("Configuração alterada pelo painel.");
                return new Resposta { Mensagem = "Configurações salvas.", Config = ArmazemConfig.Carregar() };

            default:
                return new Resposta { Ok = false, Mensagem = $"Comando desconhecido: {p.Comando}" };
        }
    }

    /// <summary>Opções que fazem o serviço copiar, apagar ou instalar coisas além da limpeza comum.</summary>
    internal static List<string> MudancasSensiveis(Configuracao atual, Configuracao nova) => Configuracao.MudancasSensiveis(atual, nova);

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
