using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Zelo.Core;

/// <summary>
/// Assinatura digital (Authenticode) de um executável, conferida pelo próprio Windows (WinVerifyTrust).
/// Usada pela atualização automática: se a versão instalada é assinada, a nova precisa ser assinada
/// pelo mesmo editor. Assim, mesmo que alguém troque o pacote e o .sha256 no caminho, a troca é recusada.
/// </summary>
public sealed record Assinatura(bool Valida, string? Editor)
{
    public static readonly Assinatura Nenhuma = new(false, null);

    public static Assinatura Ler(string arquivo)
    {
        if (!File.Exists(arquivo)) return Nenhuma;
        bool valida = Verificar(arquivo);
        string? editor = null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(arquivo));
#pragma warning restore SYSLIB0057
            editor = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch { /* sem assinatura embutida */ }
        return new Assinatura(valida && editor is not null, editor);
    }

    /// <summary>Null se pode instalar; senão, o motivo da recusa.</summary>
    public static string? Conferir(Assinatura instalada, Assinatura nova)
    {
        if (!instalada.Valida) return null; // versão instalada sem assinatura: vale a conferência pelo SHA-256
        if (!nova.Valida)
            return "O pacote novo não tem uma assinatura digital válida, e a versão instalada tem. Atualização cancelada por segurança.";
        if (!string.Equals(instalada.Editor, nova.Editor, StringComparison.OrdinalIgnoreCase))
            return $"O pacote novo foi assinado por outro editor ({nova.Editor}, e não {instalada.Editor}). Atualização cancelada por segurança.";
        return null;
    }

    /// <summary>Uma atualização não pode misturar um serviço assinado com painel ou instalador adulterado.</summary>
    public static string? ConferirPacote(Assinatura instalada, IEnumerable<(string Nome, Assinatura Nova)> componentes)
    {
        foreach (var (nome, nova) in componentes)
            if (Conferir(instalada, nova) is { } erro)
                return $"{nome}: {erro}";
        return null;
    }

    // ---------------------------------------------------------------- WinVerifyTrust

    private static readonly Guid AcaoVerificar = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid acao, ref WintrustData dados);

    private static bool Verificar(string arquivo)
    {
        const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2, WTD_REVOCATION_CHECK_NONE = 0x10;
        var info = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = arquivo };
        var pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        bool preenchido = false;
        try
        {
            Marshal.StructureToPtr(info, pInfo, false);
            preenchido = true;
            var dados = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pInfo,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE, // sem consulta à internet: funciona offline
            };
            var acao = AcaoVerificar;
            int r = WinVerifyTrust(IntPtr.Zero, ref acao, ref dados);
            dados.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref acao, ref dados);
            return r == 0;
        }
        catch { return false; }
        finally
        {
            if (preenchido) Marshal.DestroyStructure<WintrustFileInfo>(pInfo);
            Marshal.FreeHGlobal(pInfo);
        }
    }
}
