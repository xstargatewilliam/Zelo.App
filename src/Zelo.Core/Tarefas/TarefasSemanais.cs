using System.Diagnostics;

namespace Zelo.Core.Tarefas;

// ---------------------------------------------------------------- Ponto de restauração
public sealed class PontoRestauracao : Tarefa
{
    public override string Id => "ponto-restauracao";
    public override string Nome => "Ponto de restauração";
    public override string Descricao => "Cria um ponto de restauração antes das atualizações da semana, para poder voltar atrás.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };
    public override bool Habilitada(Configuracao c) => c.CriarPontoRestauracao;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        if (ctx.Simulacao) return Res(StatusTarefa.Ok, "[Simulação] Um ponto de restauração seria criado.");

        var r = await Executor.PowerShellAsync(
            "Checkpoint-Computer -Description 'Zelo - manutencao semanal' -RestorePointType MODIFY_SETTINGS", ct);
        if (r.Codigo == 0)
            return Res(StatusTarefa.Ok, "Ponto de restauração criado (o Windows pode reaproveitar um de menos de 24h).");

        return Res(StatusTarefa.Aviso,
            "Não foi possível criar o ponto de restauração. A Restauração do Sistema pode estar desativada no disco C:.",
            new[] { string.Join(" ", r.Linhas(3)), "Para ativar: Painel de Controle > Sistema > Proteção do Sistema > Configurar." });
    }
}

