using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Zelo.Core;

namespace Zelo.App;

/// <summary>Uma linha do registro, já com a cor do que aconteceu.</summary>
public sealed class LinhaLog
{
    public string Bruta { get; init; } = "";
    public string Hora { get; init; } = "";
    public string Texto { get; init; } = "";
    public NivelLog Nivel { get; init; }
    public bool Detalhe { get; init; }
    public Brush Cor => Nivel switch
    {
        NivelLog.Ok => Cores.Aurora,
        NivelLog.Aviso => Cores.Ambar,
        NivelLog.Erro => Cores.Coral,
        NivelLog.Pulado => Cores.LinhaForte,
        _ => Cores.Luar,
    };
    public Brush CorTexto => Nivel is NivelLog.Erro or NivelLog.Aviso ? Cores.Texto : Nivel == NivelLog.Pulado ? Cores.Suave : Cores.Texto2;
    public Thickness Recuo => Detalhe ? new Thickness(24, 0, 0, 0) : new Thickness(0);
}

public enum NivelLog { Info, Ok, Aviso, Erro, Pulado }

public sealed class SeloLog
{
    public string Texto { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Suave;
    public Brush Fundo { get; init; } = Cores.NeutroFundo;
}

/// <summary>Uma vistoria (de "iniciada" a "terminada") ou um evento solto.</summary>
public sealed class GrupoLog : INotifyPropertyChanged
{
    private static readonly Geometry Fechado = Geometry.Parse("M4,2 L9,7 L4,12");
    private static readonly Geometry Aberto = Geometry.Parse("M2,4 L7,9 L12,4");
    private bool _expandido;

    public string Chave { get; init; } = "";
    public bool Unico { get; init; }
    public string Titulo { get; init; } = "";
    public string Sub { get; init; } = "";
    public List<SeloLog> Selos { get; init; } = new();
    public List<LinhaLog> Linhas { get; init; } = new();
    public NivelLog Pior { get; init; }

