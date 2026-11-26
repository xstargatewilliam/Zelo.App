using Microsoft.Win32;

namespace Zelo.Core.Tarefas;

// ---------------------------------------------------------------- Defender
public sealed class DefenderRapida : Tarefa
{
    public override string Id => "defender-rapida";
    public override string Nome => "Antivírus: atualização e verificação rápida";
    public override string Descricao => "Atualiza as definições do Microsoft Defender e roda uma verificação rápida.";
    public override Categoria Categoria => Categoria.Seguranca;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };
    public override TimeSpan Limite => TimeSpan.FromMinutes(60);

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var det = new List<string>();
        var av = await Antivirus.LerAsync(ct);

        if (!av.DefenderEmUso)
        {
            if (av.OutrosAtivos.Count > 0)
                return Res(StatusTarefa.Ok,
                    $"Protegido por {string.Join(", ", av.OutrosAtivos)}. O Defender fica desligado quando outro antivírus está instalado — as verificações ficam por conta dele.");
            return Res(StatusTarefa.Critico, "Nenhum antivírus ativo: o Microsoft Defender está desligado e nenhum outro antivírus foi encontrado.",
                new[] { Antivirus.DicaSemAntivirus });
        }

        if (!ctx.Simulacao)
        {
            await Executor.PowerShellAsync("Update-MpSignature", ct); // se falhar, o scan ainda vale com as definições atuais
            var scan = await Executor.PowerShellAsync("Start-MpScan -ScanType QuickScan", ct);
            if (scan.Codigo != 0)
                det.Add("Verificação rápida não concluiu: " + string.Join(" ", scan.Linhas(2)));
            av = await Antivirus.LerAsync(ct); // relê idade das definições e ameaças após o scan
        }

        det.Add($"Proteção em tempo real: {(av.TempoReal ? "ligada" : "DESLIGADA")}");
        det.Add($"Definições: versão {av.Versao}, {av.Idade} dia(s) de idade");
        if (av.Ameacas > 0) det.Add($"{av.Ameacas} ameaça(s) detectada(s) nas últimas 24h — veja em Segurança do Windows > Histórico de proteção.");

        if (!av.TempoReal)
            return Res(StatusTarefa.Critico, "Proteção em tempo real do Defender está desligada.", det.Append(Antivirus.DicaSemAntivirus));
        if (av.Ameacas > 0)
            return Res(StatusTarefa.Critico, $"{av.Ameacas} ameaça(s) encontrada(s) nas últimas 24h.", det);
        if (av.Idade > 3)
            return Res(StatusTarefa.Aviso, $"Definições do antivírus com {av.Idade} dias.", det);
        return Res(det.Any(d => d.StartsWith("Verificação")) ? StatusTarefa.Aviso : StatusTarefa.Ok,
            ctx.Sim + "Antivírus ativo e atualizado, nenhuma ameaça.", det);
    }
}

// ---------------------------------------------------------------- Saúde do disco
public sealed class SaudeDisco : Tarefa
{
    public override string Id => "saude-disco";
    public override string Nome => "Saúde dos discos (SMART)";
    public override string Descricao => "Lê o estado de saúde, desgaste, temperatura e erros de cada SSD/HD.";
    public override Categoria Categoria => Categoria.Disco;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };

    private const string Script = """
        $r = foreach ($d in Get-PhysicalDisk) {
          $c = $null
          try { $c = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch {}
          [pscustomobject]@{
            Nome = [string]$d.FriendlyName
            Tipo = [string]$d.MediaType
            Tamanho = [double]$d.Size
            Saude = [string]$d.HealthStatus
            Desgaste = $c.Wear
            Temperatura = $c.Temperature
            ErrosLeitura = $c.ReadErrorsUncorrected
            ErrosEscrita = $c.WriteErrorsUncorrected
            Horas = $c.PowerOnHours
          }
        }
        ConvertTo-Json -InputObject @($r) -Compress
        """;

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dados = await Executor.PowerShellJsonAsync(Script, ct);
        var det = new List<string>();
        var status = StatusTarefa.Ok;
        var problemas = new List<string>();

        foreach (var d in dados.Itens())
        {
            var nome = d.Str("Nome");
            var saude = d.Str("Saude");
            var desgaste = d.Num("Desgaste");
            var temp = d.Num("Temperatura");
            var erros = (d.Num("ErrosLeitura") ?? 0) + (d.Num("ErrosEscrita") ?? 0);
            var tam = Textos.Tamanho((long)(d.Num("Tamanho") ?? 0));

            var linha = $"{nome} ({d.Str("Tipo")}, {tam}): saúde {saude}";
            if (desgaste is not null) linha += $", desgaste {desgaste:0}%";
            if (temp is > 0) linha += $", {temp:0} °C";
            if (d.Num("Horas") is double h and > 0) linha += $", {h:0} h ligado";
            if (erros > 0) linha += $", {erros:0} erro(s) não corrigido(s)";
            det.Add(linha);

            if (saude.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || erros > 0 || desgaste >= 90)
            {
                status = StatusTarefa.Critico;
                problemas.Add($"{nome} com sinais de falha — faça backup já");
            }
            else if (saude.Equals("Warning", StringComparison.OrdinalIgnoreCase) || desgaste >= 70 || temp >= 65)
            {
                if (status == StatusTarefa.Ok) status = StatusTarefa.Aviso;
                problemas.Add($"{nome} merece atenção");
            }
        }

        if (det.Count == 0) return Res(StatusTarefa.Aviso, "Nenhum disco físico encontrado para análise.");
        var resumo = problemas.Count > 0 ? string.Join("; ", problemas) + "." : $"{det.Count} disco(s) saudável(is).";
        return Res(status, resumo, det);
    }
}

