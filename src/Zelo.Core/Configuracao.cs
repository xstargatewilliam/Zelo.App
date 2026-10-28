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
    public bool LimparCacheNavegadores { get; set; } = false;
    /// <summary>Opcional: apaga da pasta Downloads o que estiver lá há mais de DiasDownloads.</summary>
    public bool LimparDownloads { get; set; } = false;
    public int DiasDownloads { get; set; } = 30;

    // Disco (% de uso)
    public int LimiteAvisoDisco { get; set; } = 85;
    public int LimiteCriticoDisco { get; set; } = 95;

    // Atualizações e segurança
    public bool InstalarWindowsUpdate { get; set; } = true;
    public bool AtualizarProgramasWinget { get; set; } = true;
    /// <summary>Instala drivers do Windows Update (exceto vídeo). Desligado por padrão.</summary>
    public bool InstalarDriversWU { get; set; } = false;
    public bool CriarPontoRestauracao { get; set; } = true;
    public bool VarreduraCompletaDefender { get; set; } = true;

    // Quando rodar
    /// <summary>Hora (0-23) a partir da qual pode rodar.</summary>
    public int HoraInicio { get; set; } = 0;
    /// <summary>Hora (1-24) até a qual pode rodar. Se menor que HoraInicio, a janela atravessa a meia-noite.</summary>
    public int HoraFim { get; set; } = 24;
    public bool SomenteNaTomada { get; set; } = true;
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
        LimiteAvisoDisco = Math.Clamp(LimiteAvisoDisco, 50, 99);
        LimiteCriticoDisco = Math.Clamp(LimiteCriticoDisco, LimiteAvisoDisco, 100);
        HoraInicio = Math.Clamp(HoraInicio, 0, 23);
        HoraFim = Math.Clamp(HoraFim, 1, 24);
        LimiteCpuOcioso = Math.Clamp(LimiteCpuOcioso, 5, 100);
        TarefasDesativadas ??= new();
        if (Perfil is not ("Domestico" or "Trabalho" or "Jogos")) Perfil = "Domestico";
    }

    /// <summary>Ajusta horários e preferências conforme o perfil escolhido.</summary>
    public void AplicarPerfil(string perfil)
    {
        Perfil = perfil;
        switch (perfil)
        {
            case "Trabalho":
                // Fora do expediente: das 19h às 7h.
                HoraInicio = 19; HoraFim = 7; LimiteCpuOcioso = 30;
                break;
            case "Jogos":
                // Madrugada, e só com o PC realmente parado.
                HoraInicio = 2; HoraFim = 8; LimiteCpuOcioso = 15;
                LimparCacheNavegadores = false;
                break;
            default:
                HoraInicio = 0; HoraFim = 24; LimiteCpuOcioso = 30;
                break;
        }
    }

    public bool DentroDaJanela(DateTime agora)
    {
        int h = agora.Hour;
        if (HoraInicio == 0 && HoraFim == 24) return true;
        return HoraInicio < HoraFim
            ? h >= HoraInicio && h < HoraFim
            : h >= HoraInicio || h < HoraFim;
    }

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
