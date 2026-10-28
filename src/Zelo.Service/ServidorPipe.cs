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
                resposta = Processar(pedido, () => ClienteEhAdministrador(pipe));
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
                var rotina = p.Rotina.Value;
                var sim = p.Simulacao;
                _ = Task.Run(async () =>
                {
                    try { await _motor.ExecutarAsync(rotina, sim, "manual", _vida.ApplicationStopping); }
                    catch (Exception ex) { Registro.Escrever("Erro na execução manual: " + ex); }
                });
                return new Resposta { Mensagem = $"Rotina {rotina.Nome()} iniciada{(sim ? " em modo simulação" : "")}." };

            case "cancelar":
                _motor.Cancelar();
                return new Resposta { Mensagem = "Cancelamento solicitado. A tarefa atual será interrompida." };

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
        foreach (var (r, quando) in ultimas)
        {
            e.Ultima[r.ToString()] = quando;
            e.Proxima[r.ToString()] = Agenda.Proxima(r, quando);
        }
        return e;
    }
}
