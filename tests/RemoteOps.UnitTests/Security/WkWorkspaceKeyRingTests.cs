using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// A TERCEIRA raiz de chave do cofre (ao lado do DPAPI e da AMK) e a única que existe para ser
/// COMPARTILHADA: a WK (Workspace Key) do time. Ela não deriva de ninguém — é aleatória — porque
/// derivar da AMK é justamente o que impede um colega de abrir o cofre (a AMK é por CONTA, então
/// dois membros do mesmo workspace derivariam chaves diferentes). Aleatória, ela pode ser entregue
/// cifrada a cada membro; guardada, fica embrulhada sob a AMK de quem a guarda.
/// </summary>
public sealed class WkWorkspaceKeyRingTests
{
    private const string Workspace = "ws-time";

    private static byte[] Amk() => RandomNumberGenerator.GetBytes(32);

    /// <summary>
    /// <b>O ponto da fatia inteira.</b> A WK NÃO é derivada: dois workspaces de time têm chaves
    /// diferentes, e nenhuma delas é a WDK que a AMK derivaria para aquele workspace. Se algum dia
    /// alguém "simplificar" isto para um HKDF da AMK, o cofre do time volta a ser indecifrável para
    /// o colega — e este teste é quem grita.
    /// </summary>
    [Fact]
    public async Task Wk_EhAleatoria_NaoDerivadaDaAmk()
    {
        byte[] amk = Amk();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk);

        using WorkspaceKey wkA = await ring.GetOrCreateWorkspaceKeyAsync("ws-time-a");
        using WorkspaceKey wkB = await ring.GetOrCreateWorkspaceKeyAsync("ws-time-b");

        Assert.Equal(32, wkA.Key.Length);
        Assert.NotEqual(wkA.Key.ToArray(), wkB.Key.ToArray());
        Assert.NotEqual(AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-time-a"), wkA.Key.ToArray());
        Assert.NotEqual(AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-time-b"), wkB.Key.ToArray());
    }

    /// <summary>Duas contas diferentes geram WKs diferentes — sorteio, não derivação.</summary>
    [Fact]
    public async Task DoisRings_MesmoWorkspace_SorteiamWksDiferentes()
    {
        using var ringA = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());
        using var ringB = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        using WorkspaceKey a = await ringA.GetOrCreateWorkspaceKeyAsync(Workspace);
        using WorkspaceKey b = await ringB.GetOrCreateWorkspaceKeyAsync(Workspace);

        Assert.NotEqual(a.Key.ToArray(), b.Key.ToArray());
    }

    /// <summary>
    /// Round-trip do embrulho: a WK persiste EMBRULHADA sob a AMK, e um ring novo (reinício do app)
    /// com a mesma AMK devolve exatamente os mesmos bytes. Sem isso, cada abertura do app geraria
    /// uma WK nova e o cofre do time não abriria no dia seguinte.
    /// </summary>
    [Fact]
    public async Task WkPersistida_ReabreComAMesmaAmk_MesmosBytes()
    {
        byte[] amk = Amk();
        var store = new InMemoryWorkspaceKeyStore();

        byte[] original;
        using (var primeira = new WkWorkspaceKeyRing(store, amk))
        using (WorkspaceKey wk = await primeira.GetOrCreateWorkspaceKeyAsync(Workspace))
        {
            original = wk.Key.ToArray();
        }

        using var segunda = new WkWorkspaceKeyRing(store, amk);
        using WorkspaceKey reaberta = await segunda.GetOrCreateWorkspaceKeyAsync(Workspace);

        Assert.Equal(original, reaberta.Key.ToArray());
    }

    /// <summary>
    /// O que vai para o disco é BLOB, nunca a WK. Se este teste falhar, a chave do time está em
    /// claro no store — e o store é o arquivo/banco que o operador leva no notebook.
    /// </summary>
    [Fact]
    public async Task OQueVaiParaOStore_NaoEhAWkEmClaro()
    {
        byte[] amk = Amk();
        var store = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(store, amk);

        using WorkspaceKey wk = await ring.GetOrCreateWorkspaceKeyAsync(Workspace);
        byte[]? blob = await FindStoredBlobAsync(store);

        Assert.NotNull(blob);
        Assert.NotEqual(wk.Key.ToArray(), blob);
        Assert.False(Contains(blob, wk.Key.Span), "a WK aparece em claro dentro do blob guardado");
    }

    /// <summary>Procura a sequência de bytes da chave dentro do blob — o teste do "em claro".</summary>
    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Blob de outra conta não abre: a AMK errada faz o tag GCM falhar. Falhar ALTO é o certo —
    /// devolver null aqui faria o ring sortear uma WK nova por baixo de segredos já selados.
    /// </summary>
    [Fact]
    public async Task WkDeOutraConta_NaoAbre_ELancaEmVezDeSortearOutra()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using (var dona = new WkWorkspaceKeyRing(store, Amk()))
        {
            using WorkspaceKey _ = await dona.GetOrCreateWorkspaceKeyAsync(Workspace);
        }

