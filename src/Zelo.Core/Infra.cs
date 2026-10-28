using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Zelo.Core;

public static class Caminhos
{
    public const string NomePipe = "Zelo.Servico";

    public static string Base => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zelo");

    public static string Config => Path.Combine(Base, "config.json");
    public static string Historico => Path.Combine(Base, "historico.jsonl");
    public static string Relatorios => Path.Combine(Base, "relatorios");
    public static string Logs => Path.Combine(Base, "logs");
    public static string Estado => Path.Combine(Base, "estado");

    public static void Garantir()
    {
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(Relatorios);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Estado);
    }
}

/// <summary>Log simples em arquivo, um por dia, guardando 30 dias.</summary>
public static class Registro
{
    private static readonly object Trava = new();

    public static void Escrever(string mensagem)
    {
        try
        {
            lock (Trava)
            {
                Directory.CreateDirectory(Caminhos.Logs);
                var arq = Path.Combine(Caminhos.Logs, $"zelo-{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(arq, $"{DateTime.Now:HH:mm:ss} {mensagem}{Environment.NewLine}");
            }
        }
        catch { /* log nunca derruba o serviço */ }
    }

    public static void LimparAntigos(int dias = 30)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Caminhos.Logs, "zelo-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-dias)) File.Delete(f);
            foreach (var f in Directory.EnumerateFiles(Caminhos.Relatorios, "*.html"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-180)) File.Delete(f);
        }
        catch { }
    }
}

/// <summary>Estado persistente pequeno (snapshots) usado pelas tarefas.</summary>
public static class Estado
{
    public static T? Ler<T>(string nome) where T : class
    {
        try
        {
            var arq = Path.Combine(Caminhos.Estado, nome + ".json");
            return File.Exists(arq) ? Json.Desserializar<T>(File.ReadAllText(arq)) : null;
        }
        catch { return null; }
    }

    public static void Gravar<T>(string nome, T valor)
    {
        Directory.CreateDirectory(Caminhos.Estado);
        File.WriteAllText(Path.Combine(Caminhos.Estado, nome + ".json"), Json.Serializar(valor, true));
    }
}

public sealed record SaidaProcesso(int Codigo, string Saida, string Erro)
{
    /// <summary>Linhas não vazias, sem barras de progresso.</summary>
    public IEnumerable<string> Linhas(int ultimas = 12) =>
        (Saida + "\n" + Erro)
            .Split('\n', '\r')
            .Select(l => l.Trim())
            .Where(l => l.Length > 2 && !l.StartsWith("#< CLIXML") && !l.StartsWith("<Objs") && !l.Any(c => c is '█' or '▒' or '░') && !l.StartsWith('[') && !l.All(c => c is '-' or '\\' or '|' or '/' or ' '))
            .TakeLast(ultimas);
}

public static class Executor
{
    public static string PowerShellExe =>
        Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public static async Task<SaidaProcesso> ProcessoAsync(
        string arquivo, string argumentos, CancellationToken ct, Encoding? codificacao = null)
    {
        var psi = new ProcessStartInfo(arquivo, argumentos)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (codificacao is not null)
        {
            psi.StandardOutputEncoding = codificacao;
            psi.StandardErrorEncoding = codificacao;
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        var saida = p.StandardOutput.ReadToEndAsync();
        var erro = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new SaidaProcesso(p.ExitCode, await saida, await erro);
    }

    /// <summary>Roda um script do Windows PowerShell 5.1 (presente em todo Windows 10/11).</summary>
    public static Task<SaidaProcesso> PowerShellAsync(string script, CancellationToken ct)
    {
        // Erros viram uma linha de texto simples (sem isso o PowerShell devolve XML "CLIXML" ilegível).
        var completo =
            "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " +
            "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; try {\n" + script +
            "\n} catch { [Console]::Out.WriteLine('ERRO: ' + $_.Exception.Message); exit 1 }";
        var b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(completo));
        return ProcessoAsync(PowerShellExe,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {b64}", ct, Encoding.UTF8);
    }

    /// <summary>Roda um script cuja última saída é JSON e devolve o elemento raiz.</summary>
    public static async Task<JsonElement> PowerShellJsonAsync(string script, CancellationToken ct)
    {
        var r = await PowerShellAsync(script, ct);
        var texto = r.Saida.Trim();
        int i = texto.IndexOfAny(new[] { '{', '[' });
        if (r.Codigo != 0 || i < 0)
        {
            var msg = string.Join(" ", r.Linhas(3));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg) ? $"PowerShell saiu com código {r.Codigo}" : msg);
        }
        using var doc = JsonDocument.Parse(texto[i..]);
        return doc.RootElement.Clone();
    }
}

/// <summary>Leitura tolerante de JSON vindo do PowerShell.</summary>
public static class JsonExt
{
    public static string Str(this JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.ToString(),
        };
    }

    public static double? Num(this JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    public static bool? Bool(this JsonElement e, string nome)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(nome, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Itens(this JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) return e.EnumerateArray();
        if (e.ValueKind == JsonValueKind.Object) return new[] { e };
        return Array.Empty<JsonElement>();
    }

    public static IEnumerable<JsonElement> Itens(this JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) ? v.Itens() : Array.Empty<JsonElement>();

    public static IEnumerable<string> Textos(this JsonElement e, string nome) =>
        e.Itens(nome).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Where(s => s.Length > 0);
}

/// <summary>Perfis de usuário do PC (pastas em C:\Users).</summary>
public static class Perfis
{
    public static List<string> Pastas()
    {
        var lista = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var pl = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (pl is not null)
            {
                foreach (var sid in pl.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-") || s.StartsWith("S-1-12-1-")))
                {
                    using var k = pl.OpenSubKey(sid);
                    var p = k?.GetValue("ProfileImagePath") as string;
                    if (!string.IsNullOrEmpty(p))
                    {
                        p = Environment.ExpandEnvironmentVariables(p);
                        if (Directory.Exists(p)) lista.Add(p);
                    }
                }
            }
        }
        catch { }

        if (lista.Count == 0)
        {
            // Plano B: o perfil de quem está rodando (útil em testes fora do serviço).
            var eu = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(eu) && !eu.Contains("systemprofile", StringComparison.OrdinalIgnoreCase)) lista.Add(eu);
        }
        return lista.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public static class Nativo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>Sem bateria (desktop) também conta como "na tomada".</summary>
    public static bool NaTomada()
    {
        if (!GetSystemPowerStatus(out var s)) return true;
        if (s.BatteryFlag == 128) return true;
        return s.ACLineStatus != 0;
    }

    public static bool TemBateria()
    {
        if (!GetSystemPowerStatus(out var s)) return false;
        return s.BatteryFlag != 128 && s.BatteryFlag != 255;
    }

    /// <summary>Uso médio de CPU (%) durante a amostra.</summary>
    public static async Task<double> UsoCpuAsync(TimeSpan amostra, CancellationToken ct)
    {
        GetSystemTimes(out var i1, out var k1, out var u1);
        await Task.Delay(amostra, ct);
        GetSystemTimes(out var i2, out var k2, out var u2);
        long idle = i2 - i1, total = (k2 - k1) + (u2 - u1); // kernel inclui idle
        if (total <= 0) return 0;
        return Math.Clamp(100.0 * (total - idle) / total, 0, 100);
    }
}