// ---------------------------------------------------------------- Espaço em disco
public sealed class EspacoDisco : Tarefa
{
    public override string Id => "espaco-disco";
    public override string Nome => "Espaço livre em disco";
    public override string Descricao => "Avisa quando uma unidade está ficando cheia.";
    public override Categoria Categoria => Categoria.Disco;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };

    protected override Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var det = new List<string>();
        var status = StatusTarefa.Ok;
        var cheias = new List<string>();
        var sistema = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";

        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            if (d.TotalSize <= 0) continue;
            var uso = 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
            det.Add($"{d.Name} {Textos.Tamanho(d.TotalFreeSpace)} livres de {Textos.Tamanho(d.TotalSize)} ({uso:0}% em uso)");

            // A unidade do sistema pesa mais: sem espaço, o Windows nem atualiza.
            bool ehSistema = d.Name.Equals(sistema, StringComparison.OrdinalIgnoreCase);
            if (uso >= ctx.Config.LimiteCriticoDisco)
            {
                status = ehSistema ? StatusTarefa.Critico : Pior(status, StatusTarefa.Aviso);
                cheias.Add($"O disco {d.Name.TrimEnd('\\')} está praticamente cheio: restam {Textos.Tamanho(d.TotalFreeSpace)} ({uso:0}% em uso)");
            }
            else if (uso >= ctx.Config.LimiteAvisoDisco)
            {
                status = Pior(status, StatusTarefa.Aviso);
                cheias.Add($"O disco {d.Name.TrimEnd('\\')} está quase cheio: restam {Textos.Tamanho(d.TotalFreeSpace)} ({uso:0}% em uso)");
            }
        }

        var resumo = cheias.Count > 0 ? string.Join("; ", cheias) + "." : "Espaço livre de sobra em todos os discos.";
        return Task.FromResult(Res(status, resumo, det));
    }
}

// ---------------------------------------------------------------- Eventos críticos
public sealed class EventosCriticos : Tarefa
{
    public override string Id => "eventos-criticos";
    public override string Nome => "Erros graves do sistema (últimas 24h)";
    public override string Descricao => "Procura desligamentos inesperados, telas azuis e falhas de disco no Visualizador de Eventos.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };

    private const string Script = """
        $ini = (Get-Date).AddHours(-{{HORAS}})
        $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Level = 1,2; StartTime = $ini } -ErrorAction SilentlyContinue)
        $r = $ev | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object {
          $e = $_.Group[0]
          $msg = ''
          if ($e.Message) { $msg = (($e.Message -split "`r?`n")[0]) }
          if ($msg.Length -gt 160) { $msg = $msg.Substring(0, 160) + '...' }
          [pscustomobject]@{ Fonte = [string]$e.ProviderName; Id = [int]$e.Id; Qtd = [int]$_.Count; Msg = $msg }
        }
        [pscustomobject]@{ Total = $ev.Count; Grupos = @($r) } | ConvertTo-Json -Compress -Depth 4
        """;

