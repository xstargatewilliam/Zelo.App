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

public sealed class GrupoAjustes : INotifyPropertyChanged
{
    private static readonly Geometry Fechado = Geometry.Parse("M4,2 L9,7 L4,12");
    private static readonly Geometry Aberto = Geometry.Parse("M2,4 L7,9 L12,4");
    private bool _expandido = true;

    public string Titulo { get; init; } = "";
    public string Sub { get; init; } = "";
    public Visibility VisSub => Sub.Length > 0 && Expandido ? Visibility.Visible : Visibility.Collapsed;
    public List<ItemAjuste> Itens { get; init; } = new();
    /// <summary>Ajustes do Windows pedem a confirmação de administrador; apps e programas, não.</summary>
    public bool PedeConfirmacao { get; init; }
    public Visibility VisEscudo => PedeConfirmacao ? Visibility.Visible : Visibility.Collapsed;
    public Action<GrupoAjustes>? AoMudar { get; init; }

    public bool Expandido
    {
        get => _expandido;
        set
        {
            if (_expandido == value) return;
            _expandido = value;
            AoMudar?.Invoke(this);
            foreach (var n in new[] { nameof(Expandido), nameof(VisItens), nameof(VisSub), nameof(Seta) }) Avisar(n);
        }
    }

    public Visibility VisItens => Expandido ? Visibility.Visible : Visibility.Collapsed;
    public Geometry Seta => Expandido ? Aberto : Fechado;

    public string Contagem
    {
        get
        {
            int marcados = Itens.Count(i => i.Selecionado && !i.Ativo), feitos = Itens.Count(i => i.Ativo);
            var partes = new List<string>();
            if (marcados > 0) partes.Add($"{marcados} marcado(s)");
            if (feitos > 0) partes.Add($"{feitos} já aplicado(s)");
            return partes.Count == 0 ? $"{Itens.Count} item(ns)" : string.Join(" · ", partes);
        }
    }

