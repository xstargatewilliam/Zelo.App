using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Zelo.Core;
using Zelo.Core.Tarefas;

namespace Zelo.App;

/// <summary>Cores da paleta "céu noturno" para uso no código.</summary>
public static class Cores
{
    private static SolidColorBrush P(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Aurora = P("#4FD1A1");
    public static readonly SolidColorBrush Ambar = P("#F2B84B");
    public static readonly SolidColorBrush Coral = P("#FF7A7A");
    public static readonly SolidColorBrush Luar = P("#8EA4FF");
    public static readonly SolidColorBrush Suave = P("#9AA5BD");
    public static readonly SolidColorBrush Texto = P("#E8ECF5");
    public static readonly SolidColorBrush Texto2 = P("#C9D1E3");
    public static readonly SolidColorBrush AuroraFundo = P("#183A33");
    public static readonly SolidColorBrush AmbarFundo = P("#33291A");
    public static readonly SolidColorBrush CoralFundo = P("#3A1F24");
    public static readonly SolidColorBrush NeutroFundo = P("#1E2638");
    public static readonly SolidColorBrush Linha = P("#222A3D");
    public static readonly SolidColorBrush BordaAmbar = P("#4A3D22");
    public static readonly SolidColorBrush BordaCoral = P("#5A3038");
    public static readonly SolidColorBrush Hover = P("#1B2233");
    public static readonly SolidColorBrush Transparente = P("#00000000");
    public static readonly SolidColorBrush LinhaForte = P("#2A3348");

    public static SolidColorBrush De(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => Aurora,
        StatusTarefa.Aviso => Ambar,
        StatusTarefa.Critico or StatusTarefa.Erro => Coral,
        _ => Suave,
    };

    public static SolidColorBrush FundoDe(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => AuroraFundo,
        StatusTarefa.Aviso => AmbarFundo,
        StatusTarefa.Critico or StatusTarefa.Erro => CoralFundo,
        _ => NeutroFundo,
    };

    public static SolidColorBrush DeScore(int score) =>
        score < 0 ? Suave : score >= 80 ? Aurora : score >= 50 ? Ambar : Coral;
}

public static class Icones
{
    public static readonly Geometry Ok = G("M20,6 L9,17 L4,12");
    public static readonly Geometry Atencao = G("M12,7 L12,13 M12,17 L12,17.01 M3,12 A9,9 0 1 0 21,12 A9,9 0 1 0 3,12");
    public static readonly Geometry Urgente = G("M12,3 L22,20 L2,20 Z M12,10 L12,14 M12,17 L12,17.01");
    public static readonly Geometry Pulado = G("M5,12 L19,12");
    public static readonly Geometry Disco = G("M4,7 C4,5.3 7.6,4 12,4 C16.4,4 20,5.3 20,7 C20,8.7 16.4,10 12,10 C7.6,10 4,8.7 4,7 Z M4,7 L4,17 C4,18.7 7.6,20 12,20 C16.4,20 20,18.7 20,17 L20,7");
    public static readonly Geometry Escudo = G("M12,3 L20,6 L20,12 C20,16.5 16.5,19.5 12,21 C7.5,19.5 4,16.5 4,12 L4,6 Z");
    public static readonly Geometry Seta = G("M12,4 L12,16 M7,11 L12,16 L17,11 M5,20 L19,20");
    public static readonly Geometry Copia = G("M8,8 L20,8 L20,20 L8,20 Z M4,16 L4,4 L16,4");
    public static readonly Geometry Sistema = G("M3,5 L21,5 L21,16 L3,16 Z M8,20 L16,20 M12,16 L12,20");

    private static Geometry G(string d)
    {
        var g = Geometry.Parse(d);
        g.Freeze();
        return g;
    }

    public static Geometry De(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => Ok,
        StatusTarefa.Aviso => Atencao,
        StatusTarefa.Critico or StatusTarefa.Erro => Urgente,
        _ => Pulado,
    };

    public static Geometry De(Categoria c) => c switch
    {
        Categoria.Disco => Disco,
        Categoria.Seguranca => Escudo,
        Categoria.Atualizacoes => Seta,
        Categoria.Backup => Copia,
        _ => Sistema,
    };
}

public static class Palavras
{
    public static string Status(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => "Tudo certo",
        StatusTarefa.Aviso => "Atenção",
        StatusTarefa.Critico => "Urgente",
        StatusTarefa.Erro => "Não deu certo",
        _ => "Pulada",
    };

