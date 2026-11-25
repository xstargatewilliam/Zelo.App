using System.Text.Json;
using Xunit;
using Zelo.Core;

namespace Zelo.Tests;

public class AppsDaContaTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Tudo_removido_e_ok()
    {
        var r = AppsDaConta.Ler(J("""{"Itens":[{"Nome":"Microsoft.BingNews","Erro":""}],"Restam":[]}"""));
        Assert.True(r.Ok);
        Assert.Equal(new[] { "Microsoft.BingNews" }, r.Removidos);
    }

    [Fact]
    public void Pacote_que_continua_instalado_nao_e_ok()
    {
        // PowerShell 5.1 manda um item sozinho como objeto, não como lista.
        var r = AppsDaConta.Ler(J("""{"Itens":{"Nome":"Microsoft.People","Erro":""},"Restam":"Microsoft.People"}"""));
        Assert.False(r.Ok);
        Assert.Equal(new[] { "Microsoft.People" }, r.Restam);
    }

    [Fact]
    public void Erro_do_Windows_vira_motivo()
    {
        var r = AppsDaConta.Ler(J("""{"Itens":[{"Nome":"Microsoft.Copilot","Erro":"Deployment failed with HRESULT: 0x80073CFA\r\nmais texto"}],"Restam":[]}"""));
        Assert.False(r.Ok);
        Assert.Contains("0x80073CFA", r.Erros[0]);
        Assert.DoesNotContain("mais texto", r.Erros[0]);
    }

    [Fact]
    public void Servico_nao_recebe_apps_nem_programas()
    {
        // App da Loja é removido pelo painel, na conta da pessoa: o serviço ignora o pedido.
        Assert.Null(Otimizador.Iniciar(new() { "app-noticias", "prog-git" }, new(), "S-1-5-21-1-2-3-1001", desfazer: false, semPonto: true, CancellationToken.None));
        for (int i = 0; i < 100 && Otimizador.Andamento.Rodando; i++) Thread.Sleep(50);
        Assert.Equal(0, Otimizador.Andamento.Total);
    }

    [Fact]
    public void Configuracao_protegida_pelo_Windows_explica_onde_fazer()
    {
        var widgets = CatalogoOtimizacao.Por("priv-widgets")!;
        var msg = Otimizador.Explicar(widgets, new UnauthorizedAccessException("Attempted to perform an unauthorized operation."));
        Assert.Contains("protege", msg);
        Assert.Contains("Barra de tarefas", msg);
    }
}
