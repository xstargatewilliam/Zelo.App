using Xunit;
using Xunit.Abstractions;
using Zelo.Core;

namespace Zelo.Tests;

public class AssinaturaTests : ComPastaTemporaria
{
    private readonly ITestOutputHelper _saida;
    public AssinaturaTests(ITestOutputHelper saida) => _saida = saida;

    [Fact]
    public void ArquivoSemAssinaturaNaoEValido()
    {
        var f = Path.Combine(Pasta, "sem-assinatura.exe");
        File.WriteAllBytes(f, new byte[] { 0x4D, 0x5A, 1, 2, 3 });
        var a = Assinatura.Ler(f);
        Assert.False(a.Valida);
        Assert.False(Assinatura.Ler(Path.Combine(Pasta, "nao-existe.exe")).Valida);
    }

    /// <summary>Um executável assinado de verdade (o dotnet.exe, assinado pela Microsoft) é reconhecido.</summary>
    [Fact]
    public void ReconheceUmExecutavelAssinadoDeVerdade()
    {
        var candidatos = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe"),
        };
        var f = candidatos.FirstOrDefault(File.Exists);
        if (f is null) { _saida.WriteLine("dotnet.exe não encontrado: verificação real NÃO EXECUTADA."); return; }
        var a = Assinatura.Ler(f);
        _saida.WriteLine($"{f}: válida={a.Valida}, editor={a.Editor}");
        Assert.True(a.Valida);
        Assert.False(string.IsNullOrWhiteSpace(a.Editor)); // a Microsoft assina o dotnet.exe como ".NET"
    }

    [Fact]
    public void Pacote_assinado_exige_que_painel_e_instalador_sejam_do_mesmo_editor()
    {
        var editor = new Assinatura(true, "SignPath Foundation");
        var outro = new Assinatura(true, "Outro editor");
        Assert.Null(Assinatura.ConferirPacote(editor, new[]
        {
            ("Serviço", editor), ("Painel", editor), ("Instalador", editor),
        }));
        var painelInvalido = Assinatura.ConferirPacote(editor, new[]
        {
            ("Serviço", editor), ("Painel", Assinatura.Nenhuma), ("Instalador", editor),
        });
        Assert.Contains("Painel", painelInvalido);
        var instaladorDiferente = Assinatura.ConferirPacote(editor, new[]
        {
            ("Serviço", editor), ("Painel", editor), ("Instalador", outro),
        });
        Assert.Contains("Instalador", instaladorDiferente);
        // Compatibilidade: instalar a primeira versão assinada ainda depende da origem e do SHA-256.
        Assert.Null(Assinatura.ConferirPacote(Assinatura.Nenhuma, new[] { ("Serviço", editor), ("Painel", Assinatura.Nenhuma) }));
    }

    [Fact]
    public void RegrasDaAtualizacao()
    {
        var semAssinatura = Assinatura.Nenhuma;
        var signPath = new Assinatura(true, "SignPath Foundation");
        var outro = new Assinatura(true, "Fulano");

        Assert.Null(Assinatura.Conferir(semAssinatura, semAssinatura)); // hoje: vale o SHA-256
        Assert.Null(Assinatura.Conferir(semAssinatura, signPath));      // primeira versão assinada
        Assert.Null(Assinatura.Conferir(signPath, signPath));
        Assert.NotNull(Assinatura.Conferir(signPath, semAssinatura));   // perdeu a assinatura: recusa
        Assert.NotNull(Assinatura.Conferir(signPath, outro));           // outro editor: recusa
    }
}
