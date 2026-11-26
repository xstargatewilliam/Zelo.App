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

    /// <summary>Depois de instalar à mão a versão nova, o erro e o "versão disponível" antigos somem.</summary>
    [Fact]
    public void EstadoDeOutraVersaoSomeAoIniciar()
    {
        var e = new Atualizador.EstadoAtualizacao
        {
            UltimaVerificacao = DateTime.Now.AddMinutes(-5),
            UltimoErro = "A última consulta ao GitHub falhou: 403",
            VersaoDisponivel = "1.5.5",
            VersaoNaVerificacao = null, // estado gravado pela 1.5.3, que não tinha esse campo
        };
        Assert.True(Atualizador.Conferir(e, new Version(1, 5, 5)));
        Assert.Null(e.UltimoErro);
        Assert.Null(e.VersaoDisponivel);
        Assert.Null(e.UltimaVerificacao); // força uma consulta logo
    }

    /// <summary>Na mesma versão (por exemplo, depois de uma atualização desfeita), o aviso continua.</summary>
    [Fact]
    public void EstadoDaMesmaVersaoFica()
    {
        var quando = DateTime.Now.AddHours(-1);
        var e = new Atualizador.EstadoAtualizacao
        {
            UltimaVerificacao = quando,
            UltimoErro = "A versão 1.6.0 não iniciou neste PC e foi desfeita.",
            VersaoDisponivel = "1.6.0",
            VersaoNaVerificacao = "1.5.6",
        };
        Assert.False(Atualizador.Conferir(e, new Version(1, 5, 6)));
        Assert.Equal("1.6.0", e.VersaoDisponivel);
        Assert.NotNull(e.UltimoErro);
        Assert.Equal(quando, e.UltimaVerificacao);
    }

    [Fact]
    public void VersaoDisponivelInvalidaSome()
    {
        var e = new Atualizador.EstadoAtualizacao { VersaoDisponivel = "abc", VersaoNaVerificacao = "1.5.6" };
        Assert.True(Atualizador.Conferir(e, new Version(1, 5, 6)));
        Assert.Null(e.VersaoDisponivel);
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
