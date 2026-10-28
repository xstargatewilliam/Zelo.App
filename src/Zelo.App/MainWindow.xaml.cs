using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zelo.Core;

namespace Zelo.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private EstadoServico? _estado;
    private List<ExecucaoRotina> _historico = new();
    private string? _ultimaExecucaoVista;
    private bool _consultando;
    private bool _historicoCarregado;
    private bool _carregandoConfig;
    private Configuracao? _config;

    /// <summary>Chamado quando chega uma execução nova (o App usa para notificar).</summary>
    public event Action<ExecucaoRotina>? NovaExecucao;

    /// <summary>Se verdadeiro, fechar a janela realmente fecha (em vez de ir para a bandeja).</summary>
    public bool Encerrando { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        for (int h = 0; h <= 23; h++) CmbHoraInicio.Items.Add($"{h:00}h");
        for (int h = 1; h <= 24; h++) CmbHoraFim.Items.Add($"{h:00}h");
        ListaTarefasConfig.ItemsSource = Catalogo.Todas.Select(t => new ItemTarefaConfig
        {
            Id = t.Id, Nome = t.Nome, Descricao = t.Descricao,
            Rotinas = " · " + string.Join(", ", t.Rotinas.Select(r => r.Nome().ToLowerInvariant())),
        }).ToList();

        _timer.Tick += async (_, _) => await AtualizarAsync();
        Loaded += async (_, _) =>
        {
            await AtualizarAsync();
            await CarregarConfigAsync();
            _timer.Start();
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Fechar no X manda para a bandeja; o serviço continua trabalhando.
        if (!Encerrando)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ================================================================ Atualização

    public async Task AtualizarAsync()
    {
        if (_consultando) return;
        _consultando = true;
        try
        {
            var r = await ClientePipe.EnviarAsync(new Pedido { Comando = "estado" });
            _estado = r.Estado;
            BannerServico.Visibility = Visibility.Collapsed;
            if (_estado is null) return;

            if (!_historicoCarregado || _estado.UltimaExecucaoId != _ultimaExecucaoVista)
            {
                var h = await ClientePipe.EnviarAsync(new Pedido { Comando = "historico", Quantidade = 120 });
                _historico = h.Historico ?? new();
                bool primeiraCarga = !_historicoCarregado;
                _historicoCarregado = true;
                _ultimaExecucaoVista = _estado.UltimaExecucaoId;
                MostrarHistorico();
                var ultima = _historico.LastOrDefault();
                if (!primeiraCarga && ultima is not null) NovaExecucao?.Invoke(ultima);
            }
            MostrarEstado(_estado);
        }
        catch
        {
            BannerServico.Visibility = Visibility.Visible;
            TxtTitulo.Text = "Sem conexão com o serviço";
            TxtSubtitulo.Text = "As rotinas automáticas dependem do serviço do Zelo.";
            PainelBotoes.IsEnabled = false;
        }
        finally
        {
            _consultando = false;
        }
    }

    private void MostrarEstado(EstadoServico e)
    {
        var s = e.Saude;
        TxtScore.Text = s.Geral < 0 ? "—" : s.Geral.ToString();
        DesenharArco(s.Geral);

        (TxtTitulo.Text, TxtSubtitulo.Text) = s.Geral switch
        {
            < 0 => ("Ainda sem diagnóstico", "Rode a rotina diária para ter o primeiro panorama do PC."),
            >= 85 => ("Seu PC está em ótimas condições", "Nada importante pendente."),
            >= 65 => ("Seu PC está bem, com alguns pontos de atenção", "Veja abaixo o que pode melhorar."),
            >= 50 => ("Seu PC precisa de atenção", "Há itens que merecem cuidado em breve."),
            _ => ("Há problemas importantes", "Veja os itens críticos abaixo."),
        };

        TxtDiaria.Text = LinhaRotina(Rotina.Diaria, e);
        TxtSemanal.Text = LinhaRotina(Rotina.Semanal, e);
        TxtMensal.Text = LinhaRotina(Rotina.Mensal, e);

        // Categorias
        var cats = Enum.GetValues<Categoria>().Select(c =>
        {
            bool tem = s.PorCategoria.TryGetValue(c.ToString(), out var v);
            return new ItemCategoria
            {
                Nome = c.Nome(),
                Valor = tem ? v : 0,
                ValorTexto = tem ? v.ToString() : "—",
                Cor = Cores.DeScore(tem ? v : -1),
            };
        }).ToList();
        ListaCategorias.ItemsSource = cats;

        // Alertas
        var alertas = s.Alertas.Select(a => new ItemAlerta
        {
            Texto = a,
            Cor = a.StartsWith("Crítico") ? Cores.Critico : a.StartsWith("Desatualizado") ? Cores.Neutro : Cores.Aviso,
        }).ToList();
        ListaAlertas.ItemsSource = alertas;
        TxtSemAlertas.Visibility = alertas.Count == 0 && s.Geral >= 0 ? Visibility.Visible : Visibility.Collapsed;

        // Progresso
        bool rodando = e.RotinaAtual is not null;
        PainelBotoes.IsEnabled = !rodando;
        PainelProgresso.Visibility = rodando ? Visibility.Visible : Visibility.Collapsed;
        if (rodando)
        {
            TxtProgresso.Text = $"{e.RotinaAtual!.Value.Nome()}{(e.SimulacaoAtual ? " (simulação)" : "")}: {e.Indice}/{e.Total} — {e.TarefaAtual}";
            BarraProgresso.Maximum = Math.Max(1, e.Total);
            BarraProgresso.Value = Math.Max(0, e.Indice - 1);
        }
    }

    private static string LinhaRotina(Rotina r, EstadoServico e)
    {
        e.Ultima.TryGetValue(r.ToString(), out var ultima);
        e.Proxima.TryGetValue(r.ToString(), out var proxima);
        var prox = proxima <= DateTime.Now ? "pendente (aguardando momento ocioso)" : Formatar.Quando(proxima);
        return $"{r.Nome()}: última {Formatar.Quando(ultima)} · próxima {prox}";
    }

    private void DesenharArco(int score)
    {
        const double tamanho = 128, espessura = 11;
        double raio = (tamanho - espessura) / 2;
        var centro = new Point(tamanho / 2, tamanho / 2);
        double frac = Math.Clamp(score, 0, 100) / 100.0;
        Arco.Stroke = Cores.DeScore(score);
        if (score <= 0) { Arco.Data = null; return; }
        if (frac >= 0.999) frac = 0.999; // um arco de 360° não desenha

        double ang = frac * 2 * Math.PI;
        var inicio = new Point(centro.X, centro.Y - raio);
        var fim = new Point(centro.X + raio * Math.Sin(ang), centro.Y - raio * Math.Cos(ang));
        var figura = new PathFigure { StartPoint = inicio, IsClosed = false };
        figura.Segments.Add(new ArcSegment(fim, new Size(raio, raio), 0, frac > 0.5, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(figura);
        Arco.Data = geo;
    }

    private void MostrarHistorico()
    {
        var ultima = _historico.LastOrDefault(h => !h.Simulacao) ?? _historico.LastOrDefault();
        if (ultima is null)
        {
            TxtUltimaExecucao.Text = "Nenhuma execução ainda.";
            ListaResultados.ItemsSource = null;
            BtnRelatorio.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtUltimaExecucao.Text =
                $"Rotina {ultima.Rotina.Nome().ToLowerInvariant()} · {Formatar.Quando(ultima.Inicio)} · {Formatar.Duracao(ultima.Fim - ultima.Inicio)}" +
                (ultima.Simulacao ? " · simulação" : "");
            // Problemas primeiro.
            ListaResultados.ItemsSource = ultima.Resultados
                .OrderBy(r => r.Status switch { StatusTarefa.Critico => 0, StatusTarefa.Erro => 1, StatusTarefa.Aviso => 2, StatusTarefa.Ok => 3, _ => 4 })
                .Select(ItemResultado.De).ToList();
            BtnRelatorio.Tag = ultima.Relatorio;
            BtnRelatorio.Visibility = string.IsNullOrEmpty(ultima.Relatorio) ? Visibility.Collapsed : Visibility.Visible;
        }

        GradeHistorico.ItemsSource = Enumerable.Reverse(_historico).Select(ItemHistorico.De).ToList();
        DesenharGrafico();
    }

    private void DesenharGrafico()
    {
        Grafico.Children.Clear();
        var pontos = _historico.Where(h => !h.Simulacao && h.Score >= 0).TakeLast(60).ToList();
        TxtGraficoVazio.Visibility = pontos.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
        double w = Grafico.ActualWidth, h = Grafico.ActualHeight;
        if (pontos.Count < 2 || w < 10 || h < 10) return;

        // Linhas guia em 50 e 80.
        foreach (var guia in new[] { 50, 80 })
        {
            double y = h - guia / 100.0 * (h - 10) - 5;
            Grafico.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y,
                Stroke = (Brush)FindResource("Linha"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 },
            });
            var rot = new TextBlock { Text = guia.ToString(), FontSize = 11, Foreground = (Brush)FindResource("Suave") };
            Canvas.SetLeft(rot, 2); Canvas.SetTop(rot, y - 15);
            Grafico.Children.Add(rot);
        }

        var linha = new Polyline { Stroke = (Brush)FindResource("Destaque"), StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
        for (int i = 0; i < pontos.Count; i++)
        {
            double x = 24 + i * (w - 34) / (pontos.Count - 1);
            double y = h - pontos[i].Score / 100.0 * (h - 10) - 5;
            linha.Points.Add(new Point(x, y));
            var ponto = new Ellipse { Width = 7, Height = 7, Fill = Cores.DeScore(pontos[i].Score), ToolTip = $"{pontos[i].Inicio:dd/MM HH:mm} · {pontos[i].Rotina.Nome()} · {pontos[i].Score}" };
            Canvas.SetLeft(ponto, x - 3.5); Canvas.SetTop(ponto, y - 3.5);
            Grafico.Children.Add(ponto);
        }
        Grafico.Children.Insert(0, linha);
    }

    private void Grafico_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharGrafico();

    // ================================================================ Ações

    private async void Executar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !Enum.TryParse<Rotina>(b.Tag as string, out var rotina)) return;
        bool sim = ChkSimulacao.IsChecked == true;

        if (!sim && rotina != Rotina.Diaria)
        {
            var aviso = rotina == Rotina.Mensal
                ? "A rotina mensal pode levar algumas horas (verificação completa do antivírus, DISM e SFC). O PC pode ficar mais lento enquanto isso.\n\nIniciar agora?"
                : "A rotina semanal instala atualizações do Windows e de programas, e pode pedir para reiniciar no final.\n\nIniciar agora?";
            if (MessageBox.Show(this, aviso, "Zelo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        await EnviarComando(new Pedido { Comando = "executar", Rotina = rotina, Simulacao = sim });
        await AtualizarAsync();
    }

    public Task ExecutarRotina(Rotina r) => EnviarComando(new Pedido { Comando = "executar", Rotina = r });

    private async void Cancelar_Click(object sender, RoutedEventArgs e)
    {
        await EnviarComando(new Pedido { Comando = "cancelar" });
    }

    private async Task EnviarComando(Pedido p)
    {
        try
        {
            var r = await ClientePipe.EnviarAsync(p);
            if (!r.Ok) MessageBox.Show(this, r.Mensagem ?? "Erro.", "Zelo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível falar com o serviço: " + ex.Message, "Zelo", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AbrirRelatorio_Click(object sender, RoutedEventArgs e) => Abrir(BtnRelatorio.Tag as string);

    private void GradeHistorico_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (GradeHistorico.SelectedItem is ItemHistorico h) Abrir(h.Relatorio);
    }

    private void Abrir(string? arquivo)
    {
        if (string.IsNullOrEmpty(arquivo) || !File.Exists(arquivo))
        {
            MessageBox.Show(this, "Relatório não encontrado (relatórios com mais de 6 meses são apagados).", "Zelo");
            return;
        }
        Process.Start(new ProcessStartInfo(arquivo) { UseShellExecute = true });
    }

    private void Aba_Checked(object sender, RoutedEventArgs e)
    {
        // Disparado já durante o InitializeComponent, antes dos painéis existirem.
        if (PainelGeral is null || PainelHistorico is null || PainelConfig is null) return;
        PainelGeral.Visibility = AbaGeral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelHistorico.Visibility = AbaHistorico.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelConfig.Visibility = AbaConfig.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (AbaHistorico.IsChecked == true) Dispatcher.BeginInvoke(new Action(DesenharGrafico), DispatcherPriority.Loaded);
    }

    // ================================================================ Configurações

    private async Task CarregarConfigAsync()
    {
        try
        {
            var r = await ClientePipe.EnviarAsync(new Pedido { Comando = "config-obter" });
            if (r.Config is not null) PreencherConfig(r.Config);
            TxtStatusConfig.Text = "";
        }
        catch
        {
            TxtStatusConfig.Text = "Não foi possível carregar as configurações (serviço fora do ar).";
        }
    }

    private void PreencherConfig(Configuracao c)
    {
        _carregandoConfig = true;
        try
        {
            _config = c;
            CmbPerfil.SelectedItem = CmbPerfil.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == c.Perfil);
            PreencherHorarios(c);
            TxtPastas.Text = string.Join(Environment.NewLine, c.PastasBackup);
            TxtDestino.Text = c.DestinoBackup;
            TxtDiasTemp.Text = c.DiasTemporarios.ToString();
            TxtDiasLixeira.Text = c.DiasLixeira.ToString();
            TxtAvisoDisco.Text = c.LimiteAvisoDisco.ToString();
            TxtCriticoDisco.Text = c.LimiteCriticoDisco.ToString();
            ChkCache.IsChecked = c.LimparCacheNavegadores;
            ChkDownloads.IsChecked = c.LimparDownloads;
            TxtDiasDownloads.Text = c.DiasDownloads.ToString();
            ChkWU.IsChecked = c.InstalarWindowsUpdate;
            ChkWinget.IsChecked = c.AtualizarProgramasWinget;
            ChkDrivers.IsChecked = c.InstalarDriversWU;
            ChkRestauracao.IsChecked = c.CriarPontoRestauracao;
            ChkDefenderCompleto.IsChecked = c.VarreduraCompletaDefender;

            var itens = (List<ItemTarefaConfig>)ListaTarefasConfig.ItemsSource;
            foreach (var i in itens) i.Ativa = !c.TarefasDesativadas.Contains(i.Id);
            ListaTarefasConfig.ItemsSource = null;
            ListaTarefasConfig.ItemsSource = itens;
        }
        finally
        {
            _carregandoConfig = false;
        }
    }

    private void PreencherHorarios(Configuracao c)
    {
        CmbHoraInicio.SelectedIndex = c.HoraInicio;
        CmbHoraFim.SelectedIndex = c.HoraFim - 1;
        TxtCpu.Text = c.LimiteCpuOcioso.ToString();
        ChkTomada.IsChecked = c.SomenteNaTomada;
    }

    private void CmbPerfil_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_carregandoConfig || _config is null || CmbPerfil.SelectedItem is not ComboBoxItem item) return;
        var temp = LerConfigDaTela();
        temp.AplicarPerfil((string)item.Tag);
        PreencherHorarios(temp);
        if (temp.Perfil == "Jogos") ChkCache.IsChecked = false;
    }

    private Configuracao LerConfigDaTela()
    {
        static int Num(string texto, int padrao) => int.TryParse(texto.Trim().TrimEnd('%'), out var v) ? v : padrao;
        var baseCfg = _config ?? new Configuracao();

        var c = new Configuracao
        {
            Perfil = (CmbPerfil.SelectedItem as ComboBoxItem)?.Tag as string ?? baseCfg.Perfil,
            HoraInicio = Math.Max(0, CmbHoraInicio.SelectedIndex),
            HoraFim = Math.Max(0, CmbHoraFim.SelectedIndex) + 1,
            LimiteCpuOcioso = Num(TxtCpu.Text, baseCfg.LimiteCpuOcioso),
            SomenteNaTomada = ChkTomada.IsChecked == true,
            PastasBackup = TxtPastas.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
            DestinoBackup = TxtDestino.Text.Trim(),
            DiasTemporarios = Num(TxtDiasTemp.Text, baseCfg.DiasTemporarios),
            DiasLixeira = Num(TxtDiasLixeira.Text, baseCfg.DiasLixeira),
            LimiteAvisoDisco = Num(TxtAvisoDisco.Text, baseCfg.LimiteAvisoDisco),
            LimiteCriticoDisco = Num(TxtCriticoDisco.Text, baseCfg.LimiteCriticoDisco),
            LimparCacheNavegadores = ChkCache.IsChecked == true,
            LimparDownloads = ChkDownloads.IsChecked == true,
            DiasDownloads = Num(TxtDiasDownloads.Text, baseCfg.DiasDownloads),
            InstalarWindowsUpdate = ChkWU.IsChecked == true,
            AtualizarProgramasWinget = ChkWinget.IsChecked == true,
            InstalarDriversWU = ChkDrivers.IsChecked == true,
            CriarPontoRestauracao = ChkRestauracao.IsChecked == true,
            VarreduraCompletaDefender = ChkDefenderCompleto.IsChecked == true,
            TarefasDesativadas = ((List<ItemTarefaConfig>)ListaTarefasConfig.ItemsSource).Where(i => !i.Ativa).Select(i => i.Id).ToList(),
        };
        return c;
    }

    private async void SalvarConfig_Click(object sender, RoutedEventArgs e)
    {
        var c = LerConfigDaTela();
        try
        {
            var r = await ClientePipe.EnviarAsync(new Pedido { Comando = "config-salvar", Config = c });
            if (!r.Ok)
            {
                TxtStatusConfig.Foreground = Cores.Critico;
                TxtStatusConfig.Text = r.Mensagem ?? "Não foi possível salvar.";
                return;
            }
            if (r.Config is not null) PreencherConfig(r.Config);
            TxtStatusConfig.Foreground = Cores.Ok;
            TxtStatusConfig.Text = $"Salvo às {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex)
        {
            TxtStatusConfig.Foreground = Cores.Critico;
            TxtStatusConfig.Text = "Serviço indisponível: " + ex.Message;
        }
    }

    private async void RecarregarConfig_Click(object sender, RoutedEventArgs e) => await CarregarConfigAsync();

    private void AdicionarPasta_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta para incluir no backup", Multiselect = true };
        if (d.ShowDialog(this) != true) return;
        var atuais = TxtPastas.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        foreach (var p in d.FolderNames)
            if (!atuais.Contains(p, StringComparer.OrdinalIgnoreCase)) atuais.Add(p);
        TxtPastas.Text = string.Join(Environment.NewLine, atuais);
    }

    private void EscolherDestino_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Onde guardar o backup" };
        if (d.ShowDialog(this) == true) TxtDestino.Text = d.FolderName;
    }
}
