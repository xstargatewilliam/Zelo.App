using System.Security.Principal;
using Microsoft.Win32;
using Xunit;
using Zelo.Core;

namespace Zelo.Tests;

/// <summary>
/// Aplica e desfaz um ajuste de verdade no registro do Windows (da conta que roda os testes)
/// e simula as falhas: ponto de restauração que não sai, serviço que cai no meio, estado adulterado.
/// </summary>
public class OtimizadorIntegracaoTests : ComPastaTemporaria
{
    private const string Chave = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private readonly string _sid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly object? _original;

    public OtimizadorIntegracaoTests()
    {
        using var k = Registry.CurrentUser.CreateSubKey(Chave);
        _original = k.GetValue("HideFileExt");
        k.SetValue("HideFileExt", 1, RegistryValueKind.DWord); // "padrão do Windows": extensões escondidas
    }

    public override void Dispose()
    {
        using var k = Registry.CurrentUser.CreateSubKey(Chave);
        if (_original is null) k.DeleteValue("HideFileExt", false); else k.SetValue("HideFileExt", _original, RegistryValueKind.DWord);
        base.Dispose();
    }

    private static int? Valor()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Chave);
        return k?.GetValue("HideFileExt") as int?;
    }

    private static async Task<AndamentoOtimizacao> Esperar()
    {
        for (int i = 0; i < 600 && Otimizador.Andamento.Rodando; i++) await Task.Delay(100);
        Assert.False(Otimizador.Andamento.Rodando, "a otimização não terminou em 60 s");
        return Otimizador.Andamento;
    }

    [Fact]
    public async Task Aplica_confere_e_desfaz_de_verdade()
    {
        Assert.True(Otimizador.SidValido(_sid), $"SID inesperado no computador de teste: {_sid}");
        Assert.Null(Otimizador.Iniciar(new() { "expl-extensoes" }, new(), _sid, desfazer: false, semPonto: true, CancellationToken.None));
        var fim = await Esperar();
        Assert.Empty(fim.Falhas);
        Assert.Equal(0, Valor());
        var reg = Otimizador.Carregar();
        Assert.True(reg.Aplicados[$"expl-extensoes@{_sid}"].Concluido);

        Assert.Null(Otimizador.Iniciar(new() { "expl-extensoes" }, new(), _sid, desfazer: true, semPonto: false, CancellationToken.None));
        fim = await Esperar();
        Assert.Empty(fim.Falhas);
        Assert.Equal(1, Valor());
        Assert.Empty(Otimizador.Carregar().Aplicados);
    }

    [Fact]
    public async Task Sem_ponto_de_restauracao_nada_muda()
    {
        Assert.Null(Otimizador.Iniciar(new() { "expl-extensoes" }, new(), _sid, desfazer: false, semPonto: false, CancellationToken.None));
        var fim = await Esperar();
        if (fim.PontoFalhou)
        {
            // Computador sem Restauração do Sistema (como os servidores de build): parou antes de mudar.
            Assert.Equal(1, Valor());
            Assert.Empty(Otimizador.Carregar().Aplicados);
        }
        else
        {
            Assert.True(fim.PontoCriado);
            Assert.Equal(0, Valor());
        }
    }

    [Fact]
    public async Task Ajuste_que_ficou_pela_metade_e_desfeito_ao_reiniciar()
    {
        // Como se o PC desligasse no meio: o valor já mudou, mas o diário não marcou como concluído.
        using (var k = Registry.CurrentUser.CreateSubKey(Chave)) k.SetValue("HideFileExt", 0, RegistryValueKind.DWord);
        var reg = new RegistroOtimizacao { Versao = 2 };
        reg.Aplicados[$"expl-extensoes@{_sid}"] = new AplicacaoAjuste
        {
            Id = "expl-extensoes", Sid = _sid, Concluido = false, Quando = DateTime.Now,
            Anterior = { [$@"HKCU\{Chave}|HideFileExt"] = "dword:1" },
        };
        Estado.Gravar("otimizacao", reg);

        await Otimizador.RecuperarAsync(CancellationToken.None);

        Assert.Equal(1, Valor());
        Assert.Empty(Otimizador.Carregar().Aplicados);
    }

    [Fact]
    public async Task Estado_adulterado_nao_escreve_fora_do_catalogo()
    {
        const string malicioso = @"Software\ZeloTesteNaoDeveExistir";
        Registry.CurrentUser.DeleteSubKeyTree(malicioso, false);
        var reg = new RegistroOtimizacao { Versao = 2 };
        reg.Aplicados[$"expl-extensoes@{_sid}"] = new AplicacaoAjuste
        {
            Id = "expl-extensoes", Sid = _sid, Concluido = true, Quando = DateTime.Now,
            Anterior = { [$@"HKCU\{malicioso}|Valor"] = "sz:injetado", [$@"HKCU\{Chave}|HideFileExt"] = "dword:1" },
        };
        Estado.Gravar("otimizacao", reg);

        await Otimizador.DesfazerTudoAsync(CancellationToken.None);

        using var k = Registry.CurrentUser.OpenSubKey(malicioso);
        Assert.Null(k);
        Assert.Equal(1, Valor());
    }
}