// ---------------------------------------------------------------- Windows Update
public sealed class WindowsUpdate : Tarefa
{
    public override string Id => "windows-update";
    public override string Nome => "Windows Update";
    public override string Descricao => "Procura e instala atualizações de qualidade e segurança (não instala upgrades de versão do Windows).";
    public override Categoria Categoria => Categoria.Atualizacoes;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };
    public override TimeSpan Limite => TimeSpan.FromHours(2);

    // A categoria "Upgrades" (troca de versão do Windows) é ignorada de propósito.
    private const string Script = """
        $instalar = {{INSTALAR}}
        $s = New-Object -ComObject Microsoft.Update.Session
        $s.ClientApplicationID = 'Zelo'
        $busca = $s.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'")
        $lista = @()
        foreach ($u in $busca.Updates) {
          $upgrade = $false
          foreach ($c in $u.Categories) { if ($c.CategoryID -eq '3689bdc8-b205-4af4-8d4b-a63924c5e9d5') { $upgrade = $true } }
          if (-not $upgrade) { $lista += $u }
        }
        $titulos = @($lista | ForEach-Object { [string]$_.Title })
        $resultado = -1; $reinicio = $false; $falhas = @()
        if ($instalar -and $lista.Count -gt 0) {
          $col = New-Object -ComObject Microsoft.Update.UpdateColl
          foreach ($u in $lista) { if (-not $u.EulaAccepted) { $u.AcceptEula() }; [void]$col.Add($u) }
          $d = $s.CreateUpdateDownloader(); $d.Updates = $col; [void]$d.Download()
          $i = $s.CreateUpdateInstaller(); $i.Updates = $col
          $r = $i.Install()
          $resultado = [int]$r.ResultCode; $reinicio = [bool]$r.RebootRequired
          for ($k = 0; $k -lt $col.Count; $k++) {
            $rc = [int]$r.GetUpdateResult($k).ResultCode
            if ($rc -ne 2 -and $rc -ne 3) { $falhas += [string]$col.Item($k).Title }
          }
        }
        [pscustomobject]@{ Pendentes = $titulos; Resultado = $resultado; Reinicio = $reinicio; Falhas = @($falhas) } | ConvertTo-Json -Compress -Depth 3
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        bool instalar = ctx.Config.InstalarWindowsUpdate && !ctx.Simulacao;
        var dados = await Executor.PowerShellJsonAsync(Script.Replace("{{INSTALAR}}", instalar ? "$true" : "$false"), ct);
        var pendentes = dados.Textos("Pendentes").ToList();
        var falhas = dados.Textos("Falhas").ToList();
        var reinicio = dados.Bool("Reinicio") ?? false;

        if (pendentes.Count == 0)
            return Res(StatusTarefa.Ok, "Windows em dia, nenhuma atualização pendente.");

        if (!instalar)
        {
            var motivo = ctx.Simulacao ? "[Simulação] " : "Instalação automática desligada. ";
            return Res(ctx.Simulacao ? StatusTarefa.Ok : StatusTarefa.Aviso,
                $"{motivo}{pendentes.Count} atualização(ões) disponível(is).", pendentes);
        }

        var det = pendentes.Select(p => (falhas.Contains(p) ? "FALHOU: " : "Instalada: ") + p).ToList();
        if (reinicio) det.Add("É preciso reiniciar o PC para concluir.");
        if (falhas.Count > 0)
            return Res(StatusTarefa.Aviso, $"{pendentes.Count - falhas.Count} instalada(s), {falhas.Count} falharam.", det);
        return Res(StatusTarefa.Ok,
            $"{pendentes.Count} atualização(ões) instalada(s){(reinicio ? " — reinicie o PC para concluir" : "")}.", det);
    }
}

// ---------------------------------------------------------------- winget
public sealed class AtualizacaoProgramas : Tarefa
{
    public override string Id => "winget";
    public override string Nome => "Atualização de programas (winget)";
    public override string Descricao => "Atualiza os programas instalados usando o Gerenciador de Pacotes do Windows.";
    public override Categoria Categoria => Categoria.Atualizacoes;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };
    public override TimeSpan Limite => TimeSpan.FromMinutes(90);
    public override bool Habilitada(Configuracao c) => c.AtualizarProgramasWinget;

    /// <summary>O serviço roda como SYSTEM, que não enxerga o atalho do winget; procuramos o executável real.</summary>
    internal static string? LocalizarWinget()
    {
        var raiz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        try
        {
            var melhor = Directory.GetDirectories(raiz, "Microsoft.DesktopAppInstaller_*_8wekyb3d8bbwe")
                .Select(d => new { Pasta = d, Exe = Path.Combine(d, "winget.exe"), Versao = VersaoDaPasta(d) })
                .Where(x => File.Exists(x.Exe))
                .OrderByDescending(x => x.Versao)
                .FirstOrDefault();
            if (melhor is not null) return melhor.Exe;
        }
        catch { }

        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe");
        return File.Exists(local) ? local : null;
    }

    private static Version VersaoDaPasta(string pasta)
    {
        var partes = Path.GetFileName(pasta).Split('_');
        return partes.Length > 1 && Version.TryParse(partes[1], out var v) ? v : new Version(0, 0);
    }

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var winget = LocalizarWinget();
        if (winget is null)
            return Res(StatusTarefa.Ignorada, "winget não encontrado. Instale o \"Instalador de Aplicativo\" pela Microsoft Store.");

        var args = ctx.Simulacao
            ? "upgrade --accept-source-agreements --disable-interactivity"
            : "upgrade --all --silent --accept-source-agreements --accept-package-agreements --disable-interactivity";
        var r = await Executor.ProcessoAsync(winget, args, ct, System.Text.Encoding.UTF8);
        var linhas = r.Linhas(25).ToList();

        if (ctx.Simulacao)
            return Res(StatusTarefa.Ok, "[Simulação] Lista de programas com atualização disponível.", linhas);

        var (ok, falhas) = Resumir(r.Saida);
        // Componentes do Windows em formato MSIX não instalam pela conta SYSTEM; a Microsoft Store os atualiza sozinha.
        var relevantes = falhas.Where(f => !Frameworks.Any(fw => f.id.StartsWith(fw, StringComparison.OrdinalIgnoreCase))).ToList();
        var ignoradas = falhas.Except(relevantes).ToList();

        var det = new List<string>();
        det.AddRange(ok.Select(n => "Atualizado: " + n));
        det.AddRange(relevantes.Select(f => $"FALHOU: {f.nome} ({f.erro})"));
        det.AddRange(ignoradas.Select(f => $"Ignorado: {f.nome} — componente do Windows, atualizado pela Microsoft Store"));

        if (ok.Count == 0 && falhas.Count == 0)
        {
            bool nadaAFazer = r.Codigo == 0 || unchecked((uint)r.Codigo) == 0x8A15002B;
            return nadaAFazer
                ? Res(StatusTarefa.Ok, "Todos os programas já estavam atualizados.")
                : Res(StatusTarefa.Aviso, $"O winget terminou com código 0x{r.Codigo:X8}.", linhas);
        }
        if (relevantes.Count > 0)
        {
            det.Add("Esses podem ser atualizados abrindo o próprio programa ou rodando \"winget upgrade --all\" no Terminal.");
            return Res(StatusTarefa.Aviso, $"{ok.Count} programa(s) atualizado(s); {relevantes.Count} não puderam ser atualizados automaticamente.", det);
        }
        return Res(StatusTarefa.Ok, $"{ok.Count} programa(s) atualizado(s).", det);
    }

    private static readonly string[] Frameworks = { "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.WindowsAppRuntime", "Microsoft.DesktopAppInstaller" };

    /// <summary>Lê a saída do winget: "(3/19) Found Nome [Id] Version x" seguido de sucesso ou erro.</summary>
    internal static (List<string> ok, List<(string nome, string id, string erro)> falhas) Resumir(string saida)
    {
        var ok = new List<string>();
        var falhas = new List<(string nome, string id, string erro)>();
        var cabecalho = new System.Text.RegularExpressions.Regex(@"^\(\d+/\d+\)\s+\S+\s+(.+?)\s+\[([^\]]+)\]");
        var erroHex = new System.Text.RegularExpressions.Regex(@"0x[0-9A-Fa-f]{8}.*");
        string? nome = null, id = null;

        foreach (var bruta in saida.Split('\n', '\r'))
        {
            var l = bruta.Trim();
            var m = cabecalho.Match(l);
            if (m.Success) { nome = m.Groups[1].Value; id = m.Groups[2].Value; continue; }
            if (nome is null) continue;
            if (l.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("instalado com êxito", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("instalado com sucesso", StringComparison.OrdinalIgnoreCase))
            {
                ok.Add(nome); nome = null;
            }
            else if (erroHex.Match(l) is { Success: true } e && !l.Contains("hash", StringComparison.OrdinalIgnoreCase))
            {
                falhas.Add((nome, id ?? "", e.Value.Trim())); nome = null;
            }
        }
        return (ok, falhas);
    }
}

// ---------------------------------------------------------------- Otimização de unidades
public sealed class OtimizarUnidades : Tarefa
{
    public override string Id => "otimizar-unidades";
    public override string Nome => "Otimização das unidades (TRIM / desfragmentação)";
    public override string Descricao => "TRIM em SSDs e desfragmentação apenas em HDs mecânicos. SSD nunca é desfragmentado.";
    public override Categoria Categoria => Categoria.Disco;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };
    public override TimeSpan Limite => TimeSpan.FromHours(2);

    private const string Script = """
        $sim = {{SIM}}
        $r = foreach ($v in Get-Volume | Where-Object { $_.DriveLetter -and [string]$_.DriveType -eq 'Fixed' -and @('NTFS','ReFS') -contains [string]$_.FileSystemType }) {
          $letra = [string]$v.DriveLetter
          $tipo = 'Unspecified'
          try {
            $disco = Get-Partition -DriveLetter $letra | Get-Disk
            $pd = Get-PhysicalDisk | Where-Object { [string]$_.DeviceId -eq [string]$disco.Number } | Select-Object -First 1
            if ($pd) { $tipo = [string]$pd.MediaType }
          } catch {}
          $acao = switch ($tipo) { 'SSD' { 'TRIM' } 'HDD' { 'Desfragmentação' } default { 'Nenhuma (tipo de disco desconhecido)' } }
          $erro = ''
          if (-not $sim) {
            try {
              if ($tipo -eq 'SSD') { Optimize-Volume -DriveLetter $letra -ReTrim -ErrorAction Stop }
              elseif ($tipo -eq 'HDD') { Optimize-Volume -DriveLetter $letra -Defrag -ErrorAction Stop }
            } catch { $erro = $_.Exception.Message }
          }
          [pscustomobject]@{ Letra = $letra; Tipo = $tipo; Acao = $acao; Erro = $erro }
        }
        ConvertTo-Json -InputObject @($r) -Compress
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dados = await Executor.PowerShellJsonAsync(Script.Replace("{{SIM}}", ctx.Simulacao ? "$true" : "$false"), ct);
        var det = new List<string>();
        int erros = 0;
        foreach (var v in dados.Itens())
        {
            var erro = v.Str("Erro");
            if (erro.Length > 0) erros++;
            det.Add($"{v.Str("Letra")}: ({v.Str("Tipo")}) {v.Str("Acao")}{(erro.Length > 0 ? " — erro: " + erro : ctx.Simulacao ? "" : " — ok")}");
        }
        if (erros > 0) return Res(StatusTarefa.Aviso, $"{erros} unidade(s) não puderam ser otimizadas.", det);
        return Res(StatusTarefa.Ok, $"{ctx.Sim}{det.Count} unidade(s) otimizada(s).", det);
    }
}