    public static string Rotina(Rotina r) => r switch
    {
        Zelo.Core.Rotina.Diaria => "Vistoria diária",
        Zelo.Core.Rotina.Semanal => "Revisão semanal",
        _ => "Check-up mensal",
    };

    public static string Area(Categoria c) => c switch
    {
        Categoria.Disco => "Discos",
        Categoria.Seguranca => "Segurança",
        Categoria.Atualizacoes => "Atualizações",
        Categoria.Backup => "Backup",
        _ => "Sistema",
    };

    public static string Hora(int h) => $"{h}h";

    public static string Duracao(TimeSpan t) =>
        t.TotalSeconds < 1 ? "menos de 1 s" :
        t.TotalMinutes < 1 ? $"{t.TotalSeconds:0} s" :
        t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min" :
        $"{(int)t.TotalHours} h {t.Minutes} min";

    public static string Quando(DateTime? d)
    {
        if (d is null) return "ainda não aconteceu";
        var v = d.Value;
        var hoje = DateTime.Today;
        if (v.Date == hoje) return $"hoje, {v:HH:mm}";
        if (v.Date == hoje.AddDays(-1)) return $"ontem, {v:HH:mm}";
        if (v.Date == hoje.AddDays(1)) return $"amanhã, a partir das {v:HH:mm}";
        return v > DateTime.Now ? $"a partir de {v:dd/MM}" : $"{v:dd/MM}, {v:HH:mm}";
    }

    /// <summary>Lê "9", "09", "9h", "09:00", "22h30" como hora cheia (0-23).</summary>
    public static int? LerHora(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var digitos = new string(texto.Trim().TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digitos, out var h) && h is >= 0 and <= 24 ? h % 24 : null;
    }
}

/// <summary>Uma das cinco áreas na tela inicial.</summary>
public sealed class ItemArea
{
    public Categoria Categoria { get; init; }
    public string Nome { get; init; } = "";
    public string Texto { get; init; } = "";
    public string StatusTexto { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Suave;
    public Brush Borda { get; init; } = Cores.Linha;
    public Geometry Icone { get; init; } = Icones.Pulado;
}

/// <summary>Uma verificação dentro do detalhe de uma área (Disco, Segurança…).</summary>
public sealed class ItemVerificacao
{
    public string Nome { get; init; } = "";
    public string StatusTexto { get; init; } = "";
    public string Resumo { get; init; } = "";
    public string Quando { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Suave;
    public Brush CorFundo { get; init; } = Cores.NeutroFundo;
    public Geometry Icone { get; init; } = Icones.Pulado;
    public List<AcaoPonto> Acoes { get; init; } = new();
    public List<ItemDetalheTexto> Detalhes { get; init; } = new();
    public int Ordem { get; init; }
    public Visibility VisAcoes => Acoes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisDetalhes => Detalhes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static ItemVerificacao De(Tarefa t, ResultadoTarefa? r, bool desligada)
    {
        var onde = Palavras.Rotina(t.Rotinas[0]).ToLowerInvariant();
        if (desligada)
            return new ItemVerificacao { Nome = t.Nome, StatusTexto = "Desligada", Resumo = "Você desligou esta verificação nos Ajustes.", Ordem = 9 };
        if (r is null)
            return new ItemVerificacao { Nome = t.Nome, StatusTexto = "Ainda não rodou", Resumo = $"Roda na {onde}.", Ordem = 8 };

        var ponto = new PontoAtencao { TarefaId = t.Id, Nome = t.Nome, Categoria = t.Categoria, Status = r.Status, Resumo = r.Resumo };
        var acoes = ItemPonto.AcoesPara(ponto);
        if (r.Status is StatusTarefa.Ok or StatusTarefa.Pulado or StatusTarefa.Ignorada)
            acoes = acoes.Where(a => !a.Id.StartsWith("dispensar:") && !a.Id.StartsWith("tarefa:")).ToList();
        return new ItemVerificacao
        {
            Nome = t.Nome,
            StatusTexto = Palavras.Status(r.Status),
            Resumo = string.IsNullOrWhiteSpace(r.Resumo) ? "Sem observações." : r.Resumo,
            Quando = $"{Palavras.Quando(r.Fim)} · na {onde}",
            Cor = Cores.De(r.Status), CorFundo = Cores.FundoDe(r.Status), Icone = Icones.De(r.Status),
            Acoes = acoes,
            Detalhes = r.Detalhes.Where(d => !string.IsNullOrWhiteSpace(d)).Select(ItemDetalheTexto.De).ToList(),
            Ordem = r.Status switch
            {
                StatusTarefa.Critico => 0, StatusTarefa.Erro => 1, StatusTarefa.Aviso => 2, StatusTarefa.Ok => 3, _ => 4,
            },
        };
    }
}

/// <summary>Um botão de ação num ponto de atenção.</summary>
public sealed class AcaoPonto
{
    public string Texto { get; init; } = "";
    /// <summary>abrir:&lt;uri&gt; · cmd:&lt;exe&gt;|&lt;args&gt; · zelo:backup · dispensar:&lt;tarefa&gt;|&lt;motivo&gt; · tarefa:&lt;id&gt;</summary>
    public string Id { get; init; } = "";
}

/// <summary>Um item de "Precisa da sua atenção".</summary>
public sealed class ItemPonto : INotifyPropertyChanged
{
    /// <summary>Os cartões abertos continuam abertos quando o painel se atualiza (a cada 2 segundos).</summary>
    private static readonly HashSet<string> Abertos = new();

