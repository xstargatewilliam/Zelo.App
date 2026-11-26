using Xunit;
using Zelo.Core;
using Zelo.Core.Tarefas;

namespace Zelo.Tests;

public class CoordenadorTests
{
    [Fact]
    public void So_uma_operacao_pesada_por_vez()
    {
        Assert.True(Coordenador.Iniciar("rotina"));
        try
        {
            Assert.False(Coordenador.Iniciar("otimizacao"));
            Assert.False(Coordenador.Iniciar("atualizacao"));
            Assert.Equal("rotina", Coordenador.Atual);
            Coordenador.Encerrar("otimizacao"); // quem não começou não libera
            Assert.Equal("rotina", Coordenador.Atual);
        }
        finally { Coordenador.Encerrar("rotina"); }
        Assert.Null(Coordenador.Atual);
        Assert.True(Coordenador.Iniciar("otimizacao"));
        Coordenador.Encerrar("otimizacao");
    }
}

public class EstadoTests : ComPastaTemporaria
{
    private sealed class Coisa { public int Valor { get; set; } }

    [Fact]
    public void Gravacao_atomica_nao_deixa_temporario()
    {
        Estado.Gravar("coisa", new Coisa { Valor = 1 });
        Estado.Gravar("coisa", new Coisa { Valor = 2 });
        Assert.Equal(2, Estado.Ler<Coisa>("coisa")!.Valor);
        Assert.Empty(Directory.GetFiles(Caminhos.Estado, "*.tmp"));
    }
}

public class CatalogoTests
{
    [Fact]
    public void Tarefas_com_ids_unicos_e_rotina()
    {
        var todas = Catalogo.Todas.ToList();
        Assert.Equal(todas.Count, todas.Select(t => t.Id).Distinct().Count());
        Assert.All(todas, t => Assert.NotEmpty(t.Rotinas));
    }
}

public class BackupVersoesTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "zelo-backup-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_raiz, true); } catch { } }

    [Fact]
    public void Guarda_a_versao_anterior_so_do_que_vai_mudar()
    {
        var origem = Path.Combine(_raiz, "origem");
        var espelho = Path.Combine(_raiz, "espelho");
        var versoes = Path.Combine(_raiz, "versoes");
        Directory.CreateDirectory(origem);
        Directory.CreateDirectory(espelho);

        File.WriteAllText(Path.Combine(espelho, "mudou.txt"), "antigo");
        File.WriteAllText(Path.Combine(espelho, "igual.txt"), "mesmo");
        File.SetLastWriteTimeUtc(Path.Combine(espelho, "mudou.txt"), DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(Path.Combine(origem, "mudou.txt"), "novo conteúdo");
        File.WriteAllText(Path.Combine(origem, "igual.txt"), "mesmo");
        File.SetLastWriteTimeUtc(Path.Combine(origem, "igual.txt"), File.GetLastWriteTimeUtc(Path.Combine(espelho, "igual.txt")));
        File.WriteAllText(Path.Combine(origem, "novo.txt"), "novo");

        var movidos = BackupIncremental.GuardarVersoes(origem, espelho, versoes, CancellationToken.None);

        Assert.Equal(1, movidos);
        Assert.Equal("antigo", File.ReadAllText(Path.Combine(versoes, "mudou.txt")));
        Assert.True(File.Exists(Path.Combine(espelho, "mudou.txt")));
        Assert.Equal("antigo", File.ReadAllText(Path.Combine(espelho, "mudou.txt")));
        Assert.True(File.Exists(Path.Combine(espelho, "igual.txt")));
    }

    [Fact]
    public void Falha_ao_preservar_versao_nao_apaga_o_espelho()
    {
        var origem = Path.Combine(_raiz, "origem");
        var espelho = Path.Combine(_raiz, "espelho");
        var versoes = Path.Combine(_raiz, "versoes");
        Directory.CreateDirectory(origem);
        Directory.CreateDirectory(espelho);
        Directory.CreateDirectory(versoes);
        var original = Path.Combine(origem, "documento.txt");
        var copia = Path.Combine(espelho, "documento.txt");
        File.WriteAllText(copia, "copia recuperavel");
        File.SetLastWriteTimeUtc(copia, DateTime.UtcNow.AddDays(-1));
        File.WriteAllText(original, "conteudo modificado");
        File.WriteAllText(Path.Combine(versoes, "documento.txt"), "versao anterior existente");

        Assert.Throws<IOException>(() => BackupIncremental.GuardarVersoes(origem, espelho, versoes, CancellationToken.None));
        Assert.Equal("copia recuperavel", File.ReadAllText(copia));
        Assert.Equal("versao anterior existente", File.ReadAllText(Path.Combine(versoes, "documento.txt")));
    }

    [Fact]
    public void Apaga_so_as_versoes_vencidas()
    {
        var destino = Path.Combine(_raiz, "destino");
        var raiz = BackupIncremental.PastaVersoes(destino);
        var velha = Path.Combine(raiz, DateTime.Now.AddDays(-40).ToString("yyyy-MM-dd_HHmm"));
        var nova = Path.Combine(raiz, DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd_HHmm"));
        var estranha = Path.Combine(raiz, "fotos-da-viagem");
        foreach (var p in new[] { velha, nova, estranha }) Directory.CreateDirectory(p);

        Assert.Equal(1, BackupIncremental.LimparVersoes(destino, 30));
        Assert.False(Directory.Exists(velha));
        Assert.True(Directory.Exists(nova));
        Assert.True(Directory.Exists(estranha));
    }
}
