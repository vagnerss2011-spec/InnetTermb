using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RemoteOps.Security.Crypto;

/// <summary>
/// <see cref="ILocalKeyProtector"/> sobre DPAPI no Windows, via P/Invoke ao
/// <c>crypt32.dll</c> (sem pacotes externos — ver ADR-003).
/// Escopo CurrentUser: o blob só é recuperável pelo mesmo usuário do Windows
/// na mesma máquina, atendendo ao threat model (notebook roubado / outro usuário).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyProtector : ILocalKeyProtector
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public DpapiKeyProtector()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DpapiKeyProtector requer Windows.");
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy) =>
        Transform(plaintext, entropy, protect: true);

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy) =>
        Transform(protectedBlob, entropy, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, ReadOnlySpan<byte> entropy, bool protect)
    {
        byte[] inputCopy = input.ToArray();
        byte[] entropyCopy = entropy.ToArray();

        GCHandle inHandle = GCHandle.Alloc(inputCopy, GCHandleType.Pinned);
        GCHandle entHandle = entropyCopy.Length > 0
            ? GCHandle.Alloc(entropyCopy, GCHandleType.Pinned)
            : default;
        DataBlob outBlob = default;
        try
        {
            var inBlob = new DataBlob { cbData = inputCopy.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entBlob = entropyCopy.Length > 0
                ? new DataBlob { cbData = entropyCopy.Length, pbData = entHandle.AddrOfPinnedObject() }
                : default;

            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                // Não vaza segredo: apenas o código de erro do Win32.
                throw new CryptographicException($"DPAPI {(protect ? "Protect" : "Unprotect")} falhou (Win32 0x{err:X8}).");
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (outBlob.pbData != IntPtr.Zero)
            {
                // Zera o buffer nativo ANTES de liberá-lo. No Unprotect ele contém a chave em claro
                // (AMK/WDK); sem isto, o material decifrado ficaria legível na heap nativa já liberada
                // até algo sobrescrever a região. No Protect é só o blob cifrado (não sensível), mas
                // como o caminho é compartilhado, zerar aqui é uniforme e inofensivo — o resultado já
                // foi copiado pra 'result' antes deste finally. Marshal.WriteByte é escrita real em
                // memória não gerenciada: o JIT não pode elidi-la (não é dead-store).
                for (int i = 0; i < outBlob.cbData; i++)
                {
                    Marshal.WriteByte(outBlob.pbData, i, 0);
                }

                LocalFree(outBlob.pbData);
            }

            if (inHandle.IsAllocated)
            {
                inHandle.Free();
            }

            if (entHandle.IsAllocated)
            {
                entHandle.Free();
            }

            CryptographicOperations.ZeroMemory(inputCopy);
            CryptographicOperations.ZeroMemory(entropyCopy);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptProtectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CryptUnprotectData")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
