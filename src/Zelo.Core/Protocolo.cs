using System.IO.Pipes;
using System.Text;

namespace Zelo.Core;

/// <summary>Pedido do painel para o serviço (uma linha JSON por conexão).</summary>
public sealed class Pedido
{
    /// <summary>estado | historico | executar | cancelar | config-obter | config-salvar</summary>
    public string Comando { get; set; } = "";
    public Rotina? Rotina { get; set; }
    public bool Simulacao { get; set; }
    public Configuracao? Config { get; set; }
    public int Quantidade { get; set; } = 60;
}

public sealed class Resposta
{
    public bool Ok { get; set; } = true;
    public string? Mensagem { get; set; }
    public EstadoServico? Estado { get; set; }
    public Configuracao? Config { get; set; }
    public List<ExecucaoRotina>? Historico { get; set; }
}

public sealed class EstadoServico
{
    public string Versao { get; set; } = "";
    public Rotina? RotinaAtual { get; set; }
    public bool SimulacaoAtual { get; set; }
    public string? TarefaAtual { get; set; }
    public int Indice { get; set; }
    public int Total { get; set; }
    public PainelSaude Saude { get; set; } = new();
    /// <summary>Chave = nome da rotina (Diaria/Semanal/Mensal).</summary>
    public Dictionary<string, DateTime?> Ultima { get; set; } = new();
    public Dictionary<string, DateTime> Proxima { get; set; } = new();
    public bool NaTomada { get; set; }
    public string? UltimaExecucaoId { get; set; }
}

/// <summary>Cliente usado pelo painel.</summary>
public static class ClientePipe
{
    public static async Task<Resposta> EnviarAsync(Pedido pedido, int timeoutMs = 2500)
    {
        using var pipe = new NamedPipeClientStream(".", Caminhos.NomePipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var cts = new CancellationTokenSource(timeoutMs);
        await pipe.ConnectAsync(cts.Token);

        var utf8 = new UTF8Encoding(false);
        using var escritor = new StreamWriter(pipe, utf8, 4096, leaveOpen: true) { AutoFlush = true };
        using var leitor = new StreamReader(pipe, utf8, false, 65536, leaveOpen: true);

        await escritor.WriteLineAsync(Json.Serializar(pedido));
        using var cts2 = new CancellationTokenSource(15000);
        var linha = await leitor.ReadLineAsync(cts2.Token)
                    ?? throw new IOException("O serviço fechou a conexão sem responder.");
        return Json.Desserializar<Resposta>(linha) ?? throw new IOException("Resposta inválida do serviço.");
    }
}
