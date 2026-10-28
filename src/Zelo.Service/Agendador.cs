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
        if (_motor.Ocupado) return;

        var cfg = ArmazemConfig.Carregar();
        var ultimas = Agenda.UltimasExecucoes(Historico.Carregar());
        var agora = DateTime.Now;

        foreach (var rotina in new[] { Rotina.Diaria, Rotina.Semanal, Rotina.Mensal })
        {
            var proxima = Agenda.Proxima(rotina, ultimas[rotina]);
            if (agora < proxima) continue;

            // Muito atrasada (ex.: PC sempre usado no horário da janela): relaxa as condições.
            bool muitoAtrasada = agora - proxima > TimeSpan.FromHours(48);

            if (!muitoAtrasada && !cfg.DentroDaJanela(agora)) { Esperando(rotina, "fora da janela de horário"); return; }
            if (cfg.SomenteNaTomada && !Nativo.NaTomada()) { Esperando(rotina, "notebook na bateria"); return; }
            if (!muitoAtrasada)
            {
                var cpu = await Nativo.UsoCpuAsync(TimeSpan.FromSeconds(10), parar);
                if (cpu > cfg.LimiteCpuOcioso) { Esperando(rotina, $"PC em uso (CPU {cpu:0}%)"); return; }
            }

            await _motor.ExecutarAsync(rotina, simulacao: false, origem: "agendada", parar);
            return; // uma rotina por ciclo
        }
    }

    private void Esperando(Rotina r, string motivo)
    {
        // Evita encher o log: no máximo uma linha por hora.
        if (DateTime.Now - _ultimoLogEspera < TimeSpan.FromHours(1)) return;
        _ultimoLogEspera = DateTime.Now;
        Registro.Escrever($"Rotina {r.Nome()} vencida, aguardando: {motivo}.");
    }
}
