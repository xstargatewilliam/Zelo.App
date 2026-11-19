using Xunit;
using Zelo.Core;

namespace Zelo.Tests;

public class ConfiguracaoTests
{
    // 16/11/2026 é segunda-feira.
    private static DateTime Dia(int dia, int hora) => new(2026, 11, dia, hora, 0, 0);

    [Fact]
    public void Expediente_comum_nos_dias_uteis()
    {
        var c = new Configuracao { Perfil = "Trabalho", ExpedienteInicio = 9, ExpedienteFim = 18 };
        Assert.True(c.EmExpediente(Dia(16, 10)));
        Assert.False(c.EmExpediente(Dia(16, 18)));
        Assert.False(c.EmExpediente(Dia(16, 8)));
        Assert.False(c.EmExpediente(Dia(21, 10))); // sábado
        Assert.True(c.DentroDaJanela(Dia(16, 20)));
        Assert.False(c.DentroDaJanela(Dia(16, 11)));
    }

    [Fact]
    public void Turno_da_noite_atravessa_a_meia_noite()
    {
        var c = new Configuracao { Perfil = "Trabalho", ExpedienteInicio = 22, ExpedienteFim = 6 };
        Assert.True(c.EmExpediente(Dia(16, 23)));   // segunda à noite
        Assert.True(c.EmExpediente(Dia(17, 3)));    // madrugada de terça pertence à segunda
        Assert.True(c.EmExpediente(Dia(21, 3)));    // madrugada de sábado pertence à sexta
        Assert.False(c.EmExpediente(Dia(22, 3)));   // madrugada de domingo: sábado não tem turno
        Assert.False(c.EmExpediente(Dia(16, 12)));
    }

    [Fact]
    public void Perfil_jogos_so_de_madrugada()
    {
        var c = new Configuracao { Perfil = "Jogos" };
        Assert.True(c.DentroDaJanela(Dia(16, 3)));
        Assert.False(c.DentroDaJanela(Dia(16, 9)));
        Assert.False(c.DentroDaJanela(Dia(16, 1)));
    }

    [Fact]
    public void Padroes_conservadores_para_a_beta()
    {
        var c = new Configuracao();
        Assert.False(c.EsvaziarLixeira);
        Assert.False(c.LimparDownloads);
        Assert.False(c.InstalarDriversWU);
        Assert.Equal(30, c.DiasVersoesBackup);
    }

    [Fact]
    public void Validar_corrige_valores_fora_do_limite()
    {
        var c = new Configuracao { DiasVersoesBackup = -5, DiasDownloads = 1, ExpedienteInicio = 9, ExpedienteFim = 9, Perfil = "qualquer" };
        c.Validar();
        Assert.Equal(0, c.DiasVersoesBackup);
        Assert.Equal(3, c.DiasDownloads);
        Assert.Equal((9, 18), (c.ExpedienteInicio, c.ExpedienteFim));
        Assert.Equal("Domestico", c.Perfil);
    }
}