    public void Renovar() => Avisar(nameof(Contagem));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Avisar(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
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
        var removidos = AppsDaConta.Removidos();
        _ajustes.Clear();
        foreach (var def in CatalogoOtimizacao.Todos)
        {
            var item = new ItemAjuste(def);
            if (def.Tipo == TipoAjuste.Programa)
                item.Instalado = instalados?.GetValueOrDefault(def.Id);
            else
            {
                porId.TryGetValue(def.Id, out var e);
                // App que o Zelo removeu desta conta: não aparece mais instalado, mas fica na lista para reinstalar.
                if (def.Tipo == TipoAjuste.App && removidos.TryGetValue(def.Id, out var quando) && e?.Situacao != SituacaoAjuste.Padrao)
                {
                    item.Situacao = SituacaoAjuste.Ativo;
                    item.PeloZelo = true;
                    item.Quando = quando;
                    _ajustes.Add(item);
                    continue;
                }
                if (e is null || e.Situacao == SituacaoAjuste.Indisponivel) continue;
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

    /// <summary>Os ajustes dos perfis escolhidos: é sobre eles que "Aplicar" age, com ou sem busca e filtro na tela.</summary>
    private IEnumerable<ItemAjuste> Visiveis() =>
        _ajustes.Where(a => ChkTodosPerfis.IsChecked == true || a.Def.Relevante(_perfisUso));

    private string _filtroOtim = "todos";
    private readonly HashSet<string> _gruposFechados = new();
    private List<GrupoAjustes> _gruposOtim = new();

    /// <summary>O que aparece na tela: os visíveis, com a busca e o filtro escolhidos.</summary>
    private IEnumerable<ItemAjuste> NaTela()
    {
        var busca = TxtBuscaOtim.Text.Trim();
        return Visiveis().Where(a =>
            (_filtroOtim switch
            {
                "marcados" => a.Selecionado && !a.Ativo,
                "aplicados" => a.Ativo,
                _ => true,
            })
            && (busca.Length == 0 || a.Nome.Contains(busca, StringComparison.OrdinalIgnoreCase)
                || a.Descricao.Contains(busca, StringComparison.OrdinalIgnoreCase) || a.Def.Grupo.Contains(busca, StringComparison.OrdinalIgnoreCase)));
    }

    private void MontarOtimizacao()
    {
        var ordem = CatalogoOtimizacao.Grupos.ToList();
        bool filtrando = TxtBuscaOtim.Text.Trim().Length > 0 || _filtroOtim != "todos";
        _gruposOtim = NaTela()
            .GroupBy(a => a.Def.Grupo)
            .OrderBy(g => ordem.IndexOf(g.Key))
            .Select(g =>
            {
                var grupo = new GrupoAjustes
                {
                    Titulo = g.Key, Sub = SubGrupos.GetValueOrDefault(g.Key, ""), Itens = g.ToList(),
                    PedeConfirmacao = g.All(i => i.Def.Tipo is not (TipoAjuste.App or TipoAjuste.Programa)),
                    AoMudar = gr => { if (gr.Expandido) _gruposFechados.Remove(gr.Titulo); else _gruposFechados.Add(gr.Titulo); },
                };
                // Buscando ou filtrando, tudo aberto; senão, como a pessoa deixou.
                grupo.Expandido = filtrando || !_gruposFechados.Contains(g.Key);
                return grupo;
            })
            .ToList();
        ListaOtimizacao.ItemsSource = _gruposOtim;
        TxtOtimNada.Visibility = _gruposOtim.Count == 0 && _otimCarregado ? Visibility.Visible : Visibility.Collapsed;
        TxtOtimStatus.Text = _perfisUso.Count == 0
            ? "Escolha acima como você usa o PC para ver os ajustes de cada perfil. Abaixo, os que servem para todo mundo."
            : $"Recomendações para: {string.Join(", ", _perfisUso.OrderBy(p => p).Select(NomesPerfil.Perfil))}.";
        AtualizarContagemOtim();
    }

    private void AtualizarContagemOtim()
    {
        foreach (var g in _gruposOtim) g.Renovar();
        var sel = Visiveis().Where(a => a.Selecionado && !a.Ativo).ToList();
        int apps = sel.Count(a => a.Def.Tipo == TipoAjuste.App);
        int programas = sel.Count(a => a.Def.Tipo == TipoAjuste.Programa);
        int sistema = sel.Count - apps - programas;
        BtnOtimAplicar.IsEnabled = sel.Count > 0 && !_otimOcupado && _otimCarregado;
        BtnOtimDesfazerTudo.Visibility = _ajustes.Any(a => a.PeloZelo && a.Def.Tipo is not (TipoAjuste.App or TipoAjuste.Programa))
            ? Visibility.Visible : Visibility.Collapsed;
        BtnOtimDesfazerTudo.IsEnabled = !_otimOcupado;
        var partes = new List<string>();
        if (sistema > 0) partes.Add($"aplicar {sistema} ajuste(s)");
        if (apps > 0) partes.Add($"remover {apps} grupo(s) de apps");
        if (programas > 0) partes.Add($"instalar {programas} programa(s)");
        TxtOtimResumo.Text = partes.Count == 0 ? "Nada marcado ainda" : "Vai " + string.Join(" · ", partes);
        BtnOtimVerLista.Visibility = sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // O escudo avisa antes: estes vão abrir o aviso do Windows (UAC).
        TxtAplicar.Text = sel.Count == 0 ? "Nada selecionado" : $"Aplicar {sel.Count}";
        EscudoAplicar.Visibility = sistema > 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnOtimAplicar.ToolTip = sistema > 0 ? "Os ajustes do Windows pedem a confirmação de administrador (o aviso do UAC)." : "Não pede confirmação de administrador.";
        if (!_otimOcupado)
            TxtOtimProgresso.Text = sistema > 0 ? "Antes de mudar o Windows, o Zelo cria um ponto de restauração. O Windows vai pedir sua confirmação." : apps > 0 ? "Os apps saem só da sua conta, sem pedir administrador." : "";
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

    private void BuscaOtim_Changed(object sender, TextChangedEventArgs e)
    {
        TxtBuscaOtimDica.Visibility = TxtBuscaOtim.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_otimCarregado) MontarOtimizacao();
    }

    private void FiltroOtim_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton b) return;
        _filtroOtim = (string)b.Tag;
        foreach (var f in new[] { FiltroOtimTodos, FiltroOtimRecomendados, FiltroOtimAplicados }) f.IsChecked = f == b;
        MontarOtimizacao();
    }

    private async void OtimVerLista_Click(object sender, RoutedEventArgs e)
    {
        var sel = Visiveis().Where(a => a.Selecionado && !a.Ativo).ToList();
        if (sel.Count == 0) return;
        var programas = sel.Where(a => a.Def.Tipo == TipoAjuste.Programa).ToList();
        var apps = sel.Where(a => a.Def.Tipo == TipoAjuste.App).ToList();
        await PerguntarAsync("O que está marcado", "Nada muda até você clicar em Aplicar.", Previa(sel.Except(programas).Except(apps).ToList(), apps, programas), "Fechar", null);
    }

    private void AjusteOtim_Click(object sender, RoutedEventArgs e) => AtualizarContagemOtim();

    private static readonly Geometry IconeEscudo = Geometry.Parse("M12,3 L20,6 L20,12 C20,16.5 16.5,19.5 12,21 C7.5,19.5 4,16.5 4,12 L4,6 Z");
    private static readonly Geometry IconeLixo = Geometry.Parse("M4,7 L20,7 M9,7 L9,4 L15,4 L15,7 M6,7 L7,20 L17,20 L18,7");
    private static readonly Geometry IconeBaixar = Geometry.Parse("M12,4 L12,16 M7,11 L12,16 L17,11 M5,20 L19,20");

    /// <summary>A prévia do que vai acontecer, agrupada: ajustes do Windows, apps a remover, programas a instalar.</summary>
    private static List<LinhaResultado> Previa(List<ItemAjuste> windows, List<ItemAjuste> apps, List<ItemAjuste> programas)
    {
        var l = new List<LinhaResultado>();
        if (windows.Count > 0)
            l.Add(new LinhaResultado
            {
                Texto = $"{windows.Count} ajuste(s) no Windows",
                Detalhe = string.Join(" · ", windows.Select(w => w.Nome)) + "\nAntes, um ponto de restauração. Pede a confirmação de administrador do Windows.",
                Icone = IconeEscudo, Cor = Cores.Luar, CorFundo = Cores.NeutroFundo,
            });
        if (apps.Count > 0)
            l.Add(new LinhaResultado
            {
                Texto = $"Remover {apps.Count} grupo(s) de apps da sua conta",
                Detalhe = string.Join(" · ", apps.Select(w => w.Nome)) + "\nNão pede administrador. Voltam só pela Microsoft Store.",
                Icone = IconeLixo, Cor = Cores.Ambar, CorFundo = Cores.AmbarFundo,
            });
        if (programas.Count > 0)
            l.Add(new LinhaResultado
            {
                Texto = $"Instalar {programas.Count} programa(s)",
                Detalhe = string.Join(" · ", programas.Select(w => w.Nome)) + "\nO Windows pode pedir confirmação para alguns.",
                Icone = IconeBaixar, Cor = Cores.Aurora, CorFundo = Cores.AuroraFundo,
            });
        return l;
    }

    private async void OtimAplicar_Click(object sender, RoutedEventArgs e)
    {
        var sel = Visiveis().Where(a => a.Selecionado && !a.Ativo).ToList();
        if (sel.Count == 0) return;
        var programas = sel.Where(a => a.Def.Tipo == TipoAjuste.Programa).ToList();
        var apps = sel.Where(a => a.Def.Tipo == TipoAjuste.App).ToList();
        var windows = sel.Except(programas).Except(apps).ToList();

        if (!await PerguntarAsync("Aplicar o que você marcou?", "Confira o que vai acontecer. Cada ajuste do Windows pode ser desfeito depois.",
                Previa(windows, apps, programas), $"Aplicar {sel.Count}")) return;

        _otimOcupado = true;
        AtualizarContagemOtim();
        var linhas = new List<LinhaResultado>();
        var resumo = new List<string>();
        bool cancelado = false;
        try
        {
            // 1) Ajustes do Windows, pelo serviço, com a confirmação do Windows.
            if (windows.Count > 0)
            {
                var pedido = new Pedido { Comando = "otimizar-aplicar", Ids = windows.Select(a => a.Id).ToList(), PerfisUso = _perfisUso.ToList() };
                TxtOtimProgresso.Text = "Esperando a sua confirmação no aviso do Windows…";
                var r = await Elevacao.EnviarAsync(pedido);
                AndamentoOtimizacao? fim = null;
                if (r.Ok) fim = await AcompanharAsync();
                if (r.Ok && fim is { PontoFalhou: true })
                {
                    var continuar = await PerguntarAsync("O ponto de restauração não foi criado",
                        "O Windows não deixou criar o ponto, então nada foi mudado ainda. Cada ajuste continua podendo ser desfeito pelo Zelo, mas sem o ponto não dá para voltar o PC inteiro de uma vez.",
                        null, "Continuar sem o ponto", "Não aplicar", TomDialogo.Atencao);
                    if (continuar)
                    {
                        pedido.SemPontoRestauracao = true;
                        r = await Elevacao.EnviarAsync(pedido);
                        fim = r.Ok ? await AcompanharAsync() : null;
                    }
                    else
                    {
                        fim = null;
                        linhas.Add(LinhaResultado.Info("Ajustes do Windows não aplicados", "Você escolheu não continuar sem o ponto de restauração."));
                    }
                }
                if (!r.Ok)
                {
                    cancelado = true;
                    linhas.Add(LinhaResultado.Falhou("Ajustes do Windows não aplicados", r.Mensagem ?? "Não deu certo."));
                }
                // Recusar o aviso do Windows (ou o "Não aplicar") costuma querer dizer "pare". Apps e programas
                // não precisam de administrador, mas só seguem se a pessoa confirmar de novo.
                if ((cancelado || fim is null) && apps.Count + programas.Count > 0
                    && !await PerguntarAsync("Continuar com o resto?",
                        "Os ajustes do Windows não foram aplicados. Quer seguir com "
                        + string.Join(" e ", new[] { apps.Count > 0 ? $"a remoção de {apps.Count} grupo(s) de apps" : "", programas.Count > 0 ? $"a instalação de {programas.Count} programa(s)" : "" }.Where(x => x.Length > 0))
                        + "? Essa parte não precisa da confirmação do Windows.",
                        null, "Continuar", "Parar aqui", TomDialogo.Atencao))
                {
                    linhas.Add(LinhaResultado.Info("Apps e programas não foram mexidos", "Você escolheu parar."));
                    apps.Clear();
                    programas.Clear();
                }
                if (fim is not null)
                {
                    if (fim.Resultado is not null) resumo.Add(fim.Resultado);
                    foreach (var it in fim.Itens)
                        linhas.Add(it.Ok
                            ? LinhaResultado.Certo(it.Nome, it.Motivo)
                            : LinhaResultado.Falhou(it.Nome, it.Motivo, it.AbrirManual is null ? "" : "Fazer à mão", it.AbrirManual is null ? "" : "abrir:" + it.AbrirManual));
                }
            }

            // 2) Apps da Loja, na conta da própria pessoa (sem administrador).
            int removidos = 0, n = 0;
            foreach (var app in apps)
            {
                n++;
                TxtOtimProgresso.Text = $"Removendo {app.Nome} ({n} de {apps.Count})…";
                await RegistrarAsync($"Otimizar: removendo {app.Nome} ({string.Join(", ", app.Def.Pacotes)}) da conta {Environment.UserName}.");
                try
                {
                    var res = await AppsDaConta.RemoverAsync(app.Def, CancellationToken.None);
                    if (res.Removidos.Count > 0 || res.Ok) AppsDaConta.Marcar(app.Id, true);
                    if (res.Ok)
                    {
                        removidos++;
                        linhas.Add(LinhaResultado.Certo("Removido: " + app.Nome));
                        await RegistrarAsync(res.Removidos.Count == 0
                            ? $"Otimizar: {app.Nome}: nenhum desses apps estava instalado."
                            : $"Otimizar: {app.Nome}: removido(s) {string.Join(", ", res.Removidos)}.");
                    }
                    else
                    {
                        var motivo = res.Erros.Count > 0 ? string.Join("; ", res.Erros)
                            : $"O Windows manteve {string.Join(", ", res.Restam)}: alguns apps do sistema não podem ser removidos.";
                        linhas.Add(LinhaResultado.Falhou(app.Nome, motivo, "Ver apps instalados", "abrir:ms-settings:appsfeatures"));
                        await RegistrarAsync($"Otimizar: {app.Nome} não removido: {motivo}");
                    }
                }
                catch (Exception ex)
                {
                    linhas.Add(LinhaResultado.Falhou(app.Nome, ex.Message));
                    await RegistrarAsync($"Otimizar: erro ao remover {app.Nome}: {ex.Message}");
                }
            }
            if (apps.Count > 0) resumo.Add($"{removidos} de {apps.Count} grupo(s) de apps removido(s).");

            // 3) Programas, pelo winget, na conta da pessoa.
            int instalados = 0, i = 0;
            foreach (var p in programas)
            {
                i++;
                TxtOtimProgresso.Text = $"Instalando {p.Nome} ({i} de {programas.Count})… pode levar alguns minutos.";
                await RegistrarAsync($"Otimizar: instalando {p.Nome} [{p.Def.WingetId}].");
                var (codigo, saida) = await WingetAsync(
                    $"install --id {p.Def.WingetId} -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --source winget", 30);
                if (WingetDeuCerto(codigo))
                {
                    instalados++;
                    linhas.Add(LinhaResultado.Certo("Instalado: " + p.Nome));
                }
                else
                {
                    var motivo = ResumoWinget(codigo, saida);
                    linhas.Add(LinhaResultado.Falhou(p.Nome, motivo));
                    await RegistrarAsync($"Otimizar: {p.Nome} não instalado: {motivo}");
                }
            }
            if (programas.Count > 0) resumo.Add($"{instalados} de {programas.Count} programa(s) instalado(s).");
        }
        catch (Exception ex)
        {
            linhas.Add(LinhaResultado.Falhou("Erro inesperado", ex.Message));
        }
        finally
        {
            _otimOcupado = false;
        }

        TxtOtimProgresso.Text = string.Join(" ", resumo);
        int falhas = linhas.Count(l => l.Cor == Cores.Coral);
        // Falhas primeiro: é o que pede atenção.
        linhas = linhas.OrderBy(l => l.Cor == Cores.Coral ? 0 : l.Cor == Cores.Luar ? 1 : 2).ToList();
        if (windows.Any(a => a.Def.Aviso?.Contains("reiniciar", StringComparison.OrdinalIgnoreCase) == true) && !cancelado)
            linhas.Add(LinhaResultado.Info("Alguns ajustes só valem depois de reiniciar o PC"));
        bool explorer = !cancelado && windows.Any(a => a.Def.ReiniciarExplorer) && linhas.Any(l => l.Cor == Cores.Aurora);
        var titulo = falhas == 0 ? "Pronto" : falhas == linhas.Count ? "Não deu certo" : $"Feito, com {falhas} item(ns) que não deram certo";
        var texto = string.Join(" ", resumo)
                    + (explorer ? " Alguns ajustes aparecem depois de reiniciar o Explorador de Arquivos (as janelas de pastas abertas fecham)." : "");
        var tom = falhas == 0 ? TomDialogo.Sucesso : TomDialogo.Atencao;
        if (explorer)
        {
            if (await PerguntarAsync(titulo, texto, linhas, "Reiniciar o Explorador", "Depois", tom))
                await ReiniciarExplorerAsync();
        }
        else await PerguntarAsync(titulo, texto, linhas, "Fechar", null, tom);

        await CarregarOtimizacaoAsync();
    }

    private async void DesfazerAjuste_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        var item = _ajustes.FirstOrDefault(a => a.Id == id);
        if (item is null || _otimOcupado) return;

        if (item.Def.Tipo == TipoAjuste.Programa)
        {
            if (!await PerguntarAsync($"Desinstalar {item.Nome}?", "O programa sai do PC. Dá para instalar de novo por aqui quando quiser.", null, "Desinstalar")) return;
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
            if (AppsDaConta.Removidos().ContainsKey(id))
            {
                AppsDaConta.Marcar(id, false);
                await CarregarOtimizacaoAsync();
                return;
            }
        }
        await DesfazerAsync(new List<string> { id });
    }