// ---------------------------------------------------------------- Lixeira e cache
public sealed class LixeiraECache : Tarefa
{
    public override string Id => "lixeira-cache";
    public override string Nome => "Lixeira antiga e cache de navegadores";
    public override string Descricao => "Esvazia itens antigos da Lixeira e, se ativado, o cache do Chrome, Edge, Firefox e Opera/Opera GX.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };

    protected override Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var det = new List<string>();
        var (bytesLix, itensLix) = LimparLixeira(ctx, ct);
        det.Add($"Lixeira: {itensLix} item(ns) com mais de {ctx.Config.DiasLixeira} dias ({Textos.Tamanho(bytesLix)})");

        long bytesCache = 0;
        if (ctx.Config.LimparCacheNavegadores)
        {
            bytesCache = LimparCaches(ctx, det, ct);
        }
        else det.Add("Cache de navegadores: desativado nas configurações.");

        var verbo = ctx.Simulacao ? "seriam liberados" : "liberados";
        return Task.FromResult(Res(StatusTarefa.Ok, $"{ctx.Sim}{Textos.Tamanho(bytesLix + bytesCache)} {verbo}.", det));
    }

    private static (long bytes, int itens) LimparLixeira(Contexto ctx, CancellationToken ct)
    {
        long bytes = 0; int itens = 0;
        var limite = DateTime.UtcNow.AddDays(-ctx.Config.DiasLixeira);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var lixeira = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
            if (!Directory.Exists(lixeira)) continue;
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(lixeira, "$I*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }).ToList(); }
            catch { continue; }

            foreach (var info in infos)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Formato do arquivo $I: [0] versão, [8] tamanho, [16] data da exclusão (FILETIME).
                    var b = File.ReadAllBytes(info);
                    if (b.Length < 24) continue;
                    var tamanho = BitConverter.ToInt64(b, 8);
                    var quando = DateTime.FromFileTimeUtc(BitConverter.ToInt64(b, 16));
                    if (quando > limite) continue;

                    var dados = Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
                    if (!ctx.Simulacao)
                    {
                        if (Directory.Exists(dados)) Directory.Delete(dados, recursive: true);
                        else if (File.Exists(dados)) { File.SetAttributes(dados, FileAttributes.Normal); File.Delete(dados); }
                        File.SetAttributes(info, FileAttributes.Normal);
                        File.Delete(info);
                    }
                    bytes += Math.Max(0, tamanho); itens++;
                }
                catch { }
            }
        }
        return (bytes, itens);
    }

    private static readonly (string processo, string pai, string[] subpastas)[] Navegadores =
    {
        ("chrome", @"AppData\Local\Google\Chrome\User Data", new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" }),
        ("msedge", @"AppData\Local\Microsoft\Edge\User Data", new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" }),
        ("firefox", @"AppData\Local\Mozilla\Firefox\Profiles", new[] { "cache2" }),
        // Cada pasta aqui é um navegador: "Opera Stable", "Opera GX Stable"...
        ("opera", @"AppData\Local\Opera Software", new[] { @"Cache\Cache_Data", "Code Cache", "GPUCache" }),
    };

    private long LimparCaches(Contexto ctx, List<string> det, CancellationToken ct)
    {
        long total = 0;
        foreach (var (processo, pai, subpastas) in Navegadores)
        {
            if (Process.GetProcessesByName(processo).Length > 0)
            {
                det.Add($"{processo}: aberto agora, cache não foi mexido.");
                continue;
            }
            long bytes = 0;
            foreach (var perfilUsuario in Perfis.Pastas())
            {
                var raiz = Path.Combine(perfilUsuario, pai);
                if (!Directory.Exists(raiz)) continue;
                foreach (var perfilNav in Directory.EnumerateDirectories(raiz))
                foreach (var sub in subpastas)
                {
                    var pasta = Path.Combine(perfilNav, sub);
                    if (!Directory.Exists(pasta)) continue;
                    foreach (var arq in Directory.EnumerateFiles(pasta, "*", Varredura()))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(arq);
                            var t = fi.Length;
                            if (!ctx.Simulacao) fi.Delete();
                            bytes += t;
                        }
                        catch { }
                    }
                }
            }
            if (bytes > 0) det.Add($"{processo}: {Textos.Tamanho(bytes)} de cache");
            total += bytes;
        }
        return total;
    }
}

