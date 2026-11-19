namespace Zelo.Core;

public sealed class Configuracao
{
    /// <summary>Domestico, Trabalho ou Jogos.</summary>
    public string Perfil { get; set; } = "Domestico";

    // Backup
    public List<string> PastasBackup { get; set; } = new();
    public string DestinoBackup { get; set; } = "";

    // Limpeza
    public int DiasTemporarios { get; set; } = 7;
    public int DiasLixeira { get; set; } = 30;
    public bool EsvaziarLixeira { get; set; } = false;
    public bool LimparCacheNavegadores { get; set; } = false;
    /// <summary>Opcional: apaga da pasta Downloads o que estiver lá há mais de DiasDownloads.</summary>
    public bool LimparDownloads { get; set; } = false;
    public int DiasDownloads { get; set; } = 30;
    /// <summary>Por quantos dias o backup guarda as versões anteriores dos arquivos alterados (0 = não guarda).</summary>
    public int DiasVersoesBackup { get; set; } = 30;

    // Disco (% de uso)
    public int LimiteAvisoDisco { get; set; } = 85;
    public int LimiteCriticoDisco { get; set; } = 95;

    // Atualizações e segurança
    public bool InstalarWindowsUpdate { get; set; } = true;
    public bool AtualizarProgramasWinget { get; set; } = true;
    /// <summary>Instala drivers do Windows Update (exceto vídeo). Desligado por padrão.</summary>
    public bool InstalarDriversWU { get; set; } = false;
    public bool CriarPontoRestauracao { get; set; } = true;
    /// <summary>Instala sozinho as versões novas publicadas na página Releases do GitHub.</summary>
    public bool AtualizacaoAutomatica { get; set; } = true;
    public bool VarreduraCompletaDefender { get; set; } = true;

    // Quando rodar
    /// <summary>Hora (0-23) a partir da qual pode rodar.</summary>
    public int HoraInicio { get; set; } = 0;
    /// <summary>Hora (1-24) até a qual pode rodar. Se menor que HoraInicio, a janela atravessa a meia-noite.</summary>
    public int HoraFim { get; set; } = 24;
    public bool SomenteNaTomada { get; set; } = true;
    /// <summary>Esperar o PC ficar parado antes de começar (desligado: começa assim que vencer, em prioridade baixa).</summary>
    public bool ExigirOcioso { get; set; } = true;
    /// <summary>Perfil Trabalho: horário do expediente (0-23). O Zelo trabalha FORA dele. Pode atravessar a meia-noite (ex.: 22 às 6).</summary>
    public int ExpedienteInicio { get; set; } = 9;
    public int ExpedienteFim { get; set; } = 18;
    /// <summary>Dias com expediente (0 = domingo … 6 = sábado). Num turno que atravessa a meia-noite, vale o dia em que ele começa.</summary>
    public List<int> DiasExpediente { get; set; } = new() { 1, 2, 3, 4, 5 };
    /// <summary>Uso de CPU (%) abaixo do qual o PC é considerado ocioso.</summary>
    public int LimiteCpuOcioso { get; set; } = 30;

    public List<string> TarefasDesativadas { get; set; } = new();

    public void Validar()
    {
        PastasBackup = PastasBackup
            .Select(p => p.Trim().Trim('"'))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        DestinoBackup = (DestinoBackup ?? "").Trim().Trim('"');
        DiasTemporarios = Math.Clamp(DiasTemporarios, 1, 365);
        DiasLixeira = Math.Clamp(DiasLixeira, 1, 3650);
        DiasDownloads = Math.Clamp(DiasDownloads, 3, 3650);
        DiasVersoesBackup = Math.Clamp(DiasVersoesBackup, 0, 3650);
        LimiteAvisoDisco = Math.Clamp(LimiteAvisoDisco, 50, 99);
        LimiteCriticoDisco = Math.Clamp(LimiteCriticoDisco, LimiteAvisoDisco, 100);
        HoraInicio = Math.Clamp(HoraInicio, 0, 23);
        HoraFim = Math.Clamp(HoraFim, 1, 24);
        LimiteCpuOcioso = Math.Clamp(LimiteCpuOcioso, 5, 100);
        TarefasDesativadas ??= new();
        ExpedienteInicio = Math.Clamp(ExpedienteInicio, 0, 23);
        ExpedienteFim = Math.Clamp(ExpedienteFim, 0, 23);
        if (ExpedienteInicio == ExpedienteFim) { ExpedienteInicio = 9; ExpedienteFim = 18; }
        DiasExpediente = (DiasExpediente ?? new()).Where(d => d is >= 0 and <= 6).Distinct().OrderBy(d => d).ToList();
        if (Perfil is not ("Domestico" or "Trabalho" or "Jogos")) Perfil = "Domestico";
    }

    /// <summary>Ajusta as preferências que dependem do perfil escolhido.</summary>
    public void AplicarPerfil(string perfil)
    {
        Perfil = perfil;
        LimiteCpuOcioso = perfil == "Jogos" ? 15 : 30;
        if (perfil == "Jogos") LimparCacheNavegadores = false;
    }

    /// <summary>Madrugada usada pelo perfil Jogos.</summary>
    public const int JogosInicio = 2, JogosFim = 8;

    /// <summary>True se a hora cai no expediente configurado (perfil Trabalho).</summary>
    public bool EmExpediente(DateTime t)
    {
        int h = t.Hour, ini = ExpedienteInicio, fim = ExpedienteFim;
        bool Trabalha(DateTime dia) => DiasExpediente.Contains((int)dia.DayOfWeek);
        if (ini < fim) return Trabalha(t) && h >= ini && h < fim;
        // Turno que atravessa a meia-noite: a parte da madrugada pertence ao dia anterior.
        return (h >= ini && Trabalha(t)) || (h < fim && Trabalha(t.AddDays(-1)));
    }

    public bool DentroDaJanela(DateTime agora) => Perfil switch
    {
        "Trabalho" => !EmExpediente(agora),
        "Jogos" => agora.Hour >= JogosInicio && agora.Hour < JogosFim,
        _ => true,
    };

    public bool BackupConfigurado => PastasBackup.Count > 0 && !string.IsNullOrWhiteSpace(DestinoBackup);
}

public static class ArmazemConfig
{
    private static readonly object Trava = new();

    public static Configuracao Carregar()
    {
        lock (Trava)
        {
            try
            {
                if (File.Exists(Caminhos.Config))
                {
                    var c = Json.Desserializar<Configuracao>(File.ReadAllText(Caminhos.Config));
                    if (c is not null) { c.Validar(); return c; }
                }
            }
            catch (Exception ex)
            {
                Registro.Escrever($"Configuração inválida, usando padrões: {ex.Message}");
            }
            var padrao = new Configuracao();
            padrao.Validar();
            return padrao;
        }
    }

    public static void Salvar(Configuracao c)
    {
        c.Validar();
        lock (Trava)
        {
            Caminhos.Garantir();
            var tmp = Caminhos.Config + ".tmp";
            File.WriteAllText(tmp, Json.Serializar(c, indentado: true));
            File.Move(tmp, Caminhos.Config, overwrite: true);
        }
    }
}
