using System.Diagnostics;
using System.Reflection;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;

namespace Zelo.Instalador;

/// <summary>
/// Instala, atualiza por cima e desinstala o Zelo. Faz o mesmo que o antigo scripts\instalar.ps1,
/// mas num .exe que pode receber assinatura digital, e registra o Zelo em Configurações → Apps
/// (com o botão Desinstalar do próprio Windows).
///
///   "Instalar Zelo.exe"                 instala (ou atualiza por cima) a partir do pacote ao lado
///   Zelo.Instalador.exe --desinstalar   remove (é o que o Windows chama pelo botão Desinstalar)
///   Zelo.Instalador.exe --registrar     só grava/atualiza a entrada em Apps (usado pela atualização automática)
///   --automatico                        sem perguntas nem pausas (testes e uso por script)
/// </summary>
public static class Program
{
    internal const string Servico = "Zelo";
    internal const string ChaveDesinstalar = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Zelo";
    internal const string ChaveRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    internal static string Destino => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zelo");
    internal static string Dados => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Zelo");
    internal static string Atalho => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        @"Microsoft\Windows\Start Menu\Programs\Zelo.lnk");

    private static bool _automatico;

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        _automatico = args.Contains("--automatico");
        try
        {
            if (args.Contains("--registrar")) { Registrar(Destino); return 0; }
            if (args.Contains("--desinstalar")) return Desinstalar();
            return Instalar();
        }
        catch (Exception ex)
        {
            Erro("Algo deu errado: " + ex.Message);
            Pausar();
            return 1;
        }
    }

    internal static string Versao =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";

    // ---------------------------------------------------------------- instalar

    private static int Instalar()
    {
        Console.Title = "Instalar o Zelo";
        Titulo($"Zelo {Versao} — manutenção do Windows, por William Stargate");

        var pacote = LocalizarPacote(AppContext.BaseDirectory);
        if (pacote is null)
        {
            Erro("Não encontrei os arquivos do Zelo ao lado deste instalador.");
            Linha("Isso acontece quando o instalador é aberto de dentro do .zip. Faça assim:");
            Linha("  1. Clique com o botão direito no Zelo-win-x64.zip e escolha \"Extrair tudo\" → \"Extrair\".");
            Linha("  2. Na pasta extraída, abra \"Instalar Zelo.exe\".");
            Pausar();
            return 2;
        }

        var dest = Destino;
        if (string.Equals(Path.GetFullPath(pacote).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Linha("Este é o instalador que fica junto do Zelo já instalado.");
            Linha("Para instalar outra versão, abra o \"Instalar Zelo.exe\" do pacote baixado da página Releases.");
            Linha("Para remover: Configurações → Apps → Apps instalados → Zelo → Desinstalar.");
            Pausar();
            return 0;
        }
        bool atualizacao = Directory.Exists(Path.Combine(dest, "Servico"));
        Etapa(1, atualizacao ? "Parando a versão instalada" : "Preparando");
        PararServico();
        foreach (var p in Process.GetProcessesByName("Zelo.App")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }

        // Guarda a versão instalada: se a cópia der errado no meio, ela volta.
        var anterior = Path.Combine(Dados, "instalacao-anterior");
        if (atualizacao)
        {
            // Não misturar restos de atualizações anteriores com esta cópia de segurança.
            if (Directory.Exists(anterior)) Directory.Delete(anterior, recursive: true);
            foreach (var parte in new[] { "Servico", "Painel", "Instalador" })
            {
                var instalada = Path.Combine(dest, parte);
                if (!Directory.Exists(instalada)) continue; // versões antigas não tinham instalador próprio
                if (!Robocopy(instalada, Path.Combine(anterior, parte)))
                {
                    IniciarServico();
                    Erro("Não foi possível guardar uma cópia da versão instalada. Nada foi trocado.");
                    Pausar();
                    return 3;
                }
            }
        }

        Etapa(2, $"Copiando os arquivos para {dest}");
        bool copiou = Robocopy(Path.Combine(pacote, "Servico"), Path.Combine(dest, "Servico"))
                      && Robocopy(Path.Combine(pacote, "Painel"), Path.Combine(dest, "Painel"))
                      && CopiarInstalador(pacote, Path.Combine(dest, "Instalador"));
        if (!copiou)
        {
            if (atualizacao)
            {
                var restaurou = RestaurarVersao(anterior, dest);
                var voltou = restaurou && IniciarServico();
                Erro(voltou
                    ? "A cópia dos arquivos novos falhou. A versão anterior foi restaurada e está rodando."
                    : "A cópia falhou e a recuperação não foi confirmada. Não use o Zelo até reparar a instalação.");
            }
            else Erro("A cópia dos arquivos falhou. Feche programas que possam estar usando a pasta e tente de novo.");
            Pausar();
            return 4;
        }
        TirarMarcaDaInternet(dest);

        Etapa(3, "Configurando o serviço e o painel");
        Directory.CreateDirectory(Dados);
        // Pasta de dados: só o sistema e administradores alteram; usuários só leem (relatórios).
        Rodar("icacls.exe", $"\"{Dados}\" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX");

        var exeServico = Path.Combine(dest, "Servico", "Zelo.Service.exe");
        if (!ServicoExiste())
        {
            var (c, saida) = Rodar("sc.exe", $"create {Servico} binPath= \"\\\"{exeServico}\\\"\" start= delayed-auto DisplayName= \"Zelo - Manutencao do Windows\"");
            if (c != 0) throw new InvalidOperationException("o Windows não aceitou criar o serviço: " + saida.Trim());
            Rodar("sc.exe", $"description {Servico} \"Executa as rotinas diaria, semanal e mensal de manutencao do Windows.\"");
        }
        else Rodar("sc.exe", $"config {Servico} binPath= \"\\\"{exeServico}\\\"\" start= delayed-auto");
        Rodar("sc.exe", $"failure {Servico} reset= 86400 actions= restart/60000/restart/60000/restart/300000");
        if (!IniciarServico())
        {
            if (atualizacao)
            {
                // A cópia ter dado certo não significa que os novos executáveis funcionam.
                PararServico();
                var restaurou = RestaurarVersao(anterior, dest);
                var voltou = restaurou && IniciarServico();
                Erro(voltou
                    ? "A versão nova não iniciou. A versão anterior foi restaurada e está rodando."
                    : "A versão nova não iniciou e não foi possível confirmar a recuperação. Repare a instalação com o pacote anterior.");
                Pausar();
                return 5;
            }
            throw new InvalidOperationException("o serviço do Zelo não iniciou. Veja os logs em " + Path.Combine(Dados, "logs"));
        }

        var painel = Path.Combine(dest, "Painel", "Zelo.App.exe");
        using (var run = Registry.LocalMachine.CreateSubKey(ChaveRun))
            run.SetValue("ZeloPainel", $"\"{painel}\" --bandeja");
        CriarAtalho(Atalho, painel);
        Registrar(dest);
        try { if (Directory.Exists(anterior)) Directory.Delete(anterior, true); } catch { }

        Etapa(4, "Pronto!");
        Linha(atualizacao
            ? $"O Zelo foi atualizado para a versão {Versao}. Seus ajustes, histórico e backups continuam como estavam."
            : "O serviço está rodando. A primeira vistoria começa em alguns minutos, quando o PC estiver parado.");
        Linha("Para remover depois: Configurações → Apps → Apps instalados → Zelo → Desinstalar.");
        // Abre o painel sem privilégio de administrador (pelo Explorador, na conta de quem está usando).
        if (!_automatico) try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{painel}\"") { UseShellExecute = false }); } catch { }
        Pausar();
        return 0;
    }

    /// <summary>A pasta "publicado" do pacote, ao lado do instalador (ou acima, quando ele roda de publicado\Instalador).</summary>
    internal static string? LocalizarPacote(string pastaDoExe)
    {
        foreach (var candidato in new[] { Path.Combine(pastaDoExe, "publicado"), Path.GetFullPath(Path.Combine(pastaDoExe, "..")) })
            if (File.Exists(Path.Combine(candidato, "Servico", "Zelo.Service.exe")) && File.Exists(Path.Combine(candidato, "Painel", "Zelo.App.exe")))
                return candidato;
        return null;
    }

    private static bool CopiarInstalador(string pacote, string destino)
    {
        Directory.CreateDirectory(destino);
        var origem = Path.Combine(pacote, "Instalador", "Zelo.Instalador.exe");
        if (!File.Exists(origem)) origem = Environment.ProcessPath!;
        var alvo = Path.Combine(destino, "Zelo.Instalador.exe");
        if (string.Equals(Path.GetFullPath(origem), Path.GetFullPath(alvo), StringComparison.OrdinalIgnoreCase)) return true;
        try { File.Copy(origem, alvo, overwrite: true); return true; }
        catch (Exception ex) { Erro("Não foi possível copiar o instalador: " + ex.Message); return false; }
    }

    /// <summary>Arquivos vindos de um .zip baixado têm a "marca da internet"; a pessoa já aprovou a instalação.</summary>
    private static void TirarMarcaDaInternet(string pasta)
    {
        foreach (var f in Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories))
            try { File.Delete(f + ":Zone.Identifier"); } catch { }
    }

    /// <summary>Entrada em Configurações → Apps, com o botão Desinstalar do Windows.</summary>
    internal static void Registrar(string dest)
    {
        var instalador = Path.Combine(dest, "Instalador", "Zelo.Instalador.exe");
        if (!File.Exists(instalador)) return;
        using var k = Registry.LocalMachine.CreateSubKey(ChaveDesinstalar);
        foreach (var (nome, valor) in ValoresDesinstalar(dest, Versao, TamanhoKb(dest)))
            if (valor is int n) k.SetValue(nome, n, RegistryValueKind.DWord);
            else k.SetValue(nome, (string)valor);
    }

    internal static List<(string Nome, object Valor)> ValoresDesinstalar(string dest, string versao, int tamanhoKb) => new()
    {
        ("DisplayName", "Zelo"),
        ("DisplayVersion", versao),
        ("Publisher", "William Stargate"),
        ("DisplayIcon", Path.Combine(dest, "Painel", "Zelo.App.exe") + ",0"),
        ("InstallLocation", dest),
        ("UninstallString", $"\"{Path.Combine(dest, "Instalador", "Zelo.Instalador.exe")}\" --desinstalar"),
        ("URLInfoAbout", "https://github.com/xstargatewilliam/Zelo.App"),
        ("HelpLink", "https://github.com/xstargatewilliam/Zelo.App"),
        ("NoModify", 1),
        ("NoRepair", 1),
        ("EstimatedSize", tamanhoKb),
    };

    private static int TamanhoKb(string pasta)
    {
        try { return (int)(Directory.EnumerateFiles(pasta, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024); }
        catch { return 0; }
    }

    // ---------------------------------------------------------------- desinstalar

    private static int Desinstalar()
    {
        Console.Title = "Desinstalar o Zelo";
        Titulo("Removendo o Zelo");
        var dest = Destino;
        var exeServico = Path.Combine(dest, "Servico", "Zelo.Service.exe");

        PararServico();
        // Antes de remover, oferece desfazer os ajustes da aba Otimizar.
        if (TemAjustesAplicados(Path.Combine(Dados, "estado", "otimizacao.json")) && File.Exists(exeServico))
        {
            Linha("");
            Linha("O Zelo fez ajustes no Windows pela aba Otimizar.");
            if (Perguntar("Voltar esses ajustes para como eram antes de remover o Zelo?", padraoSim: true))
            {
                var (_, saida) = Rodar(exeServico, "--desfazer-otimizacao", TimeSpan.FromMinutes(10));
                Linha(saida.Trim());
            }
            else Linha("Ajustes mantidos. Você ainda pode voltar o PC pelo ponto de restauração \"Zelo - antes de otimizar\".");
        }

        if (ServicoExiste())
        {
            PararServico();
            Rodar("sc.exe", $"delete {Servico}");
            Linha("Serviço removido.");
        }
        foreach (var p in Process.GetProcessesByName("Zelo.App")) { try { p.Kill(); p.WaitForExit(5000); } catch { } }
        using (var run = Registry.LocalMachine.OpenSubKey(ChaveRun, writable: true)) run?.DeleteValue("ZeloPainel", throwOnMissingValue: false);
        try { File.Delete(Atalho); } catch { }
        Registry.LocalMachine.DeleteSubKeyTree(ChaveDesinstalar, throwOnMissingSubKey: false);

        Thread.Sleep(1500);
        foreach (var parte in new[] { "Servico", "Painel" })
            try { var d = Path.Combine(dest, parte); if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch (Exception ex) { Erro($"Não foi possível apagar {parte}: {ex.Message}"); }
        Linha("Programa removido.");

        if (Directory.Exists(Dados))
        {
            if (Perguntar($"Apagar também o histórico, os relatórios e as configurações ({Dados})?", padraoSim: false))
            {
                try { Directory.Delete(Dados, true); Linha("Dados apagados."); }
                catch (Exception ex) { Erro("Não foi possível apagar todos os dados: " + ex.Message); }
            }
            else Linha($"Dados mantidos em {Dados}.");
        }
        Linha("Os arquivos de backup no seu destino NÃO foram tocados.");
        Pausar();
        ApagarPastaDepoisDeSair(dest);
        return 0;
    }

    internal static bool TemAjustesAplicados(string arquivo)
    {
        try
        {
            if (!File.Exists(arquivo)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(arquivo));
            return doc.RootElement.TryGetProperty("Aplicados", out var a)
                   && a.ValueKind == System.Text.Json.JsonValueKind.Object && a.EnumerateObject().Any();
        }
        catch { return false; }
    }

    /// <summary>O próprio instalador está dentro da pasta: ela é apagada alguns segundos depois que ele fecha.</summary>
    private static void ApagarPastaDepoisDeSair(string dest)
    {
        if (!Directory.Exists(dest)) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{dest}\"")
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        catch { }
    }

    // ---------------------------------------------------------------- serviço e processos

    private static bool ServicoExiste() => ServiceController.GetServices().Any(s => s.ServiceName.Equals(Servico, StringComparison.OrdinalIgnoreCase));

    private static void PararServico()
    {
        if (!ServicoExiste()) return;
        using var sc = new ServiceController(Servico);
        if (sc.Status is ServiceControllerStatus.Stopped) return;
        Linha("Parando o serviço do Zelo…");
        try { if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop(); } catch { }
        try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)); }
        catch { throw new InvalidOperationException("o serviço do Zelo não parou em 1 minuto. Reinicie o PC e rode o instalador de novo."); }
    }

    private static bool IniciarServico()
    {
        if (!ServicoExiste()) return false;
        using var sc = new ServiceController(Servico);
        try { if (sc.Status == ServiceControllerStatus.Stopped) sc.Start(); } catch { }
        try { sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)); return true; }
        catch { return false; }
    }

    /// <summary>Restaura todos os componentes anteriores, inclusive o desinstalador se existia.</summary>
    internal static bool RestaurarVersao(string anterior, string destino)
    {
        bool tudo = true;
        foreach (var parte in new[] { "Servico", "Painel", "Instalador" })
        {
            var salvo = Path.Combine(anterior, parte);
            var atual = Path.Combine(destino, parte);
            if (Directory.Exists(salvo))
                tudo &= Robocopy(salvo, atual);
            else if (parte == "Instalador" && Directory.Exists(atual))
            {
                try { Directory.Delete(atual, recursive: true); }
                catch { tudo = false; }
            }
        }
        return tudo;
    }

    private static bool Robocopy(string origem, string destino)
    {
        if (!Directory.Exists(origem)) return false;
        var robocopy = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        var (c, _) = Rodar(robocopy, $"\"{origem}\" \"{destino}\" /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP");
        return c < 8; // robocopy: 0-7 = sucesso
    }

    private static void CriarAtalho(string atalho, string alvo)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var ps = $"$w = New-Object -ComObject WScript.Shell; $a = $w.CreateShortcut({Q(atalho)}); $a.TargetPath = {Q(alvo)}; " +
                 "$a.Description = 'Zelo - Manutenção do Windows'; $a.Save()";
        var cod = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
        var (c, saida) = Rodar(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            $"-NoProfile -NonInteractive -EncodedCommand {cod}");
        if (c != 0) Erro("Não foi possível criar o atalho no Menu Iniciar: " + saida.Trim());
    }

    private static (int Codigo, string Saida) Rodar(string exe, string args, TimeSpan? limite = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var saida = p.StandardOutput.ReadToEndAsync();
        var erro = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)(limite ?? TimeSpan.FromMinutes(3)).TotalMilliseconds))
        {
            try { p.Kill(true); } catch { }
            return (-1, "passou do tempo limite");
        }
        return (p.ExitCode, saida.Result + erro.Result);
    }

    // ---------------------------------------------------------------- console

    private static void Titulo(string t) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(t); Console.ResetColor(); }
    private static void Etapa(int n, string t) { Console.WriteLine(); Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"==> {n}/4 {t}"); Console.ResetColor(); }
    private static void Linha(string t) => Console.WriteLine(t);
    private static void Erro(string t) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(t); Console.ResetColor(); }

    private static bool Perguntar(string pergunta, bool padraoSim)
    {
        if (_automatico) return padraoSim;
        Console.Write($"{pergunta} [{(padraoSim ? "S/n" : "s/N")}] ");
        var r = (Console.ReadLine() ?? "").Trim();
        return r.Length == 0 ? padraoSim : r.StartsWith("s", StringComparison.OrdinalIgnoreCase);
    }

    private static void Pausar()
    {
        if (_automatico) return;
        Console.WriteLine();
        Console.Write("Pressione Enter para fechar.");
        try { Console.ReadLine(); } catch { }
    }
}
