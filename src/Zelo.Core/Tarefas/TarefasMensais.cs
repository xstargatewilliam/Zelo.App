using System.Security.Cryptography;
using System.Text;

namespace Zelo.Core.Tarefas;

// ---------------------------------------------------------------- DISM + SFC
public sealed class IntegridadeSistema : Tarefa
{
    public override string Id => "integridade-sistema";
    public override string Nome => "Integridade do Windows (DISM + SFC)";
    public override string Descricao => "Repara a imagem do Windows com DISM e depois verifica os arquivos do sistema com SFC.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };
    public override TimeSpan Limite => TimeSpan.FromHours(3);

    private static string Dism => Path.Combine(Environment.SystemDirectory, "dism.exe");
    private static string Sfc => Path.Combine(Environment.SystemDirectory, "sfc.exe");

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var det = new List<string>();
        if (ctx.Simulacao)
        {
            var check = await Executor.ProcessoAsync(Dism, "/Online /Cleanup-Image /CheckHealth /English", ct);
            det.AddRange(check.Linhas(3));
            bool saudavel = check.Saida.Contains("No component store corruption detected", StringComparison.OrdinalIgnoreCase);
            return Res(saudavel ? StatusTarefa.Ok : StatusTarefa.Aviso,
                "[Simulação] Verificação rápida da imagem: " + (saudavel ? "nenhuma corrupção conhecida." : "possível corrupção — a rotina mensal faria o reparo."), det);
        }

        var dism = await Executor.ProcessoAsync(Dism, "/Online /Cleanup-Image /RestoreHealth /English", ct);
        det.Add($"DISM RestoreHealth: código {dism.Codigo}");
        det.AddRange(dism.Linhas(3).Select(l => "  " + l));

        // O SFC escreve em UTF-16.
        var sfc = await Executor.ProcessoAsync(Sfc, "/scannow", ct, Encoding.Unicode);
        var sfcLinhas = sfc.Linhas(4).ToList();
        det.Add($"SFC: código {sfc.Codigo}");
        det.AddRange(sfcLinhas.Select(l => "  " + l));

        var status = dism.Codigo == 0 && sfc.Codigo == 0 ? StatusTarefa.Ok : StatusTarefa.Aviso;
        var resumo = status == StatusTarefa.Ok
            ? "Imagem do Windows e arquivos do sistema verificados."
            : "DISM ou SFC relataram problemas — veja os detalhes (o log completo fica em C:\\Windows\\Logs\\CBS\\CBS.log).";
        return Res(status, resumo, det);
    }
}

// ---------------------------------------------------------------- Limpeza de componentes
public sealed class LimpezaComponentes : Tarefa
{
    public override string Id => "limpeza-componentes";
    public override string Nome => "Limpeza de atualizações antigas (WinSxS)";
    public override string Descricao => "Remove versões substituídas de componentes do Windows com segurança (DISM StartComponentCleanup).";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };
    public override TimeSpan Limite => TimeSpan.FromHours(2);

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        if (ctx.Simulacao)
        {
            var a = await Executor.ProcessoAsync(dism, "/Online /Cleanup-Image /AnalyzeComponentStore /English", ct);
            var linhas = a.Linhas(12).Where(l => l.Contains(':')).ToList();
            return Res(StatusTarefa.Ok, "[Simulação] Análise do repositório de componentes.", linhas);
        }

        // Nunca usamos /ResetBase: ele impede desinstalar atualizações depois.
        var r = await Executor.ProcessoAsync(dism, "/Online /Cleanup-Image /StartComponentCleanup /English", ct);
        return r.Codigo == 0
            ? Res(StatusTarefa.Ok, "Componentes antigos do Windows removidos.", r.Linhas(2))
            : Res(StatusTarefa.Aviso, $"Limpeza de componentes terminou com código {r.Codigo}.", r.Linhas(4));
    }
}

