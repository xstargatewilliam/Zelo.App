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
    }

    public static EstadoAtualizacao LerEstado() => Estado.Ler<EstadoAtualizacao>("atualizacao") ?? new EstadoAtualizacao();
    private static void GravarEstado(EstadoAtualizacao e) => Estado.Gravar("atualizacao", e);

    private static HttpClient CriarHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
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

    /// <summary>Consulta a última versão publicada. Null se não houver nenhuma com pacote para este PC.</summary>
    public static async Task<VersaoRemota?> ConsultarAsync(CancellationToken ct)
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
            var remota = await ConsultarAsync(ct);
            estado.UltimaVerificacao = DateTime.Now;
            estado.UltimoErro = null;
            var atual = VersaoAtual;

            if (remota is null || remota.Versao <= atual)
            {
                estado.VersaoDisponivel = null;
                GravarEstado(estado);
                return $"O Zelo está na versão mais recente ({atual.ToString(3)}).";
            }

            estado.VersaoDisponivel = remota.Versao.ToString(3);
            GravarEstado(estado);
            if (!instalar) return $"Nova versão disponível: {remota.Versao.ToString(3)} (você tem a {atual.ToString(3)}).";

            Registro.Escrever($"Baixando a versão {remota.Versao.ToString(3)} de {remota.UrlPacote}");
            var pasta = await PrepararAsync(remota, ct);
            estado.UltimaInstalacao = DateTime.Now;
            estado.VersaoInstalada = remota.Versao.ToString(3);
            GravarEstado(estado);
            Aplicar(pasta, remota.Versao.ToString(3));
            return $"Instalando a versão {remota.Versao.ToString(3)}. O Zelo reinicia sozinho em alguns segundos.";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            estado.UltimaVerificacao = DateTime.Now;
            estado.UltimoErro = ex.Message;
            GravarEstado(estado);
            Registro.Escrever("Falha na atualização: " + ex.Message);
            return "Não foi possível atualizar: " + ex.Message;
        }
        finally
        {
            Trava.Release();
        }
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
            $falhou = $false
            foreach ($p in 'Servico', 'Painel') {
              robocopy "$dest\$p" "$anterior\$p" /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
              robocopy "$novo\$p" "$dest\$p" /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
              if ($LASTEXITCODE -ge 8) { $falhou = $true }
            }
            if ($falhou) {
              Log 'Falha ao copiar os arquivos novos. Restaurando a versão anterior.'
              foreach ($p in 'Servico', 'Painel') { robocopy "$anterior\$p" "$dest\$p" /MIR /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null }
            } else {
              Log 'Arquivos da versão {{versao}} instalados.'
            }
            Start-Service -Name Zelo -ErrorAction SilentlyContinue
            # Reabre o painel na bandeja de quem estiver logado (tarefa de uso único).
            try {
              $acao = New-ScheduledTaskAction -Execute "$dest\Painel\Zelo.App.exe" -Argument '--bandeja'
              $quem = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
              Register-ScheduledTask -TaskName 'ZeloReabrirPainel' -Action $acao -Principal $quem -Force | Out-Null
              Start-ScheduledTask -TaskName 'ZeloReabrirPainel'
              Start-Sleep -Seconds 10
              Unregister-ScheduledTask -TaskName 'ZeloReabrirPainel' -Confirm:$false
            } catch { Log ('Painel não pôde ser reaberto automaticamente: ' + $_.Exception.Message) }
            Log 'Atualização concluída.'
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