    // Eventos que indicam problema real (e não ruído comum do Windows).
    private static readonly (string fonte, int id, string explicacao)[] Graves =
    {
        ("Microsoft-Windows-Kernel-Power", 41, "desligamento inesperado (queda de energia, travamento ou botão)"),
        ("Microsoft-Windows-WER-SystemErrorReporting", 1001, "tela azul (BSOD)"),
        ("BugCheck", 1001, "tela azul (BSOD)"),
        ("disk", 7, "setor defeituoso no disco"),
        ("disk", 51, "erro de paginação no disco"),
        ("disk", 153, "operações de disco repetidas (disco lento ou falhando)"),
        ("Ntfs", 55, "estrutura do sistema de arquivos corrompida"),
        ("Microsoft-Windows-Ntfs", 55, "estrutura do sistema de arquivos corrompida"),
        ("Microsoft-Windows-Ntfs", 98, "volume precisa de verificação"),
        ("stornvme", 11, "erro no controlador NVMe"),
    };

    // Não são falhas de hardware, mas merecem atenção.
    private static readonly (string fonte, int id, string explicacao)[] Importantes =
    {
        ("Volsnap", 25, "pontos de restauração apagados por falta de espaço reservado"),
        ("Volsnap", 36, "pontos de restauração apagados por falta de espaço reservado"),
    };

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        // Depois que a pessoa dispensa o alerta, só contam eventos mais novos que a dispensa.
        double horas = 24;
        if (Dispensas.De(Id) is { } dispensa) horas = Math.Clamp((DateTime.Now - dispensa).TotalHours, 0.05, 24);
        var dados = await Executor.PowerShellJsonAsync(Script.Replace("{{HORAS}}", horas.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)), ct);
        var total = (int)(dados.Num("Total") ?? 0);
        var det = new List<string>();
        var grupos = dados.Itens("Grupos").Select(g => (Fonte: g.Str("Fonte"), Id: (int)(g.Num("Id") ?? 0), Qtd: (int)(g.Num("Qtd") ?? 0), Msg: g.Str("Msg"))).ToList();
        foreach (var g in grupos) det.Add($"{g.Qtd}× {g.Fonte} (ID {g.Id}): {g.Msg}");
        var (status, resumo, dica) = Avaliar(grupos.Select(g => (g.Fonte, g.Id, g.Qtd)), total);
        if (dica is not null) det.Insert(0, dica);
        return Res(status, resumo, det);
    }

    /// <summary>Até 2 desligamentos inesperados em 24h sem tela azul: aviso (pode ter sido falta de luz ou o botão).</summary>
    internal const int DesligamentosParaCritico = 3;

    internal static (StatusTarefa Status, string Resumo, string? Dica) Avaliar(IEnumerable<(string Fonte, int Id, int Qtd)> grupos, int total)
    {
        var graves = new List<string>();
        var avisos = new List<string>();
        var importantes = new List<string>();
        foreach (var (fonte, id, qtd) in grupos)
        {
            var grave = Graves.FirstOrDefault(x => x.id == id && x.fonte.Equals(fonte, StringComparison.OrdinalIgnoreCase));
            if (grave.fonte is not null)
            {
                bool desligamento = grave.fonte == "Microsoft-Windows-Kernel-Power" && grave.id == 41;
                (desligamento && qtd < DesligamentosParaCritico ? avisos : graves).Add($"{qtd}× {grave.explicacao}");
            }
            var imp = Importantes.FirstOrDefault(x => x.id == id && x.fonte.Equals(fonte, StringComparison.OrdinalIgnoreCase));
            if (imp.fonte is not null) importantes.Add(imp.explicacao);
        }

        if (graves.Count > 0)
            return (StatusTarefa.Critico, string.Join("; ", graves.Concat(avisos)) + ".", null);
        if (avisos.Count > 0)
            return (StatusTarefa.Aviso, string.Join("; ", avisos) + ". Se foi você que desligou pelo botão ou faltou luz, pode dispensar este aviso.",
                "Se acontecer de novo sem motivo, pode ser travamento: veja se há tela azul, superaquecimento ou fonte com problema.");
        if (importantes.Count > 0)
            return (StatusTarefa.Aviso, string.Join("; ", importantes.Distinct()) + ".",
                "Dica: aumente o espaço da Restauração do Sistema em Proteção do Sistema > Configurar (ex.: 5–10% do disco).");
        if (total > 25)
            return (StatusTarefa.Aviso, $"{total} erros registrados nas últimas 24h (nenhum grave identificado).", null);
        return (StatusTarefa.Ok, total == 0 ? "Nenhum erro registrado." : $"{total} erro(s) comum(ns), nada grave.", null);
    }
}

