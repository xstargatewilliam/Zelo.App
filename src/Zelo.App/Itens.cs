using System;
using System.Windows;
using System.Windows.Media;
using Zelo.Core;

namespace Zelo.App;

public static class Cores
{
    private static SolidColorBrush Pincel(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Ok = Pincel("#1F8A5B");
    public static readonly SolidColorBrush Aviso = Pincel("#B7791F");
    public static readonly SolidColorBrush Critico = Pincel("#C53030");
    public static readonly SolidColorBrush Erro = Pincel("#9B2C2C");
    public static readonly SolidColorBrush Neutro = Pincel("#7A867F");

    public static SolidColorBrush De(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => Ok,
        StatusTarefa.Aviso => Aviso,
        StatusTarefa.Critico => Critico,
        StatusTarefa.Erro => Erro,
        _ => Neutro,
    };

    public static SolidColorBrush DeScore(int score) =>
        score < 0 ? Neutro : score >= 80 ? Ok : score >= 50 ? Aviso : Critico;
}

public sealed class ItemCategoria
{
    public string Nome { get; init; } = "";
    public int Valor { get; init; }
    public string ValorTexto { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Neutro;
}

public sealed class ItemAlerta
{
    public string Texto { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Neutro;
}

public sealed class ItemResultado
{
    public string Nome { get; init; } = "";
    public string Resumo { get; init; } = "";
    public string StatusTexto { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Neutro;
    public string Detalhes { get; init; } = "";
    public string Duracao { get; init; } = "";
    public Visibility VisDetalhes => string.IsNullOrEmpty(Detalhes) ? Visibility.Collapsed : Visibility.Visible;

    public static ItemResultado De(ResultadoTarefa r) => new()
    {
        Nome = r.Nome,
        Resumo = r.Resumo,
        StatusTexto = r.Status.Nome(),
        Cor = Cores.De(r.Status),
        Detalhes = string.Join(Environment.NewLine, r.Detalhes),
        Duracao = Formatar.Duracao(r.Fim - r.Inicio),
    };
}

public sealed class ItemHistorico
{
    public string Data { get; init; } = "";
    public string Rotina { get; init; } = "";
    public string Tipo { get; init; } = "";
    public string Resultado { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Neutro;
    public string Score { get; init; } = "";
    public string Duracao { get; init; } = "";
    public string? Relatorio { get; init; }

    public static ItemHistorico De(ExecucaoRotina e) => new()
    {
        Data = e.Inicio.ToString("dd/MM/yyyy HH:mm"),
        Rotina = e.Rotina.Nome(),
        Tipo = e.Simulacao ? "Simulação" : e.Origem == "manual" ? "Manual" : "Automática",
        Resultado = e.PiorStatus.Nome(),
        Cor = Cores.De(e.PiorStatus),
        Score = e.Simulacao || e.Score < 0 ? "—" : e.Score.ToString(),
        Duracao = Formatar.Duracao(e.Fim - e.Inicio),
        Relatorio = e.Relatorio,
    };
}

/// <summary>Linha editável da lista de tarefas nas configurações.</summary>
public sealed class ItemTarefaConfig
{
    public string Id { get; init; } = "";
    public string Nome { get; init; } = "";
    public string Descricao { get; init; } = "";
    public string Rotinas { get; init; } = "";
    public bool Ativa { get; set; } = true;
}

public static class Formatar
{
    public static string Duracao(TimeSpan t) =>
        t.TotalSeconds < 1 ? "<1s" :
        t.TotalMinutes < 1 ? $"{t.TotalSeconds:0}s" :
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes}min {t.Seconds}s" :
        $"{(int)t.TotalHours}h {t.Minutes}min";

    public static string Quando(DateTime? d)
    {
        if (d is null) return "nunca";
        var v = d.Value;
        var hoje = DateTime.Today;
        if (v.Date == hoje) return $"hoje às {v:HH:mm}";
        if (v.Date == hoje.AddDays(-1)) return $"ontem às {v:HH:mm}";
        if (v.Date == hoje.AddDays(1)) return $"amanhã a partir das {v:HH:mm}";
        return v > DateTime.Now ? $"a partir de {v:dd/MM HH:mm}" : $"{v:dd/MM} às {v:HH:mm}";
    }
}
