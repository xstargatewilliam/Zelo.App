using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zelo.Core;

/// <summary>
/// Atualização automática pela página Releases do GitHub.
/// Só versões publicadas (não cada commit). O pacote é baixado por HTTPS do GitHub,
/// conferido pelo SHA-256 publicado junto e instalado por um script separado,
/// porque o serviço não consegue substituir os próprios arquivos enquanto roda.
/// </summary>
public static class Atualizador
{
    public const string Repositorio = "xstargatewilliam/Zelo.App";
    public static readonly string PaginaReleases = $"https://github.com/{Repositorio}/releases";

    private static readonly SemaphoreSlim Trava = new(1, 1);
    private static readonly HttpClient Http = CriarHttp();

    // Sem seguir redirecionamentos: o endereço "releases/latest" responde com um redirecionamento
    // para a página da versão, e é dele que sai o número da versão.
    private static readonly HttpClient HttpSemRedirecionar = CriarHttp(seguirRedirecionamentos: false);

    public static bool EmAndamento => Trava.CurrentCount == 0;

    private static string Pasta => Path.Combine(Caminhos.Base, "atualizacao");

    private static string NomePacote =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Zelo-win-arm64.zip" : "Zelo-win-x64.zip";

    public static Version VersaoAtual =>
        Normalizar((Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version);

    public sealed record VersaoRemota(Version Versao, string Tag, string UrlPacote, string? UrlHash, string Notas);

    public sealed class EstadoAtualizacao
    {
        public DateTime? UltimaVerificacao { get; set; }
        public string? VersaoDisponivel { get; set; }
        public string? UltimoErro { get; set; }
        public DateTime? UltimaInstalacao { get; set; }
        public string? VersaoInstalada { get; set; }
        /// <summary>Versão do Zelo que fez a última consulta (para saber se o estado é de outra versão).</summary>
        public string? VersaoNaVerificacao { get; set; }
    }

    /// <summary>
    /// Chamado quando o serviço inicia. Se a versão mudou desde a última consulta (atualização automática
    /// ou instalação à mão), o erro e o "versão nova disponível" guardados são de outra versão e somem;
    /// a próxima consulta acontece logo. Uma versão marcada como recusada que agora roda deixa de ser recusada.
    /// </summary>
    public static void ConferirAposIniciar()
    {
        try
        {
            var atual = VersaoAtual;
            var estado = LerEstado();
            if (Conferir(estado, atual)) GravarEstado(estado);
            if (VersaoRecusada() is { } recusada && recusada <= atual)
            {
                File.Delete(ArquivoRecusada);
                Registro.Escrever($"Atualização: a versão {recusada.ToString(3)} agora roda neste PC; a marca de versão recusada foi retirada.");
            }
        }
        catch (Exception ex) { Registro.Escrever("Atualização: não foi possível conferir o estado guardado: " + ex.Message); }
    }

    /// <summary>
    /// A atualização feita por uma versão antiga (até a 1.5.6) só trocava o serviço e o painel. Se o pacote
    /// desta versão ainda está na pasta de atualização (protegida, já conferido pelo SHA-256), copia o
    /// instalador/desinstalador para a pasta do Zelo e cria a entrada em Configurações → Apps.
    /// </summary>
    public static void CompletarInstalador()
    {
        try
        {
            var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zelo", "Instalador", "Zelo.Instalador.exe");
            var noPacote = Path.Combine(Pasta, "extraido", "publicado", "Instalador", "Zelo.Instalador.exe");
            if (File.Exists(dest) || !File.Exists(noPacote)) return;
            var versaoDoPacote = FileVersionInfo.GetVersionInfo(noPacote).ProductVersion?.Split('+')[0];
            if (versaoDoPacote != VersaoAtual.ToString(3)) return; // só o instalador desta mesma versão
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(noPacote, dest);
            using var p = Process.Start(new ProcessStartInfo(dest, "--registrar --automatico") { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit(30_000);
            Registro.Escrever("Atualização: desinstalador instalado; o Zelo agora aparece em Configurações → Apps.");
        }
        catch (Exception ex) { Registro.Escrever("Atualização: não foi possível instalar o desinstalador: " + ex.Message); }
    }

    /// <summary>Limpa o que ficou de outra versão. Devolve true se mudou algo.</summary>
    internal static bool Conferir(EstadoAtualizacao e, Version atual)
    {
        bool mudou = false;
        if (e.VersaoDisponivel is not null && (LerVersao(e.VersaoDisponivel) is not { } disp || disp <= atual))
        {
            e.VersaoDisponivel = null;
            mudou = true;
        }
        if (e.VersaoNaVerificacao != atual.ToString(3) && (e.UltimoErro is not null || e.UltimaVerificacao is not null))
        {
            e.UltimoErro = null;
            e.UltimaVerificacao = null; // consulta de novo logo
            mudou = true;
        }
        return mudou;
    }

    public static EstadoAtualizacao LerEstado() => Estado.Ler<EstadoAtualizacao>("atualizacao") ?? new EstadoAtualizacao();
    private static void GravarEstado(EstadoAtualizacao e) => Estado.Gravar("atualizacao", e);

    private static HttpClient CriarHttp(bool seguirRedirecionamentos = true)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = seguirRedirecionamentos }) { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zelo", "1.0"));
        h.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return h;
    }

    private static Version Normalizar(Version? v) =>
        v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    private static Version? LerVersao(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        int corte = t.IndexOfAny(new[] { '-', '+' });
        if (corte >= 0) t = t[..corte];
        return Version.TryParse(t, out var v) ? Normalizar(v) : null;
    }

    /// <summary>
    /// Consulta a última versão publicada. Null se não houver nenhuma com pacote para este PC.
    /// Usa primeiro a página comum do GitHub ("releases/latest"), que não tem o limite de 60 consultas
    /// por hora da API; a API fica só como reserva.
    /// </summary>
    public static async Task<VersaoRemota?> ConsultarAsync(CancellationToken ct)
    {
        try
        {
            var pelaPagina = await ConsultarPelaPaginaAsync(ct);
            if (pelaPagina is not null) return pelaPagina;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Registro.Escrever("Atualização: a página de versões do GitHub não respondeu (" + ex.Message + "); tentando pela API.");
        }
        return await ConsultarPelaApiAsync(ct);
    }

    private static async Task<VersaoRemota?> ConsultarPelaPaginaAsync(CancellationToken ct)
    {
        using var resp = await HttpSemRedirecionar.GetAsync($"https://github.com/{Repositorio}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        var codigo = (int)resp.StatusCode;
        if (codigo is < 300 or >= 400 || resp.Headers.Location is null)
            throw new InvalidOperationException($"resposta inesperada ({codigo})");
        var tag = TagDoEndereco(resp.Headers.Location.ToString());
        if (tag is null) return null; // sem versão publicada: o GitHub manda para a lista de versões
        var versao = LerVersao(tag);
        if (versao is null) return null;
        var baseUrl = $"https://github.com/{Repositorio}/releases/download/{Uri.EscapeDataString(tag)}/";
        return new VersaoRemota(versao, tag, baseUrl + NomePacote, baseUrl + NomePacote + ".sha256", "");
    }

    /// <summary>Tira a tag de ".../releases/tag/v1.5.4". Null se o endereço não apontar para uma versão.</summary>
    internal static string? TagDoEndereco(string endereco)
    {
        const string marca = "/releases/tag/";
        int i = endereco.IndexOf(marca, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var tag = Uri.UnescapeDataString(endereco[(i + marca.Length)..].Split('?', '#')[0].TrimEnd('/'));
        return tag.Length == 0 || tag.Contains('/') ? null : tag;
    }

    /// <summary>Explica em português os erros comuns ao falar com o GitHub.</summary>
    internal static string ExplicarErro(Exception ex)
    {
        var m = ex.Message;
        if (m.Contains("403") || m.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || m.Contains("429"))
            return "o GitHub limitou as consultas vindas da sua rede por um tempo (é um limite por endereço de internet, comum quando a operadora divide o mesmo endereço entre vários clientes). O Zelo tenta de novo mais tarde.";
        if (ex is HttpRequestException or TaskCanceledException)
            return "não deu para falar com o GitHub (sem internet ou conexão lenta). O Zelo tenta de novo mais tarde.";
        return m;
    }

    private static async Task<VersaoRemota?> ConsultarPelaApiAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repositorio}/releases/latest", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // nenhuma versão publicada ainda
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;

        var tag = r.Str("tag_name");
        var versao = LerVersao(tag);
        if (versao is null || r.Bool("prerelease") == true || r.Bool("draft") == true) return null;

        string? pacote = null, hash = null;
        foreach (var a in r.Itens("assets"))
        {
            var nome = a.Str("name");
            var url = a.Str("browser_download_url");
            if (nome.Equals(NomePacote, StringComparison.OrdinalIgnoreCase)) pacote = url;
            else if (nome.Equals(NomePacote + ".sha256", StringComparison.OrdinalIgnoreCase)) hash = url;
        }
        if (pacote is null) return null;
        return new VersaoRemota(versao, tag, pacote, hash, r.Str("body"));
    }

    /// <summary>Verifica e, se pedido, instala. Devolve uma frase para o log e o painel.</summary>
    public static async Task<string> VerificarEAplicarAsync(bool instalar, CancellationToken ct)
    {
        if (!await Trava.WaitAsync(0, ct)) return "Uma atualização já está em andamento.";
        var estado = LerEstado();
        try
        {
            var atual = VersaoAtual;
            estado.VersaoNaVerificacao = atual.ToString(3);
            var remota = await ConsultarAsync(ct);
            estado.UltimaVerificacao = DateTime.Now;
            estado.UltimoErro = null;

            // Uma versão que já falhou ao iniciar neste PC não é reinstalada sozinha (evita um ciclo de falhas).
            var recusada = VersaoRecusada();
            if (remota is not null && recusada is not null && remota.Versao == recusada)
            {
                estado.VersaoDisponivel = null;
                estado.UltimoErro = $"A versão {recusada.ToString(3)} não iniciou neste PC e foi desfeita. O Zelo espera a próxima versão.";
                GravarEstado(estado);
                return estado.UltimoErro;
            }

            if (remota is null || remota.Versao <= atual)
            {
                estado.VersaoDisponivel = null;
                GravarEstado(estado);
                return $"O Zelo está na versão mais recente ({atual.ToString(3)}).";
            }

            estado.VersaoDisponivel = remota.Versao.ToString(3);
            GravarEstado(estado);
            if (!instalar) return $"Nova versão disponível: {remota.Versao.ToString(3)} (você tem a {atual.ToString(3)}).";

            // A instalação para o serviço: não pode acontecer no meio de uma vistoria ou otimização.
            if (!Coordenador.Iniciar("atualizacao"))
                return $"Nova versão {remota.Versao.ToString(3)} disponível; a instalação fica para depois. {Coordenador.Explicar()}";
            try
            {
                Registro.Escrever($"Baixando a versão {remota.Versao.ToString(3)} de {remota.UrlPacote}");
                var pasta = await PrepararAsync(remota, ct);
                estado.UltimaInstalacao = DateTime.Now;
                estado.VersaoInstalada = remota.Versao.ToString(3);
                GravarEstado(estado);
                Aplicar(pasta, remota.Versao.ToString(3));
            }
            catch
            {
                Coordenador.Encerrar("atualizacao");
                throw;
            }
            // O script de aplicação para e reinicia o serviço. Se por algum motivo isso não acontecer,
            // libera as outras operações depois de 10 minutos.
            _ = Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => Coordenador.Encerrar("atualizacao"));
            return $"Instalando a versão {remota.Versao.ToString(3)}. O Zelo reinicia sozinho em alguns segundos.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            estado.UltimaVerificacao = DateTime.Now;
            estado.VersaoNaVerificacao = VersaoAtual.ToString(3);
            estado.UltimoErro = "A última consulta ao GitHub falhou: " + ExplicarErro(ex);
            GravarEstado(estado);
            Registro.Escrever("Falha na atualização: " + ex.Message);
            return "Não foi possível atualizar: " + ExplicarErro(ex);
        }
        finally
        {
            Trava.Release();
        }
    }

