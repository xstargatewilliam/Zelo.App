using System.Diagnostics;
using Xunit;
using Zelo.Core;
using Zelo.Core.Tarefas;

namespace Zelo.Tests;

/// <summary>
/// Roda num Windows de verdade (o computador de build do GitHub): cria junções reais, como uma
/// conta mal-intencionada faria, e confere que o serviço não apaga nada fora da pasta certa.
/// </summary>
public class ArquivosSegurosTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "zelo-seg-" + Guid.NewGuid().ToString("N"));
    private readonly string _alvo;   // "C:\Windows\System32" do teste: nada aqui pode ser apagado
    private readonly string _pasta;  // pasta que o Zelo limpa

    public ArquivosSegurosTests()
    {
        _alvo = Path.Combine(_raiz, "alvo-protegido");
        _pasta = Path.Combine(_raiz, "temp-do-usuario");
        Directory.CreateDirectory(_alvo);
        Directory.CreateDirectory(_pasta);
    }

    public void Dispose()
    {
        // Remove as junções antes (Directory.Delete não segue junções, mas por garantia).
        try { foreach (var d in Directory.EnumerateDirectories(_raiz, "*", SearchOption.AllDirectories).Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList()) Directory.Delete(d); } catch { }
        try { Directory.Delete(_raiz, true); } catch { }
    }

    private static void Juncao(string link, string destino)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{destino}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null, "não foi possível criar a junção de teste");
    }

    private static string Velho(string caminho, string conteudo = "x")
    {
        File.WriteAllText(caminho, conteudo);
        var antigo = DateTime.Now.AddDays(-60);
        File.SetCreationTime(caminho, antigo);
        File.SetLastWriteTime(caminho, antigo);
        return caminho;
    }

    [Fact]
    public void Apaga_arquivo_comum_dentro_da_pasta()
    {
        var a = Velho(Path.Combine(_pasta, "lixo.tmp"));
        Assert.True(ArquivosSeguros.Apagar(a, _pasta));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Apaga_arquivo_somente_leitura()
    {
        var a = Velho(Path.Combine(_pasta, "ro.tmp"));
        File.SetAttributes(a, FileAttributes.ReadOnly);
        Assert.True(ArquivosSeguros.Apagar(a, _pasta));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Pasta_trocada_por_juncao_nao_e_limpa()
    {
        var importante = Velho(Path.Combine(_alvo, "sistema.dll"));
        var temp = Path.Combine(_raiz, "temp-trocado");
        Juncao(temp, _alvo);

        Assert.False(ArquivosSeguros.PastaConfiavel(temp));
        Assert.False(ArquivosSeguros.Apagar(Path.Combine(temp, "sistema.dll"), temp));
        Assert.True(File.Exists(importante));
    }

    [Fact]
    public void Juncao_dentro_da_pasta_nao_leva_o_servico_para_fora()
    {
        var importante = Velho(Path.Combine(_alvo, "documento-de-outra-pessoa.docx"));
        var lixo = Velho(Path.Combine(_pasta, "lixo.tmp"));
        Juncao(Path.Combine(_pasta, "atalho"), _alvo);

        // Mesmo pedindo o caminho pelo atalho, o arquivo real está fora: não sai.
        Assert.False(ArquivosSeguros.Apagar(Path.Combine(_pasta, "atalho", "documento-de-outra-pessoa.docx"), _pasta));

        var (_, apagados, _) = LimpezaTemporarios.ApagarAntigos(_pasta, DateTime.Now.AddDays(-7), simulacao: false, CancellationToken.None);
        Assert.Equal(1, apagados);
        Assert.False(File.Exists(lixo));
        Assert.True(File.Exists(importante));
    }

    [Fact]
    public void Simulacao_nao_apaga_nada()
    {
        var lixo = Velho(Path.Combine(_pasta, "lixo.tmp"));
        var (_, apagados, _) = LimpezaTemporarios.ApagarAntigos(_pasta, DateTime.Now.AddDays(-7), simulacao: true, CancellationToken.None);
        Assert.Equal(1, apagados);
        Assert.True(File.Exists(lixo));
    }

    [Fact]
    public void Arquivo_recente_nunca_e_apagado()
    {
        var novo = Path.Combine(_pasta, "recente.tmp");
        File.WriteAllText(novo, "x");
        var (_, apagados, _) = LimpezaTemporarios.ApagarAntigos(_pasta, DateTime.Now.AddDays(-7), simulacao: false, CancellationToken.None);
        Assert.Equal(0, apagados);
        Assert.True(File.Exists(novo));
    }

    [Fact]
    public void Apagar_pasta_inteira_preserva_o_destino_das_juncoes()
    {
        var importante = Velho(Path.Combine(_alvo, "nao-apagar.txt"));
        var versao = Path.Combine(_pasta, "2020-01-01_0000");
        Directory.CreateDirectory(Path.Combine(versao, "sub"));
        Velho(Path.Combine(versao, "sub", "a.txt"));
        Juncao(Path.Combine(versao, "atalho"), _alvo);

        Assert.True(ArquivosSeguros.ApagarPasta(versao, _pasta));
        Assert.False(Directory.Exists(versao));
        Assert.True(File.Exists(importante));
    }

    [Fact]
    public void Destino_do_backup_com_atalho_e_detectado()
    {
        var destino = Path.Combine(_raiz, "Zelo-Backup");
        Directory.CreateDirectory(Path.Combine(destino, "pasta"));
        Assert.Null(ArquivosSeguros.PrimeiroAtalho(destino));
        Juncao(Path.Combine(destino, "pasta", "atalho"), _alvo);
        Assert.NotNull(ArquivosSeguros.PrimeiroAtalho(destino));
    }

    [Fact]
    public void Caminho_com_nome_curto_do_Windows_e_aceito()
    {
        // No computador de build, a pasta temporária vem como C:\Users\RUNNER~1\...
        Assert.True(ArquivosSeguros.PastaConfiavel(_pasta));
        Assert.True(ArquivosSeguros.PastaConfiavel(Path.GetTempPath()));
    }
}
