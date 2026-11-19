using Xunit;
using Zelo.Core;

// Os testes mexem em estado global (pasta de dados, coordenador): rodam um de cada vez.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Zelo.Tests;

/// <summary>Cada teste usa uma pasta de dados temporária, nunca a do PC.</summary>
public abstract class ComPastaTemporaria : IDisposable
{
    protected readonly string Pasta = Path.Combine(Path.GetTempPath(), "zelo-testes-" + Guid.NewGuid().ToString("N"));

    protected ComPastaTemporaria()
    {
        Directory.CreateDirectory(Pasta);
        Caminhos.BaseParaTestes = Pasta;
    }

    public void Dispose()
    {
        Caminhos.BaseParaTestes = null;
        try { Directory.Delete(Pasta, true); } catch { }
    }
}