    /// <summary>Marcada pelo script de instalação quando a versão nova não consegue iniciar.</summary>
    public static string ArquivoRecusada => Path.Combine(Caminhos.Estado, "atualizacao-recusada.txt");

    private static Version? VersaoRecusada()
    {
        try { return File.Exists(ArquivoRecusada) ? LerVersao(File.ReadAllText(ArquivoRecusada)) : null; }
        catch { return null; }
    }

    private static void ExigirGitHub(string url)
    {
        var u = new Uri(url);
        if (u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Endereço de download inesperado: {u.Host}");
    }

    private static async Task<string> PrepararAsync(VersaoRemota v, CancellationToken ct)
    {
        ExigirGitHub(v.UrlPacote);
        if (v.UrlHash is null) throw new InvalidOperationException("A versão publicada não tem o arquivo de verificação (.sha256).");
        ExigirGitHub(v.UrlHash);

        if (Directory.Exists(Pasta)) Directory.Delete(Pasta, recursive: true);
        Directory.CreateDirectory(Pasta);
        var zip = Path.Combine(Pasta, NomePacote);

        await using (var arquivo = File.Create(zip))
        await using (var fluxo = await Http.GetStreamAsync(v.UrlPacote, ct))
            await fluxo.CopyToAsync(arquivo, ct);

        // O .sha256 traz "<hash>  <nome>"; vale o primeiro bloco.
        var esperado = (await Http.GetStringAsync(v.UrlHash, ct)).Trim().Split(' ', '\t', '\n', '\r')[0].ToUpperInvariant();
        string obtido;
        await using (var arquivo = File.OpenRead(zip))
            obtido = Convert.ToHexString(await SHA256.HashDataAsync(arquivo, ct));
        if (esperado.Length != 64 || esperado != obtido)
            throw new InvalidOperationException("O pacote baixado não confere com o SHA-256 publicado. Atualização cancelada.");

        var extraido = Path.Combine(Pasta, "extraido");
        ZipFile.ExtractToDirectory(zip, extraido); // recusa entradas que escapem da pasta
        var publicado = Path.Combine(extraido, "publicado");
        if (!File.Exists(Path.Combine(publicado, "Servico", "Zelo.Service.exe")) ||
            !File.Exists(Path.Combine(publicado, "Painel", "Zelo.App.exe")))
            throw new InvalidOperationException("O pacote não tem os arquivos esperados do Zelo.");

        // Assinatura digital: se a versão instalada é assinada, a nova tem que ser do mesmo editor.
        var instalada = Assinatura.Ler(Path.Combine(AppContext.BaseDirectory, "Zelo.Service.exe"));
        var nova = Assinatura.Ler(Path.Combine(publicado, "Servico", "Zelo.Service.exe"));
        if (Assinatura.Conferir(instalada, nova) is { } recusa) throw new InvalidOperationException(recusa);
        Registro.Escrever(nova.Valida
            ? $"Atualização: pacote assinado digitalmente por {nova.Editor}."
            : "Atualização: pacote sem assinatura digital (conferido pelo SHA-256).");
        return publicado;
    }

    /// <summary>Escreve e dispara o script que troca os arquivos com o serviço parado.</summary>
    private static void Aplicar(string publicado, string versao)
    {
        var dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zelo");
        var anterior = Path.Combine(Pasta, "anterior");
        var log = Path.Combine(Caminhos.Logs, $"zelo-{DateTime.Now:yyyy-MM-dd}.log");
        var script = Path.Combine(Pasta, "aplicar.ps1");

        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var ps = $$"""
            $ErrorActionPreference = 'Continue'
            $novo = {{Q(publicado)}}; $dest = {{Q(dest)}}; $anterior = {{Q(anterior)}}; $log = {{Q(log)}}
            function Log($m) { Add-Content -Path $log -Value ((Get-Date -Format 'HH:mm:ss') + ' [atualização] ' + $m) -Encoding UTF8 }
            Start-Sleep -Seconds 3
            Log 'Parando o serviço para instalar a versão {{versao}}.'
            Stop-Service -Name Zelo -Force -ErrorAction SilentlyContinue
            try { (Get-Service -Name Zelo).WaitForStatus('Stopped', '00:01:00') } catch {}
            Get-Process -Name Zelo.App -ErrorAction SilentlyContinue | Stop-Process -Force
            Start-Sleep -Seconds 2
            $recusada = {{Q(ArquivoRecusada)}}
            # 1) Guarda a versão atual. Sem essa cópia não há como voltar: então não instala nada.
            $copiaOk = $true
            $partes = @('Servico', 'Painel', 'Instalador')
            foreach ($p in $partes) {
              if (-not (Test-Path "$dest\$p")) { continue }
              robocopy "$dest\$p" "$anterior\$p" /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
              if ($LASTEXITCODE -ge 8) { $copiaOk = $false }
            }
            if (-not $copiaOk) {
              Log 'Não foi possível guardar a versão atual. Atualização cancelada; nada foi trocado.'
              Start-Service -Name Zelo -ErrorAction SilentlyContinue
              exit 1
            }
            function Voltar($motivo) {
              Log ("$motivo Restaurando a versão anterior.")
              Stop-Service -Name Zelo -Force -ErrorAction SilentlyContinue
              try { (Get-Service -Name Zelo).WaitForStatus('Stopped', '00:01:00') } catch {}
              foreach ($p in $partes) { if (Test-Path "$anterior\$p") { robocopy "$anterior\$p" "$dest\$p" /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null } }
              Start-Service -Name Zelo -ErrorAction SilentlyContinue
              try { (Get-Service -Name Zelo).WaitForStatus('Running', '00:01:00'); Log 'Versão anterior restaurada e rodando.' }
              catch { Log 'ATENÇÃO: a versão anterior foi restaurada, mas o serviço não voltou a rodar. Rode o INSTALAR.bat da versão mais recente.' }
            }
            $fim = 'Atualização concluída: a versão {{versao}} está rodando.' 
            # 2) Troca os arquivos.
            $falhou = $false
            foreach ($p in $partes) {
              if (-not (Test-Path "$novo\$p")) { continue }
              robocopy "$novo\$p" "$dest\$p" /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
              if ($LASTEXITCODE -ge 8) { $falhou = $true }
            }
            if ($falhou) {
              Voltar 'Falha ao copiar os arquivos novos.'
              $fim = 'Atualização desfeita: a versão {{versao}} não foi instalada.'
            } else {
              Log 'Arquivos da versão {{versao}} instalados.'
              # 3) Confere se a versão nova inicia e continua rodando por 30 segundos.
              Start-Service -Name Zelo -ErrorAction SilentlyContinue
              $saudavel = $false
              try {
                (Get-Service -Name Zelo).WaitForStatus('Running', '00:01:00')
                Start-Sleep -Seconds 30
                $saudavel = (Get-Service -Name Zelo).Status -eq 'Running'
              } catch {}
              if (-not $saudavel) {
                Voltar 'A versão {{versao}} não iniciou direito.'
                Set-Content -Path $recusada -Value '{{versao}}' -Encoding UTF8
                $fim = 'Atualização desfeita: a versão {{versao}} não iniciou e o Zelo voltou para a anterior. Ela não será instalada sozinha de novo.' 
              } else {
                Remove-Item $recusada -ErrorAction SilentlyContinue
                # Entrada em Configurações → Apps (versões instaladas pelo INSTALAR.bat ganham a entrada aqui).
                $inst = "$dest\Instalador\Zelo.Instalador.exe"
                if (Test-Path $inst) {
                  try { & $inst --registrar --automatico; Log 'Entrada do Zelo em Configurações → Apps atualizada.' }
                  catch { Log ('Não foi possível atualizar a entrada em Apps: ' + $_.Exception.Message) }
                }
              }
            }
            # Reabre o painel na bandeja de quem estiver logado (tarefa de uso único).
            try {
              $acao = New-ScheduledTaskAction -Execute "$dest\Painel\Zelo.App.exe" -Argument '--bandeja'
              $quem = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
              Register-ScheduledTask -TaskName 'ZeloReabrirPainel' -Action $acao -Principal $quem -Force | Out-Null
              Start-ScheduledTask -TaskName 'ZeloReabrirPainel'
              Start-Sleep -Seconds 10
              Unregister-ScheduledTask -TaskName 'ZeloReabrirPainel' -Confirm:$false
            } catch { Log ('Painel não pôde ser reaberto automaticamente: ' + $_.Exception.Message) }
            Log $fim
            """;
        File.WriteAllText(script, ps, new UTF8Encoding(true));

        // Processo independente: continua rodando depois que o serviço parar.
        Process.Start(new ProcessStartInfo(Executor.PowerShellExe,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
        Registro.Escrever($"Script de atualização iniciado ({script}).");
    }
}
