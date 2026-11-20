using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Zelo.Core;

/// <summary>
/// Operações de arquivo feitas pelo serviço (SYSTEM) em pastas que outras contas controlam:
/// Temp e Downloads de cada perfil, cache dos navegadores, Lixeira, destino do backup.
///
/// O risco: uma conta comum troca uma pasta por uma junção ou link simbólico apontando para
/// C:\Windows (ou para os arquivos de outra pessoa), e o serviço, que pode tudo, apaga ou
/// escreve lá. Por isso, antes de apagar, o arquivo é ABERTO e o próprio Windows diz onde ele
/// está de fato (GetFinalPathNameByHandle); só então ele é apagado PELO MESMO identificador.
/// Trocar uma pasta por uma junção depois da conferência não adianta: o identificador
/// continua apontando para o arquivo conferido.
/// </summary>
public static class ArquivosSeguros
{
    private const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const int FileDispositionInfo = 4, FileBasicInfo = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string nome, uint acesso, uint compartilhar, IntPtr seg, uint criar, uint flags, IntPtr modelo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder caminho, uint tamanho, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int classe, ref byte apagar, uint tamanho);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int classe, out InfoBasica info, uint tamanho);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int classe, ref InfoBasica info, uint tamanho);

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoBasica
    {
        public long Criacao, UltimoAcesso, UltimaEscrita, Mudanca;
        public uint Atributos;
    }

    /// <summary>Onde o objeto aberto está de fato, sem o prefixo \\?\.</summary>
    private static string? CaminhoReal(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) return null;
        if (n > sb.Capacity)
        {
            sb = new StringBuilder((int)n + 1);
            if (GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0) == 0) return null;
        }
        var s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s[4..];
        return s;
    }

    private static SafeFileHandle Abrir(string caminho, uint acesso) =>
        CreateFileW(@"\\?\" + Path.GetFullPath(caminho), acesso, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string curto, StringBuilder longo, uint tamanho);

    /// <summary>Caminho completo, com nomes longos (C:\Users\RUNNER~1 vira C:\Users\runneradmin).</summary>
    private static string Normal(string p)
    {
        var cheio = Path.GetFullPath(p);
        if (cheio.Contains('~'))
        {
            var sb = new StringBuilder(1024);
            var n = GetLongPathNameW(cheio, sb, (uint)sb.Capacity);
            if (n > 0 && n < sb.Capacity) cheio = sb.ToString();
        }
        return cheio.TrimEnd('\\');
    }

    /// <summary>
    /// A pasta é ela mesma (nenhum trecho do caminho, do disco até ela, é junção ou link)?
    /// Pastas sincronizadas (OneDrive) contam como normais: só junções e links são recusados.
    /// </summary>
    public static bool PastaConfiavel(string pasta)
    {
        try
        {
            using var h = Abrir(pasta, FILE_READ_ATTRIBUTES);
            if (h.IsInvalid) return false;
            var real = CaminhoReal(h);
            return real is not null && string.Equals(Normal(real), Normal(pasta), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Apaga um arquivo (ou uma pasta vazia) só se ele estiver de fato dentro de <paramref name="raiz"/>.
    /// Devolve false sem apagar quando o caminho leva para fora (junção, link) ou o arquivo está em uso.
    /// Um link encontrado dentro da raiz é apagado como link: o destino dele nunca é tocado.
    /// </summary>
    public static bool Apagar(string caminho, string raiz)
    {
        using var h = Abrir(caminho, DELETE | FILE_READ_ATTRIBUTES | 0x100 /* FILE_WRITE_ATTRIBUTES */);
        if (h.IsInvalid) return false;
        var real = CaminhoReal(h);
        var r = Normal(raiz) + "\\";
        if (real is null || !(Normal(real) + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Normal(real), Normal(raiz), StringComparison.OrdinalIgnoreCase))
            return false;

        // Somente leitura impede apagar: tira o atributo pelo mesmo identificador.
        if (GetFileInformationByHandleEx(h, FileBasicInfo, out var info, (uint)Marshal.SizeOf<InfoBasica>()) && (info.Atributos & 0x1) != 0)
        {
            info.Atributos &= ~0x1u;
            if (info.Atributos == 0) info.Atributos = 0x80; // NORMAL
            info.Criacao = info.UltimoAcesso = info.UltimaEscrita = info.Mudanca = 0; // 0 = não mudar
            SetFileInformationByHandle(h, FileBasicInfo, ref info, (uint)Marshal.SizeOf<InfoBasica>());
        }
        byte sim = 1;
        return SetFileInformationByHandle(h, FileDispositionInfo, ref sim, 1);
    }

    /// <summary>Apaga uma pasta e tudo dentro dela, conferindo cada item (não segue junções nem links).</summary>
    public static bool ApagarPasta(string pasta, string raiz)
    {
        if (!PastaConfiavel(raiz)) return false;
        var opcoes = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        bool tudo = true;
        try
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(pasta, "*", opcoes))
            {
                var attr = File.GetAttributes(item);
                bool pastaReal = (attr & FileAttributes.Directory) != 0 && (attr & FileAttributes.ReparsePoint) == 0;
                tudo &= pastaReal ? ApagarPasta(item, raiz) : Apagar(item, raiz);
            }
        }
        catch { tudo = false; }
        return Apagar(pasta, raiz) && tudo;
    }

    /// <summary>
    /// Confere uma árvore onde o serviço vai escrever (destino do backup): nenhuma subpasta pode
    /// ser junção ou link. Devolve a primeira encontrada, ou null.
    /// </summary>
    public static string? PrimeiroAtalho(string pasta)
    {
        if (!Directory.Exists(pasta)) return null;
        if (!PastaConfiavel(pasta)) return pasta;
        var opcoes = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var d in Directory.EnumerateDirectories(pasta, "*", opcoes))
        {
            try
            {
                if (new DirectoryInfo(d).LinkTarget is not null) return d;
            }
            catch { return d; }
        }
        return null;
    }
}
