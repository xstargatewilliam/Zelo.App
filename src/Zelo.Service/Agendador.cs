using Zelo.Core;

namespace Zelo.Service;

/// <summary>
/// A cada 5 minutos verifica se alguma rotina venceu e se o momento é bom
/// (dentro da janela de horário, na tomada e com o PC ocioso).
/// Se o PC ficou desligado, a rotina roda assim que der — nada é "perdido".
/// </summary>
public sealed class Agendador : BackgroundService
{
    private readonly Motor _motor;
    private DateTime _ultimoLogEspera = DateTime.MinValue;

    public Agendador(Motor motor) => _motor = motor;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        Caminhos.Garantir();
        _ = Agenda.Instalacao; // registra a data da primeira execução
        Registro.Escrever("Serviço iniciado.");

        // Dá um tempo para o Windows terminar de iniciar.
        try { await Task.Delay(TimeSpan.FromMinutes(3), parar); } catch (OperationCanceledException) { return; }

        while (!parar.IsCancellationRequested)
        {
            try
            {
                await VerificarAsync(parar);
            }
            catch (OperationCanceledException) when (parar.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Registro.Escrever("Erro no agendador: " + ex);
            }

            try { await Task.Delay(TimeSpan.FromMinutes(5), parar); } catch (OperationCanceledException) { break; }
        }
        Registro.Escrever("Serviço parado.");
    }

    private async Task VerificarAsync(CancellationToken parar)
    {
        if (_motor.Ocupado || Atualizador.EmAndamento) return;

        var cfg = ArmazemConfig.Carregar();

        // Uma consulta ao GitHub por dia; instala sozinha se a opção estiver ligada.
        var ultimaConsulta = Atualizador.LerEstado().UltimaVerificacao;
        if (ultimaConsulta is null || DateTime.Now - ultimaConsulta > TimeSpan.FromHours(20))
        {
            var msg = await Atualizador.VerificarEAplicarAsync(cfg.AtualizacaoAutomatica, parar);
            Registro.Escrever("Atualização: " + msg);
            if (Atualizador.EmAndamento || msg.StartsWith("Instalando")) return; // o serviço vai reiniciar
        }
        var ultimas = Agenda.UltimasExecucoes(Historico.Carregar());
        var agora = DateTime.Now;

        if (Pausa.Ate is { } pausa) { Esperando(Rotina.Diaria, $"pausado pelo usuário até {pausa:HH:mm}"); return; }

        foreach (var rotina in new[] { Rotina.Diaria, Rotina.Semanal, Rotina.Mensal })
        {
            var proxima = Agenda.Proxima(rotina, ultimas[rotina]);
            if (agora < proxima) continue;

            // O horário do perfil (expediente, madrugada) é uma regra rígida: nunca é ignorado,
            // por mais atrasada que a rotina esteja. O mesmo vale para o PC estar em uso.
            if (!cfg.DentroDaJanela(agora)) { Esperando(rotina, "fora do horário livre do perfil"); return; }
            if (cfg.SomenteNaTomada && !Nativo.NaTomada()) { Esperando(rotina, "notebook na bateria"); return; }
            // Rotinas pesadas exigem o PC parado por mais tempo antes de começar.
            if (cfg.ExigirOcioso)
            {
                var amostra = TimeSpan.FromSeconds(rotina == Rotina.Diaria ? 10 : 45);
                var cpu = await Nativo.UsoCpuAsync(amostra, parar);
                if (cpu > cfg.LimiteCpuOcioso) { Esperando(rotina, $"PC em uso (CPU {cpu:0}%)"); return; }
            }

            await ExecutarVigiandoAsync(rotina, parar);
            return; // uma rotina por ciclo
        }
    }

    /// <summary>
    /// Roda a rotina e, a cada 30 s, confere se ela ainda pode continuar. Se o horário livre acabar
    /// (por exemplo, a pessoa estendeu o expediente) ou houver uma pausa, a rotina é interrompida
    /// sem ser registrada como feita, e volta no próximo horário livre.
    /// </summary>
    private async Task ExecutarVigiandoAsync(Rotina rotina, CancellationToken parar)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(parar);
        var execucao = _motor.ExecutarAsync(rotina, simulacao: false, origem: "agendada", cts.Token);
        while (!execucao.IsCompleted)
        {
            await Task.WhenAny(execucao, Task.Delay(TimeSpan.FromSeconds(30)));
            if (execucao.IsCompleted || cts.IsCancellationRequested) break;
            var cfg = ArmazemConfig.Carregar();
            string? motivo = !cfg.DentroDaJanela(DateTime.Now) ? "o horário livre do perfil acabou"
                : Pausa.Ate is not null ? "pausa pedida pelo usuário"
                : cfg.SomenteNaTomada && !Nativo.NaTomada() ? "o notebook saiu da tomada"
                : null;
            if (motivo is null) continue;
            Registro.Escrever($"Rotina {rotina.Nome()} interrompida: {motivo}. Ela volta no próximo horário livre.");
            cts.Cancel();
        }
        try { await execucao; }
        catch (OperationCanceledException) { }
    }

    private void Esperando(Rotina r, string motivo)
    {
        // Evita encher o log: no máximo uma linha por hora.
        if (DateTime.Now - _ultimoLogEspera < TimeSpan.FromHours(1)) return;
        _ultimoLogEspera = DateTime.Now;
        Registro.Escrever($"Rotina {r.Nome()} vencida, aguardando: {motivo}.");
    }
}