// ---------------------------------------------------------------- Defender completo
public sealed class DefenderCompleta : Tarefa
{
    public override string Id => "defender-completa";
    public override string Nome => "Antivírus: verificação completa";
    public override string Descricao => "Verificação completa do Microsoft Defender em todos os arquivos.";
    public override Categoria Categoria => Categoria.Seguranca;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };
    public override TimeSpan Limite => TimeSpan.FromHours(6);
    public override bool Habilitada(Configuracao c) => c.VarreduraCompletaDefender;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var av = await Antivirus.LerAsync(ct);
        if (!av.DefenderEmUso)
            return av.OutrosAtivos.Count > 0
                ? Res(StatusTarefa.Ignorada, $"Protegido por {string.Join(", ", av.OutrosAtivos)}; a verificação completa fica por conta dele.")
                : Res(StatusTarefa.Critico, "Nenhum antivírus ativo para fazer a verificação completa.", new[] { Antivirus.DicaSemAntivirus });

        if (ctx.Simulacao) return Res(StatusTarefa.Ok, "[Simulação] A verificação completa seria executada.");

        var scan = await Executor.PowerShellAsync("Start-MpScan -ScanType FullScan", ct);
        if (scan.Codigo != 0)
            return Res(StatusTarefa.Aviso, "A verificação completa não concluiu.", scan.Linhas(3));

        av = await Antivirus.LerAsync(ct);
        return av.Ameacas > 0
            ? Res(StatusTarefa.Critico, $"{av.Ameacas} ameaça(s) encontrada(s). Veja Segurança do Windows > Histórico de proteção.")
            : Res(StatusTarefa.Ok, "Verificação completa concluída, nenhuma ameaça.");
    }
}

