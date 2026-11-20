using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Zelo.Core;

namespace Zelo.App;

/// <summary>
/// Ações de alto impacto precisam da confirmação de administrador do Windows (UAC). O painel comum
/// não é elevado; para essas ações ele abre uma cópia rápida de si mesmo com "Executar como
/// administrador", que só repassa o pedido ao serviço e fecha. Cada ação pede uma nova confirmação.
/// </summary>
public static class Elevacao
{
    private static string? SidAtual()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; } catch { return null; }
    }

    /// <summary>Envia ao serviço; se ele pedir a confirmação do Windows, pede e repete.</summary>
    public static async Task<Resposta> EnviarAsync(Pedido p, int esperaMs = 15000)
    {
        p.SidUsuario ??= SidAtual();
        var r = await ClientePipe.EnviarAsync(p, 2500, esperaMs);
        if (r.Ok || !r.PrecisaElevar) return r;
        return await EnviarElevadoAsync(p);
    }

    private static async Task<Resposta> EnviarElevadoAsync(Pedido p)
    {
        var pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zelo");
        Directory.CreateDirectory(pasta);
        var arqResposta = Path.Combine(pasta, $"resposta-{Guid.NewGuid():N}.json");
        var pedido = Convert.ToBase64String(Encoding.UTF8.GetBytes(Json.Serializar(p)));
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--pedido {pedido} \"{arqResposta}\"",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new Resposta { Ok = false, Mensagem = "Você não confirmou no aviso do Windows. Nada foi alterado." };
        }
        catch (Exception ex)
        {
            return new Resposta { Ok = false, Mensagem = "Não foi possível pedir a confirmação do Windows: " + ex.Message };
        }
        if (proc is null) return new Resposta { Ok = false, Mensagem = "Não foi possível pedir a confirmação do Windows." };
        // Nunca espera para sempre: se a cópia elevada não responder em 2 minutos, encerra e avisa.
        using (var limite = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2)))
        {
            try { await proc.WaitForExitAsync(limite.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                return new Resposta { Ok = false, Mensagem = "O Windows confirmou, mas o serviço do Zelo não respondeu. Nada foi alterado. Tente de novo; se continuar, me mande o log." };
            }
        }
        try
        {
            if (File.Exists(arqResposta))
            {
                var r = Json.Desserializar<Resposta>(File.ReadAllText(arqResposta));
                File.Delete(arqResposta);
                if (r is not null) return r;
            }
        }
        catch { }
        return proc.ExitCode == 0
            ? new Resposta { Ok = true }
            : new Resposta { Ok = false, Mensagem = "A ação não foi concluída. O detalhe fica no log do Zelo." };
    }

    /// <summary>Modo "--pedido": roda elevado, repassa o pedido ao serviço, grava a resposta e sai.</summary>
    public static int Executar(string pedidoBase64, string arqResposta)
    {
        Resposta r;
        try
        {
            var p = Json.Desserializar<Pedido>(Encoding.UTF8.GetString(Convert.FromBase64String(pedidoBase64)))
                    ?? throw new InvalidOperationException("Pedido inválido.");
            // Fora da thread da interface: esperar ali travava a cópia elevada para sempre.
            r = Task.Run(() => ClientePipe.EnviarAsync(p)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            r = new Resposta { Ok = false, Mensagem = ex.Message };
        }
        try { File.WriteAllText(arqResposta, Json.Serializar(r)); } catch { }
        return r.Ok ? 0 : 1;
    }
}
