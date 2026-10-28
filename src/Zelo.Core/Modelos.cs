using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zelo.Core;

/// <summary>As três rotinas de manutenção.</summary>
public enum Rotina { Diaria, Semanal, Mensal }

/// <summary>Resultado de uma tarefa, do melhor para o pior.</summary>
public enum StatusTarefa { Ok, Aviso, Critico, Erro, Ignorada }

/// <summary>Áreas que compõem o score de saúde.</summary>
public enum Categoria { Disco, Seguranca, Atualizacoes, Backup, Sistema }

public sealed class ResultadoTarefa
{
    public string TarefaId { get; set; } = "";
    public string Nome { get; set; } = "";
    public Categoria Categoria { get; set; }
    public StatusTarefa Status { get; set; }
    public string Resumo { get; set; } = "";
    public List<string> Detalhes { get; set; } = new();
    public DateTime Inicio { get; set; }
    public DateTime Fim { get; set; }

    [JsonIgnore]
    public TimeSpan Duracao => Fim - Inicio;
}

public sealed class ExecucaoRotina
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public Rotina Rotina { get; set; }
    public bool Simulacao { get; set; }
    /// <summary>"agendada" ou "manual".</summary>
    public string Origem { get; set; } = "agendada";
    public DateTime Inicio { get; set; }
    public DateTime Fim { get; set; }
    public List<ResultadoTarefa> Resultados { get; set; } = new();
    /// <summary>Score geral de saúde logo após esta execução (-1 = sem dados).</summary>
    public int Score { get; set; } = -1;
    /// <summary>Caminho do relatório HTML gerado.</summary>
    public string? Relatorio { get; set; }

    [JsonIgnore]
    public StatusTarefa PiorStatus
    {
        get
        {
            var relevantes = Resultados.Where(r => r.Status != StatusTarefa.Ignorada).ToList();
            if (relevantes.Count == 0) return StatusTarefa.Ignorada;
            if (relevantes.Any(r => r.Status == StatusTarefa.Critico)) return StatusTarefa.Critico;
            if (relevantes.Any(r => r.Status == StatusTarefa.Erro)) return StatusTarefa.Erro;
            if (relevantes.Any(r => r.Status == StatusTarefa.Aviso)) return StatusTarefa.Aviso;
            return StatusTarefa.Ok;
        }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Opcoes = Criar(false);
    public static readonly JsonSerializerOptions OpcoesIndentadas = Criar(true);

    private static JsonSerializerOptions Criar(bool indentado)
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = indentado,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static string Serializar<T>(T valor, bool indentado = false) =>
        JsonSerializer.Serialize(valor, indentado ? OpcoesIndentadas : Opcoes);

    public static T? Desserializar<T>(string texto) =>
        JsonSerializer.Deserialize<T>(texto, Opcoes);
}

public static class Textos
{
    public static string Nome(this Rotina r) => r switch
    {
        Rotina.Diaria => "Diária",
        Rotina.Semanal => "Semanal",
        Rotina.Mensal => "Mensal",
        _ => r.ToString(),
    };

    public static string Nome(this StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => "OK",
        StatusTarefa.Aviso => "Atenção",
        StatusTarefa.Critico => "Crítico",
        StatusTarefa.Erro => "Erro",
        StatusTarefa.Ignorada => "Ignorada",
        _ => s.ToString(),
    };

    public static string Nome(this Categoria c) => c switch
    {
        Categoria.Disco => "Disco",
        Categoria.Seguranca => "Segurança",
        Categoria.Atualizacoes => "Atualizações",
        Categoria.Backup => "Backup",
        Categoria.Sistema => "Sistema",
        _ => c.ToString(),
    };

    public static string Tamanho(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}