// ---------------------------------------------------------------- Sistema de arquivos
public sealed class VerificarVolumes : Tarefa
{
    public override string Id => "verificar-volumes";
    public override string Nome => "Verificação do sistema de arquivos";
    public override string Descricao => "Procura erros no NTFS com o PC ligado (equivalente ao chkdsk /scan), sem reiniciar e sem alterar nada.";
    public override Categoria Categoria => Categoria.Disco;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };
    public override TimeSpan Limite => TimeSpan.FromHours(1);

    private const string Script = """
        $r = foreach ($v in Get-Volume | Where-Object { $_.DriveLetter -and [string]$_.DriveType -eq 'Fixed' -and [string]$_.FileSystemType -eq 'NTFS' }) {
          $res = ''
          try { $res = [string](Repair-Volume -DriveLetter $v.DriveLetter -Scan -ErrorAction Stop) } catch { $res = 'Erro: ' + $_.Exception.Message }
          [pscustomobject]@{ Letra = [string]$v.DriveLetter; Resultado = $res }
        }
        ConvertTo-Json -InputObject @($r) -Compress
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        // Somente leitura: roda igual em simulação.
        var dados = await Executor.PowerShellJsonAsync(Script, ct);
        var det = new List<string>();
        var comErro = new List<string>();
        foreach (var v in dados.Itens())
        {
            var letra = v.Str("Letra");
            var res = v.Str("Resultado");
            bool ok = res.Equals("NoErrorsFound", StringComparison.OrdinalIgnoreCase);
            det.Add($"{letra}: {(ok ? "nenhum erro" : res)}");
            if (!ok) comErro.Add(letra);
        }
        if (comErro.Count > 0)
        {
            det.Add($"Para corrigir, abra o Terminal como administrador e rode: chkdsk {comErro[0]}: /spotfix");
            return Res(StatusTarefa.Aviso, $"Unidade(s) {string.Join(", ", comErro)} com problemas no sistema de arquivos.", det);
        }
        return Res(StatusTarefa.Ok, $"{det.Count} unidade(s) sem erros.", det);
    }
}

// ---------------------------------------------------------------- Auditoria de segurança
public sealed class AuditoriaSeguranca : Tarefa
{
    public override string Id => "auditoria-seguranca";
    public override string Nome => "Auditoria de segurança";
    public override string Descricao => "Confere firewall, UAC, antivírus, BitLocker, SMBv1 e contas de administrador.";
    public override Categoria Categoria => Categoria.Seguranca;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };

    private const string Script = """
        $o = [ordered]@{}
        try { $o.Firewall = @(Get-NetFirewallProfile | ForEach-Object { [pscustomobject]@{ Perfil = [string]$_.Name; Ativo = ([string]$_.Enabled -eq 'True') } }) } catch { $o.Firewall = @() }
        try { $o.UAC = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System').EnableLUA } catch { $o.UAC = -1 }
        try { $o.BitLocker = [string](Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction Stop).ProtectionStatus } catch { $o.BitLocker = 'Indisponivel' }
        try { $o.SMB1 = [bool](Get-SmbServerConfiguration).EnableSMB1Protocol } catch { $o.SMB1 = $null }
        try {
          $o.Admins = @(Get-LocalGroupMember -SID 'S-1-5-32-544' -ErrorAction Stop | ForEach-Object { [string]$_.Name })
        } catch {
          $nome = (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544').Translate([Security.Principal.NTAccount]).Value.Split('\')[1]
          $g = [ADSI]"WinNT://./$nome,group"
          $o.Admins = @($g.psbase.Invoke('Members') | ForEach-Object { [string]$_.GetType().InvokeMember('Name', 'GetProperty', $null, $_, $null) })
        }
        [pscustomobject]$o | ConvertTo-Json -Compress -Depth 4
        """;

    private sealed class Snapshot { public List<string> Admins { get; set; } = new(); }

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var d = await Executor.PowerShellJsonAsync(Script, ct);
        var det = new List<string>();
        var criticos = new List<string>();
        var avisos = new List<string>();

        foreach (var f in d.Itens("Firewall"))
        {
            var ativo = f.Bool("Ativo") ?? false;
            det.Add($"Firewall ({f.Str("Perfil")}): {(ativo ? "ligado" : "DESLIGADO")}");
            if (!ativo) criticos.Add($"firewall {f.Str("Perfil")} desligado");
        }

        var uac = (int)(d.Num("UAC") ?? -1);
        det.Add($"Controle de Conta de Usuário (UAC): {(uac == 1 ? "ligado" : uac == 0 ? "DESLIGADO" : "desconhecido")}");
        if (uac == 0) criticos.Add("UAC desligado");

        var av = await Antivirus.LerAsync(ct);
        if (av.DefenderEmUso)
        {
            det.Add($"Antivírus: Microsoft Defender, tempo real {(av.TempoReal ? "ligado" : "DESLIGADO")}");
            if (!av.TempoReal) criticos.Add("antivírus em tempo real desligado");
        }
        else if (av.OutrosAtivos.Count > 0)
        {
            det.Add($"Antivírus: {string.Join(", ", av.OutrosAtivos)}");
        }
        else
        {
            det.Add("Antivírus: NENHUM ATIVO");
            criticos.Add("nenhum antivírus ativo");
        }

        var bit = d.Str("BitLocker");
        var textoBit = bit switch { "On" => "ativo", "Off" => "desativado", _ => "não disponível nesta edição" };
        det.Add($"BitLocker no disco do sistema: {textoBit}");
        if (bit == "Off") avisos.Add("disco do sistema sem criptografia (BitLocker)");

        if (d.Bool("SMB1") == true)
        {
            det.Add("SMBv1: LIGADO (protocolo antigo e inseguro)");
            avisos.Add("SMBv1 ligado");
        }

        var admins = d.Textos("Admins").ToList();
        det.Add("Administradores: " + string.Join(", ", admins));
        var anterior = Estado.Ler<Snapshot>("admins");
        if (anterior is not null)
        {
            var novos = admins.Where(a => !anterior.Admins.Contains(a, StringComparer.OrdinalIgnoreCase)).ToList();
            if (novos.Count > 0) criticos.Add("novo(s) administrador(es): " + string.Join(", ", novos));
        }
        if (!ctx.Simulacao && admins.Count > 0) Estado.Gravar("admins", new Snapshot { Admins = admins });

        if (criticos.Count > 0) return Res(StatusTarefa.Critico, Capitalizar(string.Join("; ", criticos.Concat(avisos))) + ".", det);
        if (avisos.Count > 0) return Res(StatusTarefa.Aviso, Capitalizar(string.Join("; ", avisos)) + ".", det);
        return Res(StatusTarefa.Ok, "Configurações de segurança em ordem.", det);
    }

    private static string Capitalizar(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}

// ---------------------------------------------------------------- Bateria
public sealed class SaudeBateria : Tarefa
{
    public override string Id => "bateria";
    public override string Nome => "Saúde da bateria";
    public override string Descricao => "Compara a capacidade atual da bateria com a original e gera o relatório oficial do Windows.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };

    private const string Script = """
        $st = Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData -ErrorAction SilentlyContinue | Select-Object -First 1
        $fc = Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity -ErrorAction SilentlyContinue | Select-Object -First 1
        $cc = Get-CimInstance -Namespace root/wmi -ClassName BatteryCycleCount -ErrorAction SilentlyContinue | Select-Object -First 1
        [pscustomobject]@{ Projetada = $st.DesignedCapacity; Atual = $fc.FullChargedCapacity; Ciclos = $cc.CycleCount } | ConvertTo-Json -Compress
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        if (!Nativo.TemBateria()) return Res(StatusTarefa.Ignorada, "Este PC não tem bateria.");

        var det = new List<string>();
        var d = await Executor.PowerShellJsonAsync(Script, ct);
        var projetada = d.Num("Projetada") ?? 0;
        var atual = d.Num("Atual") ?? 0;
        var ciclos = d.Num("Ciclos");

        if (!ctx.Simulacao)
        {
            var arq = Path.Combine(Caminhos.Relatorios, $"bateria-{DateTime.Now:yyyy-MM}.html");
            await Executor.ProcessoAsync(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), $"/batteryreport /output \"{arq}\"", ct);
            if (File.Exists(arq)) det.Add("Relatório completo do Windows: " + arq);
        }

        if (projetada <= 0 || atual <= 0)
            return Res(StatusTarefa.Ok, "O fabricante não informa a capacidade da bateria.", det);

        var saude = Math.Min(100, 100 * atual / projetada);
        det.Insert(0, $"Capacidade original: {projetada:0} mWh — atual: {atual:0} mWh");
        if (ciclos is > 0) det.Insert(1, $"Ciclos de carga: {ciclos:0}");

        return saude < 60
            ? Res(StatusTarefa.Aviso, $"Bateria com {saude:0}% da capacidade original — considere trocar.", det)
            : Res(StatusTarefa.Ok, $"Bateria com {saude:0}% da capacidade original.", det);
    }
}

