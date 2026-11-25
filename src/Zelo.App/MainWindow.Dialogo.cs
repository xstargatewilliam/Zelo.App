using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Zelo.Core;

namespace Zelo.App;

/// <summary>Uma linha do painel de resultados: o que deu certo, o que não deu e o que fazer.</summary>
public sealed class LinhaResultado
{
    public string Texto { get; init; } = "";
    public string Detalhe { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Aurora;
    public Brush CorFundo { get; init; } = Cores.AuroraFundo;
    public Geometry Icone { get; init; } = Icones.Ok;
    /// <summary>Mesmos códigos dos botões dos pontos de atenção (abrir:, cmd:, tarefa:…).</summary>
    public string AcaoTexto { get; init; } = "";
    public string AcaoId { get; init; } = "";
    public Visibility VisDetalhe => Detalhe.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisAcao => AcaoId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static LinhaResultado Certo(string texto, string detalhe = "") => new() { Texto = texto, Detalhe = detalhe };

    public static LinhaResultado Falhou(string texto, string detalhe = "", string acaoTexto = "", string acaoId = "") => new()
    {
        Texto = texto, Detalhe = detalhe, Cor = Cores.Coral, CorFundo = Cores.CoralFundo, Icone = Icones.Urgente,
        AcaoTexto = acaoTexto, AcaoId = acaoId,
    };

    public static LinhaResultado Info(string texto, string detalhe = "") => new()
    {
        Texto = texto, Detalhe = detalhe, Cor = Cores.Luar, CorFundo = Cores.NeutroFundo, Icone = Icones.Atencao,
    };
}

public enum TomDialogo { Pergunta, Sucesso, Atencao, Erro }

public partial class MainWindow
{
    private TaskCompletionSource<bool>? _respostaDialogo;
    private readonly DispatcherTimer _timerNotificacao = new() { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>
    /// Mostra o painel do Zelo (em vez de uma janela do Windows) e espera a resposta.
    /// <paramref name="nao"/> nulo = só um botão. Esc ou o botão secundário = false.
    /// </summary>
    private Task<bool> PerguntarAsync(string titulo, string texto, IEnumerable<LinhaResultado>? linhas = null,
        string sim = "Continuar", string? nao = "Cancelar", TomDialogo tom = TomDialogo.Pergunta)
    {
        _respostaDialogo?.TrySetResult(false);
        _respostaDialogo = new TaskCompletionSource<bool>();
        TxtDialogoTitulo.Text = titulo;
        TxtDialogoTexto.Text = texto;
        TxtDialogoTexto.Visibility = string.IsNullOrWhiteSpace(texto) ? Visibility.Collapsed : Visibility.Visible;
        var lista = linhas?.ToList() ?? new List<LinhaResultado>();
        ListaDialogo.ItemsSource = lista;
        ListaDialogo.Visibility = lista.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnDialogoSim.Content = sim;
        BtnDialogoNao.Content = nao ?? "";
        BtnDialogoNao.Visibility = nao is null ? Visibility.Collapsed : Visibility.Visible;
        var (icone, cor, fundo) = tom switch
        {
            TomDialogo.Sucesso => (Icones.Ok, (Brush)Cores.Aurora, (Brush)Cores.AuroraFundo),
            TomDialogo.Atencao => (Icones.Atencao, Cores.Ambar, Cores.AmbarFundo),
            TomDialogo.Erro => (Icones.Urgente, Cores.Coral, Cores.CoralFundo),
            _ => (Icones.Atencao, Cores.Luar, Cores.NeutroFundo),
        };
        IconeDialogo.Data = icone;
        IconeDialogo.Stroke = cor;
        IconeDialogoFundo.Background = fundo;
        PainelDialogo.Visibility = Visibility.Visible;
        BtnDialogoSim.Focus();
        return _respostaDialogo.Task;
    }

    private void FecharDialogo(bool resposta)
    {
        PainelDialogo.Visibility = Visibility.Collapsed;
        var t = _respostaDialogo;
        _respostaDialogo = null;
        t?.TrySetResult(resposta);
    }

    private void DialogoSim_Click(object sender, RoutedEventArgs e) => FecharDialogo(true);
    private void DialogoNao_Click(object sender, RoutedEventArgs e) => FecharDialogo(false);

    /// <summary>Esc fecha o que estiver por cima: painel de mensagem, detalhe da área.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PainelDialogo.Visibility == Visibility.Visible)
        {
            FecharDialogo(false);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Avisos do Zelo: uma frase curta aparece como notificação no canto, que some sozinha;
    /// um texto maior abre o painel com um botão "Entendi".
    /// </summary>
    private void Aviso(string texto)
    {
        texto = texto.Trim();
        bool erro = texto.StartsWith("Não ", StringComparison.Ordinal) || texto.Contains("não deu certo", StringComparison.OrdinalIgnoreCase)
                    || texto.StartsWith("Só ", StringComparison.Ordinal);
        if (texto.Length <= 170 && !texto.Contains('\n'))
        {
            Notificar(texto, erro);
            return;
        }
        var partes = texto.Split("\n\n", 2);
        _ = PerguntarAsync(partes[0], partes.Length > 1 ? partes[1] : "", null, "Entendi", null, erro ? TomDialogo.Atencao : TomDialogo.Pergunta);
    }

    private void Notificar(string texto, bool erro = false)
    {
        TxtNotificacao.Text = texto;
        IconeNotificacao.Data = erro ? Icones.Atencao : Icones.Ok;
        IconeNotificacao.Stroke = erro ? Cores.Ambar : Cores.Aurora;
        Notificacao.BorderBrush = erro ? Cores.BordaAmbar : Cores.LinhaForte;
        Notificacao.Visibility = Visibility.Visible;
        NotificacaoDesloca.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Notificacao.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        _timerNotificacao.Stop();
        _timerNotificacao.Interval = TimeSpan.FromSeconds(Math.Clamp(texto.Length / 18.0, 5, 10));
        _timerNotificacao.Tick -= EsconderNotificacao;
        _timerNotificacao.Tick += EsconderNotificacao;
        _timerNotificacao.Start();
    }

    private void EsconderNotificacao(object? sender, EventArgs e)
    {
        _timerNotificacao.Stop();
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) => { if (!_timerNotificacao.IsEnabled) Notificacao.Visibility = Visibility.Collapsed; };
        Notificacao.BeginAnimation(OpacityProperty, anim);
    }

    private void FecharNotificacao_Click(object sender, RoutedEventArgs e) => EsconderNotificacao(null, EventArgs.Empty);
}
