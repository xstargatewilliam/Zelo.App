using System.Net.Http;
using Xunit;
using Xunit.Abstractions;
using Zelo.Core;

namespace Zelo.Tests;

public class AtualizadorTests
{
    private readonly ITestOutputHelper _saida;
    public AtualizadorTests(ITestOutputHelper saida) => _saida = saida;

    [Theory]
    [InlineData("https://github.com/xstargatewilliam/Zelo.App/releases/tag/v1.5.4", "v1.5.4")]
    [InlineData("/xstargatewilliam/Zelo.App/releases/tag/v2.0.0/", "v2.0.0")]
    [InlineData("https://github.com/xstargatewilliam/Zelo.App/releases/tag/v1.6.0?x=1", "v1.6.0")]
    [InlineData("https://github.com/xstargatewilliam/Zelo.App/releases", null)]
    [InlineData("https://github.com/xstargatewilliam/Zelo.App/releases/tag/", null)]
    public void LeATagDoRedirecionamento(string endereco, string? esperado) =>
        Assert.Equal(esperado, Atualizador.TagDoEndereco(endereco));

    [Fact]
    public void LimiteDoGitHubViraFraseEmPortugues()
    {
        var msg = Atualizador.ExplicarErro(new HttpRequestException("Response status code does not indicate success: 403 (rate limit exceeded)."));
        Assert.Contains("limitou as consultas", msg);
        Assert.DoesNotContain("403", msg);
    }

    /// <summary>Consulta de verdade, sem a API: a versão publicada e o .sha256 precisam ser encontrados.</summary>
    [Fact]
    public async Task EncontraAUltimaVersaoSemUsarAApi()
    {
        var v = await Atualizador.ConsultarAsync(CancellationToken.None);
        Assert.NotNull(v);
        _saida.WriteLine($"{v!.Tag} · {v.UrlPacote}");
        Assert.StartsWith("https://github.com/xstargatewilliam/Zelo.App/releases/download/", v.UrlPacote);
        Assert.True(v.Versao >= new Version(1, 5, 4));

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Zelo-testes");
        var hash = (await http.GetStringAsync(v.UrlHash)).Trim();
        _saida.WriteLine(hash);
        Assert.Equal(64, hash.Split(' ', '\t')[0].Length);
        using var pacote = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, v.UrlPacote));
        Assert.True(pacote.IsSuccessStatusCode, $"pacote: {(int)pacote.StatusCode}");
    }
}
