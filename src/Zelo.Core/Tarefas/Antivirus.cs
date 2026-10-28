namespace Zelo.Core.Tarefas;

/// <summary>
/// Descobre qual antivírus está protegendo o PC. Quando outro antivírus é instalado,
/// o Windows desliga o Defender sozinho — isso é normal e não pode virar alerta crítico.
/// </summary>
internal static class Antivirus
{
    private const string Script = """
        $r = [ordered]@{ DefenderOk = $false; TempoReal = $false; Idade = 65535; Versao = ''; Modo = ''; Ameacas = 0; Produtos = @() }
        try {
          $s = Get-MpComputerStatus -ErrorAction Stop
          $r.DefenderOk = [bool]$s.AntivirusEnabled
          $r.TempoReal = [bool]$s.RealTimeProtectionEnabled
          $r.Idade = [int]$s.AntivirusSignatureAge
          $r.Versao = [string]$s.AntivirusSignatureVersion
          $r.Modo = [string]$s.AMRunningMode
        } catch {}
        try {
          $ini = (Get-Date).AddDays(-1)
          $r.Ameacas = @(Get-MpThreatDetection -ErrorAction Stop | Where-Object { $_.InitialDetectionTime -gt $ini }).Count
        } catch {}
        try {
          $r.Produtos = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop | ForEach-Object {
            $h = '{0:x6}' -f [int]$_.productState
            [pscustomobject]@{ Nome = [string]$_.displayName; Ativo = (@('10', '11') -contains $h.Substring(2, 2)) }
          })
        } catch {}
        [pscustomobject]$r | ConvertTo-Json -Compress -Depth 4
        """;

    internal sealed class Info
    {
        public bool DefenderLigado { get; init; }
        public bool TempoReal { get; init; }
        public int Idade { get; init; }
        public string Versao { get; init; } = "";
        public string Modo { get; init; } = "";
        public int Ameacas { get; init; }
        public List<string> OutrosAtivos { get; init; } = new();

        /// <summary>O Defender é o antivírus que está de fato protegendo o PC.</summary>
        public bool DefenderEmUso =>
            DefenderLigado && Versao.Length > 0 && Idade < 65535 &&
            !Modo.Contains("Passive", StringComparison.OrdinalIgnoreCase) &&
            !Modo.Contains("Not running", StringComparison.OrdinalIgnoreCase);

        public bool AlgumAtivo => DefenderEmUso || OutrosAtivos.Count > 0;
    }

    public static async Task<Info> LerAsync(CancellationToken ct)
    {
        var d = await Executor.PowerShellJsonAsync(Script, ct);
        var outros = d.Itens("Produtos")
            .Where(p => p.Bool("Ativo") == true)
            .Select(p => p.Str("Nome"))
            .Where(n => n.Length > 0 && !n.Contains("Defender", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();
        return new Info
        {
            DefenderLigado = d.Bool("DefenderOk") ?? false,
            TempoReal = d.Bool("TempoReal") ?? false,
            Idade = (int)(d.Num("Idade") ?? 65535),
            Versao = d.Str("Versao"),
            Modo = d.Str("Modo"),
            Ameacas = (int)(d.Num("Ameacas") ?? 0),
            OutrosAtivos = outros,
        };
    }

    public const string DicaSemAntivirus =
        "Abra Segurança do Windows > Proteção contra vírus e ameaças e ligue a proteção. " +
        "Se você usa uma ferramenta de \"otimização\" que desativa o Defender, ela pode ter feito isso.";
}
