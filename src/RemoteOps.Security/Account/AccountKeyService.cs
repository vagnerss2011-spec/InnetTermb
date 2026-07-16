using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteOps.Security.Account;

/// <summary>
/// Núcleo de cripto do E2EE multi-device (Fase 1). PURO — sem IO/rede. A senha da conta deriva
/// (Argon2id) uma MasterKey; dela saem, por HKDF com domain-separation, um AuthHash (vai pro
/// servidor, prova a senha mas NÃO abre o cofre) e uma KEK (embrulha a AMK). A AMK (Account Master
/// Key, aleatória, criada 1x) é a raiz PORTÁVEL do cofre: viaja entre devices como blob cifrado
/// (escrow por senha e por chave de recuperação). O servidor nunca vê senha/MasterKey/KEK/AMK.
/// Ver docs/superpowers/specs/2026-07-16-cloud-sync-e2ee-phase1-design.md §4.
/// </summary>
public sealed class AccountKeyService
{
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private const string AuthInfo = "remoteops:e2ee:auth:v1";
    private const string KekInfo = "remoteops:e2ee:kek:v1";
    private const string RecInfo = "remoteops:e2ee:rec:v1";
    private const string AadPwd = "amk|pwd|v1";
    private const string AadRec = "amk|rec|v1";

    /// <summary>Deriva (AuthHash, KEK) da senha. Zera a MasterKey intermediária.</summary>
    public AccountKeyMaterial DeriveFromPassword(string password, byte[] salt, Argon2Params p)
    {
        byte[] master = Argon2(password, salt, p);
        try
        {
            byte[] authHash = Hkdf(master, AuthInfo);
            byte[] kek = Hkdf(master, KekInfo);
            return new AccountKeyMaterial(authHash, kek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(master);
        }
    }

    /// <summary>Cria uma conta nova: gera AMK + salt + chave de recuperação e monta os dois escrows.</summary>
    public AccountEnrollment Enroll(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        Argon2Params p = Argon2Params.Default;
        AccountKeyMaterial material = DeriveFromPassword(password, salt, p);
        byte[] amk = RandomNumberGenerator.GetBytes(KeySize);
        string recovery = RecoveryKeyCodec.Generate();
        byte[] recKey = DeriveRecoveryWrapKey(recovery);
        try
        {
            byte[] wrappedPwd = WrapKey(amk, material.Kek, AadPwd);
            byte[] wrappedRec = WrapKey(amk, recKey, AadRec);
            return new AccountEnrollment(amk, salt, p, material.AuthHash, wrappedPwd, wrappedRec, recovery);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Kek);
            CryptographicOperations.ZeroMemory(recKey);
        }
    }

    /// <summary>Device novo: recupera a AMK usando só a senha + o escrow por senha. Lança se a senha estiver errada.</summary>
    public byte[] UnwrapAmkWithPassword(string password, byte[] salt, Argon2Params p, byte[] wrappedAmkPwd)
    {
        AccountKeyMaterial material = DeriveFromPassword(password, salt, p);
        try
        {
            return UnwrapKey(wrappedAmkPwd, material.Kek, AadPwd);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Kek);
        }
    }

    /// <summary>Recupera a AMK pela chave de recuperação (segundo escrow). Lança se a chave estiver errada.</summary>
    public byte[] UnwrapAmkWithRecoveryKey(string recoveryKey, byte[] wrappedAmkRec)
    {
        byte[] recKey = DeriveRecoveryWrapKey(recoveryKey);
        try
        {
            return UnwrapKey(wrappedAmkRec, recKey, AadRec);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recKey);
        }
    }

    /// <summary>Troca de senha: re-embrulha a MESMA AMK sob a nova senha (segredos intactos).</summary>
    public (byte[] Salt, Argon2Params Params, byte[] WrappedAmkPwd, byte[] AuthHash) RewrapForNewPassword(
        byte[] amk, string newPassword)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        Argon2Params p = Argon2Params.Default;
        AccountKeyMaterial material = DeriveFromPassword(newPassword, salt, p);
        try
        {
            byte[] wrapped = WrapKey(amk, material.Kek, AadPwd);
            return (salt, p, wrapped, material.AuthHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material.Kek);
        }
    }

    // ── Primitivas AES-256-GCM (públicas: a Task 2 usa pra embrulhar a WDK sob a AMK) ──
    // Formato do blob: nonce(12) || tag(16) || ciphertext.

    public static byte[] WrapKey(byte[] plaintext, byte[] key, string context)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];
        using (var gcm = new AesGcm(key, TagSize))
        {
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));
        }
        byte[] blob = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, NonceSize);
        ciphertext.CopyTo(blob, NonceSize + TagSize);
        return blob;
    }

    public static byte[] UnwrapKey(byte[] blob, byte[] key, string context)
    {
        if (blob.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Blob cifrado inválido (curto demais).");
        }
        ReadOnlySpan<byte> nonce = blob.AsSpan(0, NonceSize);
        ReadOnlySpan<byte> tag = blob.AsSpan(NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = blob.AsSpan(NonceSize + TagSize);
        byte[] plaintext = new byte[ciphertext.Length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(context)); // lança em senha/chave errada
        return plaintext;
    }

    private static byte[] Argon2(string password, byte[] salt, Argon2Params p)
    {
        var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = p.MemoryKib,
            Iterations = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon.GetBytes(p.OutputBytes);
    }

    private static byte[] Hkdf(byte[] ikm, string info)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeySize, salt: null, info: Encoding.UTF8.GetBytes(info));

    private static byte[] DeriveRecoveryWrapKey(string recoveryKey)
    {
        byte[] raw = RecoveryKeyCodec.Parse(recoveryKey);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, KeySize, salt: null, info: Encoding.UTF8.GetBytes(RecInfo));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }
}
