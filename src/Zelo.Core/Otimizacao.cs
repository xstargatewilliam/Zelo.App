using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Zelo.Core;

/// <summary>Como a pessoa usa o PC. Dá para escolher mais de um.</summary>
public enum PerfilUso { Jogos, Trabalho, Estudos, Edicao, Dev }

public enum RiscoAjuste { Baixo, Medio, Alto }

public enum TipoAjuste { Registro, Servico, App, Script, Programa }

public enum SituacaoAjuste { Padrao, Ativo, Indisponivel }

/// <summary>Um valor no registro. Caminho começa com HKLM\ ou HKCU\ (HKCU = a pessoa que está usando o painel).</summary>
public sealed record ValorReg(string Caminho, string Nome, object Valor);

/// <summary>Um ajuste do Windows: o que muda, para quem serve e como desfazer.</summary>
public sealed class Ajuste
{
    public string Id { get; init; } = "";
    public string Grupo { get; init; } = "";
    public string Nome { get; init; } = "";
    public string Descricao { get; init; } = "";
    /// <summary>Algo que a pessoa precisa saber antes (ex.: só vale depois de reiniciar).</summary>
    public string? Aviso { get; init; }
    public TipoAjuste Tipo { get; init; }
    public RiscoAjuste Risco { get; init; } = RiscoAjuste.Baixo;
    /// <summary>Perfis para os quais o ajuste faz sentido. Vazio = todos.</summary>
    public PerfilUso[] Perfis { get; init; } = Array.Empty<PerfilUso>();
    /// <summary>Não vem marcado se algum destes perfis estiver escolhido.</summary>
    public PerfilUso[] Exceto { get; init; } = Array.Empty<PerfilUso>();
    public bool Recomendado { get; init; }
    public bool ReiniciarExplorer { get; init; }

    public ValorReg[] Valores { get; init; } = Array.Empty<ValorReg>();
    public string[] Servicos { get; init; } = Array.Empty<string>();
    /// <summary>Tipo de início desejado do serviço: 3 = manual, 4 = desativado.</summary>
    public int InicioServico { get; init; } = 4;
    /// <summary>Nomes de pacotes da Microsoft Store (aceita * como curinga).</summary>
    public string[] Pacotes { get; init; } = Array.Empty<string>();
    /// <summary>O que procurar na Microsoft Store para reinstalar.</summary>
    public string? BuscaLoja { get; init; }
    public string? WingetId { get; init; }
    /// <summary>Script que imprime ALVO (já está como queremos), INDISPONIVEL ou o valor atual.</summary>
    public string? ScriptLer { get; init; }
    public string? ScriptAplicar { get; init; }
    /// <summary>Recebe o valor anterior em {{ANTERIOR}}.</summary>
    public string? ScriptRestaurar { get; init; }

    public bool Relevante(IReadOnlyCollection<PerfilUso> escolhidos) =>
        Perfis.Length == 0 || Perfis.Any(escolhidos.Contains);

    public bool RecomendadoPara(IReadOnlyCollection<PerfilUso> escolhidos) =>
        Recomendado && Relevante(escolhidos) && !Exceto.Any(escolhidos.Contains);
}

public sealed class EstadoAjuste
{
    public string Id { get; set; } = "";
    public SituacaoAjuste Situacao { get; set; }
    /// <summary>Foi o Zelo que aplicou (e sabe desfazer).</summary>
    public bool PeloZelo { get; set; }
    public DateTime? Quando { get; set; }
}

public sealed class AndamentoOtimizacao
{
    public bool Rodando { get; set; }
    public bool Desfazendo { get; set; }
    public string Etapa { get; set; } = "";
    public int Feitos { get; set; }
    public int Total { get; set; }
    public string? Resultado { get; set; }
    public List<string> Falhas { get; set; } = new();
    public bool? PontoCriado { get; set; }
    /// <summary>O ponto de restauração falhou e nada foi alterado (a pessoa decide se continua).</summary>
    public bool PontoFalhou { get; set; }
    public DateTime? Fim { get; set; }
}

public sealed class AplicacaoAjuste
{
    public string Id { get; set; } = "";
    /// <summary>false enquanto o ajuste está sendo aplicado. Se o serviço cair nesse meio, ele é desfeito ao voltar.</summary>
    public bool Concluido { get; set; }
    public DateTime Quando { get; set; }
    public string? Sid { get; set; }
    /// <summary>Como cada item estava antes. null = não existia.</summary>
    public Dictionary<string, string?> Anterior { get; set; } = new();
}

public sealed class RegistroOtimizacao
{
    public int Versao { get; set; } = 1;
    public List<PerfilUso> Perfis { get; set; } = new();
    /// <summary>Chave: id do ajuste, ou id@SID para os que valem só para uma conta.</summary>
    public Dictionary<string, AplicacaoAjuste> Aplicados { get; set; } = new();
}

public static class NomesPerfil
{
    public static string Perfil(PerfilUso p) => p switch
    {
        PerfilUso.Jogos => "Jogos",
        PerfilUso.Trabalho => "Trabalho",
        PerfilUso.Estudos => "Estudos",
        PerfilUso.Edicao => "Edição e imagem",
        PerfilUso.Dev => "Programação e Dev",
        _ => p.ToString(),
    };
}

/// <summary>
/// Tudo que o Zelo sabe ajustar. Regras: nada que desligue o Windows Update, o antivírus,
/// o Firewall ou o UAC; nada que remova a Loja, o Edge ou componentes do sistema;
/// todo ajuste guarda como estava antes e pode ser desfeito.
/// </summary>
public static class CatalogoOtimizacao
{
    private const string GApps = "Apps que vieram com o Windows";
    private const string GPriv = "Privacidade e propaganda";
    private const string GExpl = "Explorador de Arquivos";
    private const string GDesemp = "Desempenho";
    private const string GJogos = "Jogos";
    private const string GProd = "Trabalho e estudos";
    private const string GEdic = "Edição e imagem";
    private const string GDev = "Programação";
    private const string GProg = "Programas para instalar";
    private const string GAvanc = "Avançado";

    private const string Cdm = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Adv = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static readonly PerfilUso[] Jogos = { PerfilUso.Jogos };
    private static readonly PerfilUso[] Edicao = { PerfilUso.Edicao };
    private static readonly PerfilUso[] Dev = { PerfilUso.Dev };

    public static readonly string[] Grupos = { GApps, GPriv, GExpl, GDesemp, GJogos, GProd, GEdic, GDev, GProg, GAvanc };

