using Zelo.Core;
using Zelo.Service;

// Modo de teste pelo terminal (como administrador):
//   Zelo.Service.exe --rodar diaria [--simulacao]
if (args.Length >= 2 && args[0] == "--rodar")
{
    if (!Enum.TryParse<Rotina>(args[1], ignoreCase: true, out var rotina))
    {
        Console.WriteLine("Use: --rodar diaria|semanal|mensal [--simulacao]");
        return;
    }
    Caminhos.Garantir();
    var motor = new Motor();
    bool simulacao = Array.IndexOf(args, "--simulacao") >= 0;
    var exec = await motor.ExecutarAsync(rotina, simulacao, "manual", CancellationToken.None);
    if (exec is null) return;
    foreach (var r in exec.Resultados)
    {
        Console.WriteLine($"[{r.Status.Nome(),-8}] {r.Nome}: {r.Resumo}");
        foreach (var d in r.Detalhes) Console.WriteLine("           " + d);
    }
    Console.WriteLine($"\nScore de saúde: {exec.Score}. Relatório: {exec.Relatorio}");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "Zelo");
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<Motor>();
builder.Services.AddHostedService<Agendador>();
builder.Services.AddHostedService<ServidorPipe>();
builder.Build().Run();
