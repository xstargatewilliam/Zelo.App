using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Zelo.Core;

namespace Zelo.App;

/// <summary>Página Registro: mostra o log do serviço (C:\ProgramData\Zelo\logs), ao vivo.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _timerLog = new() { Interval = TimeSpan.FromSeconds(3) };
    private string[] _linhasLog = Array.Empty<string>();
    private bool _timerLogLigado;

    private static readonly string[] MarcasDeProblema =
        { "[Aviso]", "[Critico]", "[Erro]", "FALHOU", "falhou", "Falha", "falha", "ERRO", "Erro", "erro:", "recusad", "Não foi possível", "não foi possível", "interrompid", "cancelad", "Aviso:" };

    private void AbrirRegistro()
    {
        if (!_timerLogLigado)
        {
            _timerLog.Tick += (_, _) => { if (PaginaRegistro.Visibility == Visibility.Visible && IsVisible) LerLog(manterPosicao: true); };
            _timerLogLigado = true;
        }
        var dias = DiasDeLog();
        var atual = CmbDiaLog.SelectedItem as string;
        CmbDiaLog.ItemsSource = dias;
        CmbDiaLog.SelectedItem = atual is not null && dias.Contains(atual) ? atual : dias.FirstOrDefault();
        LerLog(manterPosicao: false);
        _timerLog.Start();
    }

    /// <summary>Os dias com log, do mais novo ao mais antigo (no formato dd/MM/aaaa).</summary>
    private static List<string> DiasDeLog()
    {
        try
        {
            return Directory.EnumerateFiles(Caminhos.Logs, "zelo-*.log")
                .Select(f => Path.GetFileNameWithoutExtension(f)[5..])
                .Where(d => DateTime.TryParse(d, out _))
                .OrderByDescending(d => d)
                .Take(30)
                .Select(d => DateTime.Parse(d).ToString("dd/MM/yyyy"))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private string? ArquivoDoDia() =>
        CmbDiaLog.SelectedItem is string dia && DateTime.TryParseExact(dia, "dd/MM/yyyy", null, System.Globalization.DateTimeStyles.None, out var d)
            ? Path.Combine(Caminhos.Logs, $"zelo-{d:yyyy-MM-dd}.log")
            : null;

    private void LerLog(bool manterPosicao)
    {
        var arq = ArquivoDoDia();
        if (arq is null || !File.Exists(arq))
        {
            _linhasLog = Array.Empty<string>();
            TxtLog.Text = "Ainda não há registro. Ele aparece aqui assim que o serviço do Zelo fizer alguma coisa.";
            return;
        }
        try
        {
            // O serviço continua escrevendo enquanto lemos: abre compartilhado.
            using var fs = new FileStream(arq, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var leitor = new StreamReader(fs, Encoding.UTF8);
            var linhas = leitor.ReadToEnd().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (manterPosicao && linhas.Length == _linhasLog.Length) return; // nada novo
            _linhasLog = linhas;
            MostrarLog(manterPosicao);
        }
        catch (Exception ex)
        {
            TxtLog.Text = "Não foi possível ler o registro: " + ex.Message;
        }
    }

    private IEnumerable<string> LinhasFiltradas()
    {
        IEnumerable<string> l = _linhasLog;
        var filtro = TxtFiltroLog.Text.Trim();
        if (ChkSoProblemas.IsChecked == true) l = l.Where(x => MarcasDeProblema.Any(m => x.Contains(m)));
        if (filtro.Length > 0) l = l.Where(x => x.Contains(filtro, StringComparison.OrdinalIgnoreCase));
        return l;
    }

    private void MostrarLog(bool manterPosicao)
    {
        // Mantém a rolagem se a pessoa estiver lendo lá em cima; senão, acompanha o fim.
        bool noFim = !manterPosicao || TxtLog.VerticalOffset + TxtLog.ViewportHeight >= TxtLog.ExtentHeight - 4;
        var linhas = LinhasFiltradas().ToList();
        TxtLog.Text = linhas.Count == 0 ? "Nenhuma linha com esse filtro." : string.Join(Environment.NewLine, linhas);
        if (noFim) TxtLog.ScrollToEnd();
    }

    private void DiaLog_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ModoCaptura) return;
        _linhasLog = Array.Empty<string>();
        LerLog(manterPosicao: false);
    }

    private void FiltroLog_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        MostrarLog(manterPosicao: false);
    }

    private void CopiarLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var texto = string.Join(Environment.NewLine, LinhasFiltradas());
            Clipboard.SetText($"Zelo {Atualizador.VersaoAtual.ToString(3)} · registro de {CmbDiaLog.SelectedItem}{Environment.NewLine}{texto}");
            Aviso("Registro copiado. Cole onde quiser (Ctrl + V).");
        }
        catch (Exception ex) { Aviso("Não foi possível copiar: " + ex.Message); }
    }

    private void AbrirPastaLog_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Caminhos.Logs}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Aviso("Não foi possível abrir a pasta: " + ex.Message); }
    }

    /// <summary>Dados de exemplo para as capturas de tela do GitHub.</summary>
    private void MontarRegistroDeExemplo()
    {
        CmbDiaLog.ItemsSource = new List<string> { DateTime.Now.ToString("dd/MM/yyyy") };
        CmbDiaLog.SelectedIndex = 0;
        _linhasLog = new[]
        {
            "03:10:02 == Rotina Revisão semanal iniciada (agendada)",
            "03:10:02    winget: usando C:\\Program Files\\WindowsApps\\Microsoft.DesktopAppInstaller_1.9.25200.0_x64__8wekyb3d8bbwe\\winget.exe",
            "03:10:09    winget: 3 programa(s) com atualização: 7zip.7zip, Discord.Discord, Spotify.Spotify",
            "03:10:09    winget: atualizando 7-Zip [7zip.7zip] 23.01 → 24.08",
            "03:10:31    winget: 7-Zip atualizado.",
            "03:10:31    winget: atualizando Discord [Discord.Discord] 1.0.9170 → 1.0.9175",
            "03:12:05    winget: Discord falhou: o programa está aberto; feche-o e tente de novo (0x8A150101)",
            "03:12:05       Instalação falhou com o código de saída: 0x8A150101",
            "03:12:05    winget: atualizando Spotify [Spotify.Spotify] 1.2.40 → 1.2.52",
            "03:27:05    winget: Spotify: passou de 15 minutos e foi interrompido. O instalador provavelmente abriu uma janela, que o serviço não consegue mostrar.",
            "03:27:06    [Aviso] Atualização de programas (winget): 1 programa(s) atualizado(s); 2 não puderam ser atualizados. (1024s)",
            "03:27:06       · Atualizado: 7-Zip (23.01 → 24.08)",
            "03:27:06       · FALHOU: Discord: o programa está aberto; feche-o e tente de novo (código 0x8A150101)",
            "03:27:06       · FALHOU: Spotify: passou de 15 minutos e foi interrompido.",
            "03:41:50 == Rotina Revisão semanal terminada. Score: 86",
        };
        MostrarLog(manterPosicao: false);
    }
}