        using var intrusa = new WkWorkspaceKeyRing(store, Amk());

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => intrusa.GetOrCreateWorkspaceKeyAsync(Workspace));
    }

    /// <summary>
    /// Estável na sessão: duas chamadas devolvem a MESMA chave. E — a armadilha — a segunda chamada
    /// tem que continuar válida mesmo depois de o chamador ter feito <c>Dispose</c> na primeira
    /// (o <c>CredentialVault</c> usa <c>using</c> em toda operação, e o Dispose ZERA o buffer): o
    /// ring precisa devolver uma CÓPIA, senão a segunda senha do dia seria selada com 32 zeros.
    /// </summary>
    [Fact]
    public async Task Ring_EstavelNaSessao_EImuneAoDisposeDoChamador()
    {
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        byte[] primeira;
        using (WorkspaceKey wk = await ring.GetOrCreateWorkspaceKeyAsync(Workspace))
        {
            primeira = wk.Key.ToArray();
        }

        using WorkspaceKey segunda = await ring.GetOrCreateWorkspaceKeyAsync(Workspace);

        Assert.Equal(primeira, segunda.Key.ToArray());
        Assert.NotEqual(new byte[32], segunda.Key.ToArray());
    }

    /// <summary>
    /// <b>TryGet NÃO cria.</b> É a distinção de que o migrador depende: sortear uma WK nova por
    /// baixo de segredos já selados mascararia a PERDA da chave como se fosse cofre vazio.
    /// </summary>
    [Fact]
    public async Task TryGet_SemWkGuardada_DevolveNull_ENaoCria()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(store, Amk());

        Assert.Null(await ring.TryGetWorkspaceKeyAsync(Workspace));
        Assert.Null(await FindStoredBlobAsync(store));
        Assert.Null(await ring.TryGetWorkspaceKeyAsync(Workspace));
    }

    /// <summary>Depois de criada, o TryGet devolve a mesma WK — ele lê, só não cria.</summary>
    [Fact]
    public async Task TryGet_DepoisDeCriada_DevolveAMesmaWk()
    {
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        using WorkspaceKey criada = await ring.GetOrCreateWorkspaceKeyAsync(Workspace);
        using WorkspaceKey? lida = await ring.TryGetWorkspaceKeyAsync(Workspace);

        Assert.NotNull(lida);
        Assert.Equal(criada.Key.ToArray(), lida.Key.ToArray());
    }

    /// <summary>
    /// O carimbo é da RAIZ (e não do cifrador): é ele que vai em <c>SecretEnvelope.Algorithm</c> e
    /// que diz ao outro device COM QUAL chave o envelope foi selado.
    /// </summary>
    [Fact]
    public void AlgorithmId_EhOCarimboDaWk()
    {
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        Assert.Equal(VaultAlgorithms.WkRootedV1, ring.AlgorithmId);
        Assert.Equal("AES-256-GCM;CEK-wrap;WK-random-v1", ring.AlgorithmId);
    }

    /// <summary>
    /// O app compartilha UM <see cref="IWorkspaceKeyStore"/> entre as raízes (o
    /// <c>FileVaultStore</c> é o mesmo objeto). Se as duas gravassem sob a mesma chave, a WK do time
    /// sobrescreveria o blob DPAPI do cofre pessoal — perda de chave, não erro de leitura.
    /// </summary>
    [Fact]
    public async Task StoreCompartilhado_NaoColideComARaizDpapi()
    {
        var store = new InMemoryWorkspaceKeyStore();
        var dpapi = new WorkspaceKeyRing(store, new FakeKeyProtector("userA@machine1"));
        using var wkRing = new WkWorkspaceKeyRing(store, Amk());

        using WorkspaceKey legada = await dpapi.GetOrCreateWorkspaceKeyAsync(Workspace);

        // A raiz do time não pode "achar" que a chave legada é dela.
        Assert.Null(await wkRing.TryGetWorkspaceKeyAsync(Workspace));

        using WorkspaceKey wk = await wkRing.GetOrCreateWorkspaceKeyAsync(Workspace);
        Assert.NotEqual(legada.Key.ToArray(), wk.Key.ToArray());

        // E a legada continua intacta depois de a WK ser gravada.
        using WorkspaceKey legadaDepois = await dpapi.GetOrCreateWorkspaceKeyAsync(Workspace);
        Assert.Equal(legada.Key.ToArray(), legadaDepois.Key.ToArray());
    }

    /// <summary>
    /// O <see cref="InMemoryWorkspaceKeyStore"/> não enumera; a chave de armazenamento é detalhe da
    /// implementação. Este helper procura pelas duas formas plausíveis para que o teste afirme o
    /// COMPORTAMENTO ("existe/não existe blob guardado") sem se prender ao nome da chave.
    /// </summary>
    private static async Task<byte[]?> FindStoredBlobAsync(IWorkspaceKeyStore store) =>
        await store.LoadAsync(Workspace) ?? await store.LoadAsync("wk:" + Workspace);
}
