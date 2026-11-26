using Xunit;
using Zelo.Instalador;

namespace Zelo.Tests;

public class InstaladorTests : ComPastaTemporaria
{
    private void CriarPacote(string raiz)
    {
        Directory.CreateDirectory(Path.Combine(raiz, "publicado", "Servico"));
        Directory.CreateDirectory(Path.Combine(raiz, "publicado", "Painel"));
        Directory.CreateDirectory(Path.Combine(raiz, "publicado", "Instalador"));
        File.WriteAllText(Path.Combine(raiz, "publicado", "Servico", "Zelo.Service.exe"), "");
        File.WriteAllText(Path.Combine(raiz, "publicado", "Painel", "Zelo.App.exe"), "");
    }

    [Fact]
    public void AchaOPacoteAoLadoOuAcima()
    {
        CriarPacote(Pasta);
        var esperado = Path.Combine(Pasta, "publicado");
        Assert.Equal(esperado, Program.LocalizarPacote(Pasta));                                   // "Instalar Zelo.exe" na raiz do zip
        Assert.Equal(esperado, Program.LocalizarPacote(Path.Combine(esperado, "Instalador")));    // a cópia em publicado\Instalador
    }

    /// <summary>Aberto de dentro do .zip, o Windows extrai só o .exe: o instalador explica em vez de seguir.</summary>
    [Fact]
    public void SemOPacoteNaoInstala()
    {
        File.WriteAllText(Path.Combine(Pasta, "Instalar Zelo.exe"), "");
        Assert.Null(Program.LocalizarPacote(Pasta));
    }

    [Fact]
    public void Restaura_a_versao_anterior_inclusive_o_instalador()
    {
        var salvo = Path.Combine(Pasta, "anterior");
        var atual = Path.Combine(Pasta, "atual");
        foreach (var parte in new[] { "Servico", "Painel", "Instalador" })
        {
            Directory.CreateDirectory(Path.Combine(salvo, parte));
            Directory.CreateDirectory(Path.Combine(atual, parte));
            File.WriteAllText(Path.Combine(salvo, parte, "versao.txt"), "antiga");
            File.WriteAllText(Path.Combine(atual, parte, "versao.txt"), "nova");
        }
        Assert.True(Program.RestaurarVersao(salvo, atual));
        foreach (var parte in new[] { "Servico", "Painel", "Instalador" })
            Assert.Equal("antiga", File.ReadAllText(Path.Combine(atual, parte, "versao.txt")));
    }

    [Fact]
    public void Restaura_versao_antiga_sem_instalador_removendo_o_novo()
    {
        var salvo = Path.Combine(Pasta, "anterior");
        var atual = Path.Combine(Pasta, "atual");
        foreach (var parte in new[] { "Servico", "Painel" })
        {
            Directory.CreateDirectory(Path.Combine(salvo, parte));
            Directory.CreateDirectory(Path.Combine(atual, parte));
            File.WriteAllText(Path.Combine(salvo, parte, "versao.txt"), "antiga");
        }
        Directory.CreateDirectory(Path.Combine(atual, "Instalador"));
        File.WriteAllText(Path.Combine(atual, "Instalador", "novo.exe"), "nova");
        Assert.True(Program.RestaurarVersao(salvo, atual));
        Assert.False(Directory.Exists(Path.Combine(atual, "Instalador")));
    }

    [Fact]
    public void EntradaEmAppsApontaParaODesinstalador()
    {
        var v = Program.ValoresDesinstalar(@"C:\Program Files\Zelo", "1.5.7", 1234).ToDictionary(x => x.Nome, x => x.Valor);
        Assert.Equal("Zelo", v["DisplayName"]);
        Assert.Equal("1.5.7", v["DisplayVersion"]);
        Assert.Equal("\"C:\\Program Files\\Zelo\\Instalador\\Zelo.Instalador.exe\" --desinstalar", v["UninstallString"]);
        Assert.Equal(1, v["NoModify"]);
        Assert.Equal(1234, v["EstimatedSize"]);
    }

    [Fact]
    public void SabeSeHaAjustesParaDesfazer()
    {
        var f = Path.Combine(Pasta, "otimizacao.json");
        Assert.False(Program.TemAjustesAplicados(f));
        File.WriteAllText(f, """{"Versao":2,"Aplicados":{}}""");
        Assert.False(Program.TemAjustesAplicados(f));
        File.WriteAllText(f, """{"Versao":2,"Aplicados":{"priv-anuncios":{"Antes":[]}}}""");
        Assert.True(Program.TemAjustesAplicados(f));
        File.WriteAllText(f, "corrompido{");
        Assert.False(Program.TemAjustesAplicados(f));
    }
}
