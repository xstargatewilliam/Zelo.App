using Xunit;
using Xunit.Abstractions;
using Zelo.Core;
using Zelo.Core.Tarefas;

namespace Zelo.Tests;

public class WingetTests
{
    private readonly ITestOutputHelper _saida;
    public WingetTests(ITestOutputHelper saida) => _saida = saida;

    private const string Ingles = """
           -    \    |   
        Name                          Id                        Version      Available    Source
        ----------------------------------------------------------------------------------------
        7-Zip 23.01 (x64)             7zip.7zip                 23.01        24.08        winget
        Discord                       Discord.Discord           1.0.9170     1.0.9175     winget
        Microsoft Visual C++ 2015…    Microsoft.VCRedist.2015+… 14.38.33130  14.42.34433  winget
        3 upgrades available.
        """;

    private const string Portugues = "\r   - \r   \\ \r" + """
        Nome                Id                  Versão   Disponível Origem
        -----------------------------------------------------------------
        Git                 Git.Git             2.44.0   2.47.1     winget
        Spotify Music       9NCBCSZSJRSB        1.2.40   1.2.52     msstore
        2 atualizações disponíveis.
        """;

    [Fact]
    public void Le_a_tabela_em_ingles()
    {
        var p = AtualizacaoProgramas.LerTabela(Ingles)!;
        Assert.Equal(new[] { "7zip.7zip", "Discord.Discord", "Microsoft.VCRedist.2015+…" }.Take(2), p.Select(x => x.Id).Take(2));
        Assert.Equal("23.01", p[0].Versao);
        Assert.Equal("24.08", p[0].Disponivel);
        Assert.Equal("7-Zip 23.01 (x64)", p[0].Nome);
        // O Id cortado é lido (para ser relatado), mas não confunde com um Id de verdade.
        Assert.Equal("Microsoft.VCRedist.2015+…", p[2].Id);
    }

    [Fact]
    public void Versao_ja_instalada_conta_como_atualizado() =>
        Assert.Contains("já está instalada", AtualizacaoProgramas.ExplicarCodigo(unchecked((int)0x8A15010D)));

    [Fact]
    public void Le_a_tabela_em_portugues_com_barra_de_progresso()
    {
        var p = AtualizacaoProgramas.LerTabela(Portugues)!;
        Assert.Equal(2, p.Count);
        Assert.Equal("Git.Git", p[0].Id);
        Assert.Equal("9NCBCSZSJRSB", p[1].Id);
        Assert.Equal("1.2.52", p[1].Disponivel);
    }

    [Fact]
    public void O_proprio_winget_e_o_Edge_ficam_de_fora()
    {
        foreach (var id in new[] { "Microsoft.AppInstaller", "Microsoft.EdgeWebView2Runtime", "Microsoft.Edge", "Microsoft.VCLibs.140.00" })
            Assert.Contains(AtualizacaoProgramas.Frameworks, f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(AtualizacaoProgramas.Frameworks, f => "Microsoft.PowerShell".StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sem_tabela_e_nulo()
    {
        Assert.Null(AtualizacaoProgramas.LerTabela("No installed package found matching input criteria."));
        Assert.Null(AtualizacaoProgramas.LerTabela(""));
    }

    [Fact]
    public void Codigos_tem_explicacao_em_portugues()
    {
        Assert.Contains("aberto", AtualizacaoProgramas.ExplicarCodigo(unchecked((int)0x8A150101)));
        Assert.Contains("reiniciar", AtualizacaoProgramas.ExplicarCodigo(unchecked((int)0x8A150109)));
        Assert.Contains("source reset", AtualizacaoProgramas.ExplicarCodigo(unchecked((int)0x8A15000F)));
        Assert.False(string.IsNullOrWhiteSpace(AtualizacaoProgramas.ExplicarCodigo(12345)));
    }

    /// <summary>
    /// Roda o winget de verdade, se o computador de teste tiver, e confere que a tabela é lida.
    /// (Só lista; não atualiza nada.)
    /// </summary>
    [Fact]
    public async Task Le_a_saida_real_do_winget_quando_existe()
    {
        var winget = AtualizacaoProgramas.LocalizarWinget() ?? (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe")) ? "winget" : null);
        if (winget is null) { _saida.WriteLine("winget não existe neste computador de teste: verificação real NÃO EXECUTADA."); return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var r = await Executor.ProcessoAsync(winget, "upgrade --accept-source-agreements --disable-interactivity", cts.Token, System.Text.Encoding.UTF8);
        var pacotes = AtualizacaoProgramas.LerTabela(r.Saida);
        _saida.WriteLine($"winget: {winget}; código 0x{r.Codigo:X8} ({AtualizacaoProgramas.ExplicarCodigo(r.Codigo)}); pacotes lidos: {(pacotes is null ? "sem tabela" : string.Join(", ", pacotes.Select(p => p.Id)))}");
        foreach (var l in r.Linhas(12)) _saida.WriteLine("  " + l);
        if (r.Saida.Contains("---") && r.Saida.Contains(" Id ")) Assert.NotNull(pacotes);
        if (pacotes is not null) Assert.All(pacotes, p => Assert.Matches(@"^[\w.+-]{2,}…?$", p.Id));
    }
}