    /// <summary>Identifica o cartão entre uma atualização e outra.</summary>
    public string Chave { get; init; } = "";

    public string Titulo { get; init; } = "";
    public string Sub { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Ambar;
    public Brush CorFundo { get; init; } = Cores.AmbarFundo;
    public Geometry Icone { get; init; } = Icones.Atencao;
    public List<AcaoPonto> Acoes { get; init; } = new();
    public List<ItemDetalheTexto> Detalhes { get; init; } = new();

    public Visibility VisAcoes => Acoes.Count > 0 || Detalhes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VisBotaoDetalhes => Detalhes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool Expandido
    {
        get => Chave.Length > 0 && Abertos.Contains(Chave);
        set
        {
            if (Expandido == value || Chave.Length == 0) return;
            if (value) Abertos.Add(Chave); else Abertos.Remove(Chave);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expandido)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextoDetalhes)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisDetalhes)));
        }
    }

    public string TextoDetalhes => Expandido ? "Ocultar detalhes" : "Ver detalhes";
    public Visibility VisDetalhes => Expandido ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>O que a pessoa pode fazer, conforme a verificação e o que ela encontrou.</summary>
    public static List<AcaoPonto> AcoesPara(PontoAtencao p)
    {
        var r = new List<AcaoPonto>();
        void A(string texto, string id) => r.Add(new AcaoPonto { Texto = texto, Id = id });
        string resumo = p.Resumo;
        bool interrompida = resumo.Contains("Interrompida", StringComparison.OrdinalIgnoreCase) || p.Status == StatusTarefa.Erro;

        switch (p.TarefaId)
        {
            case "auditoria-seguranca" when resumo.Contains("administrador", StringComparison.OrdinalIgnoreCase):
                A("Gerenciar contas", "cmd:control.exe|/name Microsoft.UserAccounts /page pageAdminTasks");
                A("São contas minhas", $"dispensar:{p.TarefaId}|administradores reconhecidos");
                break;
            case "auditoria-seguranca" or "defender-rapida" or "defender-completa":
                A("Abrir Segurança do Windows", "abrir:windowsdefender:");
                break;
            case "eventos-criticos" when resumo.Contains("desligamento inesperado", StringComparison.OrdinalIgnoreCase):
                A("Foi queda de energia", $"dispensar:{p.TarefaId}|queda de energia");
                A("Fui eu que desliguei", $"dispensar:{p.TarefaId}|desligado pelo usuário");
                A("Ver no Visualizador de Eventos", "cmd:eventvwr.msc|");
                break;
            case "eventos-criticos":
                A("Ver no Visualizador de Eventos", "cmd:eventvwr.msc|");
                A("Dispensar", $"dispensar:{p.TarefaId}|");
                break;
            case "espaco-disco" or "maiores-arquivos":
                A("Ver o que ocupa espaço", "abrir:ms-settings:storagesense");
                break;
            case "backup-incremental" or "verificar-backup":
                A("Configurar backup", "zelo:backup");
                break;
            case "inicializacao":
                A("Ver aplicativos de inicialização", "abrir:ms-settings:startupapps");
                A("Reconheço todos", $"dispensar:{p.TarefaId}|programas reconhecidos");
                break;
            case "windows-update" or "reinicio-pendente" or "drivers-wu":
                if (interrompida || p.TarefaId != "reinicio-pendente") A("Tentar novamente", $"tarefa:{p.TarefaId}");
                A("Abrir o Windows Update", "abrir:ms-settings:windowsupdate");
                break;
            case "bateria":
                A("Ver a bateria", "abrir:ms-settings:batterysaver");
                break;
            default:
                if (interrompida || p.TarefaId is "winget" or "otimizar-unidades" or "ponto-restauracao" or "integridade-sistema")
                    A("Tentar novamente", $"tarefa:{p.TarefaId}");
                break;
        }
        if (p.Status is StatusTarefa.Aviso && r.All(x => !x.Id.StartsWith("dispensar:")) && p.TarefaId is not ("backup-incremental" or "espaco-disco"))
            A("Dispensar", $"dispensar:{p.TarefaId}|");
        return r;
    }
}

