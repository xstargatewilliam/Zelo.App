using System.IO.Pipes;
using System.Text;
using Xunit;
using Zelo.Core;

namespace Zelo.Tests;

public class ProtocoloTests
{
    /// <summary>Imita a thread da interface parada: nada que for "postado" nela roda.</summary>
    private sealed class ContextoTravado : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { /* nunca executa */ }
        public override void Send(SendOrPostCallback d, object? state) { /* nunca executa */ }
    }

    /// <summary>
    /// Regressão da 1.5.0: a cópia elevada do painel esperava a resposta do serviço na thread da
    /// interface e travava para sempre. O cliente do canal não pode depender dessa thread.
    /// </summary>
    [Fact]
    public async Task Cliente_do_canal_nao_trava_esperando_na_thread_da_interface()
    {
        using var servidor = new NamedPipeServerStream(Caminhos.NomePipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var atender = Task.Run(async () =>
        {
            await servidor.WaitForConnectionAsync();
            using var leitor = new StreamReader(servidor, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var escritor = new StreamWriter(servidor, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await leitor.ReadLineAsync();
            await escritor.WriteLineAsync(Json.Serializar(new Resposta { Ok = true, Mensagem = "pong" }));
        });

        Resposta? resposta = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new ContextoTravado());
            resposta = ClientePipe.EnviarAsync(new Pedido { Comando = "estado" }).GetAwaiter().GetResult();
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "o cliente travou esperando a thread da interface");
        Assert.Equal("pong", resposta?.Mensagem);
        await atender;
    }

    [Fact]
    public void Mudancas_que_apagam_ou_tiram_protecao_pedem_confirmacao()
    {
        var atual = new Configuracao { PastasBackup = { @"C:\Users\a\Documents", @"C:\Users\a\Pictures" }, DestinoBackup = @"E:\" };
        Configuracao Copia() => Json.Desserializar<Configuracao>(Json.Serializar(atual))!;

        var reordenada = Copia();
        reordenada.PastasBackup.Reverse();
        reordenada.PastasBackup[0] += "\\";
        Assert.Empty(Configuracao.MudancasSensiveis(atual, reordenada));

        var lixeira = Copia(); lixeira.EsvaziarLixeira = true;
        Assert.Contains(Configuracao.MudancasSensiveis(atual, lixeira), m => m.Contains("Lixeira"));

        var desligaBackup = Copia(); desligaBackup.TarefasDesativadas.Add("backup-incremental");
        Assert.NotEmpty(Configuracao.MudancasSensiveis(atual, desligaBackup));

        var downloads = Copia(); downloads.LimparDownloads = true;
        Assert.NotEmpty(Configuracao.MudancasSensiveis(atual, downloads));

        // Desligar o que apaga ou mudar horários: livre.
        var livre = Copia(); livre.LimparDownloads = false; livre.EsvaziarLixeira = false; livre.ExpedienteFim = 19; livre.LimparCacheNavegadores = true;
        Assert.Empty(Configuracao.MudancasSensiveis(atual, livre));
    }
}
