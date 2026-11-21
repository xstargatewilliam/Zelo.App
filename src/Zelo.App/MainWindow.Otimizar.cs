using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Zelo.Core;

namespace Zelo.App;

/// <summary>Um ajuste na tela Otimizar.</summary>
public sealed class ItemAjuste : INotifyPropertyChanged
{
    private bool _selecionado;

    public ItemAjuste(Ajuste def) => Def = def;

    public Ajuste Def { get; }
    public string Id => Def.Id;
    public string Nome => Def.Nome;
    public string Descricao => Def.Descricao;
    public string Aviso => Def.Aviso ?? "";
    public Visibility VisAviso => string.IsNullOrEmpty(Def.Aviso) ? Visibility.Collapsed : Visibility.Visible;

    public SituacaoAjuste Situacao { get; set; }
    public bool PeloZelo { get; set; }
    public DateTime? Quando { get; set; }
    /// <summary>Só para programas: null = ainda não sabemos.</summary>
    public bool? Instalado { get; set; }

    public bool Ativo => Def.Tipo == TipoAjuste.Programa ? Instalado == true : Situacao == SituacaoAjuste.Ativo;

    public bool Selecionado
    {
        get => _selecionado;
        set { if (_selecionado == value) return; _selecionado = value; Avisar(nameof(Selecionado)); }
    }

    public Visibility VisInterruptor => Ativo ? Visibility.Collapsed : Visibility.Visible;
    /// <summary>O que o interruptor ligado faz, escrito ao lado dele.</summary>
    public string TextoInterruptor => Def.Tipo switch
    {
        TipoAjuste.App => "Remover",
        TipoAjuste.Programa => "Instalar",
        _ => "Aplicar",
    };
    public string TextoAcessivel => $"{TextoInterruptor}: {Nome}";
    public Visibility VisEstado => Ativo ? Visibility.Visible : Visibility.Collapsed;
    public string TextoEstado =>
        Def.Tipo == TipoAjuste.Programa ? "Instalado"
        : Def.Tipo == TipoAjuste.App && PeloZelo ? $"Removido pelo Zelo em {Quando:dd/MM}"
        : PeloZelo ? $"Feito pelo Zelo em {Quando:dd/MM}"
        : "Já está assim";
    public Visibility VisDesfazer => Ativo && (PeloZelo || Def.Tipo == TipoAjuste.Programa) ? Visibility.Visible : Visibility.Collapsed;
    public string TextoDesfazer => Def.Tipo switch
    {
        TipoAjuste.App => "Reinstalar pela Loja",
        TipoAjuste.Programa => "Desinstalar",
        _ => "Desfazer",
    };
    public string TextoRisco => Def.Risco switch
    {
        RiscoAjuste.Medio => "Leia antes de aplicar",
        RiscoAjuste.Alto => "Avançado",
        _ => "",
    };
    public Visibility VisRisco => Def.Risco == RiscoAjuste.Baixo ? Visibility.Collapsed : Visibility.Visible;

    public void Renovar()
    {
        foreach (var n in new[] { nameof(VisInterruptor), nameof(VisEstado), nameof(TextoEstado), nameof(VisDesfazer), nameof(Selecionado) })
            Avisar(n);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Avisar(string nome) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nome));
}

public sealed class GrupoAjustes
{
    public string Titulo { get; init; } = "";
    public string Sub { get; init; } = "";
    public Visibility VisSub => Sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<ItemAjuste> Itens { get; init; } = new();
}

public partial class MainWindow
{
    private readonly List<ItemAjuste> _ajustes = new();
    private readonly HashSet<PerfilUso> _perfisUso = new();
    private bool _otimCarregado;
    private bool _otimOcupado;

    private static readonly Dictionary<string, string> SubGrupos = new()
    {
        ["Apps que vieram com o Windows"] = "Ligado = remover. Desligue os que você usa. Só aparecem os que estão no seu PC; todos voltam pela Microsoft Store se você quiser.",
        ["Programas para instalar"] = "Ligado = instalar. Instalados pelo winget, o gerenciador oficial de programas do Windows, direto do site de cada fabricante.",
        ["Avançado"] = "Mexe em partes sensíveis do Windows sem desligar nenhuma proteção. Nada vem marcado: escolha só o que fizer sentido para você.",
    };

    private ToggleButton[] BotoesPerfilUso => new[] { UsoJogos, UsoTrabalho, UsoEstudos, UsoEdicao, UsoDev };

    // ================================================================ Carregar

