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

    /// <summary>Um programa com atualização disponível, lido da tabela do "winget upgrade".</summary>
    internal sealed record PacoteWinget(string Nome, string Id, string Versao, string Disponivel);

    private static readonly TimeSpan LimitePorPrograma = TimeSpan.FromMinutes(15);

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var winget = LocalizarWinget();
        if (winget is null)
            return Res(StatusTarefa.Ignorada, "winget não encontrado. Instale o \"Instalador de Aplicativo\" pela Microsoft Store.");
        ctx.Log($"   winget: usando {winget}");

        // 1) Lista o que tem atualização. Um programa por vez depois: um instalador travado não prende os outros.
        var lista = await Executor.ProcessoAsync(winget, "upgrade --accept-source-agreements --disable-interactivity", ct, System.Text.Encoding.UTF8);
        var pacotes = LerTabela(lista.Saida);
        if (pacotes is null)
        {
            if (lista.Codigo == 0 || unchecked((uint)lista.Codigo) == 0x8A15002B)
                return Res(StatusTarefa.Ok, "Todos os programas já estavam atualizados.");
            var detLista = new List<string> { $"Código 0x{lista.Codigo:X8}: {ExplicarCodigo(lista.Codigo)}" };
            detLista.AddRange(lista.Linhas(15));
            foreach (var l in detLista) ctx.Log("   winget: " + l);
            return Res(StatusTarefa.Aviso, "Não foi possível listar as atualizações: " + ExplicarCodigo(lista.Codigo), detLista);
        }
        ctx.Log($"   winget: {pacotes.Count} programa(s) com atualização: {string.Join(", ", pacotes.Select(p => p.Id))}");
        if (pacotes.Count == 0) return Res(StatusTarefa.Ok, "Todos os programas já estavam atualizados.");

        var componentes = pacotes.Where(p => Frameworks.Any(fw => p.Id.StartsWith(fw, StringComparison.OrdinalIgnoreCase))).ToList();
        var programas = pacotes.Except(componentes).ToList();
        if (ctx.Simulacao)
            return Res(StatusTarefa.Ok, $"[Simulação] {programas.Count} programa(s) com atualização.",
                programas.Select(p => $"{p.Nome}: {p.Versao} → {p.Disponivel}").ToList());

        // 2) Atualiza um por um, com limite de tempo para cada.
        var det = new List<string>();
        int ok = 0;
        var falhas = new List<string>();
        foreach (var p in programas)
        {
            ct.ThrowIfCancellationRequested();
            ctx.Log($"   winget: atualizando {p.Nome} [{p.Id}] {p.Versao} → {p.Disponivel}");
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(LimitePorPrograma);
            SaidaProcesso r;
            try
            {
                r = await Executor.ProcessoAsync(winget,
                    $"upgrade --id \"{p.Id}\" --exact --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
                    limite.Token, System.Text.Encoding.UTF8);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var msg = $"{p.Nome}: passou de {LimitePorPrograma.TotalMinutes:0} minutos e foi interrompido. O instalador provavelmente abriu uma janela, que o serviço não consegue mostrar.";
                falhas.Add(msg);
                ctx.Log("   winget: " + msg);
                continue;
            }

            if (r.Codigo == 0 || InstalouComSucesso(r.Saida) || unchecked((uint)r.Codigo) is 0x8A150109 or 0x8A15010A)
            {
                ok++;
                det.Add($"Atualizado: {p.Nome} ({p.Versao} → {p.Disponivel})"
                        + (unchecked((uint)r.Codigo) is 0x8A150109 or 0x8A15010A ? " — termina depois de reiniciar o PC" : ""));
                ctx.Log($"   winget: {p.Nome} atualizado.");
            }
            else
            {
                var motivo = ExplicarCodigo(r.Codigo);
                falhas.Add($"{p.Nome}: {motivo} (código 0x{r.Codigo:X8})");
                ctx.Log($"   winget: {p.Nome} falhou: {motivo} (0x{r.Codigo:X8})");
                foreach (var l in r.Linhas(8)) ctx.Log("      " + l);
            }
        }

        det.AddRange(falhas.Select(f => "FALHOU: " + f));
        det.AddRange(componentes.Select(c => $"Ignorado: {c.Nome} — componente do Windows, atualizado pela Microsoft Store"));
        if (falhas.Count > 0)
        {
            det.Add("Os detalhes de cada falha estão em Registro (no menu do Zelo).");
            det.Add("Esses programas podem ser atualizados abrindo o próprio programa, ou pelo Terminal com \"winget upgrade --all\".");
            return Res(StatusTarefa.Aviso, $"{ok} programa(s) atualizado(s); {falhas.Count} não puderam ser atualizados.", det);
        }
        return Res(StatusTarefa.Ok, $"{ok} programa(s) atualizado(s).", det);
    }

    private static readonly string[] Frameworks = { "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.WindowsAppRuntime", "Microsoft.DesktopAppInstaller" };

    internal static bool InstalouComSucesso(string saida) =>
        saida.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase)
        || saida.Contains("instalado com êxito", StringComparison.OrdinalIgnoreCase)
        || saida.Contains("instalado com sucesso", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lê a tabela do "winget upgrade" (Nome, Id, Versão, Disponível, Origem), em qualquer idioma:
    /// acha o cabeçalho pela linha de traços logo abaixo e corta as colunas pelas posições dele.
    /// null = não havia tabela (nenhuma atualização, ou erro).
    /// </summary>
    internal static List<PacoteWinget>? LerTabela(string saida)
    {
        // Barras de progresso reescrevem a linha com \r: vale o que ficou por último.
        var linhas = saida.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Contains('\r') ? l[(l.LastIndexOf('\r') + 1)..] : l).ToList();
        int h = -1;
        for (int i = 0; i + 1 < linhas.Count; i++)
            if (System.Text.RegularExpressions.Regex.IsMatch(linhas[i], @"(^|\s)Id\s") && linhas[i + 1].Trim().StartsWith("---"))
            { h = i; break; }
        if (h < 0) return null;

        var colunas = System.Text.RegularExpressions.Regex.Matches(linhas[h], @"\S+").Select(m => m.Index).ToList();
        int iId = colunas.FindIndex(c => linhas[h][c..].StartsWith("Id"));
        if (iId < 1 || colunas.Count < iId + 3) return null;
        int cId = colunas[iId], cVer = colunas[iId + 1], cDisp = colunas[iId + 2];
        int cFim = colunas.Count > iId + 3 ? colunas[iId + 3] : int.MaxValue;

        string Corte(string l, int de, int ate) => de >= l.Length ? "" : l[de..Math.Min(ate, l.Length)].Trim();
        var pacotes = new List<PacoteWinget>();
        for (int j = h + 2; j < linhas.Count; j++)
        {
            var l = linhas[j];
            if (string.IsNullOrWhiteSpace(l)) continue;
            var id = Corte(l, cId, cVer);
            var versao = Corte(l, cVer, cDisp);
            var disp = Corte(l, cDisp, cFim);
            // A linha de rodapé ("2 atualizações disponíveis.") não tem um Id válido nessa coluna.
            if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[\w.+-]{2,}$") || versao.Length == 0 || disp.Length == 0) continue;
            pacotes.Add(new PacoteWinget(Corte(l, 0, cId), id, versao, disp));
        }
        return pacotes;
    }

    /// <summary>O que significa cada código do winget, em português.</summary>
    internal static string ExplicarCodigo(int codigo) => unchecked((uint)codigo) switch
    {
        0 => "ok",
        0x8A15002B => "nenhuma atualização aplicável",
        0x8A15000F => "a lista de programas do winget está incompleta (rode \"winget source reset --force\" num Terminal como administrador)",
        0x8A150011 => "o instalador baixado não confere com o esperado (hash diferente); tente de novo mais tarde",
        0x8A150101 => "o programa está aberto; feche-o e tente de novo",
        0x8A150102 => "outra instalação já está em andamento",
        0x8A150103 => "um arquivo do programa está em uso",
        0x8A150104 => "falta um componente que o programa precisa",
        0x8A150105 => "falta espaço no disco",
        0x8A150106 => "falta memória",
        0x8A150107 => "sem conexão com a internet",
        0x8A150108 => "o instalador pediu para contatar o suporte do fabricante",
        0x8A150109 or 0x8A15010A => "precisa reiniciar o PC",
        0x8A15010C => "a instalação foi cancelada",
        0x8A15010D => "a versão nova já está instalada",
        0x8A15010E => "a versão disponível é mais antiga que a instalada",
        0x8A15010F => "bloqueado por uma política do PC",
        0xC0000135 => "o winget não consegue rodar como serviço neste PC (falta um componente do Windows; atualize o \"Instalador de Aplicativo\" pela Microsoft Store)",
        0xFFFFFFFF => "o instalador do programa falhou sem dizer o motivo",
        _ => "o instalador do programa devolveu um erro",
    };
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
        long bytesLix = 0;
        if (ctx.Config.EsvaziarLixeira)
        {
            (bytesLix, var itensLix) = LimparLixeira(ctx, ct);
            det.Add($"Lixeira: {itensLix} item(ns) com mais de {ctx.Config.DiasLixeira} dias ({Textos.Tamanho(bytesLix)})");
        }
        else det.Add("Lixeira: desligada nos Ajustes.");

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

                    var pastaSid = Path.GetDirectoryName(info)!;
                    var dados = Path.Combine(pastaSid, "$R" + Path.GetFileName(info)[2..]);
                    if (!ctx.Simulacao)
                    {
                        // A pasta de cada conta na Lixeira pertence a essa conta: tudo é conferido antes de sair.
                        if (!ArquivosSeguros.PastaConfiavel(pastaSid)) continue;
                        bool ok = Directory.Exists(dados) && (File.GetAttributes(dados) & FileAttributes.ReparsePoint) == 0
                            ? ArquivosSeguros.ApagarPasta(dados, pastaSid)
                            : !File.Exists(dados) && !Directory.Exists(dados) || ArquivosSeguros.Apagar(dados, pastaSid);
                        if (!ok || !ArquivosSeguros.Apagar(info, pastaSid)) continue;
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
                if (!Directory.Exists(raiz) || !ArquivosSeguros.PastaConfiavel(raiz)) continue;
                foreach (var perfilNav in Directory.EnumerateDirectories(raiz, "*", Varredura(false)))
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
                            if (!ctx.Simulacao && !ArquivosSeguros.Apagar(arq, raiz)) continue;
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

    /// <summary>Tira o executável de uma linha de comando ("C:\x\a.exe" --flag → C:\x\a.exe).</summary>
    internal static string? Executavel(string comando)
    {
        comando = Environment.ExpandEnvironmentVariables(comando.Trim());
        if (comando.StartsWith('"'))
        {
            int fim = comando.IndexOf('"', 1);
            return fim > 1 ? comando[1..fim] : null;
        }
        int exe = comando.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? comando[..(exe + 4)] : comando.Split(' ')[0];
    }

    private static string Descrever(string nome, string usuario, string comando)
    {
        var exe = Executavel(comando);
        string data = "";
        try
        {
            if (exe is not null && File.Exists(exe))
                data = $" · instalado em {File.GetCreationTime(exe):dd/MM/yyyy}";
        }
        catch { }
        var quem = usuario.Contains("Public", StringComparison.OrdinalIgnoreCase) || usuario.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) || usuario.Length == 0
            ? "todos os usuários" : usuario;
        return $"{nome} ({quem}){data} — {exe ?? comando}";
    }

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dados = await Executor.PowerShellJsonAsync(Script, ct);
        var atuais = dados.Itens()
            .Select(i => new { Chave = i.Str("Nome") + "|" + i.Str("Comando"), Nome = i.Str("Nome"), Texto = Descrever(i.Str("Nome"), i.Str("Usuario"), i.Str("Comando")) })
            .GroupBy(x => x.Chave).Select(g => g.First())
            .OrderBy(x => x.Nome, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var anterior = Estado.Ler<Snapshot>("inicializacao");
        if (!ctx.Simulacao) Estado.Gravar("inicializacao", new Snapshot { Itens = atuais.Select(a => a.Chave).ToList() });

        var novos = anterior is null ? new() : atuais.Where(a => !anterior.Itens.Contains(a.Chave)).ToList();
        var det = new List<string>();
        if (novos.Count > 0)
        {
            det.Add("Novos desde a última revisão:");
            det.AddRange(novos.Select(n => "NOVO · " + n.Texto));
            det.Add("Todos os que abrem com o Windows:");
        }
        det.AddRange(atuais.Select(a => a.Texto));

        if (anterior is null)
            return Res(StatusTarefa.Ok, $"{atuais.Count} programa(s) abrem com o Windows (primeira análise).", det);
        if (novos.Count == 0)
            return Res(StatusTarefa.Ok, $"{atuais.Count} programa(s) abrem com o Windows, nenhum novo.", det);

        var nomes = string.Join(", ", novos.Take(4).Select(n => n.Nome)) + (novos.Count > 4 ? $" e mais {novos.Count - 4}" : "");
        return Res(StatusTarefa.Aviso,
            (novos.Count == 1 ? "Um programa novo passou a abrir com o Windows: " : $"{novos.Count} programas novos passaram a abrir com o Windows: ") + nomes + ".",
            det);
    }
}
