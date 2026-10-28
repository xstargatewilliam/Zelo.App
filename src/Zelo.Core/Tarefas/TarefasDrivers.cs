namespace Zelo.Core.Tarefas;

// ---------------------------------------------------------------- Drivers pelo Windows Update
/// <summary>
/// Instala drivers oferecidos pelo Windows Update, exceto os de vídeo:
/// o Windows Update às vezes oferece um driver de vídeo mais antigo que o do fabricante.
/// </summary>
public sealed class DriversWindowsUpdate : Tarefa
{
    public override string Id => "drivers-wu";
    public override string Nome => "Drivers pelo Windows Update";
    public override string Descricao => "Instala drivers de chipset, rede, áudio, USB e afins pelo Windows Update. Drivers de vídeo são pulados (veja a tarefa da placa de vídeo).";
    public override Categoria Categoria => Categoria.Atualizacoes;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };
    public override TimeSpan Limite => TimeSpan.FromHours(1);
    public override bool Habilitada(Configuracao c) => c.InstalarDriversWU;

    private const string Script = """
        $instalar = {{INSTALAR}}
        $s = New-Object -ComObject Microsoft.Update.Session
        $s.ClientApplicationID = 'Zelo'
        $busca = $s.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Driver'")
        $instalaveis = @(); $video = @()
        foreach ($u in $busca.Updates) {
          $classe = ''
          try { $classe = [string]$u.DriverClass } catch {}
          if ($classe -match '^(Display|Video)$' -or $u.Title -match 'Display|NVIDIA|Radeon|GeForce|Graphics') { $video += [string]$u.Title }
          else { $instalaveis += $u }
        }
        $ok = @(); $falhas = @(); $reinicio = $false
        if ($instalar -and $instalaveis.Count -gt 0) {
          $col = New-Object -ComObject Microsoft.Update.UpdateColl
          foreach ($u in $instalaveis) { if (-not $u.EulaAccepted) { $u.AcceptEula() }; [void]$col.Add($u) }
          $d = $s.CreateUpdateDownloader(); $d.Updates = $col; [void]$d.Download()
          $i = $s.CreateUpdateInstaller(); $i.Updates = $col
          $r = $i.Install()
          $reinicio = [bool]$r.RebootRequired
          for ($k = 0; $k -lt $col.Count; $k++) {
            $rc = [int]$r.GetUpdateResult($k).ResultCode
            if ($rc -eq 2 -or $rc -eq 3) { $ok += [string]$col.Item($k).Title } else { $falhas += [string]$col.Item($k).Title }
          }
        } else {
          $ok = @(); $falhas = @()
        }
        [pscustomobject]@{
          Disponiveis = @($instalaveis | ForEach-Object { [string]$_.Title })
          Ok = @($ok); Falhas = @($falhas); Video = @($video); Reinicio = $reinicio
        } | ConvertTo-Json -Compress -Depth 3
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var d = await Executor.PowerShellJsonAsync(Script.Replace("{{INSTALAR}}", ctx.Simulacao ? "$false" : "$true"), ct);
        var disponiveis = d.Textos("Disponiveis").ToList();
        var ok = d.Textos("Ok").ToList();
        var falhas = d.Textos("Falhas").ToList();
        var video = d.Textos("Video").ToList();
        var reinicio = d.Bool("Reinicio") ?? false;

        var det = new List<string>();
        if (ctx.Simulacao) det.AddRange(disponiveis.Select(t => "Seria instalado: " + t));
        det.AddRange(ok.Select(t => "Instalado: " + t));
        det.AddRange(falhas.Select(t => "FALHOU: " + t));
        det.AddRange(video.Select(t => "Pulado (vídeo): " + t + " — use o software do fabricante da placa."));
        if (reinicio) det.Add("É preciso reiniciar o PC para concluir.");

        if (disponiveis.Count == 0)
            return Res(StatusTarefa.Ok, video.Count > 0
                ? "Nenhum driver para instalar (driver de vídeo oferecido foi pulado de propósito)."
                : "Nenhum driver novo no Windows Update.", det);
        if (ctx.Simulacao)
            return Res(StatusTarefa.Ok, $"[Simulação] {disponiveis.Count} driver(s) seriam instalados.", det);
        if (falhas.Count > 0)
            return Res(StatusTarefa.Aviso, $"{ok.Count} driver(s) instalado(s), {falhas.Count} falharam.", det);
        return Res(StatusTarefa.Ok,
            $"{ok.Count} driver(s) instalado(s){(reinicio ? " — reinicie o PC para concluir" : "")}.", det);
    }
}

// ---------------------------------------------------------------- Placa de vídeo
/// <summary>Identifica a placa de vídeo e indica o software oficial do fabricante para atualizar o driver.</summary>
public sealed class DriverVideo : Tarefa
{
    public override string Id => "driver-video";
    public override string Nome => "Driver da placa de vídeo";
    public override string Descricao => "Mostra a placa de vídeo, a idade do driver e qual software do fabricante usar para atualizá-lo. Não instala nada.";
    public override Categoria Categoria => Categoria.Atualizacoes;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };

    private const string Script = """
        $placas = @(Get-CimInstance Win32_VideoController | Where-Object {
            $_.Name -and $_.Name -notmatch 'Basic Display|Remote Display|Virtual|Parsec|Meta Virtual|Mirage' } | ForEach-Object {
          $data = ''
          if ($_.DriverDate) { $data = $_.DriverDate.ToString('yyyy-MM-dd') }
          [pscustomobject]@{ Nome = [string]$_.Name; Fabricante = [string]$_.AdapterCompatibility; Versao = [string]$_.DriverVersion; Data = $data }
        })
        $chaves = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
        $apps = @(Get-ItemProperty $chaves -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName } | ForEach-Object { [string]$_.DisplayName })
        [pscustomobject]@{ Placas = $placas; Apps = $apps } | ConvertTo-Json -Compress -Depth 3
        """;

    private sealed record Fornecedor(string Marca, string[] Chaves, string App, string[] NomesInstalado, string Site);

    private static readonly Fornecedor[] Fornecedores =
    {
        new("NVIDIA", new[] { "NVIDIA" }, "NVIDIA App",
            new[] { "NVIDIA App", "NVIDIA GeForce Experience" },
            "https://www.nvidia.com/pt-br/software/nvidia-app/"),
        new("AMD", new[] { "AMD", "Advanced Micro Devices", "Radeon" }, "AMD Software: Adrenalin Edition",
            new[] { "AMD Software", "AMD Radeon Software" },
            "https://www.amd.com/pt/support/download/drivers.html"),
        new("Intel", new[] { "Intel" }, "Intel Driver & Support Assistant",
            new[] { "Intel(R) Driver & Support Assistant", "Intel® Driver & Support Assistant", "Intel Driver & Support Assistant" },
            "https://www.intel.com.br/content/www/br/pt/support/detect.html"),
    };

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var d = await Executor.PowerShellJsonAsync(Script, ct);
        var apps = d.Textos("Apps").ToList();
        var det = new List<string>();
        var pendencias = new List<string>();
        var status = StatusTarefa.Ok;

        foreach (var p in d.Itens("Placas"))
        {
            var nome = p.Str("Nome");
            var fabricante = p.Str("Fabricante");
            var f = Fornecedores.FirstOrDefault(x => x.Chaves.Any(k =>
                nome.Contains(k, StringComparison.OrdinalIgnoreCase) || fabricante.Contains(k, StringComparison.OrdinalIgnoreCase)));

            var linha = $"{nome}: driver {p.Str("Versao")}";
            int? idadeDias = null;
            if (DateTime.TryParse(p.Str("Data"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var data))
            {
                idadeDias = (int)(DateTime.Today - data).TotalDays;
                linha += $" de {data:dd/MM/yyyy}";
            }
            det.Add(linha);

            if (f is null)
            {
                det.Add("   Fabricante não identificado: procure o driver no site do fabricante do PC ou da placa.");
                continue;
            }

            bool instalado = apps.Any(a => f.NomesInstalado.Any(n => a.StartsWith(n, StringComparison.OrdinalIgnoreCase)));
            det.Add(instalado
                ? $"   Atualize pelo {f.App} (já instalado): abra o app e vá em Drivers."
                : $"   Recomendado: instalar o {f.App} — {f.Site}");

            // Placa de vídeo integrada (comum em notebooks com duas placas) costuma ter driver mais antigo; só alerta a dedicada.
            bool integrada = f.Marca == "Intel" && d.Itens("Placas").Count() > 1;
            if (idadeDias > 365 && !integrada)
            {
                status = StatusTarefa.Aviso;
                pendencias.Add($"driver da {f.Marca} com mais de 1 ano");
            }
            else if (!instalado && !integrada)
            {
                pendencias.Add($"instale o {f.App} para manter o driver da {f.Marca} em dia");
            }
        }

        if (det.Count == 0) return Res(StatusTarefa.Ignorada, "Nenhuma placa de vídeo encontrada.");
        var resumo = pendencias.Count == 0
            ? "Driver de vídeo recente. Continue atualizando pelo software do fabricante."
            : char.ToUpper(pendencias[0][0]) + string.Join("; ", pendencias)[1..] + ".";
        return Res(status, resumo, det);
    }
}
