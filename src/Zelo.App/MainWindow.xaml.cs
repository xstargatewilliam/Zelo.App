using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Zelo.Core;

namespace Zelo.App;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _salvarTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly ObservableCollection<ItemPasta> _pastas = new();
    private readonly List<ItemTarefaConfig> _tarefasConfig;

    private EstadoServico? _estado;
    private List<ExecucaoRotina> _historico = new();
    private string? _ultimaExecucaoVista;
    private bool _historicoCarregado;
    private bool _consultando;
    private bool _carregandoConfig;
    private Configuracao? _config;
    private int _scoreMostrado = int.MinValue;
    private string? _etapaMostrada;
    private Storyboard? _respiracao;
    private int _passoBv = 1;

    /// <summary>Uma execução nova terminou (o App usa para notificar).</summary>
    public event Action<ExecucaoRotina>? NovaExecucao;
    /// <summary>O estado do serviço mudou (o App usa para o ícone da bandeja). Null = serviço fora do ar.</summary>
    public event Action<EstadoServico?>? EstadoMudou;

    /// <summary>Se verdadeiro, fechar a janela realmente fecha (em vez de ir para a bandeja).</summary>
    public bool Encerrando { get; set; }

    /// <summary>Modo de capturas de tela (CI): sem serviço, sem animações.</summary>
    public static bool ModoCaptura { get; set; }

    private static bool Animar => SystemParameters.ClientAreaAnimation && !ModoCaptura;

    private static string ArquivoBoasVindas => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelo", "boas-vindas.ok");

    // ================================================================ Propriedades de layout e animação

    public static readonly DependencyProperty ColunasAreasProperty =
        DependencyProperty.Register(nameof(ColunasAreas), typeof(int), typeof(MainWindow), new PropertyMetadata(5));

    public int ColunasAreas
    {
        get => (int)GetValue(ColunasAreasProperty);
        set => SetValue(ColunasAreasProperty, value);
    }

    public static readonly DependencyProperty AnelValorProperty =
        DependencyProperty.Register(nameof(AnelValor), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, _) => ((MainWindow)d).DesenharArco()));

    public double AnelValor
    {
        get => (double)GetValue(AnelValorProperty);
        set => SetValue(AnelValorProperty, value);
    }

    // ================================================================ Início

    public MainWindow()
    {
        InitializeComponent();

        var versao = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        TxtVersaoMenu.Text = $"Zelo {versao}";
        TxtSobreVersao.Text = $"Zelo {versao}";

        ListaPastas.ItemsSource = _pastas;
        BvListaPastas.ItemsSource = _pastas;
        _tarefasConfig = Catalogo.Todas.Select(t => new ItemTarefaConfig
        {
            Id = t.Id, Nome = t.Nome, Descricao = t.Descricao,
            Rotinas = "  ·  " + string.Join(", ", t.Rotinas.Select(r => Palavras.Rotina(r).ToLowerInvariant())),
        }).ToList();
        ListaTarefasConfig.ItemsSource = _tarefasConfig;

        _timer.Tick += async (_, _) => await AtualizarAsync();
        _salvarTimer.Tick += async (_, _) => { _salvarTimer.Stop(); await SalvarConfigAsync(); };
        SizeChanged += (_, _) => AjustarColunas();
        SourceInitialized += (_, _) => TituloEscuro();

        Loaded += async (_, _) =>
        {
            AjustarColunas();
            if (ModoCaptura) return;
            EntrarPagina(ConteudoInicio);
            await AtualizarAsync();
            await CarregarConfigAsync();
            if (_config is not null && !File.Exists(ArquivoBoasVindas)) AbrirBoasVindas();
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int atributo, ref int valor, int tamanho);

    /// <summary>Barra de título escura no Windows 10 (20H1+) e 11.</summary>
    private void TituloEscuro()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int sim = 1;
            DwmSetWindowAttribute(hwnd, 20, ref sim, sizeof(int));
        }
        catch { }
    }

    private void AjustarColunas()
    {
        double largura = ActualWidth - 240 - 80;
        ColunasAreas = largura >= 980 ? 5 : largura >= 640 ? 3 : 2;
    }

    // ================================================================ Navegação

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PaginaInicio is null || PaginaHistorico is null || PaginaAjustes is null || PaginaOtimizar is null || PaginaRegistro is null) return;
        PaginaRegistro.Visibility = NavRegistro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavRegistro.IsChecked == true)
        {
            EntrarPagina(ConteudoRegistro);
            if (!ModoCaptura) AbrirRegistro();
        }
        PaginaOtimizar.Visibility = NavOtimizar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavOtimizar.IsChecked == true)
        {
            EntrarPagina(ConteudoOtimizar);
            if (!ModoCaptura && !_otimCarregado) _ = CarregarOtimizacaoAsync();
        }
        PaginaInicio.Visibility = NavInicio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaginaHistorico.Visibility = NavHistorico.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PaginaAjustes.Visibility = NavAjustes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavInicio.IsChecked == true) EntrarPagina(ConteudoInicio);
        if (NavHistorico.IsChecked == true)
        {
            EntrarPagina(ConteudoHistorico);
            Dispatcher.BeginInvoke(new Action(() => DesenharGrafico(animar: true)), DispatcherPriority.Loaded);
        }
        if (NavAjustes.IsChecked == true) EntrarPagina(ConteudoAjustes);
    }

    public void IrPara(string pagina)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (pagina == "ajustes") NavAjustes.IsChecked = true;
        else if (pagina == "otimizar") NavOtimizar.IsChecked = true;
        else if (pagina == "registro") NavRegistro.IsChecked = true;
        else if (pagina == "historico") NavHistorico.IsChecked = true;
        else NavInicio.IsChecked = true;
    }

    /// <summary>O conteúdo sobe 8 px e aparece.</summary>
    private static void EntrarPagina(FrameworkElement el)
    {
        if (!Animar) return;
        var dur = TimeSpan.FromMilliseconds(220);
        var facil = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = facil });
        if (el.RenderTransform is TranslateTransform t)
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, dur) { EasingFunction = facil });
    }

    // ================================================================ Estado do serviço

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
                var h = await ClientePipe.EnviarAsync(new Pedido { Comando = "historico", Quantidade = 200 });
                _historico = h.Historico ?? new();
                bool primeira = !_historicoCarregado;
                _historicoCarregado = true;
                _ultimaExecucaoVista = _estado.UltimaExecucaoId;
                MostrarHistorico();
                var ultima = _historico.LastOrDefault();
                if (!primeira && ultima is not null) NovaExecucao?.Invoke(ultima);
            }
            MostrarEstado(_estado);
            EstadoMudou?.Invoke(_estado);
        }
        catch
        {
            BannerServico.Visibility = Visibility.Visible;
            TxtTitulo.Text = "Sem contato com o serviço";
            TxtSubtitulo.Text = "As vistorias automáticas dependem do serviço do Zelo.";
            BtnCuidar.IsEnabled = BtnSimular.IsEnabled = BtnMaisRotinas.IsEnabled = false;
            EstadoMudou?.Invoke(null);
        }
        finally
        {
            _consultando = false;
        }
    }

    private void MostrarEstado(EstadoServico e)
    {
        // Pausa
        BannerPausa.Visibility = e.PausadoAte is null ? Visibility.Collapsed : Visibility.Visible;
        if (e.PausadoAte is { } ate) TxtPausa.Text = $"Vistorias automáticas pausadas até {ate:HH:mm}.";

        bool rodando = e.RotinaAtual is not null;
        HeroNormal.Visibility = rodando ? Visibility.Collapsed : Visibility.Visible;
        HeroCuidando.Visibility = rodando ? Visibility.Visible : Visibility.Collapsed;
        SecaoEtapas.Visibility = rodando ? Visibility.Visible : Visibility.Collapsed;
        SecaoAreas.Visibility = rodando ? Visibility.Collapsed : Visibility.Visible;
        SecaoAtencao.Visibility = rodando ? Visibility.Collapsed : Visibility.Visible;
        BtnCuidar.IsEnabled = BtnSimular.IsEnabled = BtnMaisRotinas.IsEnabled = !rodando && !e.Atualizando;

        if (rodando) MostrarCuidando(e);
        else
        {
            PararRespiracao();
            _etapaMostrada = null;
            MostrarSaude(e);
        }
        MostrarAgenda(e);
        MostrarAtualizacao(e);
    }

    private void MostrarSaude(EstadoServico e)
    {
        var s = e.Saude;
        if (s.Geral != _scoreMostrado)
        {
            _scoreMostrado = s.Geral;
            TxtScore.Text = s.Geral < 0 ? "—" : s.Geral.ToString(CultureInfo.InvariantCulture);
            TxtScoreDe.Visibility = s.Geral < 0 ? Visibility.Collapsed : Visibility.Visible;
            Arco.Stroke = Cores.DeScore(s.Geral);
            double alvo = Math.Max(0, s.Geral);
            if (Animar)
                BeginAnimation(AnelValorProperty, new DoubleAnimation(0, alvo, TimeSpan.FromMilliseconds(900))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else AnelValor = alvo;
        }

        var pontos = s.Pontos;
        bool urgente = pontos.Any(p => p.Status is StatusTarefa.Critico && !p.Desatualizado);
        (TxtTitulo.Text, TxtSubtitulo.Text) = s.Geral switch
        {
            < 0 => ("Vamos conhecer o seu PC.", "Faça a primeira vistoria para ter um panorama completo."),
            _ when urgente => ("Há algo importante para resolver.", "Veja logo abaixo o que aconteceu e o que fazer."),
            >= 85 => ("Seu PC está ótimo.", Pendencias(pontos.Count)),
            >= 70 => ("Seu PC está bem.", Pendencias(pontos.Count)),
            >= 50 => ("Seu PC precisa de cuidado.", Pendencias(pontos.Count)),
            _ => ("Há problemas importantes.", "Veja logo abaixo o que aconteceu e o que fazer."),
        };
        TxtCobertura.Text = s.Geral < 0 ? "" :
            s.Cobertura >= 5 ? "Cobertura: 5 de 5 áreas verificadas"
                             : $"Cobertura: {s.Cobertura} de 5 áreas verificadas — a nota vale só para elas";
        e.Ultima.TryGetValue(nameof(Rotina.Diaria), out var ultimaDiaria);
        TxtUltima.Text = ultimaDiaria is null ? "" : $"Última vistoria: {Palavras.Quando(ultimaDiaria)}";

        // Áreas
        var ordem = new[] { Categoria.Disco, Categoria.Seguranca, Categoria.Atualizacoes, Categoria.Backup, Categoria.Sistema };
        ListaAreas.ItemsSource = ordem.Select(c =>
        {
            if (!s.Areas.TryGetValue(c.ToString(), out var a))
                return new ItemArea { Categoria = c, Nome = Palavras.Area(c), Texto = "Ainda não verificada.", StatusTexto = "Sem dados", Cor = Cores.Suave, Icone = Icones.Pulado };
            return new ItemArea
            {
                Categoria = c,
                Nome = Palavras.Area(c),
                Texto = a.Desatualizado ? a.Texto + " (verificação antiga)" : a.Texto,
                StatusTexto = Palavras.Status(a.Status),
                Cor = Cores.De(a.Status),
                Borda = a.Status switch
                {
                    StatusTarefa.Aviso => Cores.BordaAmbar,
                    StatusTarefa.Critico or StatusTarefa.Erro => Cores.BordaCoral,
                    _ => Cores.Linha,
                },
                Icone = Icones.De(a.Status),
            };
        }).ToList();

        // Pontos de atenção
        var itens = pontos.Select(p => new ItemPonto
        {
            Chave = p.TarefaId,
            Titulo = p.Resumo,
            Sub = $"{p.Nome} · {Palavras.Quando(p.Quando)}{(p.Desatualizado ? " · vale conferir de novo" : "")}",
            Cor = Cores.De(p.Status), CorFundo = Cores.FundoDe(p.Status), Icone = Icones.De(p.Categoria),
            Acoes = ItemPonto.AcoesPara(p),
            Detalhes = p.Detalhes.Select(ItemDetalheTexto.De).ToList(),
        }).ToList();
        if (ModoCaptura) foreach (var i in itens.Where(i => i.Detalhes.Count > 0)) i.Expandido = true;
        ListaPontos.ItemsSource = itens;
        TxtSemPontos.Visibility = itens.Count == 0 && s.Geral >= 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================ Detalhe de uma área

    private void Area_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Categoria c) AbrirArea(c);
    }

    private void AbrirArea(Categoria c)
    {
        var desligadas = _config?.TarefasDesativadas ?? new List<string>();
        var itens = Catalogo.Todas.Where(t => t.Categoria == c).Select(t =>
        {
            ResultadoTarefa? r = null;
            for (int i = _historico.Count - 1; i >= 0 && r is null; i--)
            {
                if (_historico[i].Simulacao) continue;
                r = _historico[i].Resultados.LastOrDefault(x => x.TarefaId == t.Id && x.Status != StatusTarefa.Ignorada);
            }
            return ItemVerificacao.De(t, r, desligadas.Contains(t.Id), _estado?.Saude.Silenciados.Contains(t.Id) == true);
        }).OrderBy(i => i.Ordem).ToList();

        StatusTarefa? status = null;
        string texto = "Ainda não verificada.";
        if (_estado?.Saude.Areas.TryGetValue(c.ToString(), out var a) == true)
        {
            status = a.Status;
            texto = $"{Palavras.Status(a.Status)} · {a.Texto}";
        }
        TxtAreaTitulo.Text = Palavras.Area(c);
        TxtAreaSub.Text = texto;
        IconeArea.Data = Icones.De(c);
        IconeArea.Stroke = status is null ? Cores.Suave : Cores.De(status.Value);
        IconeAreaFundo.Background = status is null ? Cores.NeutroFundo : Cores.FundoDe(status.Value);
        ListaVerificacoes.ItemsSource = itens;
        RolagemArea.ScrollToTop();
        PainelArea.Visibility = Visibility.Visible;
    }

    private void FecharArea_Click(object sender, RoutedEventArgs e) => PainelArea.Visibility = Visibility.Collapsed;

    private static string Pendencias(int n) => n switch
    {
        0 => "Nada pede a sua atenção. O Zelo segue cuidando sozinho.",
        1 => "Um ponto merece um olhar quando der. O resto, o Zelo cuida sozinho.",
        _ => $"{n} pontos merecem um olhar quando der. O resto, o Zelo cuida sozinho.",
    };

    private void MostrarCuidando(EstadoServico e)
    {
        var rotina = Palavras.Rotina(e.RotinaAtual!.Value);
        TxtRotinaRodando.Text = (e.SimulacaoAtual ? $"{rotina} em simulação" : $"{rotina} em andamento").ToUpper(new CultureInfo("pt-BR"));

        var etapa = (e.TarefaAtual ?? "Preparando") + "…";
        if (etapa != _etapaMostrada)
        {
            _etapaMostrada = etapa;
            TxtEtapaAtual.Text = etapa;
            if (Animar && TxtEtapaAtual.RenderTransform is TranslateTransform t)
            {
                var dur = TimeSpan.FromMilliseconds(320);
                TxtEtapaAtual.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, dur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }

        int total = Math.Max(1, e.Total);
        int feitas = Math.Max(0, e.Indice - 1);
        TxtEtapaInfo.Text = $"Etapa {Math.Max(1, e.Indice)} de {total}" + (e.SimulacaoAtual ? " · simulação: nada será alterado" : "");
        if (BarraProgresso.Parent is Grid trilho)
        {
            double alvo = trilho.ActualWidth * feitas / total;
            if (Animar) BarraProgresso.BeginAnimation(WidthProperty, new DoubleAnimation(alvo, TimeSpan.FromMilliseconds(400)));
            else BarraProgresso.Width = alvo;
        }

        ListaEtapas.ItemsSource = e.Etapas.Select(ItemEtapa.De).ToList();
        IniciarRespiracao();
    }

    /// <summary>A lua "respira" e a estrela pisca enquanto o Zelo trabalha.</summary>
    private void IniciarRespiracao()
    {
        if (!Animar || _respiracao is not null) return;
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        void Anima(DependencyObject alvo, string prop, double de, double ate, double segundos)
        {
            var a = new DoubleAnimation(de, ate, TimeSpan.FromSeconds(segundos))
            {
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(a, alvo);
            Storyboard.SetTargetProperty(a, new PropertyPath(prop));
            sb.Children.Add(a);
        }
        Anima(EscalaLua, "ScaleX", 1, 1.06, 1.8);
        Anima(EscalaLua, "ScaleY", 1, 1.06, 1.8);
        Anima(Halo, "Opacity", 0.18, 0.42, 1.8);
        Anima(Estrela, "Opacity", 1, 0.4, 0.9);
        sb.Begin(this, true);
        _respiracao = sb;
    }

    private void PararRespiracao()
    {
        if (_respiracao is null) return;
        _respiracao.Stop(this);
        _respiracao = null;
    }

    private void MostrarAgenda(EstadoServico e)
    {
        var rotinas = new[] { Rotina.Diaria, Rotina.Semanal, Rotina.Mensal };
        ListaAgenda.ItemsSource = rotinas.Select((r, i) =>
        {
            e.Ultima.TryGetValue(r.ToString(), out var ultima);
            e.Proxima.TryGetValue(r.ToString(), out var proxima);
            string prox = e.PausadoAte is not null ? "Pausada até " + e.PausadoAte.Value.ToString("HH:mm")
                : proxima <= DateTime.Now ? (e.DentroDaJanela ? "Próxima: assim que o PC estiver livre" : "Próxima: no próximo horário livre")
                : "Próxima: " + Palavras.Quando(proxima);
            return new ItemAgenda
            {
                Nome = Palavras.Rotina(r),
                Ultima = "Última: " + Palavras.Quando(ultima),
                Proxima = prox,
                Separador = i < 2 ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            };
        }).ToList();
        TxtJanela.Text = DescreverJanela(_config, e.Perfil);
    }

    private static string DescreverJanela(Configuracao? c, string perfil) => perfil switch
    {
        "Trabalho" when c is not null => c.DiasExpediente.Count == 0
            ? "Perfil Trabalho sem dias marcados: o Zelo trabalha a qualquer hora."
            : $"Perfil Trabalho: o Zelo cuida do PC fora do seu expediente — das {Palavras.Hora(c.ExpedienteFim)} às {Palavras.Hora(c.ExpedienteInicio)} nos dias de trabalho"
              + (c.DiasExpediente.Count == 7 ? "." : ", e a qualquer hora nos outros dias."),
        "Jogos" => $"Perfil Jogos: o Zelo trabalha de madrugada, das {Configuracao.JogosInicio}h às {Configuracao.JogosFim}h, com o PC bem parado.",
        _ => c is { ExigirOcioso: false }
            ? "O Zelo trabalha a qualquer hora, assim que cada vistoria vence (com prioridade baixa)."
            : "O Zelo trabalha sempre que o PC está parado, a qualquer hora.",
    };

    private void MostrarAtualizacao(EstadoServico e)
    {
        TxtAtualizacao.Text =
            e.Atualizando ? "Atualizando o Zelo…" :
            e.VersaoDisponivel is not null ? $"Versão nova disponível: {e.VersaoDisponivel} (você tem a {e.Versao})." :
            e.ErroAtualizacao is not null ? $"A última consulta ao GitHub falhou: {e.ErroAtualizacao}" :
            e.UltimaVerificacaoAtualizacao is not null ? $"Você tem a versão mais recente (consultado {Palavras.Quando(e.UltimaVerificacaoAtualizacao)})." :
            "Ainda não consultou o GitHub.";
        BtnAtualizar.IsEnabled = !e.Atualizando && e.RotinaAtual is null;
        BtnAtualizar.Content = e.VersaoDisponivel is not null ? "Atualizar agora" : "Procurar atualização";
    }

    private void DesenharArco()
    {
        const double centro = 66, raio = 52;
        double frac = Math.Clamp(AnelValor, 0, 100) / 100.0;
        if (frac <= 0) { Arco.Data = null; return; }
        if (frac >= 0.999) frac = 0.999;
        double ang = frac * 2 * Math.PI;
        var inicio = new Point(centro, centro - raio);
        var fim = new Point(centro + raio * Math.Sin(ang), centro - raio * Math.Cos(ang));
        var figura = new PathFigure { StartPoint = inicio, IsClosed = false };
        figura.Segments.Add(new ArcSegment(fim, new Size(raio, raio), 0, frac > 0.5, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(figura);
        Arco.Data = geo;
    }

    // ================================================================ Histórico

    private void MostrarHistorico()
    {
        var hoje = DateTime.Today;
        string Grupo(DateTime d) =>
            d.Date == hoje ? "HOJE" :
            d.Date == hoje.AddDays(-1) ? "ONTEM" :
            d.Date > hoje.AddDays(-7) ? "ESTA SEMANA" :
            d.Year == hoje.Year && d.Month == hoje.Month ? "ESTE MÊS" :
            d.ToString("MMMM 'de' yyyy", new CultureInfo("pt-BR")).ToUpper(new CultureInfo("pt-BR"));

        IEnumerable<ExecucaoRotina> lista = Enumerable.Reverse(_historico);
        var itens = lista.Select(e => (e, item: ItemExecucao.De(e)));
        if (FiltroProblemas.IsChecked == true) itens = itens.Where(x => x.item.TemProblema && !x.item.Simulacao);
        else if (FiltroSimulacoes.IsChecked == true) itens = itens.Where(x => x.item.Simulacao);

        var grupos = itens.Take(120)
            .GroupBy(x => Grupo(x.e.Inicio))
            .Select(g => new GrupoExecucoes { Titulo = g.Key, Itens = g.Select(x => x.item).ToList() })
            .ToList();
        ListaGrupos.ItemsSource = grupos;
        TxtHistoricoVazio.Visibility = grupos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DesenharGrafico(animar: false);
    }

    private void Filtro_Checked(object sender, RoutedEventArgs e)
    {
        if (ListaGrupos is null) return;
        MostrarHistorico();
    }

    private void DesenharGrafico(bool animar)
    {
        Grafico.Children.Clear();
        var limite = DateTime.Now.AddDays(-30);
        var pontos = _historico.Where(h => !h.Simulacao && h.Score >= 0 && h.Fim >= limite).ToList();
        TxtGraficoVazio.Visibility = pontos.Count < 2 ? Visibility.Visible : Visibility.Collapsed;
        TxtGraficoResumo.Text = pontos.Count < 2 ? "" :
            $"De {pontos[0].Score} em {pontos[0].Fim:dd/MM} para {pontos[^1].Score} {(pontos[^1].Fim.Date == DateTime.Today ? "hoje" : "em " + pontos[^1].Fim.ToString("dd/MM"))}";
        double w = Grafico.ActualWidth, h = Grafico.ActualHeight;
        if (pontos.Count < 2 || w < 40 || h < 40) return;

        const double esquerda = 36, topo = 8, base_ = 24;
        double Y(double v) => topo + (100 - v) / 100.0 * (h - topo - base_);
        double t0 = pontos[0].Fim.Ticks, t1 = Math.Max(t0 + 1, pontos[^1].Fim.Ticks);
        double X(DateTime d) => esquerda + (d.Ticks - t0) / (t1 - t0) * (w - esquerda - 12);

        foreach (var guia in new[] { 100, 80, 50 })
        {
            Grafico.Children.Add(new Line
            {
                X1 = esquerda, X2 = w, Y1 = Y(guia), Y2 = Y(guia),
                Stroke = Cores.Linha, StrokeThickness = 1,
                StrokeDashArray = guia == 100 ? null : new DoubleCollection { 4, 4 },
            });
            var rotulo = new TextBlock { Text = guia.ToString(CultureInfo.InvariantCulture), FontSize = 11, Foreground = Cores.Suave };
            Canvas.SetLeft(rotulo, 4); Canvas.SetTop(rotulo, Y(guia) - 8);
            Grafico.Children.Add(rotulo);
        }

        var linha = new Polyline { Stroke = Cores.Aurora, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        double comprimento = 0;
        Point? anterior = null;
        foreach (var p in pontos)
        {
            var pt = new Point(X(p.Fim), Y(p.Score));
            if (anterior is { } a) comprimento += (pt - a).Length;
            anterior = pt;
            linha.Points.Add(pt);
        }
        Grafico.Children.Add(linha);
        if (animar && Animar && comprimento > 0)
        {
            double unidades = comprimento / linha.StrokeThickness;
            linha.StrokeDashArray = new DoubleCollection { unidades, unidades };
            linha.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(unidades, 0, TimeSpan.FromMilliseconds(1000))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }

        foreach (var p in new[] { pontos[0], pontos[^1] })
        {
            var bola = new Ellipse { Width = 9, Height = 9, Fill = Cores.DeScore(p.Score), ToolTip = $"{p.Fim:dd/MM HH:mm} · saúde {p.Score}" };
            Canvas.SetLeft(bola, X(p.Fim) - 4.5); Canvas.SetTop(bola, Y(p.Score) - 4.5);
            Grafico.Children.Add(bola);
        }
        var d0 = new TextBlock { Text = pontos[0].Fim.ToString("dd/MM"), FontSize = 11, Foreground = Cores.Suave };
        Canvas.SetLeft(d0, esquerda); Canvas.SetTop(d0, h - 16);
        var d1 = new TextBlock { Text = pontos[^1].Fim.Date == DateTime.Today ? "hoje" : pontos[^1].Fim.ToString("dd/MM"), FontSize = 11, Foreground = Cores.Suave };
        Canvas.SetRight(d1, 12); Canvas.SetTop(d1, h - 16);
        Grafico.Children.Add(d0);
        Grafico.Children.Add(d1);
    }

    private void Grafico_SizeChanged(object sender, SizeChangedEventArgs e) => DesenharGrafico(animar: false);

    private void AbrirRelatorio_Click(object sender, RoutedEventArgs e)
    {
        var arquivo = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(arquivo) || !File.Exists(arquivo))
        {
            Aviso("Esse relatório não está mais disponível (relatórios com mais de 6 meses são apagados).");
            return;
        }
        Abrir(arquivo);
    }

    // ================================================================ Ações

    private async void Cuidar_Click(object sender, RoutedEventArgs e) => await ExecutarRotina(Rotina.Diaria);
    private async void Simular_Click(object sender, RoutedEventArgs e) => await ExecutarRotina(Rotina.Diaria, simulacao: true);

    private void MaisRotinas_Click(object sender, RoutedEventArgs e)
    {
        MenuRotinas.PlacementTarget = BtnMaisRotinas;
        MenuRotinas.Placement = PlacementMode.Bottom;
        MenuRotinas.IsOpen = true;
    }

    private async void RotinaMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem m || m.Tag is not string tag) return;
        var partes = tag.Split('|');
        if (!Enum.TryParse<Rotina>(partes[0], out var rotina)) return;
        bool sim = partes.Length > 1;
        if (!sim)
        {
            var (titulo, texto) = rotina == Rotina.Mensal
                ? ("Começar o check-up mensal agora?", "Pode levar algumas horas: repara arquivos do Windows e confere o disco. O PC pode ficar mais lento enquanto isso.")
                : ("Começar a revisão semanal agora?", "Instala atualizações do Windows e dos seus programas, um por vez, e pode pedir para reiniciar no final.");
            if (!await PerguntarAsync(titulo, texto, null, "Começar agora")) return;
        }
        await ExecutarRotina(rotina, sim);
    }

    public async Task ExecutarRotina(Rotina r, bool simulacao = false)
    {
        await EnviarComando(new Pedido { Comando = "executar", Rotina = r, Simulacao = simulacao });
        await AtualizarAsync();
    }

    private async void Parar_Click(object sender, RoutedEventArgs e) => await Parar();

    public async Task Pausar(int minutos)
    {
        await EnviarComando(new Pedido { Comando = "pausar", Minutos = minutos });
        await AtualizarAsync();
    }

    private async void Retomar_Click(object sender, RoutedEventArgs e) => await Pausar(0);

    private async void Atualizar_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var r = await ClientePipe.EnviarAsync(new Pedido { Comando = "atualizar" });
            TxtAtualizacao.Text = r.Mensagem ?? "";
        }
        catch (Exception ex)
        {
            TxtAtualizacao.Text = "Serviço indisponível: " + ex.Message;
        }
    }

    private async void AcaoPonto_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id || id.Length == 0) return;
        if (id == "zelo:backup")
        {
            NavAjustes.IsChecked = true;
            Dispatcher.BeginInvoke(new Action(() => TituloBackup.BringIntoView()), DispatcherPriority.Loaded);
        }
        else if (id.StartsWith("abrir:")) Abrir(id[6..]);
        else if (id.StartsWith("cmd:"))
        {
            // Só programas do próprio Windows, definidos no código do painel.
            var partes = id[4..].Split('|', 2);
            try { Process.Start(new ProcessStartInfo(partes[0], partes.Length > 1 ? partes[1] : "") { UseShellExecute = true }); }
            catch { }
        }
        else if (id.StartsWith("tarefa:"))
        {
            await EnviarComando(new Pedido { Comando = "executar-tarefa", TarefaId = id[7..] });
            await AtualizarAsync();
        }
        else if (id.StartsWith("dispensar:"))
        {
            var partes = id[10..].Split('|', 2);
            var motivo = partes.Length > 1 ? partes[1] : "";
            try
            {
                var r = await Elevacao.EnviarAsync(new Pedido { Comando = "dispensar", TarefaId = partes[0], Motivo = motivo });
                if (!r.Ok) { Aviso(r.Mensagem ?? "Não deu certo."); return; }
                if (motivo.Length == 0 && r.Mensagem is not null) Aviso(r.Mensagem);
                if (motivo == "queda de energia")
                    Aviso("Anotado. Se as quedas de energia forem frequentes, um nobreak (UPS) protege o PC e evita perder arquivos abertos.");
            }
            catch (Exception ex) { Aviso("Não foi possível falar com o serviço do Zelo: " + ex.Message); }
            _scoreMostrado = int.MinValue;
            await AtualizarAsync();
        }
    }

    private async Task EnviarComando(Pedido p)
    {
        try
        {
            var r = await ClientePipe.EnviarAsync(p);
            if (!r.Ok) Aviso(r.Mensagem ?? "Não deu certo.");
        }
        catch (Exception ex)
        {
            Aviso("Não foi possível falar com o serviço do Zelo: " + ex.Message);
        }
    }


    private static void Abrir(string alvo)
    {
        try { Process.Start(new ProcessStartInfo(alvo) { UseShellExecute = true }); }
        catch { }
    }

    private void AbrirLink_Click(object sender, RoutedEventArgs e)
    {
        // Só abre endereços https (os detalhes vêm do serviço; nada de executar outra coisa).
        if ((sender as FrameworkElement)?.Tag is string link && link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Abrir(link);
    }

    public async Task Parar() => await EnviarComando(new Pedido { Comando = "cancelar" });

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Abrir(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    // ================================================================ Ajustes

    private async Task CarregarConfigAsync()
    {
        try
        {
            var r = await ClientePipe.EnviarAsync(new Pedido { Comando = "config-obter" });
            if (r.Config is not null) PreencherConfig(r.Config);
        }
        catch
        {
            TxtSalvo.Foreground = Cores.Coral;
            TxtSalvo.Text = "Não foi possível carregar os ajustes (serviço fora do ar).";
        }
    }

    private void PreencherConfig(Configuracao c)
    {
        _carregandoConfig = true;
        try
        {
            _config = c;
            PerfilCasa.IsChecked = c.Perfil == "Domestico";
            PerfilTrabalho.IsChecked = c.Perfil == "Trabalho";
            PerfilJogos.IsChecked = c.Perfil == "Jogos";
            PainelExpediente.Visibility = c.Perfil == "Trabalho" ? Visibility.Visible : Visibility.Collapsed;
            TxtExpInicio.Text = Palavras.Hora(c.ExpedienteInicio);
            TxtExpFim.Text = Palavras.Hora(c.ExpedienteFim);
            foreach (var t in PainelDias.Children.OfType<ToggleButton>())
                t.IsChecked = int.TryParse(t.Tag as string, out var d) && c.DiasExpediente.Contains(d);
            AtualizarResumoExpediente(c);
            ChkTomada.IsChecked = c.SomenteNaTomada;
            ChkOcioso.IsChecked = c.ExigirOcioso;

            _pastas.Clear();
            foreach (var p in c.PastasBackup) _pastas.Add(new ItemPasta { Caminho = p });
            AtualizarDestino(c.DestinoBackup);

            bool temp = !c.TarefasDesativadas.Contains("limpeza-temp");
            ChkTemp.IsChecked = temp;
            ChkLixeira.IsChecked = c.EsvaziarLixeira;
            ChkCache.IsChecked = c.LimparCacheNavegadores;
            ChkDownloads.IsChecked = c.LimparDownloads;
            TxtTituloTemp.Text = $"Arquivos temporários com mais de {c.DiasTemporarios} dias";
            TxtTituloLixeira.Text = $"Lixeira com mais de {c.DiasLixeira} dias";
            TxtTituloDownloads.Text = $"Pasta Downloads: arquivos com mais de {c.DiasDownloads} dias";

            ChkWU.IsChecked = c.InstalarWindowsUpdate;
            ChkWinget.IsChecked = c.AtualizarProgramasWinget;
            ChkDrivers.IsChecked = c.InstalarDriversWU;
            ChkAutoUpdate.IsChecked = c.AtualizacaoAutomatica;

            TxtDiasTemp.Text = c.DiasTemporarios.ToString(CultureInfo.InvariantCulture);
            TxtDiasLixeira.Text = c.DiasLixeira.ToString(CultureInfo.InvariantCulture);
            TxtDiasDownloads.Text = c.DiasDownloads.ToString(CultureInfo.InvariantCulture);
            TxtAvisoDisco.Text = c.LimiteAvisoDisco.ToString(CultureInfo.InvariantCulture);
            TxtCriticoDisco.Text = c.LimiteCriticoDisco.ToString(CultureInfo.InvariantCulture);
            TxtCpu.Text = c.LimiteCpuOcioso.ToString(CultureInfo.InvariantCulture);
            ChkRestauracao.IsChecked = c.CriarPontoRestauracao;
            ChkDefenderCompleto.IsChecked = c.VarreduraCompletaDefender;

            foreach (var t in _tarefasConfig) t.Ativa = !c.TarefasDesativadas.Contains(t.Id);
            ListaTarefasConfig.ItemsSource = null;
            ListaTarefasConfig.ItemsSource = _tarefasConfig;
        }
        finally
        {
            _carregandoConfig = false;
        }
    }

    private void AtualizarDestino(string destino)
    {
        string texto = string.IsNullOrWhiteSpace(destino)
            ? "Nenhum lugar escolhido ainda. Um disco externo ou uma pasta do OneDrive ou Google Drive (que guardam versões antigas) é o ideal."
            : destino;
        TxtDestino.Text = texto;
        BvTxtDestino.Text = texto;
        BtnDestino.Content = string.IsNullOrWhiteSpace(destino) ? "Escolher destino" : "Trocar destino";
    }

    private void AtualizarResumoExpediente(Configuracao c)
    {
        TxtResumoExpediente.Text = c.DiasExpediente.Count == 0
            ? "Nenhum dia marcado: o Zelo trabalha a qualquer hora."
            : $"Nos dias de trabalho, o Zelo cuida do PC das {Palavras.Hora(c.ExpedienteFim)} às {Palavras.Hora(c.ExpedienteInicio)}"
              + (c.DiasExpediente.Count == 7 ? "." : ". Nos outros dias, a qualquer hora.");
    }

    /// <summary>Monta a configuração a partir da tela, preservando o que a tela não mostra.</summary>
    private Configuracao LerConfigDaTela()
    {
        var c = _config is null ? new Configuracao() : Json.Desserializar<Configuracao>(Json.Serializar(_config)) ?? new Configuracao();
        static int Num(string texto, int padrao) =>
            int.TryParse(new string(texto.Trim().TakeWhile(char.IsDigit).ToArray()), out var v) ? v : padrao;

        c.ExpedienteInicio = Palavras.LerHora(TxtExpInicio.Text) ?? c.ExpedienteInicio;
        c.ExpedienteFim = Palavras.LerHora(TxtExpFim.Text) ?? c.ExpedienteFim;
        c.DiasExpediente = PainelDias.Children.OfType<ToggleButton>()
            .Where(t => t.IsChecked == true)
            .Select(t => int.Parse((string)t.Tag, CultureInfo.InvariantCulture))
            .OrderBy(d => d).ToList();
        c.SomenteNaTomada = ChkTomada.IsChecked == true;
        c.ExigirOcioso = ChkOcioso.IsChecked == true;

        c.PastasBackup = _pastas.Select(p => p.Caminho).ToList();

        c.LimparCacheNavegadores = ChkCache.IsChecked == true;
        c.LimparDownloads = ChkDownloads.IsChecked == true;
        c.InstalarWindowsUpdate = ChkWU.IsChecked == true;
        c.AtualizarProgramasWinget = ChkWinget.IsChecked == true;
        c.InstalarDriversWU = ChkDrivers.IsChecked == true;
        c.AtualizacaoAutomatica = ChkAutoUpdate.IsChecked == true;

        c.DiasTemporarios = Num(TxtDiasTemp.Text, c.DiasTemporarios);
        c.DiasLixeira = Num(TxtDiasLixeira.Text, c.DiasLixeira);
        c.DiasDownloads = Num(TxtDiasDownloads.Text, c.DiasDownloads);
        c.LimiteAvisoDisco = Num(TxtAvisoDisco.Text, c.LimiteAvisoDisco);
        c.LimiteCriticoDisco = Num(TxtCriticoDisco.Text, c.LimiteCriticoDisco);
        c.LimiteCpuOcioso = Num(TxtCpu.Text, c.LimiteCpuOcioso);
        c.CriarPontoRestauracao = ChkRestauracao.IsChecked == true;
        c.VarreduraCompletaDefender = ChkDefenderCompleto.IsChecked == true;
        c.EsvaziarLixeira = ChkLixeira.IsChecked == true;

        // Trocar de perfil ajusta o limite de processador; vem por último para valer sobre o campo.
        var perfil = PerfilTrabalho.IsChecked == true ? "Trabalho" : PerfilJogos.IsChecked == true ? "Jogos" : "Domestico";
        if (perfil != c.Perfil) c.AplicarPerfil(perfil);

        // Interruptores de limpeza e a lista avançada mexem nas mesmas tarefas: a lista é a base.
        var desligadas = _tarefasConfig.Where(t => !t.Ativa).Select(t => t.Id).ToHashSet();
        void Define(string id, bool ligado) { if (ligado) desligadas.Remove(id); else desligadas.Add(id); }
        Define("limpeza-temp", ChkTemp.IsChecked == true);
        c.TarefasDesativadas = desligadas.ToList();
        return c;
    }

    private void AgendarSalvar()
    {
        if (_carregandoConfig || _config is null) return;
        TxtSalvo.Foreground = Cores.Suave;
        TxtSalvo.Text = "Salvando…";
        _salvarTimer.Stop();
        _salvarTimer.Start();
    }

    private bool _salvando, _salvarDeNovo;

    /// <summary>
    /// Um salvamento por vez, sempre com o que está na tela no momento do envio. Se a pessoa mudar
    /// algo enquanto um salvamento espera (por exemplo, a confirmação do Windows), salva de novo no fim;
    /// a resposta antiga nunca sobrescreve a tela mais nova.
    /// </summary>
    private async Task SalvarConfigAsync()
    {
        if (_salvando) { _salvarDeNovo = true; return; }
        _salvando = true;
        try
        {
            do
            {
                _salvarDeNovo = false;
                var c = LerConfigDaTela();
                var r = await Elevacao.EnviarAsync(new Pedido { Comando = "config-salvar", Config = c });
                if (!r.Ok)
                {
                    TxtSalvo.Foreground = Cores.Coral;
                    TxtSalvo.Text = r.Mensagem ?? "Não foi possível salvar.";
                    _salvarDeNovo = false;
                    await CarregarConfigAsync(); // volta a tela ao que está valendo
                    return;
                }
                if (!_salvarDeNovo && r.Config is not null) PreencherConfig(r.Config);
                TxtSalvo.Foreground = Cores.Aurora;
                TxtSalvo.Text = $"✓ Salvo às {DateTime.Now:HH:mm}";
                if (_estado is not null) MostrarAgenda(_estado);
            } while (_salvarDeNovo);
        }
        catch (Exception ex)
        {
            TxtSalvo.Foreground = Cores.Coral;
            TxtSalvo.Text = "Serviço indisponível: " + ex.Message;
        }
        finally
        {
            _salvando = false;
        }
    }

    private void Ajuste_Click(object sender, RoutedEventArgs e) => AgendarSalvar();

    private void Numero_LostFocus(object sender, RoutedEventArgs e) => AgendarSalvar();

    private void Perfil_Checked(object sender, RoutedEventArgs e)
    {
        if (PainelExpediente is null) return;
        PainelExpediente.Visibility = PerfilTrabalho.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AgendarSalvar();
    }

    private void Expediente_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox caixa || _config is null) return;
        var hora = Palavras.LerHora(caixa.Text);
        if (hora is null)
        {
            caixa.Text = Palavras.Hora(caixa == TxtExpInicio ? _config.ExpedienteInicio : _config.ExpedienteFim);
            return;
        }
        caixa.Text = Palavras.Hora(hora.Value);
        AtualizarResumoExpediente(LerConfigDaTela());
        AgendarSalvar();
    }

    private void Dia_Click(object sender, RoutedEventArgs e)
    {
        AtualizarResumoExpediente(LerConfigDaTela());
        AgendarSalvar();
    }

    private void Avancado_Click(object sender, RoutedEventArgs e)
    {
        bool aberto = BtnAvancado.IsChecked == true;
        PainelAvancado.Visibility = aberto ? Visibility.Visible : Visibility.Collapsed;
        BtnAvancado.Content = aberto ? "Ocultar opções avançadas" : "Mostrar opções avançadas";
    }

    private void AdicionarPasta_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Pastas para incluir no backup", Multiselect = true };
        if (d.ShowDialog(this) != true) return;
        foreach (var p in d.FolderNames)
            if (!_pastas.Any(x => x.Caminho.Equals(p, StringComparison.OrdinalIgnoreCase)))
                _pastas.Add(new ItemPasta { Caminho = p });
        AgendarSalvar();
    }

    private void RemoverPasta_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string caminho) return;
        var item = _pastas.FirstOrDefault(p => p.Caminho == caminho);
        if (item is not null) _pastas.Remove(item);
        AgendarSalvar();
    }

    private void AbrirVersoes_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null || string.IsNullOrWhiteSpace(_config.DestinoBackup))
        {
            Aviso("Escolha primeiro onde guardar o backup.");
            return;
        }
        var pasta = Zelo.Core.Tarefas.BackupIncremental.PastaVersoes(_config.DestinoBackup);
        if (!Directory.Exists(pasta))
        {
            Aviso("Ainda não há versões anteriores: elas aparecem depois que algum arquivo copiado mudar." +
                  (Directory.Exists(System.IO.Path.GetPathRoot(_config.DestinoBackup) ?? "") ? "" : "\n\nO disco do backup parece desconectado."));
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{pasta}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Aviso("Não foi possível abrir a pasta: " + ex.Message); }
    }

    private void EscolherDestino_Click(object sender, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = "Onde guardar a cópia dos seus arquivos" };
        if (d.ShowDialog(this) != true || _config is null) return;
        _config.DestinoBackup = d.FolderName;
        AtualizarDestino(d.FolderName);
        AgendarSalvar();
    }

    // ================================================================ Boas-vindas

    private void AbrirBoasVindas()
    {
        _passoBv = 1;
        if (_config is not null)
        {
            BvCasa.IsChecked = _config.Perfil == "Domestico";
            BvTrabalho.IsChecked = _config.Perfil == "Trabalho";
            BvJogos.IsChecked = _config.Perfil == "Jogos";
            BvExpInicio.Text = Palavras.Hora(_config.ExpedienteInicio);
            BvExpFim.Text = Palavras.Hora(_config.ExpedienteFim);
        }
        MostrarPassoBv();
        BoasVindas.Visibility = Visibility.Visible;
        EntrarPagina(BoasVindas);
    }

    private void MostrarPassoBv()
    {
        BvPasso1.Visibility = _passoBv == 1 ? Visibility.Visible : Visibility.Collapsed;
        BvPasso2.Visibility = _passoBv == 2 ? Visibility.Visible : Visibility.Collapsed;
        BvPasso3.Visibility = _passoBv == 3 ? Visibility.Visible : Visibility.Collapsed;
        BvBarra2.Background = _passoBv >= 2 ? Cores.Luar : Cores.LinhaForte;
        BvBarra3.Background = _passoBv >= 3 ? Cores.Luar : Cores.LinhaForte;
        BvPassoTexto.Text = $"Passo {_passoBv} de 3";
        if (_passoBv == 3)
        {
            // Mostra o que vale hoje; a pessoa confirma ou muda antes de começar.
            BvWU.IsChecked = ChkWU.IsChecked;
            BvWinget.IsChecked = ChkWinget.IsChecked;
            BvDrivers.IsChecked = ChkDrivers.IsChecked;
            BvAutoUpdate.IsChecked = ChkAutoUpdate.IsChecked;
        }
        BvVoltar.Content = _passoBv == 1 ? "Fazer isso depois" : "Voltar";
        BvAvancar.Content = _passoBv switch
        {
            2 => _pastas.Count > 0 ? "Continuar" : "Pular por enquanto",
            3 => "Ver a simulação",
            _ => "Continuar",
        };
        BvErro.Text = "";
        BvPainelExpediente.Visibility = BvTrabalho.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BvPerfil_Checked(object sender, RoutedEventArgs e)
    {
        if (BvPainelExpediente is null) return;
        BvPainelExpediente.Visibility = BvTrabalho.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BvVoltar_Click(object sender, RoutedEventArgs e)
    {
        if (_passoBv == 1) { FecharBoasVindas(); return; }
        _passoBv--;
        MostrarPassoBv();
    }

    private async void BvAvancar_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) { FecharBoasVindas(); return; }
        if (_passoBv == 1)
        {
            if (BvTrabalho.IsChecked == true)
            {
                var ini = Palavras.LerHora(BvExpInicio.Text);
                var fim = Palavras.LerHora(BvExpFim.Text);
                if (ini is null || fim is null || ini == fim)
                {
                    BvErro.Text = "Confira o horário (ex.: 9h às 18h).";
                    return;
                }
                PerfilTrabalho.IsChecked = true;
                TxtExpInicio.Text = Palavras.Hora(ini.Value);
                TxtExpFim.Text = Palavras.Hora(fim.Value);
            }
            else if (BvJogos.IsChecked == true) PerfilJogos.IsChecked = true;
            else PerfilCasa.IsChecked = true;
            AtualizarResumoExpediente(LerConfigDaTela());
            AgendarSalvar();
        }
        else if (_passoBv == 3)
        {
            bool mudou = ChkWU.IsChecked != BvWU.IsChecked || ChkWinget.IsChecked != BvWinget.IsChecked
                         || ChkDrivers.IsChecked != BvDrivers.IsChecked || ChkAutoUpdate.IsChecked != BvAutoUpdate.IsChecked;
            if (mudou)
            {
                ChkWU.IsChecked = BvWU.IsChecked;
                ChkWinget.IsChecked = BvWinget.IsChecked;
                ChkDrivers.IsChecked = BvDrivers.IsChecked;
                ChkAutoUpdate.IsChecked = BvAutoUpdate.IsChecked;
                await SalvarConfigAsync();
            }
            FecharBoasVindas();
            await ExecutarRotina(Rotina.Diaria, simulacao: true);
            return;
        }
        _passoBv++;
        MostrarPassoBv();
    }

    private void FecharBoasVindas()
    {
        BoasVindas.Visibility = Visibility.Collapsed;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ArquivoBoasVindas)!);
            File.WriteAllText(ArquivoBoasVindas, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch { }
    }

    // ================================================================ Capturas de tela (CI)

    /// <summary>Preenche a tela com dados de exemplo e salva PNGs de cada tela. Usado só no GitHub Actions.</summary>
    public async Task CapturarAsync(string pasta)
    {
        Directory.CreateDirectory(pasta);
        var agora = DateTime.Now;
        var cfg = new Configuracao
        {
            Perfil = "Trabalho", ExpedienteInicio = 22, ExpedienteFim = 6,
            PastasBackup = new() { @"C:\Users\Ana\Documents", @"C:\Users\Ana\Pictures" },
        };
        cfg.Validar();
        PreencherConfig(cfg);

        ResultadoTarefa R(string id, string nome, Categoria c, StatusTarefa s, string resumo, double horas) => new()
        {
            TarefaId = id, Nome = nome, Categoria = c, Status = s, Resumo = resumo,
            Inicio = agora.AddHours(-horas), Fim = agora.AddHours(-horas).AddSeconds(12),
        };
        _historico = Enumerable.Range(0, 12).Select(i => new ExecucaoRotina
        {
            Id = "ex" + i, Rotina = i % 4 == 0 ? Rotina.Semanal : Rotina.Diaria, Simulacao = i == 3,
            Inicio = agora.AddDays(-(11 - i) * 2.5).AddMinutes(-3), Fim = agora.AddDays(-(11 - i) * 2.5),
            Score = i == 0 ? 49 : (int)(70 + i * 1.5),
            Resultados = new()
            {
                R("defender-rapida", "Antivírus", Categoria.Seguranca, StatusTarefa.Ok, "Protegido por Malwarebytes.", 2),
                R("espaco-disco", "Espaço livre em disco", Categoria.Disco, i > 5 ? StatusTarefa.Aviso : StatusTarefa.Ok, "O disco A: está quase cheio: restam 15,7 GB (86% em uso).", 2),
                new ResultadoTarefa { TarefaId = "drivers-antigos", Nome = "Drivers muito antigos", Categoria = Categoria.Sistema, Status = StatusTarefa.Ok,
                    Resumo = "2 driver(s) com mais de 4 anos. Veja nos detalhes a versão instalada e onde atualizar.", Inicio = agora, Fim = agora,
                    Detalhes = new() { "Sua placa-mãe: ASRock B450M Steel Legend", "Realtek High Definition Audio — Realtek, versão 6.0.1.8 de 12/03/2019",
                        "   Onde atualizar: página de suporte da sua placa-mãe ASRock B450M Steel Legend — https://www.asrock.com/support/index.asp" } },
            },
        }).ToList();
        MostrarHistorico();

        var saude = new PainelSaude
        {
            Geral = 84, Cobertura = 5,
            Areas = new()
            {
                ["Disco"] = new ResumoArea { Status = StatusTarefa.Aviso, Texto = "O disco A: está quase cheio: restam 15,7 GB (86% em uso)." },
                ["Seguranca"] = new ResumoArea { Status = StatusTarefa.Ok, Texto = "Protegido por Malwarebytes." },
                ["Atualizacoes"] = new ResumoArea { Status = StatusTarefa.Ok, Texto = "Windows em dia, nenhuma atualização pendente." },
                ["Backup"] = new ResumoArea { Status = StatusTarefa.Aviso, Texto = "Seus arquivos ainda não têm cópia." },
                ["Sistema"] = new ResumoArea { Status = StatusTarefa.Ok, Texto = "7 erro(s) comum(ns), nada grave." },
            },
            Pontos = new()
            {
                new PontoAtencao { TarefaId = "auditoria-seguranca", Nome = "Auditoria de segurança", Categoria = Categoria.Seguranca, Status = StatusTarefa.Critico, Resumo = "Novo(s) administrador(es): StargatePC\\eusou.", Quando = agora.AddHours(-2) },
                new PontoAtencao { TarefaId = "eventos-criticos", Nome = "Eventos críticos", Categoria = Categoria.Sistema, Status = StatusTarefa.Aviso, Resumo = "1× desligamento inesperado nas últimas 24 horas.", Quando = agora.AddHours(-2) },
                new PontoAtencao { TarefaId = "inicializacao", Nome = "Programas que iniciam com o Windows", Categoria = Categoria.Sistema, Status = StatusTarefa.Aviso, Resumo = "2 programas novos passaram a abrir com o Windows: Discord e Steam.", Quando = agora.AddDays(-3),
                    Detalhes = new() { "Novos desde a última revisão:", "NOVO · Discord (eusou) · instalado em 02/11/2026 — C:\\Users\\eusou\\AppData\\Local\\Discord\\Update.exe", "NOVO · Steam (eusou) · instalado em 10/11/2026 — C:\\Program Files (x86)\\Steam\\steam.exe", "Todos os que abrem com o Windows:", "SecurityHealth (todos) — C:\\Windows\\System32\\SecurityHealthSystray.exe" } },
                new PontoAtencao { TarefaId = "winget", Nome = "Atualizar programas (winget)", Categoria = Categoria.Atualizacoes, Status = StatusTarefa.Erro, Resumo = "Interrompida após 90 minutos (tempo limite).", Quando = agora.AddDays(-3) },
                new PontoAtencao { TarefaId = "backup-incremental", Nome = "Backup incremental", Categoria = Categoria.Backup, Status = StatusTarefa.Aviso, Resumo = "Seus arquivos ainda não têm cópia. Escolha as pastas e onde guardar em Ajustes.", Quando = agora.AddHours(-2) },
                new PontoAtencao { TarefaId = "espaco-disco", Nome = "Espaço livre em disco", Categoria = Categoria.Disco, Status = StatusTarefa.Aviso, Resumo = "O disco A: está quase cheio: restam 15,7 GB (86% em uso).", Quando = agora.AddHours(-2) },
            },
        };
        var estado = new EstadoServico
        {
            Versao = "1.2.0", Saude = saude, Perfil = cfg.Perfil, DentroDaJanela = true,
            Ultima = new() { ["Diaria"] = agora.AddHours(-2), ["Semanal"] = agora.AddDays(-3), ["Mensal"] = agora.AddDays(-14) },
            Proxima = new() { ["Diaria"] = agora.AddHours(18), ["Semanal"] = agora.AddDays(3.5), ["Mensal"] = agora.AddDays(15) },
            UltimaVerificacaoAtualizacao = agora.AddHours(-1),
        };
        _estado = estado;

        async Task Esperar()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(300);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        }

        NavInicio.IsChecked = true;
        MostrarEstado(estado);
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "1-inicio.png"));
        SalvarConteudo(ConteudoInicio, System.IO.Path.Combine(pasta, "1b-inicio-inteiro.png"));

        var rodando = Json.Desserializar<EstadoServico>(Json.Serializar(estado))!;
        rodando.RotinaAtual = Rotina.Diaria; rodando.Indice = 4; rodando.Total = 7; rodando.TarefaAtual = "Procurando erros graves no sistema";
        rodando.Etapas = new()
        {
            new EtapaAtual { Nome = "Conferindo o antivírus", Status = StatusTarefa.Ok, Resumo = "Protegido por Malwarebytes.", Segundos = 3 },
            new EtapaAtual { Nome = "Checando a saúde dos discos", Status = StatusTarefa.Ok, Resumo = "2 disco(s) saudável(is).", Segundos = 11 },
            new EtapaAtual { Nome = "Medindo o espaço livre", Status = StatusTarefa.Aviso, Resumo = "O disco A: está quase cheio." },
            new EtapaAtual { Nome = "Procurando erros graves no sistema", EmAndamento = true },
            new EtapaAtual { Nome = "Vendo se o Windows pediu para reiniciar" },
            new EtapaAtual { Nome = "Limpando arquivos temporários antigos" },
            new EtapaAtual { Nome = "Copiando seus arquivos (backup)" },
        };
        MostrarEstado(rodando);
        await Esperar();
        MostrarEstado(rodando); // a barra de progresso usa a largura já medida
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "2-cuidando.png"));
        MostrarEstado(estado);

        NavHistorico.IsChecked = true;
        await Esperar();
        DesenharGrafico(animar: false);
        if (ListaGrupos.ItemsSource is List<GrupoExecucoes> g && g.Count > 0 && g[0].Itens.Count > 0) g[0].Itens[0].Expandido = true;
        await Esperar();
        SalvarConteudo(ConteudoHistorico, System.IO.Path.Combine(pasta, "3-historico.png"));

        NavAjustes.IsChecked = true;
        BtnAvancado.IsChecked = true;
        Avancado_Click(this, new RoutedEventArgs());
        await Esperar();
        SalvarConteudo(ConteudoAjustes, System.IO.Path.Combine(pasta, "4-ajustes.png"));

        NavInicio.IsChecked = true;
        AbrirArea(Categoria.Seguranca);
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "8-detalhe-area.png"));
        PainelArea.Visibility = Visibility.Collapsed;

        NavOtimizar.IsChecked = true;
        MontarOtimizacaoDeExemplo();
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "7-otimizar.png"));
        SalvarConteudo(ConteudoOtimizar, System.IO.Path.Combine(pasta, "7b-otimizar-inteiro.png"));

        NavRegistro.IsChecked = true;
        MontarRegistroDeExemplo();
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "9-registro.png"));

        NavInicio.IsChecked = true;
        AbrirBoasVindas();
        BvTrabalho.IsChecked = true;
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "5-boas-vindas.png"));
        _passoBv = 2; MostrarPassoBv();
        await Esperar();
        SalvarPng(this, System.IO.Path.Combine(pasta, "6-boas-vindas-backup.png"));
    }

    private static void SalvarPng(Visual alvo, string arquivo)
    {
        var el = (FrameworkElement)alvo;
        int w = (int)Math.Ceiling(el.ActualWidth), h = (int)Math.Ceiling(el.ActualHeight);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(alvo);
        Gravar(rtb, arquivo);
    }

    /// <summary>Renderiza o conteúdo inteiro de uma página (além da parte visível), com o fundo.</summary>
    private void SalvarConteudo(FrameworkElement el, string arquivo)
    {
        int w = (int)Math.Ceiling(el.ActualWidth + 80), h = (int)Math.Ceiling(el.ActualHeight + 80);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("Noite"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(40, 40, el.ActualWidth, el.ActualHeight));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        Gravar(rtb, arquivo);
    }

    private static void Gravar(System.Windows.Media.Imaging.BitmapSource bmp, string arquivo)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(arquivo);
        enc.Save(fs);
    }
}
