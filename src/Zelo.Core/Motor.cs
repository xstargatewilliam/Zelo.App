using Zelo.Core.Tarefas;

namespace Zelo.Core;

public static class Catalogo
{
    /// <summary>Todas as tarefas, na ordem em que rodam dentro de cada rotina.</summary>
    public static IReadOnlyList<Tarefa> Todas { get; } = new Tarefa[]
    {
        // Diária
        new DefenderRapida(),
        new SaudeDisco(),
        new EspacoDisco(),
        new EventosCriticos(),
        new ReinicioPendente(),
        new LimpezaTemporarios(),
        new LimpezaDownloads(),
        new BackupIncremental(),
        // Semanal (o ponto de restauração vem antes das atualizações)
        new PontoRestauracao(),
        new WindowsUpdate(),
        new DriversWindowsUpdate(),
        new DriverVideo(),
        new AtualizacaoProgramas(),
        new OtimizarUnidades(),
        new LixeiraECache(),
        new ProgramasInicializacao(),
        // Mensal
        new IntegridadeSistema(),
        new LimpezaComponentes(),
        new VerificarVolumes(),
        new AuditoriaSeguranca(),
        new DefenderCompleta(),
        new VerificarBackup(),
        new SaudeBateria(),
        new MaioresArquivos(),
        new DriversAntigos(),
    };

    public static IEnumerable<Tarefa> Da(Rotina r) => Todas.Where(t => t.Rotinas.Contains(r));
}

public static class Historico
{
    private const int MaxExecucoes = 600;
    private static readonly object Trava = new();

    public static void Adicionar(ExecucaoRotina e)
    {
        lock (Trava)
        {
            Caminhos.Garantir();
            var linhas = File.Exists(Caminhos.Historico) ? File.ReadAllLines(Caminhos.Historico).ToList() : new List<string>();
            linhas.Add(Json.Serializar(e));
            if (linhas.Count > MaxExecucoes) linhas = linhas.Skip(linhas.Count - MaxExecucoes).ToList();
            var tmp = Caminhos.Historico + ".tmp";
            File.WriteAllLines(tmp, linhas);
            File.Move(tmp, Caminhos.Historico, overwrite: true);
        }
    }

    // O painel consulta o estado a cada poucos segundos: guardamos o arquivo já lido em memória.
    private static List<ExecucaoRotina>? _cache;
    private static DateTime _cacheVersao;

    /// <summary>Execuções em ordem cronológica (mais antiga primeiro).</summary>
    public static List<ExecucaoRotina> Carregar(int max = MaxExecucoes)
    {
        lock (Trava)
        {
            if (!File.Exists(Caminhos.Historico)) return new();
            var versao = File.GetLastWriteTimeUtc(Caminhos.Historico);
            if (_cache is null || versao != _cacheVersao)
            {
                var lido = new List<ExecucaoRotina>();
                foreach (var l in File.ReadLines(Caminhos.Historico))
                {
                    try { if (Json.Desserializar<ExecucaoRotina>(l) is { } e) lido.Add(e); }
                    catch { /* linha corrompida é ignorada */ }
                }
                _cache = lido;
                _cacheVersao = versao;
            }
            return _cache.Count > max ? _cache.Skip(_cache.Count - max).ToList() : _cache.ToList();
        }
    }
}

public sealed class PainelSaude
{
    /// <summary>0 a 100, ou -1 quando ainda não há dados.</summary>
    public int Geral { get; set; } = -1;
    public Dictionary<string, int> PorCategoria { get; set; } = new();
    public List<string> Alertas { get; set; } = new();
}

public static class Saude
{
    private static readonly Dictionary<Categoria, int> Pesos = new()
    {
        [Categoria.Disco] = 25,
        [Categoria.Seguranca] = 25,
        [Categoria.Backup] = 20,
        [Categoria.Atualizacoes] = 15,
        [Categoria.Sistema] = 15,
    };

    private static int Pontos(StatusTarefa s) => s switch
    {
        StatusTarefa.Ok => 100,
        StatusTarefa.Aviso => 60,
        StatusTarefa.Erro => 50,
        StatusTarefa.Critico => 15,
        _ => 100,
    };