// ---------------------------------------------------------------- Maiores arquivos
public sealed class MaioresArquivos : Tarefa
{
    public override string Id => "maiores-arquivos";
    public override string Nome => "Maiores arquivos dos usuários";
    public override string Descricao => "Relatório dos maiores arquivos nas pastas de usuário, para você decidir o que apagar. Não apaga nada.";
    public override Categoria Categoria => Categoria.Disco;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };

    protected override Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        const long minimo = 300L * 1024 * 1024;
        var grandes = new List<(long tam, string caminho)>();
        var det = new List<string>();

        foreach (var perfil in Perfis.Pastas())
        {
            long downloads = 0;
            var pastaDownloads = Path.Combine(perfil, "Downloads");
            foreach (var arq in Directory.EnumerateFiles(perfil, "*", Varredura()))
            {
                ct.ThrowIfCancellationRequested();
                // AppData é cuidado por outras tarefas e pelos próprios programas.
                if (arq.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var t = new FileInfo(arq).Length;
                    if (arq.StartsWith(pastaDownloads, StringComparison.OrdinalIgnoreCase)) downloads += t;
                    if (t >= minimo) grandes.Add((t, arq));
                }
                catch { }
            }
            det.Add($"Pasta Downloads de {Path.GetFileName(perfil)}: {Textos.Tamanho(downloads)}");
        }

        det.AddRange(grandes.OrderByDescending(g => g.tam).Take(20).Select(g => $"{Textos.Tamanho(g.tam),9}  {g.caminho}"));
        var total = grandes.Sum(g => g.tam);
        return Task.FromResult(Res(StatusTarefa.Ok,
            grandes.Count == 0 ? "Nenhum arquivo acima de 300 MB." : $"{grandes.Count} arquivo(s) acima de 300 MB somando {Textos.Tamanho(total)}.",
            det));
    }
}

// ---------------------------------------------------------------- Drivers antigos
public sealed class DriversAntigos : Tarefa
{
    public override string Id => "drivers-antigos";
    public override string Nome => "Drivers muito antigos";
    public override string Descricao => "Lista drivers de fabricantes com mais de 4 anos, para conferir no site do fabricante. Não instala nada.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };

