using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using RemoteOps.Desktop.Account;
using RemoteOps.Security.Crypto;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.Account;

/// <summary>
/// Cache local da AMK (spec §4.3): depois do login a AMK fica em repouso protegida por DPAPI
/// CurrentUser, pra o app reabrir sem pedir a senha. A KEK e a MasterKey NUNCA são cacheadas — a
/// portabilidade vem do escrow no servidor; o DPAPI protege só esta cópia local.
///
/// O protetor é injetado (<see cref="ILocalKeyProtector"/>, mesmo padrão do WorkspaceKeyRing) pra o
/// teste rodar sem depender do DPAPI real; há um teste dedicado com o DpapiKeyProtector de verdade.
/// </summary>
public sealed class DpapiAmkCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "remoteops-amk-cache-tests", Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_dir, "account.amk");

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* limpeza best-effort */ }
    }

    /// <summary>
    /// Protetor fake que só XOR-a com a entropia — não é cripto, é um espelho: o que importa nos
    /// testes é que a entropia PARTICIPA (blob protegido com uma entropia não abre com outra) e que
    /// o round-trip preserva os bytes.
    /// </summary>
    private sealed class XorProtector : ILocalKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
            => Xor(plaintext, entropy);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy)
            => Xor(protectedBlob, entropy);

        private static byte[] Xor(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entropy)
        {
            byte[] key = SHA256.HashData(entropy);
            byte[] result = data.ToArray();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= key[i % key.Length];
            }

            return result;
        }
    }

    private DpapiAmkCache NewCache(ILocalKeyProtector? protector = null)
        => new(CachePath, protector ?? new XorProtector());

    private static byte[] SampleAmk() => [.. Enumerable.Range(0, 32).Select(i => (byte)(i + 1))];

    /// <summary>Sem cache, o app não tem conta — tem que pedir login (não inventar sessão).</summary>
    [Fact]
    public async Task LoadAsync_WithoutFile_ReturnsNull()
    {
        Assert.Null(await NewCache().LoadAsync());
    }

    /// <summary>O caso do relaunch: salva e reabre com a MESMA AMK, sem senha nenhuma.</summary>
    [Fact]
    public async Task SaveThenLoad_RoundTripsTheAmk()
    {
        byte[] amk = SampleAmk();
        using (var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, amk))
        {
            await NewCache().SaveAsync(entry);
        }

        using CachedAccount? loaded = await NewCache().LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(SampleAmk(), loaded!.Amk);
        Assert.Equal("op@innet.tec.br", loaded.Email);
        Assert.Equal("ws-1", loaded.WorkspaceId);
        Assert.Equal(1, loaded.AmkKeyVersion);
    }

    /// <summary>
    /// A AMK NÃO pode aparecer em claro no arquivo — o cache é a cópia em repouso da raiz do cofre.
    /// Procura os bytes da AMK tanto crus quanto em base64.
    /// </summary>
    [Fact]
    public async Task SaveAsync_NeverWritesTheAmkInClear()
    {
        byte[] amk = SampleAmk();
        using (var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, amk))
        {
            await NewCache().SaveAsync(entry);
        }

        byte[] onDisk = await File.ReadAllBytesAsync(CachePath);
        string text = Encoding.UTF8.GetString(onDisk);

        Assert.DoesNotContain(Convert.ToBase64String(SampleAmk()), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(SampleAmk()), text, StringComparison.OrdinalIgnoreCase);
        Assert.False(ContainsSequence(onDisk, SampleAmk()), "a AMK crua vazou pro arquivo de cache");
    }

    /// <summary>
    /// A entropia é ligada à CONTA/WORKSPACE (spec §4.3): um blob salvo para uma conta não pode ser
    /// aberto sob a identidade de outra. Sem isso, trocar o e-mail no arquivo (que é texto) faria o
    /// app abrir o cofre de uma conta com a identidade de outra.
    /// </summary>
    [Fact]
    public async Task LoadAsync_WhenIdentityWasTampered_Fails()
    {
        using (var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, SampleAmk()))
        {
            await NewCache().SaveAsync(entry);
        }

        // Troca a identidade em claro no arquivo, mantendo o blob protegido intacto.
        string json = await File.ReadAllTextAsync(CachePath);
        await File.WriteAllTextAsync(CachePath, json.Replace("op@innet.tec.br", "outro@innet.tec.br"));

        // Com o XorProtector (que não autentica) o unwrap "funciona" mas devolve bytes diferentes;
        // com DPAPI de verdade ele lança. Os dois casos são cobertos: a AMK recuperada NÃO pode ser
        // a original — é isso que a entropia garante.
        CachedAccount? loaded = null;
        try
        {
            loaded = await NewCache().LoadAsync();
        }
        catch (CryptographicException)
        {
            return; // entropia rejeitou o blob — comportamento do DPAPI real
        }

        if (loaded is not null)
        {
            using (loaded)
            {
                Assert.NotEqual(SampleAmk(), loaded.Amk);
            }
        }
    }

    /// <summary>Logout/trocar conta: o cache some do disco e o app volta a pedir login.</summary>
    [Fact]
    public async Task ClearAsync_RemovesTheCache()
    {
        using (var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, SampleAmk()))
        {
            await NewCache().SaveAsync(entry);
        }

        await NewCache().ClearAsync();

        Assert.False(File.Exists(CachePath));
        Assert.Null(await NewCache().LoadAsync());
    }

    /// <summary>Limpar um cache que não existe é no-op — logout não pode explodir por isso.</summary>
    [Fact]
    public async Task ClearAsync_WithoutFile_IsNoOp()
    {
        await NewCache().ClearAsync();
        Assert.False(File.Exists(CachePath));
    }

    /// <summary>
    /// Arquivo corrompido (disco cheio no meio da escrita, edição à mão) não pode derrubar o
    /// startup: vira "sem cache" e o app pede login. Fail-open é a postura do app (offline-first).
    /// </summary>
    [Fact]
    public async Task LoadAsync_WithCorruptFile_ReturnsNullInsteadOfThrowing()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(CachePath, "isto não é json");

        Assert.Null(await NewCache().LoadAsync());
    }

    /// <summary>
    /// ⚠️ <b>Blob DPAPI de outro usuário/máquina vira "sem cache", nunca uma exceção.</b>
    ///
    /// <para>É a pasta <c>%APPDATA%\RemoteOps</c> restaurada de backup noutro computador (o operador
    /// tem dois), ou o perfil do Windows recriado: o JSON está perfeito e o blob não abre. O contrato
    /// do <c>ILocalKeyProtector</c> diz que <c>Unprotect</c> LANÇA nesse caso.</para>
    ///
    /// <para>Enquanto essa exceção escapava do <c>LoadAsync</c>, ela subia até o <c>App</c>, que a
    /// tratava como falha de ativação e ENCERRAVA o processo dizendo "abra o RemoteOps de novo e
    /// entre na conta" — e reabrir relê o mesmo blob e repete tudo. Beco sem saída, com o acervo
    /// intacto e inalcançável. A AMK é portável (o escrow está no servidor): pedir login RESOLVE.</para>
    /// </summary>
    [Fact]
    public async Task LoadAsync_QuandoOBlobNaoAbre_DevolveNULL_EmVezDeDerrubarOBoot()
    {
        using (var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, SampleAmk()))
        {
            await NewCache().SaveAsync(entry);
        }

        var deOutraMaquina = new DpapiAmkCache(CachePath, new ProtectorQueRecusa());

        Assert.Null(await deOutraMaquina.LoadAsync());
    }

    /// <summary>DPAPI de outro usuário/máquina, como o contrato do <c>ILocalKeyProtector</c> manda.</summary>
    private sealed class ProtectorQueRecusa : ILocalKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> entropy)
            => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBlob, ReadOnlySpan<byte> entropy)
            => throw new CryptographicException("blob protegido por outro usuário/máquina");
    }

    /// <summary>A AMK é 32B: um cache com tamanho errado é lixo, não raiz de cofre.</summary>
    [Fact]
    public void CachedAccount_RejectsWrongSizedAmk()
    {
        Assert.Throws<ArgumentException>(() => new CachedAccount("op@innet.tec.br", "ws-1", 1, new byte[16]));
    }

    /// <summary>Dispose zera a AMK — o cache carregado não pode sobrar vivo na heap.</summary>
    [Fact]
    public void CachedAccount_Dispose_ZeroesTheAmk()
    {
        var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, SampleAmk());
        byte[] amk = entry.Amk;

        entry.Dispose();

        Assert.True(amk.All(b => b == 0));
    }

    /// <summary>
    /// A prova com o DPAPI REAL (não o fake): é o protetor que roda em produção. Só no Windows —
    /// o resto da suíte roda com o protetor injetado, mas se este round-trip quebrar, o operador
    /// tem que digitar a senha a cada abertura do app.
    /// </summary>
    [Fact]
    public async Task RoundTrip_WithRealDpapi_Works()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = new DpapiAmkCache(CachePath, new DpapiKeyProtector());
        using (var entry = new CachedAccount("op@innet.tec.br", "ws-1", 1, SampleAmk()))
        {
            await cache.SaveAsync(entry);
        }

        using CachedAccount? loaded = await cache.LoadAsync();

        Assert.NotNull(loaded);
        Assert.Equal(SampleAmk(), loaded!.Amk);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