    public bool Expandido
    {
        get => Unico || _expandido;
        set
        {
            if (_expandido == value) return;
            _expandido = value;
            foreach (var n in new[] { nameof(Expandido), nameof(VisLinhas), nameof(Seta) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }
    }

    public Visibility VisCabecalho => Unico ? Visibility.Collapsed : Visibility.Visible;
    public Visibility VisLinhas => Expandido ? Visibility.Visible : Visibility.Collapsed;
    public Thickness MargemLinhas => Unico ? new Thickness(12, 8, 12, 5) : new Thickness(36, 0, 12, 10);
    public Geometry Seta => Expandido ? Aberto : Fechado;
    public Brush Fundo => Unico ? Cores.Transparente : Cores.NeutroFundo;
    public Brush Borda => Unico ? Cores.Transparente : Pior == NivelLog.Erro ? Cores.BordaCoral : Pior == NivelLog.Aviso ? Cores.BordaAmbar : Cores.Linha;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Página Registro: mostra o log do serviço (C:\ProgramData\Zelo\logs), ao vivo, colorido e agrupado.</summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _timerLog = new() { Interval = TimeSpan.FromSeconds(3) };
    private string[] _linhasLog = Array.Empty<string>();
    private bool _timerLogLigado;
    /// <summary>Grupos que a pessoa abriu ou fechou à mão (vale até trocar de dia).</summary>
    private readonly Dictionary<string, bool> _gruposAbertos = new();

    private static readonly Regex Linha = new(@"^(\d{2}:\d{2}:\d{2}) ?(.*)$");
    private static readonly Regex Inicio = new(@"^== Rotina (.+?) iniciada \((.+?)\)");
    private static readonly Regex Fim = new(@"^== Rotina (.+?) terminada\.?\s*(Score:\s*(-?\d+))?");

    internal static NivelLog NivelDe(string t)
    {
        if (t.Contains("[Ok]")) return NivelLog.Ok;
        if (t.Contains("[Ignorada]") || t.Contains("[Pulado]")) return NivelLog.Pulado;
        if (t.Contains("[Critico]") || t.Contains("[Erro]") || t.Contains("FALHOU") || t.Contains("falhou") || t.Contains("não aplicado")
            || t.Contains("não removido") || t.Contains("não instalado") || t.Contains("erro inesperado", StringComparison.OrdinalIgnoreCase))
            return NivelLog.Erro;
        if (t.Contains("[Aviso]") || t.StartsWith("Aviso:") || t.Contains("recusad") || t.Contains("Não foi possível") || t.Contains("não foi possível")
            || t.Contains("interrompid") || t.Contains("cancelad") || t.Contains("adiada"))
            return NivelLog.Aviso;
        return NivelLog.Info;
    }

    /// <summary>Lê as linhas do dia em grupos: cada vistoria vira um bloco; o resto fica solto.</summary>
    internal static List<GrupoLog> Agrupar(IEnumerable<string> linhas, Func<string, bool?> aberto)
    {
        var grupos = new List<GrupoLog>();
        List<LinhaLog>? atual = null;
        string? tituloAtual = null, origem = null, horaIni = null;

        void Fechar(string? horaFim, string? score, bool terminou)
        {
            if (atual is null) return;
            var tarefas = atual.Where(l => !l.Detalhe && l.Nivel != NivelLog.Info).ToList();
            int ok = tarefas.Count(l => l.Nivel == NivelLog.Ok), av = tarefas.Count(l => l.Nivel == NivelLog.Aviso),
                er = tarefas.Count(l => l.Nivel == NivelLog.Erro), ig = tarefas.Count(l => l.Nivel == NivelLog.Pulado);
            var pior = atual.Any(l => l.Nivel == NivelLog.Erro) ? NivelLog.Erro : atual.Any(l => l.Nivel == NivelLog.Aviso) ? NivelLog.Aviso : NivelLog.Ok;
            var selos = new List<SeloLog>();
            if (er > 0) selos.Add(new SeloLog { Texto = $"{er} com problema", Cor = Cores.Coral, Fundo = Cores.CoralFundo });
            if (av > 0) selos.Add(new SeloLog { Texto = $"{av} aviso(s)", Cor = Cores.Ambar, Fundo = Cores.AmbarFundo });
            if (ok > 0) selos.Add(new SeloLog { Texto = $"{ok} ok", Cor = Cores.Aurora, Fundo = Cores.AuroraFundo });
            if (!terminou) selos.Add(new SeloLog { Texto = "em andamento", Cor = Cores.Luar, Fundo = Cores.NeutroFundo });
            var chave = $"{horaIni} {tituloAtual}";
            var sub = horaFim is null ? $"Desde {horaIni}" : $"{horaIni} – {horaFim}";
            if (ig > 0) sub += $" · {ig} ignorada(s)";
            if (score is not null) sub += $" · Saúde {score}";
            var g = new GrupoLog
            {
                Chave = chave, Titulo = $"Rotina {tituloAtual}" + (origem is null ? "" : $" ({origem})"), Sub = sub,
                Selos = selos, Linhas = atual, Pior = pior,
            };
            // Abre sozinho o que teve problema ou ainda está rodando; o resto fica fechado.
            g.Expandido = aberto(chave) ?? (pior != NivelLog.Ok || !terminou);
            grupos.Add(g);
            atual = null;
        }

        LinhaLog? anterior = null;
        foreach (var bruta in linhas)
        {
            var m = Linha.Match(bruta);
            var hora = m.Success ? m.Groups[1].Value[..5] : "";
            var texto = (m.Success ? m.Groups[2].Value : bruta).TrimEnd();
            var limpo = texto.TrimStart();
            var mi = Inicio.Match(limpo);
            if (mi.Success)
            {
                Fechar(null, null, terminou: false);
                atual = new List<LinhaLog>();
                tituloAtual = mi.Groups[1].Value;
                origem = mi.Groups[2].Value;
                horaIni = hora;
                anterior = null;
                continue;
            }
            var mf = Fim.Match(limpo);
            if (mf.Success && atual is not null)
            {
                Fechar(hora, mf.Groups[3].Success ? mf.Groups[3].Value : null, terminou: true);
                anterior = null;
                continue;
            }
            bool detalhe = limpo.StartsWith("·") || (texto.StartsWith("      ") && anterior is not null);
            var nivel = NivelDe(limpo);
            if (detalhe && anterior is not null && nivel == NivelLog.Info) nivel = anterior.Nivel == NivelLog.Ok ? NivelLog.Info : anterior.Nivel;
            var l = new LinhaLog
            {
                Bruta = bruta, Hora = detalhe ? "" : hora, Nivel = nivel, Detalhe = detalhe,
                Texto = Regex.Replace(limpo.TrimStart('·', ' '), @"^\[(Ok|Aviso|Critico|Erro|Ignorada|Pulado)\]\s*", ""),
            };
            if (!detalhe) anterior = l;
            if (atual is not null) atual.Add(l);
            else grupos.Add(new GrupoLog { Chave = bruta, Unico = true, Linhas = new() { l }, Pior = nivel });
        }
        Fechar(null, null, terminou: false);
        return grupos;
    }

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
            MostrarVazio("Ainda não há registro. Ele aparece aqui assim que o serviço do Zelo fizer alguma coisa.");
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
            MostrarVazio("Não foi possível ler o registro: " + ex.Message);
        }
    }

    private void MostrarVazio(string texto)
    {
        ListaLog.ItemsSource = null;
        TxtLogVazio.Text = texto;
        TxtLogVazio.Visibility = Visibility.Visible;
    }