// ---------------------------------------------------------------- Temporários
public sealed class LimpezaTemporarios : Tarefa
{
    public override string Id => "limpeza-temp";
    public override string Nome => "Limpeza de arquivos temporários";
    public override string Descricao => "Apaga arquivos temporários antigos do Windows e de cada usuário.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };

    protected override Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var limite = DateTime.Now.AddDays(-ctx.Config.DiasTemporarios);
        var pastas = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
        pastas.AddRange(Perfis.Pastas().Select(p => Path.Combine(p, @"AppData\Local\Temp")));

        long bytes = 0; int apagados = 0, emUso = 0;
        var ignoradas = new List<string>();
        foreach (var pasta in pastas.Where(Directory.Exists))
        {
            ct.ThrowIfCancellationRequested();
            // Uma pasta trocada por atalho levaria o serviço a apagar fora dela: fica de fora.
            if (!ArquivosSeguros.PastaConfiavel(pasta)) { ignoradas.Add(pasta); continue; }
            var (b, q, u) = ApagarAntigos(pasta, limite, ctx.Simulacao, ct);
            bytes += b; apagados += q; emUso += u;
        }

        var verbo = ctx.Simulacao ? "seriam liberados" : "liberados";
        var det = new List<string> { $"Pastas verificadas: {pastas.Count(Directory.Exists) - ignoradas.Count}", $"Arquivos com mais de {ctx.Config.DiasTemporarios} dias: {apagados}" };
        if (emUso > 0) det.Add($"{emUso} arquivo(s) em uso ou fora da pasta foram mantidos.");
        foreach (var p in ignoradas) det.Add($"Ignorada por segurança (é um atalho para outro lugar): {p}");
        return Task.FromResult(Res(StatusTarefa.Ok, $"{ctx.Sim}{Textos.Tamanho(bytes)} {verbo} ({apagados} arquivos).", det));
    }

    /// <summary>
    /// Apaga, dentro de <paramref name="raiz"/>, os arquivos criados e alterados antes de <paramref name="limite"/>.
    /// Cada arquivo é conferido pelo Windows antes de sair (ArquivosSeguros): nada fora da raiz é tocado.
    /// </summary>
    internal static (long bytes, int apagados, int mantidos) ApagarAntigos(string raiz, DateTime limite, bool simulacao, CancellationToken ct)
    {
        long bytes = 0; int apagados = 0, mantidos = 0;
        foreach (var arq in Directory.EnumerateFiles(raiz, "*", Varredura()))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(arq);
                if (fi.LastWriteTime > limite || fi.CreationTime > limite) continue;
                var tam = fi.Length;
                if (!simulacao && !ArquivosSeguros.Apagar(arq, raiz)) { mantidos++; continue; }
                bytes += tam; apagados++;
            }
            catch { mantidos++; }
        }
        if (!simulacao) ApagarPastasVazias(raiz, limite);
        return (bytes, apagados, mantidos);
    }

    internal static void ApagarPastasVazias(string raiz, DateTime limite)
    {
        try
        {
            var dirs = Directory.EnumerateDirectories(raiz, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .OrderByDescending(d => d.Length)
                .ToList();
            foreach (var d in dirs)
            {
                try
                {
                    if (Directory.GetLastWriteTime(d) < limite && !Directory.EnumerateFileSystemEntries(d).Any())
                        ArquivosSeguros.Apagar(d, raiz); // só sai se estiver vazia e dentro da raiz
                }
                catch { }
            }
        }
        catch { }
    }
}