    /// <summary>Por quanto tempo o resultado de cada rotina continua "valendo".</summary>
    public static TimeSpan Validade(Rotina r) => r switch
    {
        Rotina.Diaria => TimeSpan.FromDays(3),
        Rotina.Semanal => TimeSpan.FromDays(14),
        _ => TimeSpan.FromDays(45),
    };

    /// <param name="desativadas">Tarefas desligadas nas configurações: somem do score e dos alertas na hora.</param>
    public static PainelSaude Calcular(IEnumerable<ExecucaoRotina> historico, IEnumerable<string>? desativadas = null)
    {
        var agora = DateTime.Now;
        // Último resultado real (não simulado) de cada tarefa. Se o mais recente foi "Ignorada"
        // (tarefa desativada ou sem aplicação), o alerta antigo dela deixa de valer.
        var ultimos = new Dictionary<string, (ResultadoTarefa res, Rotina rotina)>();
        foreach (var e in historico.Where(h => !h.Simulacao).OrderBy(h => h.Fim))
            foreach (var r in e.Resultados.Where(r => r.TarefaId != "cancelada"))
            {
                if (r.Status == StatusTarefa.Ignorada) ultimos.Remove(r.TarefaId);
                else ultimos[r.TarefaId] = (r, e.Rotina);
            }
        if (desativadas is not null)
            foreach (var id in desativadas) ultimos.Remove(id);

        var painel = new PainelSaude();
        if (ultimos.Count == 0) return painel;

        var porCategoria = new Dictionary<Categoria, List<int>>();
        bool algumCritico = false;
        foreach (var (res, rotina) in ultimos.Values.OrderByDescending(v => (int)v.res.Status))
        {
            var status = res.Status;
            bool vencido = agora - res.Fim > Validade(rotina);
            int pts = Pontos(status);
            if (vencido) pts = Math.Min(pts, 70);
            if (status == StatusTarefa.Critico && !vencido) algumCritico = true;

            if (!porCategoria.TryGetValue(res.Categoria, out var l)) porCategoria[res.Categoria] = l = new();
            l.Add(pts);

            if (status is StatusTarefa.Critico or StatusTarefa.Aviso or StatusTarefa.Erro)
                painel.Alertas.Add($"{status.Nome()} · {res.Nome}: {res.Resumo}");
            else if (vencido)
                painel.Alertas.Add($"Desatualizado · {res.Nome}: última verificação em {res.Fim:dd/MM}.");
        }

        double soma = 0, pesos = 0;
        foreach (var (cat, lista) in porCategoria)
        {
            // Um item crítico derruba a categoria inteira: média, mas limitada pelo pior item + 30.
            int valor = (int)Math.Round(Math.Min(lista.Average(), lista.Min() + 30));
            painel.PorCategoria[cat.ToString()] = valor;
            soma += valor * Pesos[cat];
            pesos += Pesos[cat];
        }

        int geral = (int)Math.Round(soma / pesos);
        if (algumCritico) geral = Math.Min(geral, 49);
        painel.Geral = geral;
        return painel;
    }
}

/// <summary>Decide quando cada rotina está "vencida".</summary>
public static class Agenda
{
    public static TimeSpan Intervalo(Rotina r) => r switch
    {
        // Um pouco menos que o período cheio, para o horário não "escorregar" a cada dia.
        Rotina.Diaria => TimeSpan.FromHours(20),
        Rotina.Semanal => TimeSpan.FromDays(6.5),
        _ => TimeSpan.FromDays(29),
    };

    /// <summary>Na primeira instalação, espalha as rotinas pesadas para não rodar tudo no mesmo dia.</summary>
    private static TimeSpan AtrasoInicial(Rotina r) => r switch
    {
        Rotina.Diaria => TimeSpan.Zero,
        Rotina.Semanal => TimeSpan.FromDays(1),
        _ => TimeSpan.FromDays(3),
    };

    public static DateTime Instalacao
    {
        get
        {
            var arq = Path.Combine(Caminhos.Estado, "instalacao.txt");
            try
            {
                if (File.Exists(arq) && DateTime.TryParse(File.ReadAllText(arq), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var d))
                    return d;
                Caminhos.Garantir();
                var agora = DateTime.Now;
                File.WriteAllText(arq, agora.ToString("o"));
                return agora;
            }
            catch { return DateTime.Now; }
        }
    }