    private const string Script = """
        $lim = (Get-Date).AddYears(-4)
        $r = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DriverDate -and $_.DriverDate -lt $lim -and $_.DeviceName -and $_.DriverProviderName -and $_.DriverProviderName -notmatch 'Microsoft' } |
          Sort-Object DriverDate | Select-Object -First 25 | ForEach-Object {
            [pscustomobject]@{ Dispositivo = [string]$_.DeviceName; Fabricante = [string]$_.DriverProviderName; Versao = [string]$_.DriverVersion; Data = $_.DriverDate.ToString('dd/MM/yyyy') }
          }
        ConvertTo-Json -InputObject @($r) -Compress
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dados = await Executor.PowerShellJsonAsync(Script, ct);
        var det = dados.Itens()
            .Select(d => $"{d.Str("Data")}  {d.Str("Dispositivo")} — {d.Str("Fabricante")} v{d.Str("Versao")}")
            .ToList();
        return Res(StatusTarefa.Ok,
            det.Count == 0 ? "Nenhum driver de fabricante com mais de 4 anos." : $"{det.Count} driver(s) antigo(s) — vale conferir no site do fabricante (driver antigo nem sempre é problema).",
            det);
    }
}

// ---------------------------------------------------------------- Teste do backup
public sealed class VerificarBackup : Tarefa
{
    public override string Id => "verificar-backup";
    public override string Nome => "Teste do backup";
    public override string Descricao => "Sorteia arquivos do backup e confere, byte a byte (SHA-256), se são iguais aos originais.";
    public override Categoria Categoria => Categoria.Backup;
    public override Rotina[] Rotinas => new[] { Rotina.Mensal };

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var c = ctx.Config;
        if (!c.BackupConfigurado)
            return Res(StatusTarefa.Aviso, "Backup não configurado, nada para testar.");
        if (!Directory.Exists(Path.GetPathRoot(c.DestinoBackup) ?? ""))
            return Res(StatusTarefa.Aviso, "Destino do backup indisponível no momento do teste.");

        var ultimo = Estado.Ler<BackupIncremental.EstadoBackup>("backup")?.UltimoSucesso;
        var det = new List<string> { ultimo is null ? "Nenhum backup concluído ainda." : $"Último backup completo: {ultimo:dd/MM/yyyy HH:mm}" };

        // Pares (cópia, original) de todas as pastas.
        var pares = new List<(string copia, string original)>();
        foreach (var origem in c.PastasBackup)
        {
            var espelho = BackupIncremental.PastaEspelho(c.DestinoBackup, origem);
            if (!Directory.Exists(espelho)) continue;
            foreach (var copia in Directory.EnumerateFiles(espelho, "*", Varredura()).Take(20000))
                pares.Add((copia, Path.Combine(origem, Path.GetRelativePath(espelho, copia))));
        }
        if (pares.Count == 0)
            return Res(StatusTarefa.Aviso, "Nenhum arquivo encontrado no backup.", det);

        int iguais = 0, divergentes = 0, alteradosDepois = 0;
        foreach (var (copia, original) in pares.OrderBy(_ => Random.Shared.Next()).Take(25))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(original)) { alteradosDepois++; continue; }
                var fo = new FileInfo(original);
                var fc = new FileInfo(copia);
                // Original mudou depois do backup: não dá para comparar, não é erro.
                if (fo.LastWriteTimeUtc > fc.LastWriteTimeUtc.AddSeconds(2)) { alteradosDepois++; continue; }
                if (fo.Length > 500L * 1024 * 1024) { iguais++; continue; } // grandes: só tamanho
                bool igual = fo.Length == fc.Length;
                if (igual)
                {
                    // Os dois hashes são calculados antes da comparação (span não pode atravessar um await).
                    var hashOriginal = await Hash(original, ct);
                    var hashCopia = await Hash(copia, ct);
                    igual = Convert.ToHexString(hashOriginal) == Convert.ToHexString(hashCopia);
                }
                if (!igual)
                {
                    divergentes++;
                    det.Add("DIFERENTE: " + copia);
                }
                else iguais++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { divergentes++; det.Add($"Ilegível: {copia} ({ex.Message})"); }
        }

        det.Insert(1, $"Amostra: {iguais} idênticos, {divergentes} com problema, {alteradosDepois} alterados depois do backup");
        if (divergentes > 0)
            return Res(StatusTarefa.Critico, $"{divergentes} arquivo(s) do backup não batem com o original — o backup pode estar corrompido.", det);
        if (ultimo is null || ultimo < DateTime.Now.AddDays(-7))
            return Res(StatusTarefa.Aviso, "Cópias conferem, mas o último backup completo tem mais de 7 dias.", det);
        return Res(StatusTarefa.Ok, $"Backup testado: {iguais} arquivo(s) conferidos byte a byte.", det);
    }

    private static async Task<byte[]> Hash(string arquivo, CancellationToken ct)
    {
        await using var fs = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return await SHA256.HashDataAsync(fs, ct);
    }
}
