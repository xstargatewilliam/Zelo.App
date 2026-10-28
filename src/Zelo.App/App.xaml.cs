using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Zelo.Core;
using Forms = System.Windows.Forms;

namespace Zelo.App;

public partial class App : Application
{
    private Mutex? _instanciaUnica;
    private Forms.NotifyIcon? _icone;
    private MainWindow? _janela;

    private static string ArquivoVisto => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelo", "ultima-notificada.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanciaUnica = new Mutex(true, @"Local\Zelo.Painel", out bool primeira);
        if (!primeira)
        {
            // Já existe um painel aberto (provavelmente na bandeja).
            MessageBox.Show("O Zelo já está aberto. Procure o ícone do escudo perto do relógio.", "Zelo");
            Shutdown();
            return;
        }

        _janela = new MainWindow();
        _janela.NovaExecucao += Notificar;
        CriarIconeBandeja();

        // Iniciado junto com o Windows: fica só na bandeja.
        if (!e.Args.Contains("--bandeja")) MostrarJanela();
        else _ = _janela.AtualizarAsync(); // ainda carrega o estado para poder notificar

        // Mantém o estado atualizado mesmo com a janela escondida (para as notificações).
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) => { if (_janela is { IsVisible: false }) await _janela.AtualizarAsync(); };
        timer.Start();
    }

    private void CriarIconeBandeja()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir o Zelo", null, (_, _) => MostrarJanela());
        menu.Items.Add("Executar rotina diária agora", null, async (_, _) =>
        {
            if (_janela is not null) await _janela.ExecutarRotina(Rotina.Diaria);
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Fechar o painel (o serviço continua)", null, (_, _) => Sair());

        _icone = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "Zelo — Manutenção do Windows",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icone.MouseClick += (_, ev) => { if (ev.Button == Forms.MouseButtons.Left) MostrarJanela(); };
        _icone.BalloonTipClicked += (_, _) => MostrarJanela();
    }

    private void MostrarJanela()
    {
        if (_janela is null) return;
        _janela.Show();
        if (_janela.WindowState == WindowState.Minimized) _janela.WindowState = WindowState.Normal;
        _janela.Activate();
    }

    /// <summary>Avisa (uma vez por execução) quando uma rotina termina com problemas.</summary>
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

        if (problemas.Count == 0)
        {
            // Rotina sem problemas: só avisa se o usuário pediu (manual).
            if (exec.Origem == "manual")
                _icone.ShowBalloonTip(5000, $"Rotina {exec.Rotina.Nome().ToLowerInvariant()} concluída",
                    $"Tudo certo. Saúde do PC: {exec.Score}/100.", Forms.ToolTipIcon.Info);
            return;
        }

        var primeiro = problemas[0];
        var texto = primeiro.Resumo + (problemas.Count > 1 ? $" (+{problemas.Count - 1} outro(s) item(ns))" : "");
        var critico = primeiro.Status == StatusTarefa.Critico;
        _icone.ShowBalloonTip(10000,
            critico ? $"Zelo: {primeiro.Nome}" : "Zelo: alguns itens precisam de atenção",
            texto.Length > 200 ? texto[..200] + "…" : texto,
            critico ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
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
}
