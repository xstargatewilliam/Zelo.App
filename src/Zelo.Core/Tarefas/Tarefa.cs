namespace Zelo.Core.Tarefas;

public sealed class Contexto
{
    public required Configuracao Config { get; init; }
    public bool Simulacao { get; init; }
    public Action<string> Log { get; init; } = Registro.Escrever;

    /// <summary>Prefixo usado nos resumos quando nada é alterado de verdade.</summary>
    public string Sim => Simulacao ? "[Simulação] " : "";
}

/// <summary>
/// Uma tarefa de manutenção. Cada tarefa é independente: falhar uma não interrompe as outras.
/// </summary>
public abstract class Tarefa
{
    public abstract string Id { get; }
    public abstract string Nome { get; }
    public abstract string Descricao { get; }
    public abstract Categoria Categoria { get; }
    public abstract Rotina[] Rotinas { get; }

    /// <summary>Tempo máximo antes de a tarefa ser interrompida.</summary>
    public virtual TimeSpan Limite => TimeSpan.FromMinutes(30);

    /// <summary>Permite que a configuração desligue a tarefa (além da lista de desativadas).</summary>
    public virtual bool Habilitada(Configuracao c) => true;

    protected abstract Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct);

    public async Task<ResultadoTarefa> ExecutarAsync(Contexto ctx, CancellationToken ct)
    {
        var inicio = DateTime.Now;
        ResultadoTarefa r;

        if (ctx.Config.TarefasDesativadas.Contains(Id) || !Habilitada(ctx.Config))
        {
            r = Res(StatusTarefa.Ignorada, "Desativada nas configurações.");
        }
        else
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(Limite);
            try
            {
                r = await ExecutarInternoAsync(ctx, limite.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                r = Res(StatusTarefa.Aviso, $"Interrompida após {Limite.TotalMinutes:0} minutos (tempo limite).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                r = Res(StatusTarefa.Erro, "Falhou: " + ex.Message);
            }
        }

        r.TarefaId = Id;
        r.Nome = Nome;
        r.Categoria = Categoria;
        r.Inicio = inicio;
        r.Fim = DateTime.Now;
        return r;
    }

    protected static ResultadoTarefa Res(StatusTarefa s, string resumo, IEnumerable<string>? detalhes = null) =>
        new() { Status = s, Resumo = resumo, Detalhes = detalhes?.ToList() ?? new() };

    /// <summary>Junta vários status no pior deles.</summary>
    protected static StatusTarefa Pior(params StatusTarefa[] lista) =>
        lista.Contains(StatusTarefa.Critico) ? StatusTarefa.Critico :
        lista.Contains(StatusTarefa.Erro) ? StatusTarefa.Erro :
        lista.Contains(StatusTarefa.Aviso) ? StatusTarefa.Aviso : StatusTarefa.Ok;

    /// <summary>Enumeração segura: ignora pastas sem acesso e não segue junções/atalhos de pasta.</summary>
    protected static EnumerationOptions Varredura(bool recursiva = true) => new()
    {
        RecurseSubdirectories = recursiva,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };
}