// ---------------------------------------------------------------- Downloads
public sealed class LimpezaDownloads : Tarefa
{
    public override string Id => "limpeza-downloads";
    public override string Nome => "Limpeza da pasta Downloads";
    public override string Descricao => "Opcional: apaga o que está na pasta Downloads há mais tempo que o prazo configurado. Arquivos recentes nunca são tocados.";
    public override Categoria Categoria => Categoria.Sistema;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };
    public override bool Habilitada(Configuracao c) => c.LimparDownloads;

    protected override Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var dias = ctx.Config.DiasDownloads;
        var limite = DateTime.Now.AddDays(-dias);
        long bytes = 0; int apagados = 0, emUso = 0;
        var det = new List<string>();

        foreach (var perfil in Perfis.Pastas())
        {
            var pasta = Path.Combine(perfil, "Downloads");
            if (!Directory.Exists(pasta)) continue;
            if (!ArquivosSeguros.PastaConfiavel(pasta)) { det.Add($"Ignorada por segurança (é um atalho para outro lugar): {pasta}"); continue; }
            // CreationTime = quando o arquivo chegou na pasta; LastWriteTime protege o que foi editado depois.
            var (bytesPerfil, qtdPerfil, mantidos) = LimpezaTemporarios.ApagarAntigos(pasta, limite, ctx.Simulacao, ct);
            emUso += mantidos;
            if (qtdPerfil > 0) det.Add($"{Path.GetFileName(perfil)}: {qtdPerfil} arquivo(s), {Textos.Tamanho(bytesPerfil)}");
            bytes += bytesPerfil; apagados += qtdPerfil;
        }

        if (emUso > 0) det.Add($"{emUso} arquivo(s) em uso ou fora da pasta foram mantidos.");
        var verbo = ctx.Simulacao ? "seriam apagados" : "apagados";
        return Task.FromResult(Res(StatusTarefa.Ok,
            apagados == 0 ? $"Nada com mais de {dias} dias na pasta Downloads."
                          : $"{ctx.Sim}{apagados} arquivo(s) com mais de {dias} dias {verbo} ({Textos.Tamanho(bytes)}).", det));
    }
}