    private static Ajuste App(string id, string nome, string desc, string busca, bool rec, params string[] pacotes) => new()
    {
        Id = id, Grupo = GApps, Nome = nome, Descricao = desc, Tipo = TipoAjuste.App, Recomendado = rec,
        Pacotes = pacotes, BuscaLoja = busca,
    };

    private static Ajuste Prog(string id, string nome, string desc, string wingetId, PerfilUso[] perfis, bool rec) => new()
    {
        Id = id, Grupo = GProg, Nome = nome, Descricao = desc, Tipo = TipoAjuste.Programa, Recomendado = rec,
        WingetId = wingetId, Perfis = perfis,
    };

    public static readonly List<Ajuste> Todos = new()
    {
        // ------------------------------------------------------------ Apps que vieram com o Windows
        App("app-propaganda", "Jogos de propaganda (Candy Crush e afins)", "Vêm instalados como anúncio. Remover não afeta nada do Windows.", "Candy Crush", true,
            "king.com.*", "*CandyCrush*", "*BubbleWitch*", "*MarchofEmpires*", "*HiddenCity*"),
        App("app-dicas", "Dicas do Windows", "O app de \"Introdução\" que aparece com sugestões.", "Dicas", true, "Microsoft.Getstarted"),
        App("app-noticias", "Microsoft Notícias", "Notícias do Bing. Você continua com as notícias no navegador.", "Microsoft Notícias", true, "Microsoft.BingNews"),
        App("app-feedback", "Hub de Comentários", "Para enviar sugestões à Microsoft. Pouca gente usa.", "Hub de Comentários", true, "Microsoft.WindowsFeedbackHub"),
        App("app-mapas", "Mapas do Windows", "Mapas offline do Windows. O Google Maps no navegador faz o mesmo.", "Mapas do Windows", true, "Microsoft.WindowsMaps"),
        App("app-antigos", "Apps aposentados (Cortana, Skype, Pessoas, Portal de Realidade Mista)", "A própria Microsoft encerrou esses apps.", "Skype", true,
            "Microsoft.549981C3F5F10", "Microsoft.SkypeApp", "Microsoft.People", "Microsoft.MixedReality.Portal", "Microsoft.Windows.DevHome"),
        App("app-filmes", "Filmes e TV", "O player antigo de vídeos comprados na Loja. O Media Player continua.", "Filmes e TV", true, "Microsoft.ZuneVideo"),
        new Ajuste
        {
            Id = "app-office-hub", Grupo = GApps, Nome = "Microsoft 365 (atalho do Office)", Tipo = TipoAjuste.App, Recomendado = true,
            Descricao = "É só um atalho para assinar ou abrir o Office na web. Não remove Word, Excel ou PowerPoint instalados.",
            Exceto = new[] { PerfilUso.Trabalho, PerfilUso.Estudos }, Pacotes = new[] { "Microsoft.MicrosoftOfficeHub" }, BuscaLoja = "Microsoft 365",
        },
        new Ajuste
        {
            Id = "app-clipchamp", Grupo = GApps, Nome = "Clipchamp", Tipo = TipoAjuste.App, Recomendado = true,
            Descricao = "Editor de vídeo online da Microsoft.", Exceto = Edicao, Pacotes = new[] { "Clipchamp.Clipchamp" }, BuscaLoja = "Clipchamp",
        },
        new Ajuste
        {
            Id = "app-power-automate", Grupo = GApps, Nome = "Power Automate", Tipo = TipoAjuste.App, Recomendado = true,
            Descricao = "Automação de tarefas para empresas.", Exceto = new[] { PerfilUso.Trabalho, PerfilUso.Dev },
            Pacotes = new[] { "Microsoft.PowerAutomateDesktop" }, BuscaLoja = "Power Automate",
        },
        App("app-paciencia", "Paciência (Solitaire Collection)", "Os jogos de cartas da Microsoft, com anúncios.", "Solitaire", false, "Microsoft.MicrosoftSolitaireCollection"),
        App("app-clima", "Clima", "Previsão do tempo do Bing.", "Clima", false, "Microsoft.BingWeather"),
        App("app-todo", "Microsoft To Do", "Lista de tarefas da Microsoft.", "Microsoft To Do", false, "Microsoft.Todos"),
        App("app-copilot", "Copilot", "O assistente de IA da Microsoft. Remover só tira o app, não mexe no Windows.", "Copilot", false, "Microsoft.Copilot"),
        App("app-terceiros", "Spotify, Disney+, TikTok, Instagram e Facebook pré-instalados", "Só remove se vieram com o Windows e estiverem instalados. Se você usa algum, deixe desmarcado.", "Spotify", false,
            "SpotifyAB.SpotifyMusic", "Disney.37853FC22B2CE", "*TikTok*", "*Instagram*", "*Facebook*"),
        new Ajuste
        {
            Id = "app-xbox", Grupo = GApps, Nome = "Apps do Xbox (para quem não joga no PC)", Tipo = TipoAjuste.App, Risco = RiscoAjuste.Medio,
            Descricao = "Xbox, Game Bar e o login do Xbox. Jogos da Loja e do Game Pass precisam deles.", Exceto = Jogos,
            Pacotes = new[] { "Microsoft.GamingApp", "Microsoft.XboxApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxSpeechToTextOverlay" }, BuscaLoja = "Xbox",
        },

        // ------------------------------------------------------------ Privacidade e propaganda
        new Ajuste
        {
            Id = "priv-sugestoes", Grupo = GPriv, Nome = "Tirar sugestões e anúncios do Iniciar e das Configurações", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "Sem \"apps sugeridos\", dicas e propaganda no menu Iniciar, na tela de bloqueio e nas Configurações.",
            Valores = new[]
            {
                new ValorReg(Cdm, "SubscribedContent-338388Enabled", 0), new ValorReg(Cdm, "SubscribedContent-338389Enabled", 0),
                new ValorReg(Cdm, "SubscribedContent-353694Enabled", 0), new ValorReg(Cdm, "SubscribedContent-353696Enabled", 0),
                new ValorReg(Cdm, "SystemPaneSuggestionsEnabled", 0), new ValorReg(Cdm, "SoftLandingEnabled", 0),
            },
        },
        new Ajuste
        {
            Id = "priv-instalar-sozinho", Grupo = GPriv, Nome = "Impedir o Windows de instalar apps de propaganda sozinho", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "Evita que Candy Crush e afins voltem depois de uma atualização.",
            Valores = new[]
            {
                new ValorReg(Cdm, "SilentInstalledAppsEnabled", 0), new ValorReg(Cdm, "PreInstalledAppsEnabled", 0),
                new ValorReg(Cdm, "OemPreInstalledAppsEnabled", 0),
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1),
            },
        },
        new Ajuste
        {
            Id = "priv-id-anuncio", Grupo = GPriv, Nome = "Desligar o ID de publicidade", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "Os apps deixam de usar um identificador seu para mostrar anúncios personalizados.",
            Valores = new[] { new ValorReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0) },
        },
        new Ajuste
        {
            Id = "priv-recomendacoes", Grupo = GPriv, Nome = "Menos recomendações e avisos de conta no Iniciar", Tipo = TipoAjuste.Registro, Recomendado = true, ReiniciarExplorer = true,
            Descricao = "Tira as dicas e os lembretes de conta Microsoft que aparecem no menu Iniciar do Windows 11.",
            Valores = new[] { new ValorReg(Adv, "Start_IrisRecommendations", 0), new ValorReg(Adv, "Start_AccountNotifications", 0) },
        },
        new Ajuste
        {
            Id = "priv-telemetria", Grupo = GPriv, Nome = "Enviar à Microsoft só os dados de diagnóstico obrigatórios", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "O Windows Update continua funcionando normalmente.",
            Aviso = "As Configurações vão dizer que \"algumas configurações são gerenciadas pela sua organização\". É normal.",
            Valores = new[]
            {
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1),
                new ValorReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            },
        },
        new Ajuste
        {
            Id = "priv-atividades", Grupo = GPriv, Nome = "Não guardar o histórico de atividades", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "O Windows deixa de registrar quais arquivos e apps você abriu para mandar para a nuvem.",
            Valores = new[]
            {
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            },
        },
        new Ajuste
        {
            Id = "priv-bing", Grupo = GPriv, Nome = "Pesquisa do Iniciar sem resultados da web (Bing)", Tipo = TipoAjuste.Registro, Recomendado = true, ReiniciarExplorer = true,
            Descricao = "A busca do menu Iniciar fica só com seus apps, arquivos e configurações, e mais rápida.",
            Valores = new[] { new ValorReg(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1) },
        },
        new Ajuste
        {
            Id = "priv-widgets", Grupo = GPriv, Nome = "Tirar Widgets e notícias da barra de tarefas", Tipo = TipoAjuste.Registro, Recomendado = true, ReiniciarExplorer = true,
            Descricao = "Some o painel de notícias, clima e anúncios que abre ao passar o mouse.",
            Valores = new[]
            {
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0),
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0),
            },
        },
        new Ajuste
        {
            Id = "priv-recall", Grupo = GPriv, Nome = "Desligar o Recall (capturas automáticas da tela)", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "Nos PCs Copilot+, o Recall fotografa a tela o tempo todo. Nos outros, não muda nada.",
            Valores = new[]
            {
                new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                new ValorReg(@"HKCU\Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
            },
        },

        // ------------------------------------------------------------ Explorador de Arquivos
        new Ajuste
        {
            Id = "expl-extensoes", Grupo = GExpl, Nome = "Mostrar a extensão dos arquivos (.pdf, .exe…)", Tipo = TipoAjuste.Registro, Recomendado = true, ReiniciarExplorer = true,
            Descricao = "Também é segurança: fica fácil ver que \"fatura.pdf.exe\" é um programa, não um PDF.",
            Valores = new[] { new ValorReg(Adv, "HideFileExt", 0) },
        },
        new Ajuste
        {
            Id = "expl-ocultos", Grupo = GExpl, Nome = "Mostrar arquivos e pastas ocultos", Tipo = TipoAjuste.Registro, Recomendado = true, ReiniciarExplorer = true,
            Descricao = "Pastas como AppData e .git aparecem. Os arquivos protegidos do sistema continuam escondidos.",
            Perfis = Dev, Valores = new[] { new ValorReg(Adv, "Hidden", 1) },
        },
        new Ajuste
        {
            Id = "expl-este-pc", Grupo = GExpl, Nome = "Abrir o Explorador em \"Este Computador\"", Tipo = TipoAjuste.Registro, ReiniciarExplorer = true,
            Descricao = "Em vez da tela Início, abre direto nos discos.",
            Perfis = new[] { PerfilUso.Trabalho, PerfilUso.Dev, PerfilUso.Edicao }, Valores = new[] { new ValorReg(Adv, "LaunchTo", 1) },
        },

        // ------------------------------------------------------------ Desempenho
        new Ajuste
        {
            Id = "desemp-plano", Grupo = GDesemp, Nome = "Plano de energia Alto desempenho", Tipo = TipoAjuste.Script, Recomendado = true,
            Descricao = "O processador não reduz a velocidade para economizar. Ideal para PC de mesa.",
            Aviso = "Em notebook, gasta mais bateria.",
            Perfis = new[] { PerfilUso.Jogos, PerfilUso.Edicao },
            ScriptLer = """
                $g = [regex]::Match((powercfg /getactivescheme | Out-String), '[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}').Value
                if ($g -in @('8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c','e9a42b02-d5df-448d-aa00-03f14749eb61','5a1e10de-2e10-4c1f-9a3d-7a5e5e10de01')) { 'ALVO' } else { $g }
                """,
            ScriptAplicar = """
                $null = powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c
                if ($LASTEXITCODE -ne 0) {
                  powercfg /duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 5a1e10de-2e10-4c1f-9a3d-7a5e5e10de01 | Out-Null
                  powercfg /setactive 5a1e10de-2e10-4c1f-9a3d-7a5e5e10de01
                  if ($LASTEXITCODE -ne 0) { throw 'Este PC não tem o plano Alto desempenho.' }
                }
                """,
            ScriptRestaurar = "powercfg /setactive {{ANTERIOR}}",
        },
        new Ajuste
        {
            Id = "desemp-inicializacao", Grupo = GDesemp, Nome = "Abrir os programas de inicialização sem esperar", Tipo = TipoAjuste.Registro,
            Descricao = "O Windows segura os programas que iniciam com ele por alguns segundos. Sem a espera, eles abrem logo (o começo fica um pouco mais carregado).",
            Valores = new[] { new ValorReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0) },
        },

        // ------------------------------------------------------------ Jogos
        new Ajuste
        {
            Id = "jogo-modo", Grupo = GJogos, Nome = "Modo de Jogo ligado", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Jogos,
            Descricao = "Durante o jogo, o Windows segura atualizações e tarefas de fundo.",
            Valores = new[] { new ValorReg(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1), new ValorReg(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1) },
        },
        new Ajuste
        {
            Id = "jogo-gpu", Grupo = GJogos, Nome = "Agendamento de GPU acelerado por hardware", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Jogos, Risco = RiscoAjuste.Medio,
            Descricao = "A placa de vídeo organiza o próprio trabalho: menos atraso e, às vezes, mais FPS.",
            Aviso = "Vale depois de reiniciar. Placas antigas ignoram o ajuste.",
            Valores = new[] { new ValorReg(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2) },
        },
        new Ajuste
        {
            Id = "jogo-mouse", Grupo = GJogos, Nome = "Desligar a aceleração do mouse", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Jogos,
            Descricao = "A mira anda sempre a mesma distância para o mesmo movimento da mão.",
            Aviso = "Vale depois de sair e entrar de novo no Windows.",
            Valores = new[]
            {
                new ValorReg(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0"), new ValorReg(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0"),
                new ValorReg(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0"),
            },
        },
        new Ajuste
        {
            Id = "jogo-dvr", Grupo = GJogos, Nome = "Desligar a gravação em segundo plano da Game Bar", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Jogos,
            Descricao = "A Game Bar para de gravar os últimos minutos do jogo o tempo todo. Prints e gravação manual continuam.",
            Valores = new[]
            {
                new ValorReg(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0),
                new ValorReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0),
            },
        },
        new Ajuste
        {
            Id = "jogo-janela", Grupo = GJogos, Nome = "Otimizações para jogos em janela", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Jogos,
            Descricao = "Jogos em janela e tela cheia sem borda ficam com menos atraso (Windows 11).",
            Valores = new[] { new ValorReg(@"HKCU\Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=1;") },
        },

        // ------------------------------------------------------------ Trabalho e estudos
        new Ajuste
        {
            Id = "prod-area-transferencia", Grupo = GProd, Nome = "Histórico da área de transferência (Win + V)", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "Guarda as últimas coisas que você copiou. Win + V mostra a lista.",
            Perfis = new[] { PerfilUso.Trabalho, PerfilUso.Estudos, PerfilUso.Dev, PerfilUso.Edicao },
            Valores = new[] { new ValorReg(@"HKCU\Software\Microsoft\Clipboard", "EnableClipboardHistory", 1) },
        },
        new Ajuste
        {
            Id = "prod-notif-dicas", Grupo = GProd, Nome = "Sem notificações de dicas e \"aproveite ainda mais o Windows\"", Tipo = TipoAjuste.Registro, Recomendado = true,
            Descricao = "Menos interrupções enquanto você trabalha ou estuda.",
            Perfis = new[] { PerfilUso.Trabalho, PerfilUso.Estudos, PerfilUso.Dev },
            Valores = new[]
            {
                new ValorReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
                new ValorReg(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\Windows.SystemToast.Suggested", "Enabled", 0),
            },
        },

        // ------------------------------------------------------------ Edição e imagem
        new Ajuste
        {
            Id = "edic-papel-parede", Grupo = GEdic, Nome = "Papel de parede sem perda de qualidade", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Edicao,
            Descricao = "O Windows deixa de comprimir as imagens que você põe como papel de parede.",
            Aviso = "Vale para o próximo papel de parede que você escolher.",
            Valores = new[] { new ValorReg(@"HKCU\Control Panel\Desktop", "JPEGImportQuality", 100) },
        },

        // ------------------------------------------------------------ Programação
        new Ajuste
        {
            Id = "dev-caminhos-longos", Grupo = GDev, Nome = "Permitir caminhos longos (mais de 260 caracteres)", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Dev,
            Descricao = "Evita erros com node_modules e repositórios com muitas pastas.",
            Valores = new[] { new ValorReg(@"HKLM\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1) },
        },
        new Ajuste
        {
            Id = "dev-modo", Grupo = GDev, Nome = "Modo de desenvolvedor", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Dev, Risco = RiscoAjuste.Medio,
            Descricao = "Permite criar links simbólicos sem ser administrador e instalar apps que você mesmo compilou.",
            Valores = new[] { new ValorReg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", 1) },
        },
        new Ajuste
        {
            Id = "dev-powershell", Grupo = GDev, Nome = "PowerShell roda seus próprios scripts", Tipo = TipoAjuste.Registro, Recomendado = true, Perfis = Dev,
            Descricao = "Política RemoteSigned só para você: scripts locais rodam; os baixados da internet precisam de assinatura.",
            Valores = new[] { new ValorReg(@"HKCU\Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell", "ExecutionPolicy", "RemoteSigned") },
        },

        // ------------------------------------------------------------ Programas
        Prog("prog-steam", "Steam", "A maior loja de jogos para PC.", "Valve.Steam", Jogos, true),
        Prog("prog-discord", "Discord", "Voz e chat para jogar com os amigos.", "Discord.Discord", Jogos, true),
        Prog("prog-epic", "Epic Games Launcher", "Loja da Epic, com jogos grátis toda semana.", "EpicGames.EpicGamesLauncher", Jogos, false),
        Prog("prog-obs", "OBS Studio", "Gravar a tela e fazer lives.", "OBSProject.OBSStudio", new[] { PerfilUso.Jogos, PerfilUso.Edicao }, false),
        Prog("prog-7zip", "7-Zip", "Abre e cria arquivos .zip, .rar e .7z.", "7zip.7zip", new[] { PerfilUso.Trabalho, PerfilUso.Estudos, PerfilUso.Dev, PerfilUso.Edicao }, true),
        Prog("prog-sumatra", "SumatraPDF", "Leitor de PDF leve e rápido.", "SumatraPDF.SumatraPDF", new[] { PerfilUso.Trabalho, PerfilUso.Estudos }, true),
        Prog("prog-obsidian", "Obsidian", "Anotações ligadas entre si, ótimo para estudar.", "Obsidian.Obsidian", new[] { PerfilUso.Estudos }, false),
        Prog("prog-powertoys", "PowerToys", "Utilitários da Microsoft: atalhos, janelas lado a lado, renomear em massa.", "Microsoft.PowerToys", new[] { PerfilUso.Trabalho, PerfilUso.Dev }, false),
        Prog("prog-krita", "Krita", "Pintura digital e ilustração, gratuito.", "KDE.Krita", Edicao, true),
        Prog("prog-inkscape", "Inkscape", "Ilustração vetorial (como o Illustrator), gratuito.", "Inkscape.Inkscape", Edicao, false),
        Prog("prog-audacity", "Audacity", "Edição de áudio.", "Audacity.Audacity", Edicao, false),
        Prog("prog-git", "Git", "Controle de versão.", "Git.Git", Dev, true),
        Prog("prog-vscode", "Visual Studio Code", "Editor de código.", "Microsoft.VisualStudioCode", Dev, true),
        Prog("prog-terminal", "Windows Terminal", "Terminal moderno com abas (já vem no Windows 11).", "Microsoft.WindowsTerminal", Dev, true),
        Prog("prog-pwsh", "PowerShell 7", "A versão nova do PowerShell, ao lado da antiga.", "Microsoft.PowerShell", Dev, false),
        Prog("prog-node", "Node.js LTS", "JavaScript fora do navegador.", "OpenJS.NodeJS.LTS", Dev, false),
        Prog("prog-python", "Python 3", "Linguagem Python.", "Python.Python.3.13", Dev, false),

        // ------------------------------------------------------------ Avançado (áreas sensíveis, sem desligar proteção)
        new Ajuste
        {
            Id = "av-wu-reiniciar", Grupo = GAvanc, Nome = "Windows Update: não reiniciar sozinho com você conectado", Tipo = TipoAjuste.Registro, Risco = RiscoAjuste.Medio,
            Descricao = "As atualizações continuam baixando e instalando; o Windows só espera você reiniciar.",
            Aviso = "Reinicie quando o Zelo avisar que há reinício pendente.",
            Valores = new[] { new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1) },
        },
        new Ajuste
        {
            Id = "av-wu-p2p", Grupo = GAvanc, Nome = "Windows Update: não enviar atualizações para PCs da internet", Tipo = TipoAjuste.Registro,
            Descricao = "Seu PC só compartilha atualizações com PCs da sua rede de casa, poupando sua internet.",
            Valores = new[] { new ValorReg(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1) },
        },
        new Ajuste
        {
            Id = "av-defender-cpu", Grupo = GAvanc, Nome = "Antivírus: verificação usando no máximo 30% do processador", Tipo = TipoAjuste.Script, Risco = RiscoAjuste.Medio,
            Descricao = "A verificação do Microsoft Defender fica mais leve (e um pouco mais demorada). A proteção em tempo real não muda.",
            ScriptLer = """
                $s = Get-MpComputerStatus
                if (-not $s.AntivirusEnabled) { 'INDISPONIVEL' }
                else { $v = (Get-MpPreference).ScanAvgCPULoadFactor; if ($v -le 30) { 'ALVO' } else { "$v" } }
                """,
            ScriptAplicar = "Set-MpPreference -ScanAvgCPULoadFactor 30",
            ScriptRestaurar = "Set-MpPreference -ScanAvgCPULoadFactor {{ANTERIOR}}",
        },
        new Ajuste
        {
            Id = "av-uac-sempre", Grupo = GAvanc, Nome = "Controle de Conta de Usuário: sempre avisar", Tipo = TipoAjuste.Registro,
            Descricao = "Mais seguro: o Windows pede confirmação até para mudanças nas configurações dele.",
            Valores = new[]
            {
                new ValorReg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "ConsentPromptBehaviorAdmin", 2),
                new ValorReg(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", "PromptOnSecureDesktop", 1),
            },
        },
        new Ajuste
        {
            Id = "av-telemetria-servico", Grupo = GAvanc, Nome = "Desligar o serviço de telemetria", Tipo = TipoAjuste.Servico, Risco = RiscoAjuste.Medio,
            Descricao = "\"Experiências do Usuário Conectado e Telemetria\" (DiagTrack). O Windows Update não depende dele.",
            Servicos = new[] { "DiagTrack" }, InicioServico = 4,
        },
        new Ajuste
        {
            Id = "av-spooler", Grupo = GAvanc, Nome = "Desligar o serviço de impressão (só se você não tem impressora)", Tipo = TipoAjuste.Servico, Risco = RiscoAjuste.Medio,
            Descricao = "Fecha uma porta usada em ataques conhecidos (PrintNightmare).",
            Aviso = "Nenhuma impressão funciona, nem \"Imprimir para PDF\", até desfazer.",
            Exceto = new[] { PerfilUso.Trabalho, PerfilUso.Estudos }, Servicos = new[] { "Spooler" }, InicioServico = 4,
        },
        new Ajuste
        {
            Id = "av-hibernacao", Grupo = GAvanc, Nome = "Desligar a hibernação", Tipo = TipoAjuste.Script, Risco = RiscoAjuste.Medio,
            Descricao = "Apaga o arquivo hiberfil.sys e libera vários GB no disco C:.",
            Aviso = "Desliga também a Inicialização Rápida; o PC pode demorar alguns segundos a mais para ligar.",
            ScriptLer = """
                $h = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -Name HibernateEnabled -ErrorAction SilentlyContinue).HibernateEnabled
                if ($h -eq 0) { 'ALVO' } else { 'on' }
                """,
            ScriptAplicar = "powercfg /hibernate off; if ($LASTEXITCODE -ne 0) { throw 'O Windows não deixou desligar a hibernação.' }",
            ScriptRestaurar = "powercfg /hibernate {{ANTERIOR}}",
        },
    };

    public static Ajuste? Por(string id) => Todos.FirstOrDefault(a => a.Id == id);
}

/// <summary>Aplica e desfaz os ajustes. Roda no serviço (SYSTEM), exceto a instalação de programas.</summary>
public static class Otimizador
{
    private const string NomeEstado = "otimizacao";
    private static readonly object TravaRegistro = new();

    public static AndamentoOtimizacao Andamento { get; private set; } = new();
    public static bool Rodando => Andamento.Rodando;

    /// <summary>Ajustes que valem só para uma conta (registro HKCU ou apps da Loja).</summary>
    public static bool PorUsuario(Ajuste a) =>
        a.Tipo == TipoAjuste.App || a.Valores.Any(v => v.Caminho.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase));

    public static string Chave(Ajuste a, string? sid) => PorUsuario(a) ? $"{a.Id}@{sid}" : a.Id;

    public static RegistroOtimizacao Carregar()
    {
        var r = Estado.Ler<RegistroOtimizacao>(NomeEstado) ?? new RegistroOtimizacao { Versao = 2 };
        if (r.Versao < 2)
        {
            // Versão 1 guardava tudo pelo id; os ajustes por conta passam a levar o SID na chave.
            var novos = new Dictionary<string, AplicacaoAjuste>();
            foreach (var (id, ap) in r.Aplicados)
            {
                var def = CatalogoOtimizacao.Por(id);
                ap.Id = id;
                ap.Concluido = true;
                if (def is null) continue;
                novos[Chave(def, ap.Sid)] = ap;
            }
            r.Aplicados = novos;
            r.Versao = 2;
        }
        return r;
    }

    private static void Salvar(RegistroOtimizacao r) { lock (TravaRegistro) Estado.Gravar(NomeEstado, r); }

    // ------------------------------------------------------------------ leitura

    public static async Task<List<EstadoAjuste>> ListarAsync(string? sid, CancellationToken ct)
    {
        var reg = Carregar();
        var apps = await AppsInstaladosAsync(sid, ct);
        var lista = new List<EstadoAjuste>();
        foreach (var a in CatalogoOtimizacao.Todos.Where(a => a.Tipo != TipoAjuste.Programa))
        {
            SituacaoAjuste s;
            try { s = await LerAsync(a, sid, apps, ct); }
            catch { s = SituacaoAjuste.Indisponivel; }
            reg.Aplicados.TryGetValue(Chave(a, sid), out var ap);
            if (ap is { Concluido: false }) ap = null;
            // App removido pelo Zelo: não está mais instalado, mas continua na lista para poder reinstalar.
            if (a.Tipo == TipoAjuste.App && s == SituacaoAjuste.Indisponivel && ap is not null) s = SituacaoAjuste.Ativo;
            lista.Add(new EstadoAjuste { Id = a.Id, Situacao = s, PeloZelo = ap is not null, Quando = ap?.Quando });
        }
        return lista;
    }

    private static async Task<SituacaoAjuste> LerAsync(Ajuste a, string? sid, HashSet<string>? apps, CancellationToken ct)
    {
        switch (a.Tipo)
        {
            case TipoAjuste.Registro:
                if (PorUsuario(a) && !SidValido(sid)) return SituacaoAjuste.Indisponivel;
                return a.Valores.All(v => Igual(LerValor(v, sid), v.Valor)) ? SituacaoAjuste.Ativo : SituacaoAjuste.Padrao;

            case TipoAjuste.Servico:
                var inicios = a.Servicos.Select(InicioDoServico).Where(i => i is not null).Select(i => i!.Value).ToList();
                if (inicios.Count == 0) return SituacaoAjuste.Indisponivel;
                return inicios.All(i => a.InicioServico == 3 ? i >= 3 : i == a.InicioServico) ? SituacaoAjuste.Ativo : SituacaoAjuste.Padrao;

            case TipoAjuste.App:
                if (apps is null) return SituacaoAjuste.Indisponivel;
                return apps.Any(n => a.Pacotes.Any(p => Curinga(n, p))) ? SituacaoAjuste.Padrao : SituacaoAjuste.Indisponivel;

            case TipoAjuste.Script:
                var r = await Executor.PowerShellAsync(a.ScriptLer!, ct);
                var saida = UltimaLinha(r.Saida);
                if (r.Codigo != 0 || saida.Length == 0 || saida == "INDISPONIVEL" || saida.StartsWith("ERRO")) return SituacaoAjuste.Indisponivel;
                return saida == "ALVO" ? SituacaoAjuste.Ativo : SituacaoAjuste.Padrao;
        }
        return SituacaoAjuste.Indisponivel;
    }

    /// <summary>Apps da Loja instalados para a conta de quem usa o painel (não para as outras contas).</summary>
    private static async Task<HashSet<string>?> AppsInstaladosAsync(string? sid, CancellationToken ct)
    {
        if (!SidValido(sid)) return null;
        try
        {
            var json = await Executor.PowerShellJsonAsync(
                $"ConvertTo-Json -Compress -InputObject @(Get-AppxPackage -User '{sid}' | Select-Object -ExpandProperty Name | Sort-Object -Unique)", ct);
            return new HashSet<string>(json.Itens().Select(e => e.GetString() ?? ""), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Registro.Escrever("Otimizar: não foi possível listar os apps da Loja: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ aplicar e desfazer

    public static string? Iniciar(List<string> ids, List<PerfilUso> perfis, string? sid, bool desfazer, bool semPonto, CancellationToken ct)
    {
        var ajustes = ids.Distinct().Select(CatalogoOtimizacao.Por).Where(a => a is not null && a.Tipo != TipoAjuste.Programa).Select(a => a!).ToList();
        if (ajustes.Any(PorUsuario) && !SidValido(sid)) return "Não foi possível identificar a sua conta do Windows.";
        if (!Coordenador.Iniciar("otimizacao")) return Coordenador.Explicar();
        Andamento = new AndamentoOtimizacao { Rodando = true, Desfazendo = desfazer, Total = ajustes.Count, Etapa = "Começando" };
        _ = Task.Run(async () =>
        {
            try
            {
                if (desfazer) await DesfazerAsync(ajustes, sid, ct);
                else await AplicarAsync(ajustes, perfis, sid, semPonto, ct);
            }
            catch (Exception ex)
            {
                Andamento.Falhas.Add(ex.Message);
                Registro.Escrever("Otimizar: erro inesperado: " + ex);
            }
            finally
            {
                Andamento.Rodando = false;
                Andamento.Fim = DateTime.Now;
                Andamento.Etapa = "";
                Coordenador.Encerrar("otimizacao");
            }
        });
        return null;
    }

    private static async Task AplicarAsync(List<Ajuste> ajustes, List<PerfilUso> perfis, string? sid, bool semPonto, CancellationToken ct)
    {
        var reg = Carregar();
        reg.Perfis = perfis;
        Salvar(reg);

        if (ajustes.Count > 0)
        {
            Andamento.Etapa = "Criando um ponto de restauração";
            Andamento.PontoCriado = await CriarPontoAsync(ct);
            if (Andamento.PontoCriado == false && !semPonto)
            {
                Andamento.PontoFalhou = true;
                Andamento.Resultado = "Não foi possível criar o ponto de restauração. Nada foi alterado.";
                Registro.Escrever("Otimizar: cancelado porque o ponto de restauração falhou.");
                return;
            }
        }

        int ok = 0;
        var apps = await AppsInstaladosAsync(sid, ct);
        foreach (var a in ajustes)
        {
            ct.ThrowIfCancellationRequested();
            Andamento.Etapa = a.Nome;
            var chave = Chave(a, sid);
            try
            {
                var situacao = await LerAsync(a, sid, apps, ct);
                if (situacao == SituacaoAjuste.Ativo) { ok++; continue; }
                if (situacao == SituacaoAjuste.Indisponivel) throw new InvalidOperationException("não se aplica a este PC.");

                // Diário: primeiro anota como estava, depois muda, confere e só então marca como concluído.
                bool novo = !reg.Aplicados.ContainsKey(chave);
                if (novo)
                {
                    reg.Aplicados[chave] = new AplicacaoAjuste { Id = a.Id, Quando = DateTime.Now, Sid = sid, Anterior = LerAnterior(a, sid) };
                    if (a.Tipo == TipoAjuste.Script)
                        reg.Aplicados[chave].Anterior["script"] = UltimaLinha((await Executor.PowerShellAsync(a.ScriptLer!, ct)).Saida);
                    Salvar(reg);
                }
                try
                {
                    await AplicarUmAsync(a, sid, ct);
                    var depois = await LerAsync(a, sid, a.Tipo == TipoAjuste.App ? await AppsInstaladosAsync(sid, ct) : apps, ct);
                    bool conferido = a.Tipo == TipoAjuste.App ? depois == SituacaoAjuste.Indisponivel : depois == SituacaoAjuste.Ativo;
                    if (!conferido) throw new InvalidOperationException("o Windows não aceitou a mudança.");
                }
                catch
                {
                    // Volta este item ao que era antes de seguir para o próximo.
                    if (novo)
                    {
                        try { await DesfazerUmAsync(a, reg.Aplicados[chave], ct); } catch { }
                        reg.Aplicados.Remove(chave);
                        Salvar(reg);
                    }
                    throw;
                }
                reg.Aplicados[chave].Concluido = true;
                Salvar(reg);
                ok++;
            }
            catch (Exception ex)
            {
                Andamento.Falhas.Add($"{a.Nome}: {Limpar(ex.Message)}");
            }
            Andamento.Feitos++;
        }
        Andamento.Resultado = ajustes.Count == 0 ? "Nada a mudar no Windows."
            : $"{ok} de {ajustes.Count} ajuste(s) aplicado(s)."
              + (Andamento.PontoCriado == false ? " Sem ponto de restauração (você escolheu continuar); cada ajuste ainda pode ser desfeito pelo Zelo." : "");
        Registro.Escrever($"Otimizar: {Andamento.Resultado} Perfis: {string.Join(", ", perfis)}. Falhas: {Andamento.Falhas.Count}.");
    }

    private static async Task DesfazerAsync(List<Ajuste> ajustes, string? sid, CancellationToken ct)
    {
        var reg = Carregar();
        int ok = 0;
        foreach (var a in ajustes)
        {
            Andamento.Etapa = a.Nome;
            var chave = Chave(a, sid);
            if (!reg.Aplicados.TryGetValue(chave, out var ap)) { Andamento.Feitos++; continue; }
            try
            {
                await DesfazerUmAsync(a, ap, ct);
                reg.Aplicados.Remove(chave);
                Salvar(reg);
                ok++;
            }
            catch (Exception ex)
            {
                Andamento.Falhas.Add($"{a.Nome}: {Limpar(ex.Message)}");
            }
            Andamento.Feitos++;
        }
        Andamento.Resultado = $"{ok} ajuste(s) desfeito(s).";
        Registro.Escrever($"Otimizar: {Andamento.Resultado} Falhas: {Andamento.Falhas.Count}.");
    }

    /// <summary>
    /// Ao iniciar o serviço: um ajuste que ficou "aplicando" (o PC desligou ou o serviço caiu no meio)
    /// volta ao que era antes, para nunca ficar uma mudança sem registro de como desfazer.
    /// </summary>
    public static async Task RecuperarAsync(CancellationToken ct)
    {
        var reg = Carregar();
        var pendentes = reg.Aplicados.Where(kv => !kv.Value.Concluido).ToList();
        foreach (var (chave, ap) in pendentes)
        {
            var a = CatalogoOtimizacao.Por(ap.Id);
            try { if (a is not null) await DesfazerUmAsync(a, ap, ct); }
            catch (Exception ex) { Registro.Escrever($"Otimizar: não foi possível desfazer {ap.Id} interrompido: {ex.Message}"); }
            reg.Aplicados.Remove(chave);
            Registro.Escrever($"Otimizar: ajuste {ap.Id} tinha ficado pela metade e foi desfeito.");
        }
        if (pendentes.Count > 0) Salvar(reg);
    }

    private static Dictionary<string, string?> LerAnterior(Ajuste a, string? sid)
    {
        var anterior = new Dictionary<string, string?>();
        if (a.Tipo == TipoAjuste.Registro)
            foreach (var v in a.Valores) anterior[$"{v.Caminho}|{v.Nome}"] = LerBruto(v.Caminho, v.Nome, sid);
        else if (a.Tipo == TipoAjuste.Servico)
            foreach (var s in a.Servicos)
                if (InicioDoServico(s) is { } i) anterior[s] = i.ToString();
        return anterior;
    }

    private static async Task AplicarUmAsync(Ajuste a, string? sid, CancellationToken ct)
    {
        switch (a.Tipo)
        {
            case TipoAjuste.Registro:
                foreach (var v in a.Valores) Escrever(v.Caminho, v.Nome, v.Valor, sid);
                break;

            case TipoAjuste.Servico:
                foreach (var s in a.Servicos)
                {
                    if (InicioDoServico(s) is null) continue;
                    Escrever($@"HKLM\SYSTEM\CurrentControlSet\Services\{s}", "Start", a.InicioServico, null);
                    if (a.InicioServico == 4)
                        await Executor.PowerShellAsync($"Stop-Service -Name '{s}' -Force -ErrorAction SilentlyContinue", ct);
                }
                break;

            case TipoAjuste.App:
                // Só da conta de quem pediu. As outras contas do PC e as contas futuras não são afetadas.
                var lista = string.Join(",", a.Pacotes.Select(p => $"'{p}'"));
                var r = await Executor.PowerShellAsync($$"""
                    foreach ($p in @({{lista}})) {
                      Get-AppxPackage -User '{{sid}}' -Name $p | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName -User '{{sid}}' }
                    }
                    'OK'
                    """, ct);
                if (r.Codigo != 0) throw new InvalidOperationException(string.Join(" ", r.Linhas(2)));
                break;

            case TipoAjuste.Script:
                var ap = await Executor.PowerShellAsync(a.ScriptAplicar!, ct);
                if (ap.Codigo != 0) throw new InvalidOperationException(string.Join(" ", ap.Linhas(2)));
                break;
        }
    }

    private static async Task DesfazerUmAsync(Ajuste a, AplicacaoAjuste ap, CancellationToken ct)
    {
        switch (a.Tipo)
        {
            case TipoAjuste.Registro:
                foreach (var (chave, valor) in ap.Anterior)
                {
                    var partes = chave.Split('|', 2);
                    if (partes.Length != 2) continue;
                    Restaurar(partes[0], partes[1], valor, ap.Sid);
                }
                break;

            case TipoAjuste.Servico:
                foreach (var (s, valor) in ap.Anterior)
                {
                    if (!int.TryParse(valor, out var inicio)) continue;
                    Escrever($@"HKLM\SYSTEM\CurrentControlSet\Services\{s}", "Start", inicio, null);
                    if (inicio == 2) await Executor.PowerShellAsync($"Start-Service -Name '{s}' -ErrorAction SilentlyContinue", ct);
                }
                break;

            case TipoAjuste.App:
                // Não dá para reinstalar daqui: a Loja precisa da conta da pessoa. O painel abre a página.
                break;

            case TipoAjuste.Script:
                var anterior = ap.Anterior.GetValueOrDefault("script") ?? "";
                if (!Regex.IsMatch(anterior, "^[A-Za-z0-9-]{1,64}$") || anterior == "ALVO") break;
                var r = await Executor.PowerShellAsync(a.ScriptRestaurar!.Replace("{{ANTERIOR}}", anterior), ct);
                if (r.Codigo != 0) throw new InvalidOperationException(string.Join(" ", r.Linhas(2)));
                break;
        }
    }

    private static async Task<bool> CriarPontoAsync(CancellationToken ct)
    {
        // O Windows só cria um ponto por dia; liberamos só para este, e voltamos como estava.
        var r = await Executor.PowerShellAsync("""
            $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
            $antes = (Get-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
            Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord
            try {
              try { Checkpoint-Computer -Description 'Zelo - antes de otimizar' -RestorePointType MODIFY_SETTINGS }
              catch {
                Enable-ComputerRestore -Drive "$env:SystemDrive\"
                Checkpoint-Computer -Description 'Zelo - antes de otimizar' -RestorePointType MODIFY_SETTINGS
              }
            } finally {
              if ($null -eq $antes) { Remove-ItemProperty $k -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
              else { Set-ItemProperty $k -Name SystemRestorePointCreationFrequency -Value $antes -Type DWord }
            }
            'OK'
            """, ct);
        if (r.Codigo != 0) Registro.Escrever("Otimizar: ponto de restauração não criado: " + string.Join(" ", r.Linhas(2)));
        return r.Codigo == 0;
    }

    // ------------------------------------------------------------------ registro do Windows

    public static bool SidValido(string? sid) => sid is not null && Regex.IsMatch(sid, @"^S-1-5-21-[\d-]+$");

    private static (RegistryKey raiz, string sub)? Separar(string caminho, string? sid)
    {
        if (caminho.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase))
            return (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), caminho[5..]);
        if (caminho.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            // O serviço é SYSTEM: o "HKCU" de quem está no painel fica em HKEY_USERS\<SID>.
            if (!SidValido(sid)) return null;
            return (RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64), sid + "\\" + caminho[5..]);
        }
        throw new ArgumentException("Caminho de registro inválido: " + caminho);
    }

    private static object? LerValor(ValorReg v, string? sid)
    {
        var s = Separar(v.Caminho, sid);
        if (s is null) return null;
        using var raiz = s.Value.raiz;
        using var k = raiz.OpenSubKey(s.Value.sub, false);
        return k?.GetValue(v.Nome, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    private static bool Igual(object? atual, object alvo) => alvo switch
    {
        int i => atual is int a && a == i,
        string t => atual is string a && a == t,
        _ => false,
    };

    private static string? LerBruto(string caminho, string nome, string? sid)
    {
        var s = Separar(caminho, sid) ?? throw new InvalidOperationException("não foi possível identificar o seu usuário.");
        using var raiz = s.raiz;
        using var k = raiz.OpenSubKey(s.sub, false);
        var o = k?.GetValue(nome, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (o is null) return null;
        return k!.GetValueKind(nome) switch
        {
            RegistryValueKind.DWord => "dword:" + (int)o,
            RegistryValueKind.String => "sz:" + o,
            RegistryValueKind.ExpandString => "ex:" + o,
            _ => "?:",
        };
    }

    private static void Escrever(string caminho, string nome, object valor, string? sid)
    {
        var s = Separar(caminho, sid) ?? throw new InvalidOperationException("não foi possível identificar o seu usuário.");
        using var raiz = s.raiz;
        using var k = raiz.CreateSubKey(s.sub, true) ?? throw new InvalidOperationException("sem acesso ao registro.");
        if (valor is int i) k.SetValue(nome, i, RegistryValueKind.DWord);
        else k.SetValue(nome, valor.ToString() ?? "", RegistryValueKind.String);
    }

    private static void Restaurar(string caminho, string nome, string? anterior, string? sid)
    {
        var s = Separar(caminho, sid) ?? throw new InvalidOperationException("não foi possível identificar o usuário que aplicou.");
        using var raiz = s.raiz;
        if (anterior is null)
        {
            using var k = raiz.OpenSubKey(s.sub, true);
            k?.DeleteValue(nome, false);
            return;
        }
        if (anterior.StartsWith("?:")) return;
        using var w = raiz.CreateSubKey(s.sub, true) ?? throw new InvalidOperationException("sem acesso ao registro.");
        if (anterior.StartsWith("dword:")) w.SetValue(nome, int.Parse(anterior[6..]), RegistryValueKind.DWord);
        else if (anterior.StartsWith("ex:")) w.SetValue(nome, anterior[3..], RegistryValueKind.ExpandString);
        else if (anterior.StartsWith("sz:")) w.SetValue(nome, anterior[3..], RegistryValueKind.String);
    }

    private static int? InicioDoServico(string servico)
    {
        using var raiz = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = raiz.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{servico}", false);
        return k?.GetValue("Start") is int i ? i : null;
    }

    // ------------------------------------------------------------------ utilidades

    private static bool Curinga(string texto, string padrao) =>
        Regex.IsMatch(texto, "^" + Regex.Escape(padrao).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    private static string UltimaLinha(string s) =>
        s.Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    private static string Limpar(string s) => s.StartsWith("ERRO: ") ? s[6..] : s;
}