    private async Task CarregarOtimizacaoAsync()
    {
        if (_otimOcupado) return;
        TxtOtimStatus.Text = "Lendo como o seu Windows está agora…";
        BtnOtimAplicar.IsEnabled = false;
        Resposta r;
        try { r = await ClientePipe.EnviarAsync(new Pedido { Comando = "otimizar-estado" }, 2500, 120000); }
        catch (Exception ex)
        {
            TxtOtimStatus.Text = "Não foi possível falar com o serviço do Zelo: " + ex.Message;
            return;
        }
        if (!r.Ok || r.Ajustes is null)
        {
            TxtOtimStatus.Text = r.Mensagem ?? "O serviço do Zelo não respondeu.";
            return;
        }

        if (!_otimCarregado && r.PerfisUso is { Count: > 0 })
        {
            _perfisUso.Clear();
            foreach (var p in r.PerfisUso) _perfisUso.Add(p);
        }
        var anteriores = _ajustes.ToDictionary(a => a.Id, a => a.Selecionado);
        var instalados = _ajustes.Where(a => a.Def.Tipo == TipoAjuste.Programa).ToDictionary(a => a.Id, a => a.Instalado);
        MontarItens(r.Ajustes, instalados);
        if (_otimCarregado)
            foreach (var a in _ajustes) a.Selecionado = !a.Ativo && anteriores.GetValueOrDefault(a.Id, a.Def.RecomendadoPara(_perfisUso));
        else
            Recomendar();
        _otimCarregado = true;

        foreach (var b in BotoesPerfilUso) b.IsChecked = _perfisUso.Contains(Enum.Parse<PerfilUso>((string)b.Tag));
        MontarOtimizacao();
        if (r.Otimizacao is { Rodando: true }) _ = AcompanharAsync();
        _ = VerificarProgramasAsync();
    }

    private void MontarItens(List<EstadoAjuste> estados, Dictionary<string, bool?>? instalados = null)
    {
        var porId = estados.ToDictionary(e => e.Id);
        _ajustes.Clear();
        foreach (var def in CatalogoOtimizacao.Todos)
        {
            var item = new ItemAjuste(def);
            if (def.Tipo == TipoAjuste.Programa)
                item.Instalado = instalados?.GetValueOrDefault(def.Id);
            else
            {
                if (!porId.TryGetValue(def.Id, out var e) || e.Situacao == SituacaoAjuste.Indisponivel) continue;
                item.Situacao = e.Situacao;
                item.PeloZelo = e.PeloZelo;
                item.Quando = e.Quando;
            }
            _ajustes.Add(item);
        }
    }

    /// <summary>Marca o que o Zelo recomenda para os perfis escolhidos.</summary>
    private void Recomendar()
    {
        foreach (var a in _ajustes) a.Selecionado = !a.Ativo && a.Def.RecomendadoPara(_perfisUso);
    }

    private IEnumerable<ItemAjuste> Visiveis() =>
        _ajustes.Where(a => ChkTodosPerfis.IsChecked == true || a.Def.Relevante(_perfisUso));

    private void MontarOtimizacao()
    {
        var ordem = CatalogoOtimizacao.Grupos.ToList();
        ListaOtimizacao.ItemsSource = Visiveis()
            .GroupBy(a => a.Def.Grupo)
            .OrderBy(g => ordem.IndexOf(g.Key))
            .Select(g => new GrupoAjustes { Titulo = g.Key, Sub = SubGrupos.GetValueOrDefault(g.Key, ""), Itens = g.ToList() })
            .ToList();
        TxtOtimStatus.Text = _perfisUso.Count == 0
            ? "Escolha acima como você usa o PC para ver os ajustes de cada perfil. Abaixo, os que servem para todo mundo."
            : $"Recomendações para: {string.Join(", ", _perfisUso.OrderBy(p => p).Select(NomesPerfil.Perfil))}.";
        AtualizarContagemOtim();
    }

    private void AtualizarContagemOtim()
    {
        var sel = Visiveis().Where(a => a.Selecionado && !a.Ativo).ToList();
        int sistema = sel.Count(a => a.Def.Tipo != TipoAjuste.Programa);
        int programas = sel.Count - sistema;
        BtnOtimAplicar.Content = sel.Count == 0 ? "Nada selecionado" : $"Aplicar {sel.Count}";
        BtnOtimAplicar.IsEnabled = sel.Count > 0 && !_otimOcupado && _otimCarregado;
        BtnOtimDesfazerTudo.Visibility = _ajustes.Any(a => a.PeloZelo && a.Def.Tipo is not (TipoAjuste.App or TipoAjuste.Programa))
            ? Visibility.Visible : Visibility.Collapsed;
        BtnOtimDesfazerTudo.IsEnabled = !_otimOcupado;
        var partes = new List<string>();
        if (sistema > 0) partes.Add($"{sistema} ajuste(s) no Windows");
        if (programas > 0) partes.Add($"{programas} programa(s) para instalar");
        TxtOtimResumo.Text = partes.Count == 0 ? "Nada marcado ainda" : string.Join(" · ", partes);
        if (!_otimOcupado)
            TxtOtimProgresso.Text = sistema > 0 ? "Antes de mudar qualquer coisa, o Zelo cria um ponto de restauração." : "";
    }