// ---------------------------------------------------------------- Backup
public sealed class BackupIncremental : Tarefa
{
    public override string Id => "backup-incremental";
    public override string Nome => "Backup incremental";
    public override string Descricao => "Copia para o destino de backup os arquivos novos ou alterados das pastas escolhidas. Antes de substituir uma cópia, guarda a versão anterior (por 30 dias, por padrão). Nada que você apagar nas pastas originais é apagado do backup.";
    public override Categoria Categoria => Categoria.Backup;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };
    public override TimeSpan Limite => TimeSpan.FromHours(3);

    internal sealed class EstadoBackup { public DateTime? UltimoSucesso { get; set; } }

    internal static string NomeEspelho(string origem) => origem.TrimEnd('\\').Replace(":", "").Replace('\\', '_');

    internal static string PastaEspelho(string destino, string origem) => Path.Combine(destino, "Zelo-Backup", NomeEspelho(origem));

    /// <summary>Onde ficam as versões anteriores: Zelo-Backup\_versoes\aaaa-MM-dd_HHmm\&lt;pasta&gt;\...</summary>
    public static string PastaVersoes(string destino) => Path.Combine(destino, "Zelo-Backup", "_versoes");

    /// <summary>
    /// Antes do robocopy substituir uma cópia, move a cópia atual para a pasta de versões do dia.
    /// O robocopy (com /XO /FFT) só substitui quando o original é mais novo (tolerância de 2 s)
    /// ou quando o tamanho mudou com a mesma data; a regra aqui é a mesma.
    /// </summary>
    internal static int GuardarVersoes(string origem, string espelho, string pastaDoDia, CancellationToken ct)
    {
        if (!Directory.Exists(espelho)) return 0;
        int movidos = 0;
        foreach (var original in Directory.EnumerateFiles(origem, "*", Varredura()))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(origem, original);
            var copia = Path.Combine(espelho, rel);
            try
            {
                if (!File.Exists(copia)) continue;
                var fo = new FileInfo(original);
                var fc = new FileInfo(copia);
                var diferenca = (fo.LastWriteTimeUtc - fc.LastWriteTimeUtc).TotalSeconds;
                bool vaiSubstituir = diferenca > 2 || (Math.Abs(diferenca) <= 2 && fo.Length != fc.Length);
                if (!vaiSubstituir) continue;
                var destinoVersao = Path.Combine(pastaDoDia, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(destinoVersao)!);
                File.Move(copia, destinoVersao, overwrite: true);
                movidos++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return movidos;
    }

    /// <summary>Apaga as versões com mais de N dias (só dentro da pasta de versões do próprio Zelo).</summary>
    internal static int LimparVersoes(string destino, int dias)
    {
        var raiz = PastaVersoes(destino);
        if (dias <= 0 || !Directory.Exists(raiz) || !ArquivosSeguros.PastaConfiavel(raiz)) return 0;
        int apagadas = 0;
        foreach (var pasta in Directory.EnumerateDirectories(raiz))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(pasta), "yyyy-MM-dd_HHmm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var quando)) continue;
            if (quando >= DateTime.Now.AddDays(-dias)) continue;
            if (ArquivosSeguros.ApagarPasta(pasta, raiz)) apagadas++;
        }
        return apagadas;
    }

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var c = ctx.Config;
        if (!c.BackupConfigurado)
            return Res(StatusTarefa.Aviso, "Seus arquivos ainda não têm cópia. Escolha as pastas e onde guardar em Ajustes.");

        var raizDestino = Path.GetPathRoot(c.DestinoBackup);
        if (string.IsNullOrEmpty(raizDestino) || !Directory.Exists(raizDestino))
            return ResultadoSemDestino($"Destino {c.DestinoBackup} indisponível (disco externo desconectado?).");

        // O serviço escreve como SYSTEM no destino: nenhum trecho dele pode ser atalho para outro lugar.
        var raizZelo = Path.Combine(c.DestinoBackup, "Zelo-Backup");
        if (!ctx.Simulacao)
        {
            try
            {
                Directory.CreateDirectory(c.DestinoBackup);
                if (!ArquivosSeguros.PastaConfiavel(c.DestinoBackup))
                    return Res(StatusTarefa.Critico, "Por segurança, o backup não rodou: o destino é um atalho para outra pasta. Escolha o destino de novo em Ajustes.");
                Directory.CreateDirectory(raizZelo);
                ProtegerDestino(raizZelo);
                if (ArquivosSeguros.PrimeiroAtalho(raizZelo) is { } atalho)
                    return Res(StatusTarefa.Critico, "Por segurança, o backup não rodou: alguém criou um atalho de pasta dentro do backup.",
                        new List<string> { "Atalho encontrado: " + atalho, "Apague esse atalho (não a pasta de destino dele) e rode o backup de novo." });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ResultadoSemDestino($"Não foi possível usar o destino {c.DestinoBackup}: {ex.Message}");
            }
        }

        var det = new List<string>();
        var falhas = 0;
        var versoes = 0;
        var robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        var pastaDoDia = Path.Combine(PastaVersoes(c.DestinoBackup), DateTime.Now.ToString("yyyy-MM-dd_HHmm"));

        foreach (var origem in c.PastasBackup)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(origem)) { det.Add($"Pasta não encontrada: {origem}"); falhas++; continue; }
            var destino = PastaEspelho(c.DestinoBackup, origem);
            if (!ctx.Simulacao && c.DiasVersoesBackup > 0)
            {
                try { versoes += GuardarVersoes(origem, destino, Path.Combine(pastaDoDia, NomeEspelho(origem)), ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { det.Add($"{origem}: não foi possível guardar as versões anteriores ({ex.Message})."); }
            }
            // /E subpastas, /XO não sobrescreve mais novos, /XJ ignora junções, /FFT tolera discos FAT/exFAT/NAS.
            var args = $"\"{origem.TrimEnd('\\')}\" \"{destino}\" /E /XO /XJ /FFT /R:1 /W:1 /MT:8 /NP /NDL /NJH";
            if (ctx.Simulacao) args += " /L";
            var r = await Executor.ProcessoAsync(robocopy, args, ct);
            // Robocopy: 0-7 = sucesso (com ou sem cópias), 8+ = falhas.
            if (r.Codigo >= 8)
            {
                falhas++;
                det.Add($"{origem}: falhou (código {r.Codigo}). " + string.Join(" ", r.Linhas(3)));
            }
            else
            {
                det.Add($"{origem} → {destino}: {(r.Codigo == 0 ? "já estava em dia" : "atualizado")}");
            }
        }

        if (falhas == 0 && !ctx.Simulacao)
            Estado.Gravar("backup", new EstadoBackup { UltimoSucesso = DateTime.Now });
        if (!ctx.Simulacao)
        {
            var apagadas = LimparVersoes(c.DestinoBackup, c.DiasVersoesBackup);
            if (versoes > 0) det.Add($"{versoes} versão(ões) anterior(es) guardada(s) em {pastaDoDia}");
            if (apagadas > 0) det.Add($"{apagadas} pasta(s) de versões com mais de {c.DiasVersoesBackup} dias apagada(s).");
            if (c.DiasVersoesBackup > 0) det.Add($"Versões anteriores ficam por {c.DiasVersoesBackup} dias em {PastaVersoes(c.DestinoBackup)}");
        }

        if (falhas > 0)
            return Res(StatusTarefa.Aviso, $"{falhas} de {c.PastasBackup.Count} pasta(s) com problema no backup.", det);
        return Res(StatusTarefa.Ok, $"{ctx.Sim}Backup de {c.PastasBackup.Count} pasta(s) concluído.", det);
    }

    /// <summary>
    /// Num disco NTFS fora das pastas de perfil (disco externo, segundo disco), a pasta Zelo-Backup fica
    /// só com escrita para o sistema e administradores; as outras contas podem ler (para recuperar
    /// arquivos), mas não criar nada dentro dela. Isso impede que alguém plante um atalho para fazer o
    /// serviço escrever fora do backup. Em pastas de perfil (OneDrive, Documentos) nada é mudado.
    /// </summary>
    internal static void ProtegerDestino(string raizZelo)
    {
        try
        {
            var cheio = Path.GetFullPath(raizZelo);
            if (Perfis.Pastas().Any(p => cheio.StartsWith(Path.GetFullPath(p).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return;
            var unidade = new DriveInfo(Path.GetPathRoot(cheio)!);
            if (!string.Equals(unidade.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) return;

            var heranca = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            var nenhuma = System.Security.AccessControl.PropagationFlags.None;
            var permitir = System.Security.AccessControl.AccessControlType.Allow;
            var ds = new System.Security.AccessControl.DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            foreach (var (sid, direito) in new[]
                     {
                         (System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl),
                         (System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl),
                         (System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute),
                     })
                ds.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid, null), direito, heranca, nenhuma, permitir));
            ds.SetOwner(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
            new DirectoryInfo(cheio).SetAccessControl(ds);
        }
        catch (Exception ex)
        {
            Registro.Escrever("Backup: não foi possível proteger as permissões da pasta Zelo-Backup: " + ex.Message);
        }
    }

    private static ResultadoTarefa ResultadoSemDestino(string msg)
    {
        var ultimo = Estado.Ler<EstadoBackup>("backup")?.UltimoSucesso;
        var det = new List<string> { ultimo is null ? "Nenhum backup concluído ainda." : $"Último backup bem-sucedido: {ultimo:dd/MM/yyyy HH:mm}" };
        var status = ultimo is null || ultimo < DateTime.Now.AddDays(-7) ? StatusTarefa.Critico : StatusTarefa.Aviso;
        return Res(status, msg, det);
    }
}

// ---------------------------------------------------------------- Reinício pendente
public sealed class ReinicioPendente : Tarefa
{
    public override string Id => "reinicio-pendente";
    public override string Nome => "Reinicialização pendente";
    public override string Descricao => "Verifica se o Windows está esperando reiniciar para concluir atualizações.";
    public override Categoria Categoria => Categoria.Atualizacoes;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };

    private sealed class EstadoReinicio { public DateTime? DesdeQuando { get; set; } }

    protected override Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        bool cbs = Existe(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        bool wu = Existe(hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");

        var est = Estado.Ler<EstadoReinicio>("reinicio") ?? new EstadoReinicio();
        if (!cbs && !wu)
        {
            if (est.DesdeQuando is not null && !ctx.Simulacao) Estado.Gravar("reinicio", new EstadoReinicio());
            return Task.FromResult(Res(StatusTarefa.Ok, "Nenhuma reinicialização pendente."));
        }

        if (est.DesdeQuando is null)
        {
            est.DesdeQuando = DateTime.Now;
            if (!ctx.Simulacao) Estado.Gravar("reinicio", est);
        }
        var dias = (DateTime.Now - est.DesdeQuando.Value).TotalDays;
        var det = new List<string> { $"Pendente desde {est.DesdeQuando:dd/MM/yyyy HH:mm}", cbs ? "Origem: componentes do Windows" : "Origem: Windows Update" };
        return Task.FromResult(dias >= 3
            ? Res(StatusTarefa.Aviso, $"O Windows espera reiniciar há {dias:0} dias — reinicie para concluir as atualizações.", det)
            : Res(StatusTarefa.Ok, "Reinicialização pendente (normal logo após atualizações).", det));
    }

    private static bool Existe(RegistryKey raiz, string caminho)
    {
        using var k = raiz.OpenSubKey(caminho);
        return k is not null;
    }
}
