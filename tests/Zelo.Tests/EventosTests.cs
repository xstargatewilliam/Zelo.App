using Xunit;
using Zelo.Core;
using Zelo.Core.Tarefas;

namespace Zelo.Tests;

public class EventosTests
{
    private const string KP = "Microsoft-Windows-Kernel-Power";

    /// <summary>Um desligamento inesperado (falta de luz, botão) não derruba a nota para "urgente".</summary>
    [Fact]
    public void UmDesligamentoEAviso()
    {
        var (status, resumo, _) = EventosCriticos.Avaliar(new[] { (KP, 41, 1) }, 3);
        Assert.Equal(StatusTarefa.Aviso, status);
        Assert.Contains("pode dispensar", resumo);
    }

    [Fact]
    public void DesligamentosRepetidosSaoCriticos() =>
        Assert.Equal(StatusTarefa.Critico, EventosCriticos.Avaliar(new[] { (KP, 41, EventosCriticos.DesligamentosParaCritico) }, 5).Status);

    [Fact]
    public void TelaAzulContinuaCritica()
    {
        var (status, resumo, _) = EventosCriticos.Avaliar(new[] { (KP, 41, 1), ("Microsoft-Windows-WER-SystemErrorReporting", 1001, 1) }, 4);
        Assert.Equal(StatusTarefa.Critico, status);
        Assert.Contains("tela azul", resumo);
        Assert.Contains("desligamento", resumo);
    }

    [Fact]
    public void SemNadaGraveEOk() =>
        Assert.Equal(StatusTarefa.Ok, EventosCriticos.Avaliar(new[] { ("Service Control Manager", 7000, 2) }, 2).Status);
}
