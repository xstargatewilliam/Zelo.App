using Xunit;
using Zelo.Core;

namespace Zelo.Tests;

/// <summary>Regras de segurança do catálogo da aba Otimizar, conferidas a cada versão.</summary>
public class OtimizacaoTests
{
    private static readonly List<Ajuste> Todos = CatalogoOtimizacao.Todos;

    [Fact]
    public void Ids_unicos_e_grupos_conhecidos()
    {
        Assert.Equal(Todos.Count, Todos.Select(a => a.Id).Distinct().Count());
        Assert.All(Todos, a => Assert.Contains(a.Grupo, CatalogoOtimizacao.Grupos));
        Assert.All(Todos, a => Assert.False(string.IsNullOrWhiteSpace(a.Nome) || string.IsNullOrWhiteSpace(a.Descricao), a.Id));
    }

    [Fact]
    public void Cada_tipo_tem_o_que_precisa_para_aplicar_e_desfazer()
    {
        foreach (var a in Todos)
        {
            switch (a.Tipo)
            {
                case TipoAjuste.Registro:
                    Assert.NotEmpty(a.Valores);
                    Assert.All(a.Valores, v => Assert.True(v.Caminho.StartsWith(@"HKLM\") || v.Caminho.StartsWith(@"HKCU\"), a.Id));
                    Assert.All(a.Valores, v => Assert.True(v.Valor is int or string, a.Id));
                    break;
                case TipoAjuste.Servico:
                    Assert.NotEmpty(a.Servicos);
                    Assert.Contains(a.InicioServico, new[] { 3, 4 });
                    break;
                case TipoAjuste.App:
                    Assert.NotEmpty(a.Pacotes);
                    Assert.False(string.IsNullOrWhiteSpace(a.BuscaLoja), a.Id);
                    break;
                case TipoAjuste.Script:
                    Assert.False(string.IsNullOrWhiteSpace(a.ScriptLer), a.Id);
                    Assert.False(string.IsNullOrWhiteSpace(a.ScriptAplicar), a.Id);
                    Assert.Contains("{{ANTERIOR}}", a.ScriptRestaurar ?? "");
                    break;
                case TipoAjuste.Programa:
                    Assert.Matches(@"^[\w.+-]+\.[\w.+-]+$", a.WingetId ?? "");
                    break;
            }
        }
    }

    [Fact]
    public void Nunca_mexe_em_protecoes_do_Windows()
    {
        var servicosProibidos = new[] { "WinDefend", "WdNisSvc", "mpssvc", "wuauserv", "UsoSvc", "BITS", "WaaSMedicSvc", "SecurityHealthService", "wscsvc", "TrustedInstaller", "CryptSvc" };
        var trechosProibidos = new[] { "Windows Defender", "WindowsFirewall", "EnableLUA", "DisableAntiSpyware", "DisableRealtimeMonitoring", "NoAutoUpdate", "WindowsStore", "RemoveWindowsStore" };
        var pacotesProibidos = new[] { "Microsoft.WindowsStore", "Microsoft.MicrosoftEdge", "Microsoft.Win32WebViewHost", "Microsoft.SecHealthUI", "Microsoft.DesktopAppInstaller", "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET" };

        foreach (var a in Todos)
        {
            Assert.DoesNotContain(a.Servicos, s => servicosProibidos.Contains(s, StringComparer.OrdinalIgnoreCase));
            foreach (var v in a.Valores)
                Assert.DoesNotContain(trechosProibidos, t => (v.Caminho + "\\" + v.Nome).Contains(t, StringComparison.OrdinalIgnoreCase));
            foreach (var p in a.Pacotes)
            {
                Assert.NotEqual("*", p);
                var regra = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Assert.DoesNotContain(pacotesProibidos, x => regra.IsMatch(x) || regra.IsMatch(x + ".Qualquer"));
            }
            foreach (var script in new[] { a.ScriptAplicar, a.ScriptLer, a.ScriptRestaurar })
            {
                Assert.DoesNotContain("DisableRealtimeMonitoring", script ?? "");
                Assert.DoesNotContain("Set-NetFirewallProfile", script ?? "");
            }
        }
    }

    [Fact]
    public void Avancado_nunca_vem_marcado()
    {
        var todosPerfis = Enum.GetValues<PerfilUso>().ToList();
        Assert.All(Todos.Where(a => a.Grupo == "Avançado"), a => Assert.False(a.RecomendadoPara(todosPerfis), a.Id));
    }

    [Fact]
    public void Ajustes_por_conta_levam_a_conta_na_chave()
    {
        var hkcu = Todos.First(a => a.Valores.Any(v => v.Caminho.StartsWith("HKCU")) && a.Valores.All(v => v.Caminho.StartsWith("HKCU")));
        var hklm = Todos.First(a => a.Tipo == TipoAjuste.Registro && a.Valores.All(v => v.Caminho.StartsWith("HKLM")));
        Assert.Equal($"{hkcu.Id}@S-1-5-21-1-2-3-1001", Otimizador.Chave(hkcu, "S-1-5-21-1-2-3-1001"));
        Assert.Equal(hklm.Id, Otimizador.Chave(hklm, "S-1-5-21-1-2-3-1001"));
        Assert.True(Otimizador.PorUsuario(Todos.First(a => a.Tipo == TipoAjuste.App)));
    }

    [Fact]
    public void Sid_aceita_so_contas_de_pessoas()
    {
        Assert.True(Otimizador.SidValido("S-1-5-21-111-222-333-1001"));
        Assert.False(Otimizador.SidValido("S-1-5-18"));            // SYSTEM
        Assert.False(Otimizador.SidValido("S-1-5-21-1'; rm -rf"));
        Assert.False(Otimizador.SidValido(null));
    }
}
