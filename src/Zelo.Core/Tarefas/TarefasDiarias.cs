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
                cheias.Add($"{d.Name.TrimEnd('\\')} quase cheia ({uso:0}%)");
            }
            else if (uso >= ctx.Config.LimiteAvisoDisco)
            {
                status = Pior(status, StatusTarefa.Aviso);
                cheias.Add($"{d.Name.TrimEnd('\\')} com {uso:0}% em uso");
            }
        }

        var resumo = cheias.Count > 0 ? string.Join("; ", cheias) + "." : "Espaço livre adequado em todas as unidades.";
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
        $ini = (Get-Date).AddHours(-24)
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
        var dados = await Executor.PowerShellJsonAsync(Script, ct);
        var total = (int)(dados.Num("Total") ?? 0);
        var det = new List<string>();
        var graves = new List<string>();
        var importantes = new List<string>();

        foreach (var g in dados.Itens("Grupos"))
        {
            var fonte = g.Str("Fonte");
            var id = (int)(g.Num("Id") ?? 0);
            var qtd = (int)(g.Num("Qtd") ?? 0);
            det.Add($"{qtd}× {fonte} (ID {id}): {g.Str("Msg")}");
            var grave = Graves.FirstOrDefault(x => x.id == id && x.fonte.Equals(fonte, StringComparison.OrdinalIgnoreCase));
            if (grave.fonte is not null) graves.Add($"{qtd}× {grave.explicacao}");
            var imp = Importantes.FirstOrDefault(x => x.id == id && x.fonte.Equals(fonte, StringComparison.OrdinalIgnoreCase));
            if (imp.fonte is not null) importantes.Add(imp.explicacao);
        }

        if (graves.Count > 0)
            return Res(StatusTarefa.Critico, string.Join("; ", graves) + ".", det);
        if (importantes.Count > 0)
        {
            det.Insert(0, "Dica: aumente o espaço da Restauração do Sistema em Proteção do Sistema > Configurar (ex.: 5–10% do disco).");
            return Res(StatusTarefa.Aviso, string.Join("; ", importantes.Distinct()) + ".", det);
        }
        if (total > 25)
            return Res(StatusTarefa.Aviso, $"{total} erros registrados nas últimas 24h (nenhum grave identificado).", det);
        return Res(StatusTarefa.Ok, total == 0 ? "Nenhum erro registrado." : $"{total} erro(s) comum(ns), nada grave.", det);
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
        foreach (var pasta in pastas.Where(Directory.Exists))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var arq in Directory.EnumerateFiles(pasta, "*", Varredura()))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(arq);
                    if (fi.LastWriteTime > limite || fi.CreationTime > limite) continue;
                    var tam = fi.Length;
                    if (!ctx.Simulacao)
                    {
                        if (fi.IsReadOnly) fi.IsReadOnly = false;
                        fi.Delete();
                    }
                    bytes += tam; apagados++;
                }
                catch { emUso++; }
            }
            if (!ctx.Simulacao) ApagarPastasVazias(pasta, limite);
        }

        var verbo = ctx.Simulacao ? "seriam liberados" : "liberados";
        var det = new List<string> { $"Pastas verificadas: {pastas.Count(Directory.Exists)}", $"Arquivos com mais de {ctx.Config.DiasTemporarios} dias: {apagados}" };
        if (emUso > 0) det.Add($"{emUso} arquivo(s) em uso foram mantidos.");
        return Task.FromResult(Res(StatusTarefa.Ok, $"{ctx.Sim}{Textos.Tamanho(bytes)} {verbo} ({apagados} arquivos).", det));
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
                        Directory.Delete(d);
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
            long bytesPerfil = 0; int qtdPerfil = 0;
            foreach (var arq in Directory.EnumerateFiles(pasta, "*", Varredura()))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(arq);
                    // CreationTime = quando o arquivo chegou na pasta; LastWriteTime protege o que foi editado depois.
                    if (fi.CreationTime > limite || fi.LastWriteTime > limite) continue;
                    var tam = fi.Length;
                    if (!ctx.Simulacao)
                    {
                        if (fi.IsReadOnly) fi.IsReadOnly = false;
                        fi.Delete();
                    }
                    bytesPerfil += tam; qtdPerfil++;
                }
                catch { emUso++; }
            }
            if (!ctx.Simulacao) LimpezaTemporarios.ApagarPastasVazias(pasta, limite);
            if (qtdPerfil > 0) det.Add($"{Path.GetFileName(perfil)}: {qtdPerfil} arquivo(s), {Textos.Tamanho(bytesPerfil)}");
            bytes += bytesPerfil; apagados += qtdPerfil;
        }

        if (emUso > 0) det.Add($"{emUso} arquivo(s) em uso foram mantidos.");
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
    public override string Descricao => "Copia para o destino de backup os arquivos novos ou alterados das pastas escolhidas (nada é apagado no destino).";
    public override Categoria Categoria => Categoria.Backup;
    public override Rotina[] Rotinas => new[] { Rotina.Diaria };
    public override TimeSpan Limite => TimeSpan.FromHours(3);

    internal sealed class EstadoBackup { public DateTime? UltimoSucesso { get; set; } }

    internal static string PastaEspelho(string destino, string origem)
    {
        var nome = origem.TrimEnd('\\').Replace(":", "").Replace('\\', '_');
        return Path.Combine(destino, "Zelo-Backup", nome);
    }

    protected override async Task<ResultadoTarefa> ExecutarInternoAsync(Contexto ctx, CancellationToken ct)
    {
        var c = ctx.Config;
        if (!c.BackupConfigurado)
            return Res(StatusTarefa.Aviso, "Backup não configurado: escolha as pastas e o destino nas Configurações.");

        var raizDestino = Path.GetPathRoot(c.DestinoBackup);
        if (string.IsNullOrEmpty(raizDestino) || !Directory.Exists(raizDestino))
            return ResultadoSemDestino($"Destino {c.DestinoBackup} indisponível (disco externo desconectado?).");

        var det = new List<string>();
        var falhas = 0;
        var robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");

        foreach (var origem in c.PastasBackup)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(origem)) { det.Add($"Pasta não encontrada: {origem}"); falhas++; continue; }
            var destino = PastaEspelho(c.DestinoBackup, origem);
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

        if (falhas > 0)
            return Res(StatusTarefa.Aviso, $"{falhas} de {c.PastasBackup.Count} pasta(s) com problema no backup.", det);
        return Res(StatusTarefa.Ok, $"{ctx.Sim}Backup de {c.PastasBackup.Count} pasta(s) concluído.", det);
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
