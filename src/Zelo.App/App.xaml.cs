using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Zelo.Core;
using Forms = System.Windows.Forms;
using Desenho = System.Drawing;

namespace Zelo.App;

public partial class App : Application
{
    private Mutex? _instanciaUnica;
    private Forms.NotifyIcon? _icone;
    private Forms.ToolStripMenuItem? _itemCabecalho;
    private Forms.ToolStripMenuItem? _itemPausa;
    private MainWindow? _janela;
    private string _iconeAtual = "";
    private bool _pausado;

    private static string ArquivoVisto => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelo", "ultima-notificada.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanciaUnica = new Mutex(true, @"Local\Zelo.Painel", out bool primeira);
        if (!primeira)
        {
            MessageBox.Show("O Zelo já está aberto. Procure o ícone da lua perto do relógio.", "Zelo");
            Shutdown();
            return;
        }

        _janela = new MainWindow();
        _janela.NovaExecucao += Notificar;
        _janela.EstadoMudou += AtualizarBandeja;
        CriarIconeBandeja();

        // Iniciado junto com o Windows: fica só na bandeja.
        if (!e.Args.Contains("--bandeja")) MostrarJanela();
        else _ = _janela.AtualizarAsync();

        // Mantém o estado em dia mesmo com a janela escondida (ícone e avisos).
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) => { if (_janela is { IsVisible: false }) await _janela.AtualizarAsync(); };
        timer.Start();
    }

    private static Desenho.Icon CarregarIcone(string nome)
    {
        var info = GetResourceStream(new Uri($"pack://application:,,,/Assets/{nome}.ico"));
        using var s = info.Stream;
        return new Desenho.Icon(s, Forms.SystemInformation.SmallIconSize);
    }

    private void CriarIconeBandeja()
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new CoresMenu()),
            ShowImageMargin = false,
            BackColor = Desenho.Color.FromArgb(0x1F, 0x25, 0x35),
            ForeColor = Desenho.Color.FromArgb(0xE8, 0xEC, 0xF5),
            Font = new Desenho.Font("Segoe UI", 10f),
            Padding = new Forms.Padding(4),
        };
        _itemCabecalho = new Forms.ToolStripMenuItem("Zelo") { Enabled = false };
        menu.Items.Add(_itemCabecalho);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Abrir o Zelo", null, (_, _) => MostrarJanela());
        menu.Items.Add("Cuidar agora", null, async (_, _) =>
        {
            if (_janela is not null) await _janela.ExecutarRotina(Rotina.Diaria);
        });
        _itemPausa = new Forms.ToolStripMenuItem("Pausar por 1 hora", null, async (_, _) =>
        {
            if (_janela is not null) await _janela.Pausar(_pausado ? 0 : 60);
        });
        menu.Items.Add(_itemPausa);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Fechar o painel (o Zelo continua cuidando)", null, (_, _) => Sair());
        foreach (Forms.ToolStripItem i in menu.Items) i.Padding = new Forms.Padding(8, 6, 8, 6);

        _icone = new Forms.NotifyIcon
        {
            Icon = CarregarIcone("bandeja-neutro"),
            Text = "Zelo — conectando…",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _iconeAtual = "bandeja-neutro";
        _icone.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) MostrarJanela(); };
        _icone.BalloonTipClicked += (_, _) => MostrarJanela();
    }

    /// <summary>O ponto do ícone muda de cor com a saúde, e o menu mostra o resumo.</summary>
    private void AtualizarBandeja(EstadoServico? e)
    {
        if (_icone is null) return;
        string nome;
        string resumo;
        if (e is null)
        {
            nome = "bandeja-neutro";
            resumo = "Serviço do Zelo fora do ar";
        }
        else
        {
            var s = e.Saude;
            bool urgente = s.Pontos.Any(p => p.Status == StatusTarefa.Critico && !p.Desatualizado);
            nome = s.Geral < 0 ? "bandeja-neutro" : urgente || s.Geral < 50 ? "bandeja-urgente" : s.Geral < 80 || s.Pontos.Count > 0 ? "bandeja-atencao" : "bandeja-ok";
            resumo = e.RotinaAtual is not null ? $"{Palavras.Rotina(e.RotinaAtual.Value)} em andamento"
                : s.Geral < 0 ? "Ainda sem vistoria"
                : urgente ? $"Saúde {s.Geral} · há algo urgente"
                : s.Pontos.Count > 0 ? $"Saúde {s.Geral} · {s.Pontos.Count} ponto(s) de atenção"
                : $"Saúde {s.Geral} · tudo em ordem";
            _pausado = e.PausadoAte is not null;
            if (_itemPausa is not null)
                _itemPausa.Text = _pausado ? $"Retomar agora (pausado até {e.PausadoAte:HH:mm})" : "Pausar por 1 hora";
        }
        if (nome != _iconeAtual)
        {
            var antigo = _icone.Icon;
            _icone.Icon = CarregarIcone(nome);
            antigo?.Dispose();
            _iconeAtual = nome;
        }
        var dica = "Zelo — " + resumo;
        _icone.Text = dica.Length > 127 ? dica[..127] : dica;
        if (_itemCabecalho is not null) _itemCabecalho.Text = resumo;
    }

    private void MostrarJanela()
    {
        if (_janela is null) return;
        _janela.Show();
        if (_janela.WindowState == WindowState.Minimized) _janela.WindowState = WindowState.Normal;
        _janela.Activate();
    }

    /// <summary>Avisa (uma vez por execução) quando uma vistoria termina com algo a resolver.</summary>
    private void Notificar(ExecucaoRotina exec)
    {
        if (_icone is null || exec.Simulacao) return;
        try
        {
            var vista = File.Exists(ArquivoVisto) ? File.ReadAllText(ArquivoVisto).Trim() : "";
            if (vista == exec.Id) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ArquivoVisto)!);
            File.WriteAllText(ArquivoVisto, exec.Id);
        }
        catch { }

        var problemas = exec.Resultados
            .Where(r => r.Status is StatusTarefa.Critico or StatusTarefa.Aviso or StatusTarefa.Erro)
            .OrderBy(r => r.Status == StatusTarefa.Critico ? 0 : 1)
            .ToList();
        var rotina = Palavras.Rotina(exec.Rotina);

        if (problemas.Count == 0)
        {
            // Sem problemas, só avisa quando foi você quem pediu; as automáticas ficam em silêncio.
            if (exec.Origem == "manual")
                _icone.ShowBalloonTip(5000, $"{rotina} concluída", $"Tudo certo. Saúde: {exec.Score}.", Forms.ToolTipIcon.None);
            return;
        }

        var primeiro = problemas[0];
        bool urgente = primeiro.Status == StatusTarefa.Critico;
        var texto = primeiro.Resumo + (problemas.Count > 1 ? $" (e mais {problemas.Count - 1})" : "");
        _icone.ShowBalloonTip(10000,
            urgente ? "Zelo · urgente" : "Zelo · algo merece um olhar",
            texto.Length > 220 ? texto[..220] + "…" : texto,
            urgente ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.None);
    }

    private void Sair()
    {
        if (_icone is not null) { _icone.Visible = false; _icone.Dispose(); }
        if (_janela is not null) { _janela.Encerrando = true; _janela.Close(); }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _icone?.Dispose();
        _instanciaUnica?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Cores escuras para o menu da bandeja.</summary>
    private sealed class CoresMenu : Forms.ProfessionalColorTable
    {
        private static readonly Desenho.Color Fundo = Desenho.Color.FromArgb(0x1F, 0x25, 0x35);
        private static readonly Desenho.Color Destaque = Desenho.Color.FromArgb(0x2E, 0x36, 0x50);
        private static readonly Desenho.Color Borda = Desenho.Color.FromArgb(0x2E, 0x36, 0x50);

        public override Desenho.Color ToolStripDropDownBackground => Fundo;
        public override Desenho.Color MenuBorder => Borda;
        public override Desenho.Color MenuItemBorder => Destaque;
        public override Desenho.Color MenuItemSelected => Destaque;
        public override Desenho.Color MenuItemSelectedGradientBegin => Destaque;
        public override Desenho.Color MenuItemSelectedGradientEnd => Destaque;
        public override Desenho.Color SeparatorDark => Borda;
        public override Desenho.Color SeparatorLight => Fundo;
        public override Desenho.Color ImageMarginGradientBegin => Fundo;
        public override Desenho.Color ImageMarginGradientMiddle => Fundo;
        public override Desenho.Color ImageMarginGradientEnd => Fundo;
    }
}