    public static Dictionary<Rotina, DateTime?> UltimasExecucoes(IEnumerable<ExecucaoRotina> historico)
    {
        var d = Enum.GetValues<Rotina>().ToDictionary(r => r, _ => (DateTime?)null);
        foreach (var e in historico.Where(h => !h.Simulacao))
            if (d[e.Rotina] is null || e.Fim > d[e.Rotina]) d[e.Rotina] = e.Fim;
        return d;
    }

    public static DateTime Proxima(Rotina r, DateTime? ultima) =>
        ultima is null ? Instalacao + AtrasoInicial(r) : ultima.Value + Intervalo(r);
}

/// <summary>Executa rotinas, uma de cada vez.</summary>
public sealed class Motor
{
    private readonly SemaphoreSlim _trava = new(1, 1);
    private CancellationTokenSource? _cts;

    public Rotina? RotinaAtual { get; private set; }
    public bool SimulacaoAtual { get; private set; }
    public string? TarefaAtual { get; private set; }
    public int IndiceAtual { get; private set; }
    public int TotalAtual { get; private set; }

    public bool Ocupado => RotinaAtual is not null;

    public void Cancelar()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Retorna null se já houver uma rotina rodando.</summary>
    public async Task<ExecucaoRotina?> ExecutarAsync(Rotina rotina, bool simulacao, string origem, CancellationToken ct)
    {
        if (!await _trava.WaitAsync(0, ct)) return null;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        var token = cts.Token;
        try
        {
            var cfg = ArmazemConfig.Carregar();
            var ctx = new Contexto { Config = cfg, Simulacao = simulacao };
            var tarefas = Catalogo.Da(rotina).ToList();
            var exec = new ExecucaoRotina { Rotina = rotina, Simulacao = simulacao, Origem = origem, Inicio = DateTime.Now };

            RotinaAtual = rotina; SimulacaoAtual = simulacao; TotalAtual = tarefas.Count;
            Registro.Escrever($"== Rotina {rotina.Nome()} iniciada ({origem}{(simulacao ? ", simulação" : "")})");

            bool cancelada = false;
            for (int i = 0; i < tarefas.Count; i++)
            {
                var t = tarefas[i];
                IndiceAtual = i + 1; TarefaAtual = t.Nome;
                try
                {
                    var res = await t.ExecutarAsync(ctx, token);
                    exec.Resultados.Add(res);
                    Registro.Escrever($"   [{res.Status}] {t.Nome}: {res.Resumo} ({res.Duracao.TotalSeconds:0}s)");
                }
                catch (OperationCanceledException)
                {
                    cancelada = true;
                    Registro.Escrever($"   Rotina cancelada durante: {t.Nome}");
                    break;
                }
            }

            exec.Fim = DateTime.Now;
            // Interrompida porque o serviço está parando (desligamento do PC): não salva,
            // assim a rotina continua "vencida" e roda de novo depois.
            // Cancelada pelo usuário: salva, para não recomeçar sozinha em seguida.
            if (cancelada && ct.IsCancellationRequested) return null;
            if (cancelada)
                exec.Resultados.Add(new ResultadoTarefa
                {
                    TarefaId = "cancelada", Nome = "Execução interrompida", Status = StatusTarefa.Ignorada,
                    Resumo = "A rotina foi cancelada antes de terminar.", Inicio = exec.Fim, Fim = exec.Fim,
                });

            var historico = Historico.Carregar();
            historico.Add(exec);
            exec.Score = Saude.Calcular(historico, cfg.TarefasDesativadas).Geral;
            try { exec.Relatorio = Relatorio.Gerar(exec); } catch (Exception ex) { Registro.Escrever("Falha ao gerar relatório: " + ex.Message); }
            Historico.Adicionar(exec);
            Registro.Escrever($"== Rotina {rotina.Nome()} terminada. Score: {exec.Score}");
            Registro.LimparAntigos();
            return exec;
        }
        finally
        {
            RotinaAtual = null; TarefaAtual = null; IndiceAtual = 0; TotalAtual = 0;
            _cts = null;
            cts.Dispose();
            _trava.Release();
        }
    }
}