    // ================================================================ Interação

    private void PerfilUso_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton b) return;
        var p = Enum.Parse<PerfilUso>((string)b.Tag);
        if (b.IsChecked == true) _perfisUso.Add(p); else _perfisUso.Remove(p);
        Recomendar();
        MontarOtimizacao();
    }

    private void TodosPerfis_Click(object sender, RoutedEventArgs e) => MontarOtimizacao();

    private void AjusteOtim_Click(object sender, RoutedEventArgs e) => AtualizarContagemOtim();

    private async void OtimAplicar_Click(object sender, RoutedEventArgs e)
    {
        var sel = Visiveis().Where(a => a.Selecionado && !a.Ativo).ToList();
        if (sel.Count == 0) return;
        var sistema = sel.Where(a => a.Def.Tipo != TipoAjuste.Programa).ToList();
        var programas = sel.Where(a => a.Def.Tipo == TipoAjuste.Programa).ToList();

        var texto = new StringBuilder();
        if (sistema.Count > 0)
        {
            texto.AppendLine($"O Zelo vai aplicar {sistema.Count} ajuste(s) no Windows.");
            texto.AppendLine("Antes, ele cria um ponto de restauração (se a Restauração do Sistema estiver desligada, ele a liga no disco C:). Se não conseguir, ele para e pergunta.");
            texto.AppendLine("O Windows vai pedir a sua confirmação de administrador.");
            int apps = sistema.Count(a => a.Def.Tipo == TipoAjuste.App);
            if (apps > 0) texto.AppendLine($"{apps} item(ns) removem apps só da sua conta. Eles não voltam com \"Desfazer\": para tê-los de novo, reinstale pela Microsoft Store (o Zelo abre a página).");
        }
        if (programas.Count > 0)
            texto.AppendLine($"Depois, instala: {string.Join(", ", programas.Select(p => p.Nome))}. O Windows pode pedir confirmação para alguns.");
        texto.AppendLine();
        texto.Append("Continuar?");
        if (MessageBox.Show(this, texto.ToString(), "Zelo · Otimizar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _otimOcupado = true;
        AtualizarContagemOtim();
        var falhas = new List<string>();
        var resumo = new List<string>();
        try
        {
            var pedido = new Pedido
            {
                Comando = "otimizar-aplicar",
                Ids = sistema.Select(a => a.Id).ToList(),
                PerfisUso = _perfisUso.ToList(),
            };
            TxtOtimProgresso.Text = "Esperando a sua confirmação no aviso do Windows…";
            var r = await Elevacao.EnviarAsync(pedido);
            if (!r.Ok)
            {
                TxtOtimProgresso.Text = "Nada foi alterado.";
                Aviso(r.Mensagem ?? "Não deu certo.");
                return;
            }
            if (sistema.Count > 0)
            {
                var fim = await AcompanharAsync();
                if (fim is { PontoFalhou: true })
                {
                    // Sem ponto de restauração, nada foi mudado: a pessoa decide.
                    var continuar = MessageBox.Show(this,
                        "O Windows não deixou criar o ponto de restauração, então o Zelo não mudou nada.\n\n" +
                        "Cada ajuste ainda pode ser desfeito pelo próprio Zelo, mas sem o ponto de restauração não há como voltar o PC inteiro de uma vez.\n\n" +
                        "Continuar mesmo assim?", "Zelo · Otimizar", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                    if (!continuar) { TxtOtimProgresso.Text = "Nada foi alterado."; return; }
                    pedido.SemPontoRestauracao = true;
                    r = await Elevacao.EnviarAsync(pedido);
                    if (!r.Ok) { Aviso(r.Mensagem ?? "Não deu certo."); return; }
                    fim = await AcompanharAsync();
                }
                if (fim is not null)
                {
                    if (fim.Resultado is not null) resumo.Add(fim.Resultado);
                    falhas.AddRange(fim.Falhas);
                }
            }

            int i = 0, instalados = 0;
            foreach (var p in programas)
            {
                i++;
                TxtOtimProgresso.Text = $"Instalando {p.Nome} ({i} de {programas.Count})… pode levar alguns minutos.";
                var (codigo, saida) = await WingetAsync(
                    $"install --id {p.Def.WingetId} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --source winget", 30);
                if (WingetDeuCerto(codigo)) instalados++;
                else falhas.Add($"{p.Nome}: {ResumoWinget(codigo, saida)}");
            }
            if (programas.Count > 0) resumo.Add($"{instalados} de {programas.Count} programa(s) instalado(s).");
        }
        catch (Exception ex)
        {
            falhas.Add(ex.Message);
        }
        finally
        {
            _otimOcupado = false;
        }

        TxtOtimProgresso.Text = string.Join(" ", resumo);
        var msg = string.Join("\n", resumo);
        if (falhas.Count > 0) msg += "\n\nNão deu certo:\n• " + string.Join("\n• ", falhas);
        bool explorer = sistema.Any(a => a.Def.ReiniciarExplorer);
        if (sistema.Any(a => a.Def.Aviso?.Contains("reiniciar", StringComparison.OrdinalIgnoreCase) == true))
            msg += "\n\nAlguns ajustes só valem depois de reiniciar o PC.";
        if (explorer)
        {
            msg += "\n\nAlguns ajustes aparecem depois de reiniciar o Explorador de Arquivos (as janelas de pastas abertas vão fechar). Reiniciar agora?";
            if (MessageBox.Show(this, msg.Trim(), "Zelo · Otimizar", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                await ReiniciarExplorerAsync();
        }
        else Aviso(msg.Trim());

        await CarregarOtimizacaoAsync();
    }

    private async void DesfazerAjuste_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var item = _ajustes.FirstOrDefault(a => a.Id == id);
        if (item is null || _otimOcupado) return;

        if (item.Def.Tipo == TipoAjuste.Programa)
        {
            if (MessageBox.Show(this, $"Desinstalar {item.Nome}?", "Zelo · Otimizar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _otimOcupado = true;
            AtualizarContagemOtim();
            TxtOtimProgresso.Text = $"Desinstalando {item.Nome}…";
            var (codigo, saida) = await WingetAsync($"uninstall --id {item.Def.WingetId} -e --silent --accept-source-agreements --disable-interactivity", 20);
            _otimOcupado = false;
            if (codigo == 0) { item.Instalado = false; item.Selecionado = false; item.Renovar(); TxtOtimProgresso.Text = $"{item.Nome} desinstalado."; }
            else Aviso($"Não foi possível desinstalar {item.Nome}: {ResumoWinget(codigo, saida)}");
            AtualizarContagemOtim();
            return;
        }

        if (item.Def.Tipo == TipoAjuste.App)
        {
            // Reinstalar pede a conta da pessoa na Loja; o Zelo abre a busca.
            Abrir("ms-windows-store://search/?query=" + Uri.EscapeDataString(item.Def.BuscaLoja ?? item.Nome));
        }
        await DesfazerAsync(new List<string> { id });
    }

    private async void OtimDesfazerTudo_Click(object sender, RoutedEventArgs e)
    {
        var ids = _ajustes.Where(a => a.PeloZelo && a.Def.Tipo is not (TipoAjuste.App or TipoAjuste.Programa)).Select(a => a.Id).ToList();
        if (ids.Count == 0) return;
        if (MessageBox.Show(this,
                $"Voltar {ids.Count} ajuste(s) feitos pelo Zelo para como estavam antes?\n\nApps removidos e programas instalados ficam como estão (cada um tem o próprio botão).",
                "Zelo · Otimizar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await DesfazerAsync(ids);
    }

    private async Task DesfazerAsync(List<string> ids)
    {
        _otimOcupado = true;
        AtualizarContagemOtim();
        try
        {
            var r = await Elevacao.EnviarAsync(new Pedido { Comando = "otimizar-desfazer", Ids = ids });
            if (!r.Ok) { Aviso(r.Mensagem ?? "Não deu certo."); return; }
            var fim = await AcompanharAsync();
            if (fim is { Falhas.Count: > 0 }) Aviso("Não deu certo:\n• " + string.Join("\n• ", fim.Falhas));
            TxtOtimProgresso.Text = fim?.Resultado ?? "";
        }
        catch (Exception ex) { Aviso("Não foi possível falar com o serviço do Zelo: " + ex.Message); }
        finally { _otimOcupado = false; }
        await CarregarOtimizacaoAsync();
    }

    /// <summary>Mostra o progresso do serviço até ele terminar.</summary>
    private async Task<AndamentoOtimizacao?> AcompanharAsync()
    {
        AndamentoOtimizacao? a = null;
        for (int i = 0; i < 60 * 60; i++)
        {
            await Task.Delay(800);
            try { a = (await ClientePipe.EnviarAsync(new Pedido { Comando = "otimizar-andamento" })).Otimizacao; }
            catch { continue; }
            if (a is null) return null;
            if (!a.Rodando) return a;
            TxtOtimProgresso.Text = a.Total > 0 ? $"{a.Etapa}… ({a.Feitos} de {a.Total})" : a.Etapa + "…";
        }
        return a;
    }

    private static async Task ReiniciarExplorerAsync()
    {
        try
        {
            // Só o Explorador desta sessão (não mexe em outras contas conectadas no PC).
            var sessao = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer").Where(p => p.SessionId == sessao)) { try { p.Kill(); } catch { } }
            await Task.Delay(2500);
            if (!Process.GetProcessesByName("explorer").Any(p => p.SessionId == sessao))
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch { }
    }

    // ================================================================ Programas (winget, no usuário)

    private async Task VerificarProgramasAsync()
    {
        HashSet<string>? ids = null;
        var arq = Path.Combine(Path.GetTempPath(), "zelo-programas.json");
        try
        {
            try { File.Delete(arq); } catch { }
            await WingetAsync($"export -o \"{arq}\" --accept-source-agreements --disable-interactivity", 4);
            if (File.Exists(arq))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(arq));
                ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fonte in doc.RootElement.GetProperty("Sources").EnumerateArray())
                    foreach (var pac in fonte.GetProperty("Packages").EnumerateArray())
                        ids.Add(pac.GetProperty("PackageIdentifier").GetString() ?? "");
            }
        }
        catch { }
        if (ids is null) return;
        foreach (var a in _ajustes.Where(a => a.Def.Tipo == TipoAjuste.Programa))
        {
            a.Instalado = ids.Contains(a.Def.WingetId ?? "");
            if (a.Instalado == true) a.Selecionado = false;
            a.Renovar();
        }
        AtualizarContagemOtim();
    }

    private static async Task<(int codigo, string saida)> WingetAsync(string argumentos, int minutos)
    {
        var psi = new ProcessStartInfo("winget", argumentos)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var saida = p.StandardOutput.ReadToEndAsync();
            var erro = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(minutos));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (-1, "demorou demais e foi interrompido.");
            }
            return (p.ExitCode, await saida + "\n" + await erro);
        }
        catch (Exception ex)
        {
            return (-2, "o winget não está disponível neste PC (" + ex.Message + ").");
        }
    }

    private static bool WingetDeuCerto(int codigo) =>
        codigo == 0
        || codigo == unchecked((int)0x8A150061)  // já instalado
        || codigo == unchecked((int)0x8A15002B); // nada a atualizar

    private static string ResumoWinget(int codigo, string saida)
    {
        if (codigo is -1 or -2) return saida;
        var linha = saida.Split('\n', '\r').Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 3 && !l.All(c => c is '-' or '\\' or '|' or '/' or '█' or '▒' or ' '));
        return $"{linha ?? "erro"} (código 0x{codigo:X8})";
    }

    // ================================================================ Capturas (CI)

    private void MontarOtimizacaoDeExemplo()
    {
        _perfisUso.Clear();
        _perfisUso.Add(PerfilUso.Jogos);
        _perfisUso.Add(PerfilUso.Dev);
        var ativos = new HashSet<string> { "expl-extensoes", "jogo-modo", "priv-recall" };
        var instalados = new HashSet<string> { "app-propaganda", "app-dicas", "app-noticias", "app-feedback", "app-clipchamp", "app-office-hub", "app-paciencia", "app-copilot", "app-xbox" };
        var estados = CatalogoOtimizacao.Todos.Where(a => a.Tipo != TipoAjuste.Programa).Select(a => new EstadoAjuste
        {
            Id = a.Id,
            Situacao = a.Tipo == TipoAjuste.App
                ? (instalados.Contains(a.Id) ? SituacaoAjuste.Padrao : SituacaoAjuste.Indisponivel)
                : ativos.Contains(a.Id) ? SituacaoAjuste.Ativo : SituacaoAjuste.Padrao,
            PeloZelo = a.Id == "priv-recall",
            Quando = DateTime.Now.AddDays(-2),
        }).ToList();
        MontarItens(estados, new Dictionary<string, bool?> { ["prog-steam"] = true, ["prog-git"] = false, ["prog-discord"] = false });
        Recomendar();
        _otimCarregado = true;
        foreach (var b in BotoesPerfilUso) b.IsChecked = _perfisUso.Contains(Enum.Parse<PerfilUso>((string)b.Tag));
        MontarOtimizacao();
    }
}