    private async void OtimDesfazerTudo_Click(object sender, RoutedEventArgs e)
    {
        var ids = _ajustes.Where(a => a.PeloZelo && a.Def.Tipo is not (TipoAjuste.App or TipoAjuste.Programa)).Select(a => a.Id).ToList();
        if (ids.Count == 0) return;
        if (!await PerguntarAsync($"Voltar {ids.Count} ajuste(s) para como estavam?",
                "Apps removidos e programas instalados ficam como estão: cada um tem o próprio botão.", null, "Desfazer tudo")) return;
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
            TxtOtimProgresso.Text = fim?.Resultado ?? "";
            if (fim is { Falhas.Count: > 0 })
                await PerguntarAsync("Alguns ajustes não voltaram", fim.Resultado ?? "",
                    fim.Itens.Select(it => it.Ok ? LinhaResultado.Certo("Desfeito: " + it.Nome) : LinhaResultado.Falhou(it.Nome, it.Motivo)).ToList(),
                    "Fechar", null, TomDialogo.Atencao);
            else if (fim is not null) Notificar(fim.Resultado ?? "Desfeito.");
        }
        catch (Exception ex) { Aviso("Não foi possível falar com o serviço do Zelo: " + ex.Message); }
        finally { _otimOcupado = false; }
        await CarregarOtimizacaoAsync();
    }

    /// <summary>Manda uma linha para o Registro do Zelo (a pasta do log só aceita escrita do serviço).</summary>
    private static async Task RegistrarAsync(string linha)
    {
        try { await ClientePipe.EnviarAsync(new Pedido { Comando = "registrar", Motivo = linha }); } catch { }
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
