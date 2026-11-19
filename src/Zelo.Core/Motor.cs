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
    /// <summary>Quantas das 5 áreas têm verificação válida (a nota só vale sobre elas).</summary>
    public int Cobertura { get; set; }
    public List<string> Alertas { get; set; } = new();
    /// <summary>Itens que pedem atenção, do mais grave ao mais leve.</summary>
    public List<PontoAtencao> Pontos { get; set; } = new();
    /// <summary>Por área (nome do enum Categoria): pior situação e a frase que a resume.</summary>
    public Dictionary<string, ResumoArea> Areas { get; set; } = new();
    /// <summary>Avisos que a pessoa silenciou: saem da lista, mas continuam contando na nota.</summary>
    public List<string> Silenciados { get; set; } = new();
}

public sealed class PontoAtencao
{
    public string TarefaId { get; set; } = "";
    public string Nome { get; set; } = "";
    public Categoria Categoria { get; set; }
    public StatusTarefa Status { get; set; }
    public string Resumo { get; set; } = "";
    public bool Desatualizado { get; set; }
    public DateTime Quando { get; set; }
    public List<string> Detalhes { get; set; } = new();
}

public sealed class ResumoArea
{
    public StatusTarefa Status { get; set; }
    public string Texto { get; set; } = "";
    public bool Desatualizado { get; set; }
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