public sealed class ItemEtapa
{
    public string Nome { get; init; } = "";
    public string Detalhe { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Aurora;
    public Brush CorFundo { get; init; } = Cores.AuroraFundo;
    public Brush CorNome { get; init; } = Cores.Texto;
    public Brush CorDetalhe { get; init; } = Cores.Suave;
    public Brush Fundo { get; init; } = Cores.Transparente;
    public Geometry Icone { get; init; } = Icones.Ok;
    public FontWeight Peso { get; init; } = FontWeights.Normal;
    public Visibility VisFeito { get; init; } = Visibility.Collapsed;
    public Visibility VisPendente { get; init; } = Visibility.Collapsed;
    public Visibility VisRodando { get; init; } = Visibility.Collapsed;

    public static ItemEtapa De(EtapaAtual e)
    {
        if (e.EmAndamento)
            return new ItemEtapa
            {
                Nome = e.Nome, Detalhe = "agora", Peso = FontWeights.SemiBold, Fundo = Cores.Hover,
                CorDetalhe = Cores.Texto2, VisRodando = Visibility.Visible,
            };
        if (e.Status is null)
            return new ItemEtapa { Nome = e.Nome, CorNome = Cores.Texto2, VisPendente = Visibility.Visible };

        var s = e.Status.Value;
        return new ItemEtapa
        {
            Nome = e.Nome,
            Detalhe = s == StatusTarefa.Ok ? $"{e.Resumo} · {e.Segundos} s" : e.Resumo,
            Cor = Cores.De(s), CorFundo = Cores.FundoDe(s), Icone = Icones.De(s),
            CorDetalhe = s == StatusTarefa.Ok || s == StatusTarefa.Ignorada ? Cores.Suave : Cores.De(s),
            VisFeito = Visibility.Visible,
        };
    }
}

public sealed class ItemAgenda
{
    public string Nome { get; init; } = "";
    public string Ultima { get; init; } = "";
    public string Proxima { get; init; } = "";
    public Thickness Separador { get; init; } = new(0, 0, 0, 1);
}

/// <summary>Uma execução no Histórico (expansível).</summary>
public sealed class ItemExecucao : INotifyPropertyChanged
{
    private bool _expandido;

