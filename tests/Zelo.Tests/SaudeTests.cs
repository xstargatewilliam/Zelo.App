using Xunit;
using Zelo.Core;

namespace Zelo.Tests;

public class SaudeTests : ComPastaTemporaria
{
    private static List<ExecucaoRotina> Historico(StatusTarefa statusSeguranca)
    {
        var fim = DateTime.Now.AddHours(-1);
        ResultadoTarefa R(string id, Categoria c, StatusTarefa s, string resumo) =>
            new() { TarefaId = id, Nome = id, Categoria = c, Status = s, Resumo = resumo, Inicio = fim.AddMinutes(-1), Fim = fim };
        return new()
        {
            new ExecucaoRotina
            {
                Rotina = Rotina.Diaria, Inicio = fim.AddMinutes(-5), Fim = fim,
                Resultados =
                {
                    R("defender-rapida", Categoria.Seguranca, statusSeguranca, "Ameaça encontrada."),
                    R("espaco-disco", Categoria.Disco, StatusTarefa.Ok, "Espaço ok."),
                    R("eventos-criticos", Categoria.Sistema, StatusTarefa.Ok, "Sem eventos."),
                },
            },
        };
    }

    [Fact]
    public void Silenciar_tira_da_lista_mas_nao_melhora_a_nota()
    {
        var antes = Saude.Calcular(Historico(StatusTarefa.Critico));
        Assert.Contains(antes.Pontos, p => p.TarefaId == "defender-rapida");

        Dispensas.Dispensar("defender-rapida");
        var depois = Saude.Calcular(Historico(StatusTarefa.Critico));

        Assert.Equal(antes.Geral, depois.Geral);
        Assert.DoesNotContain(depois.Pontos, p => p.TarefaId == "defender-rapida");
        Assert.Contains("defender-rapida", depois.Silenciados);
    }

    [Fact]
    public void Explicar_o_achado_conta_como_resolvido()
    {
        var antes = Saude.Calcular(Historico(StatusTarefa.Critico));
        Dispensas.Dispensar("defender-rapida", "foi um teste meu");
        var depois = Saude.Calcular(Historico(StatusTarefa.Critico));

        Assert.True(depois.Geral > antes.Geral);
        Assert.DoesNotContain(depois.Pontos, p => p.TarefaId == "defender-rapida");
        Assert.Empty(depois.Silenciados);
    }

    [Fact]
    public void Nova_verificacao_depois_da_dispensa_volta_a_valer()
    {
        Dispensas.Dispensar("defender-rapida");
        var historico = Historico(StatusTarefa.Critico);
        // Uma verificação feita depois de silenciar encontra o problema de novo.
        foreach (var r in historico[0].Resultados) { r.Fim = DateTime.Now.AddMinutes(5); }
        var painel = Saude.Calcular(historico);
        Assert.Contains(painel.Pontos, p => p.TarefaId == "defender-rapida");
        Assert.Empty(painel.Silenciados);
    }
}
