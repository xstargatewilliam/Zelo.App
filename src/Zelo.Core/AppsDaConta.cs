using System.Text.Json;

namespace Zelo.Core;

/// <summary>
/// Remove apps da Microsoft Store da conta de quem está usando o painel, rodando como essa pessoa
/// (sem privilégio de administrador, que não é preciso para isso). Pelo serviço (SYSTEM), o Windows
/// aceitava o pedido mas não removia o app da conta da pessoa.
/// </summary>
public static class AppsDaConta
{
    public sealed record Resultado(bool Ok, List<string> Removidos, List<string> Restam, List<string> Erros);

    private static string Lista(IEnumerable<string> pacotes) => string.Join(",", pacotes.Select(p => "'" + p.Replace("'", "''") + "'"));

    public static async Task<Resultado> RemoverAsync(Ajuste a, CancellationToken ct)
    {
        if (a.Tipo != TipoAjuste.App) throw new ArgumentException("Não é um app da Loja.", nameof(a));
        var lista = Lista(a.Pacotes);
        var script = $$"""
            $itens = @(foreach ($p in @({{lista}})) {
              foreach ($pk in @(Get-AppxPackage -Name $p)) {
                $erro = ''
                try { Remove-AppxPackage -Package $pk.PackageFullName -ErrorAction Stop } catch { $erro = $_.Exception.Message }
                [pscustomobject]@{ Nome = [string]$pk.Name; Erro = [string]$erro }
              }
            })
            # A remoção termina em segundo plano: confere de novo por alguns segundos.
            $restam = @()
            for ($i = 0; $i -lt 5; $i++) {
              $restam = @(foreach ($p in @({{lista}})) { Get-AppxPackage -Name $p | ForEach-Object { [string]$_.Name } })
              if ($restam.Count -eq 0) { break }
              Start-Sleep -Seconds 2
            }
            ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{ Itens = $itens; Restam = @($restam) })
            """;
        var json = await Executor.PowerShellJsonAsync(script, ct);
        return Ler(json);
    }

    internal static Resultado Ler(JsonElement json)
    {
        var removidos = new List<string>();
        var erros = new List<string>();
        if (json.TryGetProperty("Itens", out var itens))
            foreach (var i in itens.Itens())
            {
                var erro = i.Str("Erro");
                if (erro.Length == 0) removidos.Add(i.Str("Nome"));
                else erros.Add($"{i.Str("Nome")}: {PrimeiraLinha(erro)}");
            }
        var restam = json.TryGetProperty("Restam", out var r)
            ? r.Itens().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Where(x => x.Length > 0).Distinct().ToList()
            : new List<string>();
        return new Resultado(restam.Count == 0 && erros.Count == 0, removidos, restam, erros);
    }

    private static string PrimeiraLinha(string s) => s.Split('\n', '\r').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? s;

    // ---------------------------------------------------------------- o que o Zelo removeu (por conta)

    private static string Arquivo => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelo", "apps-removidos.json");

    public static Dictionary<string, DateTime> Removidos()
    {
        try { return File.Exists(Arquivo) ? Json.Desserializar<Dictionary<string, DateTime>>(File.ReadAllText(Arquivo)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Marcar(string id, bool removido)
    {
        var d = Removidos();
        if (removido) d[id] = DateTime.Now; else d.Remove(id);
        Directory.CreateDirectory(Path.GetDirectoryName(Arquivo)!);
        var temp = Arquivo + ".tmp";
        File.WriteAllText(temp, Json.Serializar(d));
        File.Move(temp, Arquivo, overwrite: true);
    }
}