    private IEnumerable<string> LinhasFiltradas()
    {
        IEnumerable<string> l = _linhasLog;
        var filtro = TxtFiltroLog.Text.Trim();
        // Início e fim de rotina ficam sempre, para os grupos continuarem inteiros.
        bool Moldura(string x) => x.Contains("== Rotina");
        if (ChkSoProblemas.IsChecked == true) l = l.Where(x => Moldura(x) || NivelDe(Linha.Match(x) is { Success: true } m ? m.Groups[2].Value.TrimStart() : x) is NivelLog.Aviso or NivelLog.Erro);
        if (filtro.Length > 0) l = l.Where(x => Moldura(x) || x.Contains(filtro, StringComparison.OrdinalIgnoreCase));
        return l;
    }

    private bool Filtrando => ChkSoProblemas.IsChecked == true || TxtFiltroLog.Text.Trim().Length > 0;

    private void MostrarLog(bool manterPosicao)
    {
        // Mantém a rolagem se a pessoa estiver lendo lá em cima; senão, acompanha o fim.
        bool noFim = !manterPosicao || RolagemLog.VerticalOffset + RolagemLog.ViewportHeight >= RolagemLog.ExtentHeight - 4;
        var filtrando = Filtrando;
        var grupos = Agrupar(LinhasFiltradas(), chave => filtrando ? true : _gruposAbertos.TryGetValue(chave, out var a) ? a : null)
            .Where(g => g.Unico || g.Linhas.Count > 0)
            .ToList();
        foreach (var g in grupos.Where(g => !g.Unico))
            g.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GrupoLog.Expandido) && !Filtrando) _gruposAbertos[g.Chave] = g.Expandido; };
        if (grupos.Count == 0)
        {
            MostrarVazio(filtrando ? "Nenhuma linha com esse filtro." : "Ainda não há registro hoje.");
            return;
        }
        TxtLogVazio.Visibility = Visibility.Collapsed;
        ListaLog.ItemsSource = grupos;
        if (noFim) Dispatcher.BeginInvoke(new Action(() => RolagemLog.ScrollToEnd()), DispatcherPriority.Loaded);
    }

    private void DiaLog_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ModoCaptura) return;
        _linhasLog = Array.Empty<string>();
        _gruposAbertos.Clear();
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
            Notificar("Registro copiado. Cole onde quiser (Ctrl + V).");
        }
        catch (Exception ex) { Aviso("Não foi possível copiar: " + ex.Message); }
    }

    private void CopiarLinhaLog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string linha) return;
        try { Clipboard.SetText(linha); Notificar("Linha copiada."); } catch { }
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
            "09:00:13 Atualização: O Zelo está na versão mais recente (1.5.4).",
            "14:13:00 Rotina Diária vencida, aguardando: fora do horário livre do perfil.",
            "18:03:11 == Rotina Diária iniciada (agendada)",
            "18:05:55    [Ok] Antivírus: atualização e verificação rápida: Antivírus ativo e atualizado, nenhuma ameaça. (164s)",
            "18:05:58    [Ok] Saúde dos discos (SMART): 1 disco(s) saudável(is). (3s)",
            "18:06:00    [Ok] Limpeza de arquivos temporários: 336,2 MB liberados (169 arquivos). (0s)",
            "18:06:00    [Ignorada] Backup incremental: Desativada nas configurações. (0s)",
            "18:06:00 == Rotina Diária terminada. Score: 96",
            "20:02:17 Otimizar: 16 de 25 ajuste(s) aplicado(s). Perfis: Trabalho, Dev. Falhas: 1.",
            "20:02:17    Otimizar · não aplicado: Tirar Widgets e notícias da barra de tarefas: o próprio Windows protege esta configuração e não deixa programas mudarem.",
            "20:28:33 == Rotina Semanal iniciada (manual)",
            "20:28:58    [Ok] Windows Update: Windows em dia, nenhuma atualização pendente. (24s)",
            "20:29:14    winget: 3 programa(s) com atualização: 7zip.7zip, Discord.Discord, Spotify.Spotify",
            "20:29:31    winget: 7-Zip atualizado.",
            "20:31:05    winget: Discord falhou: o programa está aberto; feche-o e tente de novo (0x8A150101)",
            "20:32:27    [Aviso] Atualização de programas (winget): 2 programa(s) atualizado(s); 1 não pôde ser atualizado. (195s)",
            "20:32:27       · Atualizado: 7-Zip (23.01 → 24.08)",
            "20:32:27       · FALHOU: Discord: o programa está aberto; feche-o e tente de novo (código 0x8A150101)",
            "20:32:38    [Ok] Otimização das unidades (TRIM / desfragmentação): 1 unidade(s) otimizada(s). (11s)",
            "20:32:40 == Rotina Semanal terminada. Score: 92",
        };
        MostrarLog(manterPosicao: false);
    }
}