// ---------------------------------------------------------------- Programas na inicialização
public sealed class ProgramasInicializacao : Tarefa
{
    public override string Id => "inicializacao";
    public override string Nome => "Programas que iniciam com o Windows";
    public override string Descricao => "Lista o que abre junto com o Windows e avisa quando algo novo aparece. Não desativa nada sozinho.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Semanal };

    private const string Script = """
        $r = Get-CimInstance Win32_StartupCommand | ForEach-Object {
          [pscustomobject]@{ Nome = [string]$_.Name; Comando = [string]$_.Command; Local = [string]$_.Location; Usuario = [string]$_.User }
        }
        ConvertTo-Json -InputObject @($r) -Compress
        """;

    private sealed class Snapshot { public List<string> Itens { get; set; } = new(); }

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dados = await Executor.PowerShellJsonAsync(Script, ct);
        var atuais = dados.Itens()
            .Select(i => new { Chave = i.Str("Nome") + "|" + i.Str("Comando"), Texto = $"{i.Str("Nome")} ({i.Str("Usuario")}) — {i.Str("Comando")}" })
            .GroupBy(x => x.Chave).Select(g => g.First())
            .ToList();

        var anterior = Estado.Ler<Snapshot>("inicializacao");
        if (!ctx.Simulacao) Estado.Gravar("inicializacao", new Snapshot { Itens = atuais.Select(a => a.Chave).ToList() });

        var det = atuais.Select(a => a.Texto).ToList();
        if (anterior is null)
            return Res(StatusTarefa.Ok, $"{atuais.Count} programa(s) iniciam com o Windows (primeira análise).", det);

        var novos = atuais.Where(a => !anterior.Itens.Contains(a.Chave)).ToList();
        if (novos.Count == 0)
            return Res(StatusTarefa.Ok, $"{atuais.Count} programa(s) na inicialização, nenhum novo.", det);

        det.Insert(0, "NOVOS desde a última verificação:");
        det.InsertRange(1, novos.Select(n => "  + " + n.Texto));
        return Res(StatusTarefa.Aviso,
            $"{novos.Count} programa(s) novo(s) passou(aram) a iniciar com o Windows. Se não reconhecer, desative no Gerenciador de Tarefas > Aplicativos de inicialização.",
            det);
    }
}