    public string Titulo { get; init; } = "";
    public string Resumo { get; init; } = "";
    public string StatusTexto { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Suave;
    public Brush CorFundo { get; init; } = Cores.NeutroFundo;
    public string? Relatorio { get; init; }
    public bool Simulacao { get; init; }
    public bool TemProblema { get; init; }
    public List<LinhaDetalhe> Linhas { get; init; } = new();

    public bool Expandido
    {
        get => _expandido;
        set
        {
            if (_expandido == value) return;
            _expandido = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expandido)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextoBotao)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VisDetalhes)));
        }
    }

    public string TextoBotao => Expandido ? "Ocultar detalhes" : "Ver detalhes";
    public Visibility VisDetalhes => Expandido ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static ItemExecucao De(ExecucaoRotina e)
    {
        var relevantes = e.Resultados.Where(r => r.TarefaId != "cancelada").ToList();
        int problemas = relevantes.Count(r => r.Status is StatusTarefa.Aviso or StatusTarefa.Critico or StatusTarefa.Erro);
        bool interrompida = e.Resultados.Any(r => r.TarefaId == "cancelada");
        var pior = e.PiorStatus;

        string status = e.Simulacao ? "Simulação" : interrompida ? "Interrompida" : Palavras.Status(pior);
        var partes = new List<string> { $"{relevantes.Count} verificações" };
        if (problemas > 0) partes.Add(problemas == 1 ? "1 ponto de atenção" : $"{problemas} pontos de atenção");
        if (e.Simulacao) partes.Add("nada foi alterado");
        partes.Add(Palavras.Duracao(e.Fim - e.Inicio));

        var linhas = relevantes
            .OrderBy(r => r.Status switch { StatusTarefa.Critico => 0, StatusTarefa.Erro => 1, StatusTarefa.Aviso => 2, StatusTarefa.Ok => 3, _ => 4 })
            .Select(r => new LinhaDetalhe
            {
                Rotulo = Palavras.Status(r.Status),
                Cor = Cores.De(r.Status),
                Nome = r.Nome,
                Resumo = r.Resumo,
                Detalhes = r.Detalhes.Take(60).Select(ItemDetalheTexto.De).ToList(),
            }).ToList();

        return new ItemExecucao
        {
            Titulo = e.Origem == "tarefa"
                ? $"Nova tentativa: {relevantes.FirstOrDefault()?.Nome ?? "verificação"} · {e.Inicio:HH:mm}"
                : $"{Palavras.Rotina(e.Rotina)} · {e.Inicio:HH:mm}",
            Resumo = string.Join(" · ", partes),
            StatusTexto = status,
            Cor = e.Simulacao || interrompida ? Cores.Texto2 : Cores.De(pior),
            CorFundo = e.Simulacao || interrompida ? Cores.NeutroFundo : Cores.FundoDe(pior),
            Relatorio = e.Relatorio,
            Simulacao = e.Simulacao,
            TemProblema = problemas > 0,
            Linhas = linhas,
        };
    }
}

public sealed class LinhaDetalhe
{
    public string Rotulo { get; init; } = "";
    public Brush Cor { get; init; } = Cores.Suave;
    public string Nome { get; init; } = "";
    public string Resumo { get; init; } = "";
    public List<ItemDetalheTexto> Detalhes { get; init; } = new();
    public Visibility VisDetalhes => Detalhes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Uma linha de detalhe; se tiver um endereço https, vira um botão "Abrir site".</summary>
public sealed class ItemDetalheTexto
{
    private static readonly System.Text.RegularExpressions.Regex Url = new(@"https://\S+");

    public string Texto { get; init; } = "";
    public string? Link { get; init; }
    public Visibility VisLink => Link is null ? Visibility.Collapsed : Visibility.Visible;

    public static ItemDetalheTexto De(string linha)
    {
        var m = Url.Match(linha);
        if (!m.Success) return new ItemDetalheTexto { Texto = linha.Trim() };
        var texto = linha.Remove(m.Index, m.Length).Trim().TrimEnd('—', '-', ':', ' ');
        return new ItemDetalheTexto { Texto = texto, Link = m.Value.TrimEnd('.', ',', ')') };
    }
}

public sealed class GrupoExecucoes
{
    public string Titulo { get; init; } = "";
    public List<ItemExecucao> Itens { get; init; } = new();
}

public sealed class ItemPasta
{
    public string Caminho { get; init; } = "";
    public string Nome => Path.GetFileName(Caminho.TrimEnd('\\')) is { Length: > 0 } n ? n : Caminho;
    public string RotuloRemover => $"Tirar {Nome} do backup";
}

/// <summary>Linha editável da lista de verificações (opções avançadas).</summary>
public sealed class ItemTarefaConfig
{
    public string Id { get; init; } = "";
    public string Nome { get; init; } = "";
    public string Descricao { get; init; } = "";
    public string Rotinas { get; init; } = "";
    public bool Ativa { get; set; } = true;
}
