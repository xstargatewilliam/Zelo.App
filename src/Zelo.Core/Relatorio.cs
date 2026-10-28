using System.Net;
using System.Text;

namespace Zelo.Core;

/// <summary>Gera um relatório HTML autossuficiente de cada execução.</summary>
public static class Relatorio
{
    public static string Gerar(ExecucaoRotina e)
    {
        Caminhos.Garantir();
        var nome = $"{e.Inicio:yyyy-MM-dd_HHmm}_{e.Rotina.ToString().ToLowerInvariant()}{(e.Simulacao ? "_simulacao" : "")}.html";
        var arq = Path.Combine(Caminhos.Relatorios, nome);
        File.WriteAllText(arq, Html(e), Encoding.UTF8);
        return arq;
    }

    private static string H(string s) => WebUtility.HtmlEncode(s);

    private static string Cor(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => "#1f8a5b",
        StatusTarefa.Aviso => "#b7791f",
        StatusTarefa.Critico => "#c53030",
        StatusTarefa.Erro => "#9b2c2c",
        _ => "#718096",
    };

    public static string Html(ExecucaoRotina e)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <!doctype html><html lang="pt-BR"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Zelo — relatório</title>
            <style>
            :root{--fundo:#f6f7f5;--cartao:#fff;--texto:#1d2521;--suave:#5f6b65;--linha:#e3e7e4}
            @media (prefers-color-scheme:dark){:root{--fundo:#141816;--cartao:#1c211e;--texto:#e7ece9;--suave:#9aa69f;--linha:#2c332f}}
            body{margin:0;background:var(--fundo);color:var(--texto);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
            main{max-width:900px;margin:0 auto;padding:32px 16px}
            h1{font-size:26px;margin:0 0 4px}.sub{color:var(--suave);margin:0 0 24px}
            .score{font-size:44px;font-weight:700;margin:0}
            .t{background:var(--cartao);border:1px solid var(--linha);border-radius:10px;padding:14px 16px;margin:10px 0}
            .t header{display:flex;gap:10px;align-items:baseline;flex-wrap:wrap}
            .pill{color:#fff;border-radius:999px;padding:1px 10px;font-size:12px;font-weight:600}
            .nome{font-weight:600}.dur{margin-left:auto;color:var(--suave);font-size:12px}
            .res{margin:6px 0 0}
            details{margin-top:6px}summary{cursor:pointer;color:var(--suave);font-size:13px}
            pre{white-space:pre-wrap;font:12.5px/1.45 Consolas,monospace;margin:6px 0 0;color:var(--suave)}
            </style></head><body><main>
            """);
        sb.Append($"<h1>Rotina {H(e.Rotina.Nome())}{(e.Simulacao ? " — simulação" : "")}</h1>");
        sb.Append($"<p class=\"sub\">{e.Inicio:dd/MM/yyyy HH:mm} até {e.Fim:HH:mm} · {H(e.Origem)} · {e.Resultados.Count} tarefas</p>");
        if (e.Score >= 0) sb.Append($"<p class=\"score\">{e.Score}<span style=\"font-size:18px;color:var(--suave)\"> / 100 de saúde</span></p>");

        foreach (var r in e.Resultados)
        {
            sb.Append("<section class=\"t\"><header>");
            sb.Append($"<span class=\"pill\" style=\"background:{Cor(r.Status)}\">{H(r.Status.Nome())}</span>");
            sb.Append($"<span class=\"nome\">{H(r.Nome)}</span><span class=\"dur\">{r.Duracao.TotalSeconds:0}s</span></header>");
            sb.Append($"<p class=\"res\">{H(r.Resumo)}</p>");
            if (r.Detalhes.Count > 0)
                sb.Append($"<details{((r.Status is StatusTarefa.Critico or StatusTarefa.Aviso) ? " open" : "")}><summary>Detalhes</summary><pre>{H(string.Join("\n", r.Detalhes))}</pre></details>");
            sb.Append("</section>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