    private static int Gravidade(StatusTarefa s) => s switch
    {
        StatusTarefa.Critico => 3, StatusTarefa.Erro => 2, StatusTarefa.Aviso => 1, _ => 0,
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

        // Duas coisas diferentes:
        // • Reconhecido (com motivo: "fui eu que desliguei", "são contas minhas"): a pessoa explicou o que
        //   o Zelo viu, e isso resolve aquele achado até a próxima verificação.
        // • Silenciado (sem motivo): só tira o aviso da lista. O problema continua contando na nota,
        //   e só uma nova verificação que não o encontre mais o resolve.
        var dispensas = Dispensas.Ler();
        var silenciados = new HashSet<string>();
        foreach (var id in ultimos.Keys.ToList())
        {
            var (res, rot) = ultimos[id];
            if (res.Status is StatusTarefa.Ok || !dispensas.Quando.TryGetValue(id, out var quando) || quando < res.Fim) continue;
            if (dispensas.Motivos.TryGetValue(id, out var motivo) && !string.IsNullOrWhiteSpace(motivo))
                ultimos[id] = (new ResultadoTarefa
                {
                    TarefaId = res.TarefaId, Nome = res.Nome, Categoria = res.Categoria, Status = StatusTarefa.Ok,
                    Resumo = $"Você confirmou: {motivo}.", Detalhes = res.Detalhes, Inicio = res.Inicio, Fim = res.Fim,
                }, rot);
            else
                silenciados.Add(id);
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

        // Resumo por área: o pior item; se tudo estiver bem, a frase da verificação principal da área.
        var principal = new Dictionary<Categoria, string[]>
        {
            [Categoria.Disco] = new[] { "saude-disco", "espaco-disco" },
            [Categoria.Seguranca] = new[] { "defender-rapida", "auditoria-seguranca" },
            [Categoria.Atualizacoes] = new[] { "windows-update", "winget" },
            [Categoria.Backup] = new[] { "backup-incremental", "verificar-backup" },
            [Categoria.Sistema] = new[] { "eventos-criticos", "integridade-sistema" },
        };
        foreach (var grupo in ultimos.Values.GroupBy(v => v.res.Categoria))
        {
            var pior = grupo.OrderByDescending(v => Gravidade(v.res.Status)).ThenByDescending(v => v.res.Fim).First();
            var escolhido = Gravidade(pior.res.Status) > 0
                ? pior
                : grupo.OrderBy(v => Array.IndexOf(principal[grupo.Key], v.res.TarefaId) is var i && i >= 0 ? i : 99).First();
            painel.Areas[grupo.Key.ToString()] = new ResumoArea
            {
                Status = escolhido.res.Status,
                Texto = escolhido.res.Resumo,
                Desatualizado = agora - escolhido.res.Fim > Validade(escolhido.rotina),
            };
        }
        painel.Silenciados = silenciados.ToList();
        foreach (var (res, rotina) in ultimos.Values
                     .Where(v => Gravidade(v.res.Status) > 0 && !silenciados.Contains(v.res.TarefaId))
                     .OrderByDescending(v => Gravidade(v.res.Status)))
            painel.Pontos.Add(new PontoAtencao
            {
                TarefaId = res.TarefaId, Nome = res.Nome, Categoria = res.Categoria, Status = res.Status,
                Resumo = res.Resumo, Desatualizado = agora - res.Fim > Validade(rotina), Quando = res.Fim,
                Detalhes = res.Detalhes.Take(40).ToList(),
            });

        double soma = 0, pesos = 0;
        foreach (var (cat, lista) in porCategoria)
        {
            // Um item crítico derruba a categoria inteira: média, mas limitada pelo pior item + 30.
            int valor = (int)Math.Round(Math.Min(lista.Average(), lista.Min() + 30));
            painel.PorCategoria[cat.ToString()] = valor;
            soma += valor * Pesos[cat];
            pesos += Pesos[cat];
        }

        painel.Cobertura = porCategoria.Count;
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
        // Simulações e rotinas interrompidas não contam como feitas.
        foreach (var e in historico.Where(h => !h.Simulacao && h.Origem != "tarefa" && !h.Resultados.Any(r => r.TarefaId == "cancelada")))
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

    private readonly object _travaEtapas = new();
    private List<EtapaAtual> _etapas = new();

    /// <summary>Cópia das etapas da rotina em andamento, para o painel mostrar o progresso.</summary>
    public List<EtapaAtual> Etapas
    {
        get { lock (_travaEtapas) return _etapas.Select(e => e.Copia()).ToList(); }
    }

    /// <summary>Como cada tarefa aparece enquanto roda: um verbo no gerúndio, em linguagem simples.</summary>
    public static string Acao(string id) => id switch
    {
        "defender-rapida" => "Conferindo o antivírus",
        "saude-disco" => "Checando a saúde dos discos",
        "espaco-disco" => "Medindo o espaço livre",
        "eventos-criticos" => "Procurando erros graves no sistema",
        "reinicio-pendente" => "Vendo se o Windows pediu para reiniciar",
        "limpeza-temp" => "Limpando arquivos temporários antigos",
        "limpeza-downloads" => "Limpando a pasta Downloads",
        "backup-incremental" => "Copiando seus arquivos (backup)",
        "ponto-restauracao" => "Criando um ponto de restauração",
        "windows-update" => "Atualizando o Windows",
        "drivers-wu" => "Atualizando drivers",
        "driver-video" => "Conferindo o driver de vídeo",
        "winget" => "Atualizando seus programas",
        "otimizar-unidades" => "Otimizando os discos",
        "lixeira-cache" => "Esvaziando a Lixeira antiga",
        "inicializacao" => "Vendo o que abre com o Windows",
        "integridade-sistema" => "Reparando arquivos do Windows",
        "limpeza-componentes" => "Limpando atualizações antigas",
        "verificar-volumes" => "Checando o sistema de arquivos",
        "auditoria-seguranca" => "Revisando a segurança",
        "defender-completa" => "Fazendo a verificação completa de vírus",
        "verificar-backup" => "Testando o backup",
        "bateria" => "Medindo a saúde da bateria",
        "maiores-arquivos" => "Procurando os maiores arquivos",
        "drivers-antigos" => "Procurando drivers muito antigos",
        _ => id,
    };

    public void Cancelar()
    {
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Retorna null se já houver uma rotina rodando.</summary>
    public async Task<ExecucaoRotina?> ExecutarAsync(Rotina rotina, bool simulacao, string origem, CancellationToken ct, string? somenteTarefa = null)
    {
        if (!await _trava.WaitAsync(0, ct)) return null;
        if (!Coordenador.Iniciar("rotina"))
        {
            _trava.Release();
            Registro.Escrever($"Rotina {rotina.Nome()} adiada: {Coordenador.Explicar()}");
            return null;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        var token = cts.Token;
        try
        {
            var cfg = ArmazemConfig.Carregar();
            var ctx = new Contexto { Config = cfg, Simulacao = simulacao };
            var tarefas = somenteTarefa is null
                ? Catalogo.Da(rotina).ToList()
                : Catalogo.Todas.Where(t => t.Id == somenteTarefa).ToList();
            var exec = new ExecucaoRotina { Rotina = rotina, Simulacao = simulacao, Origem = origem, Inicio = DateTime.Now };

            lock (_travaEtapas)
                _etapas = tarefas.Select(t => new EtapaAtual { Id = t.Id, Nome = Acao(t.Id) }).ToList();
            RotinaAtual = rotina; SimulacaoAtual = simulacao; TotalAtual = tarefas.Count;
            Registro.Escrever($"== Rotina {rotina.Nome()} iniciada ({origem}{(simulacao ? ", simulação" : "")})");

            bool cancelada = false;
            for (int i = 0; i < tarefas.Count; i++)
            {
                var t = tarefas[i];
                IndiceAtual = i + 1; TarefaAtual = Acao(t.Id);
                lock (_travaEtapas) _etapas[i].EmAndamento = true;
                try
                {
                    var res = await t.ExecutarAsync(ctx, token);
                    exec.Resultados.Add(res);
                    lock (_travaEtapas)
                    {
                        _etapas[i].EmAndamento = false;
                        _etapas[i].Status = res.Status;
                        _etapas[i].Resumo = res.Resumo;
                        _etapas[i].Segundos = (int)res.Duracao.TotalSeconds;
                    }
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
            lock (_travaEtapas) _etapas = new();
            _cts = null;
            cts.Dispose();
            Coordenador.Encerrar("rotina");
            _trava.Release();
        }
    }
}

public sealed class EtapaAtual
{
    public string Id { get; set; } = "";
    public string Nome { get; set; } = "";
    /// <summary>Null = ainda não rodou.</summary>
    public StatusTarefa? Status { get; set; }
    public string Resumo { get; set; } = "";
    public int Segundos { get; set; }
    public bool EmAndamento { get; set; }

    public EtapaAtual Copia() => (EtapaAtual)MemberwiseClone();
}

/// <summary>Pausa pedida pelo usuário (menu da bandeja): as rotinas automáticas esperam até a hora marcada.</summary>
public static class Pausa
{
    private sealed class Dados { public DateTime? Ate { get; set; } }

    public static DateTime? Ate
    {
        get
        {
            var ate = Estado.Ler<Dados>("pausa")?.Ate;
            return ate > DateTime.Now ? ate : null;
        }
    }

    public static void Definir(TimeSpan duracao) =>
        Estado.Gravar("pausa", new Dados { Ate = duracao > TimeSpan.Zero ? DateTime.Now + duracao : null });
}

/// <summary>
/// Alertas que a pessoa marcou como resolvidos ("foi queda de energia", "o administrador sou eu").
/// Um resultado dispensado deixa de pesar na nota até a próxima verificação daquela tarefa.
/// </summary>
public static class Dispensas
{
    public sealed class Dados
    {
        public Dictionary<string, DateTime> Quando { get; set; } = new();
        /// <summary>Vazio = só silenciado; preenchido = a pessoa explicou (reconheceu) o achado.</summary>
        public Dictionary<string, string> Motivos { get; set; } = new();
    }

    public static Dados Ler() => Estado.Ler<Dados>("dispensas") ?? new Dados();

    public static Dictionary<string, DateTime> Todas() => Ler().Quando;

    public static DateTime? De(string tarefaId) => Todas().TryGetValue(tarefaId, out var d) ? d : null;

    public static void Dispensar(string tarefaId, string? motivo = null)
    {
        var d = Ler();
        d.Quando[tarefaId] = DateTime.Now;
        if (string.IsNullOrWhiteSpace(motivo)) d.Motivos.Remove(tarefaId);
        else d.Motivos[tarefaId] = motivo.Trim();
        Estado.Gravar("dispensas", d);
    }
}
