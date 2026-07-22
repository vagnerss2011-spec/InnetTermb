using System.Reflection;
using System.Security.Cryptography;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
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
        using var ring = TeamKeyRingFactory.New(amk);

        using WorkspaceKey wkA = await ring.MintWorkspaceKeyAsync("ws-time-a");
        using WorkspaceKey wkB = await ring.MintWorkspaceKeyAsync("ws-time-b");

        Assert.Equal(32, wkA.Key.Length);
        Assert.NotEqual(wkA.Key.ToArray(), wkB.Key.ToArray());
        Assert.NotEqual(AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-time-a"), wkA.Key.ToArray());
        Assert.NotEqual(AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-time-b"), wkB.Key.ToArray());
    }

    /// <summary>Duas contas diferentes geram WKs diferentes — sorteio, não derivação.</summary>
    [Fact]
    public async Task DoisRings_MesmoWorkspace_SorteiamWksDiferentes()
    {
        using var ringA = TeamKeyRingFactory.New(Amk());
        using var ringB = TeamKeyRingFactory.New(Amk());

        using WorkspaceKey a = await ringA.MintWorkspaceKeyAsync(Workspace);
        using WorkspaceKey b = await ringB.MintWorkspaceKeyAsync(Workspace);

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
        using (var primeira = TeamKeyRingFactory.New(store, amk))
        using (WorkspaceKey wk = await primeira.MintWorkspaceKeyAsync(Workspace))
        {
            original = wk.Key.ToArray();
        }

        using var segunda = TeamKeyRingFactory.New(store, amk);
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
        using var ring = TeamKeyRingFactory.New(store, amk);

        using WorkspaceKey wk = await ring.MintWorkspaceKeyAsync(Workspace);
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
        using (var dona = TeamKeyRingFactory.New(store, Amk()))
        {
            using WorkspaceKey _ = await dona.MintWorkspaceKeyAsync(Workspace);
        }

        using var intrusa = TeamKeyRingFactory.New(store, Amk());

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
        using var ring = TeamKeyRingFactory.New(Amk());

        byte[] primeira;
        using (WorkspaceKey wk = await ring.MintWorkspaceKeyAsync(Workspace))
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
        using var ring = TeamKeyRingFactory.New(store, Amk());

        Assert.Null(await ring.TryGetWorkspaceKeyAsync(Workspace));
        Assert.Null(await FindStoredBlobAsync(store));
        Assert.Null(await ring.TryGetWorkspaceKeyAsync(Workspace));
    }

    /// <summary>Depois de criada, o TryGet devolve a mesma WK — ele lê, só não cria.</summary>
    [Fact]
    public async Task TryGet_DepoisDeCriada_DevolveAMesmaWk()
    {
        using var ring = TeamKeyRingFactory.New(Amk());

        using WorkspaceKey criada = await ring.MintWorkspaceKeyAsync(Workspace);
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
        using var ring = TeamKeyRingFactory.New(Amk());

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
        using var wkRing = TeamKeyRingFactory.New(store, Amk());

        using WorkspaceKey legada = await dpapi.GetOrCreateWorkspaceKeyAsync(Workspace);

        // A raiz do time não pode "achar" que a chave legada é dela.
        Assert.Null(await wkRing.TryGetWorkspaceKeyAsync(Workspace));

        using WorkspaceKey wk = await wkRing.MintWorkspaceKeyAsync(Workspace);
        Assert.NotEqual(legada.Key.ToArray(), wk.Key.ToArray());

        // E a legada continua intacta depois de a WK ser gravada.
        using WorkspaceKey legadaDepois = await dpapi.GetOrCreateWorkspaceKeyAsync(Workspace);
        Assert.Equal(legada.Key.ToArray(), legadaDepois.Key.ToArray());
    }

    // ── Fail-closed por ESTRUTURA, e não por bandeira (1h) ───────────────────────────────

    /// <summary>
    /// <b>Cofre de time sem WK RECUSA — nunca sorteia.</b> Este é o defeito número um da fatia: o
    /// <c>CredentialVault</c> pede a chave em TODA operação, então qualquer coisa que toque o cofre
    /// do time antes de a chave chegar ganharia uma WK aleatória. O convidado passaria semanas
    /// cadastrando senhas que ninguém do time abre, sem um único erro na tela.
    ///
    /// <para>E a recusa vem com frase acionável: o operador precisa saber que falta ACEITAR o
    /// convite, não que "ocorreu um erro".</para>
    /// </summary>
    [Fact]
    public async Task CofreDoTimeSemChave_RECUSA_NuncaSorteia()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(store, Amk());
        var vault = new CredentialVault(
            new InMemoryCredentialStore(), ring, new InMemoryVaultAuditSink());

        var ex = await Assert.ThrowsAsync<VaultException>(() => vault.StoreAsync(
            new VaultStoreRequest
            {
                WorkspaceId = Workspace,
                CredentialId = "c1",
                ActorUserId = "operador",
            },
            "senha".AsMemory()));

        Assert.Contains("Aceite o convite", ex.Message, StringComparison.Ordinal);

        // E nada foi plantado no disco: recusar não pode deixar meia chave para trás.
        Assert.Null(await FindStoredBlobAsync(store));
    }

    /// <summary>
    /// <b>Só o "mint" faz a chave nascer — e ele NÃO está na interface.</b> Criar chave é o ato de
    /// FUNDAR o time, e só o fluxo de convite pode praticá-lo. O <c>CredentialVault</c> enxerga
    /// apenas <see cref="IWorkspaceKeyRing"/>, então nenhum caminho do cofre alcança a criação: o
    /// fail-closed deixa de depender de uma bandeira que alguém pode construir errado e passa a ser
    /// propriedade do TIPO.
    ///
    /// <para>Uma guarda com um caminho só é uma guarda que não pode ser desligada por engano.</para>
    /// </summary>
    [Fact]
    public async Task SoOMint_FazAChaveNascer_ENaoEstaNaInterface()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(store, Amk());

        // A interface do cofre não tem nenhum membro capaz de fazer uma chave nascer.
        Assert.DoesNotContain(
            typeof(IWorkspaceKeyRing).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            m => m.Name.Contains("Mint", StringComparison.OrdinalIgnoreCase));

        // Pela interface, o caminho de "GetOrCreate" só GETa: sem chave, recusa.
        IWorkspaceKeyRing peloCofre = ring;
        await Assert.ThrowsAsync<VaultException>(
            () => peloCofre.GetOrCreateWorkspaceKeyAsync(Workspace));
        Assert.Null(await FindStoredBlobAsync(store));

        // Pelo tipo concreto (o fluxo de convite), a chave nasce.
        using WorkspaceKey nascida = await ring.MintWorkspaceKeyAsync(Workspace);
        Assert.Equal(32, nascida.Key.Length);

        // E depois de nascida, o caminho do cofre passa a funcionar normalmente.
        using WorkspaceKey pelaInterface = await peloCofre.GetOrCreateWorkspaceKeyAsync(Workspace);
        Assert.Equal(nascida.Key.ToArray(), pelaInterface.Key.ToArray());
    }

    /// <summary>
    /// <b>Chave e marcador são gravados JUNTOS</b>, nos três caminhos em que a WK aterrissa: nascer
    /// (mint), chegar pelo convite (import) e voltar do servidor (restore).
    ///
    /// <para>Por que importa: o resolvedor de escopo do boot usa o marcador para responder
    /// "este workspace é de time?" mesmo quando a chave sumiu. Se o marcador morasse em outro lugar
    /// que não a gravação da chave, os dois divergiriam — e um cofre de time sem chave voltaria a
    /// ser lido como PESSOAL, que é justamente o caminho por onde o app começa a escrever no lugar
    /// errado sem avisar.</para>
    /// </summary>
    [Fact]
    public async Task ChaveEMarcador_SaoGravadosJuntos()
    {
        byte[] amk = Amk();

        // 1) Nascer.
        var storeMint = new InMemoryWorkspaceKeyStore();
        using (WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(storeMint, amk))
        {
            Assert.Null(await storeMint.LoadKeyRootingAsync(Workspace));
            (await ring.MintWorkspaceKeyAsync(Workspace)).Dispose();
            Assert.Equal(VaultKeyRooting.WkRandom, await storeMint.LoadKeyRootingAsync(Workspace));
        }

        // 2) Chegar pelo convite (import da WK crua).
        byte[] wkCrua = RandomNumberGenerator.GetBytes(32);
        var storeImport = new InMemoryWorkspaceKeyStore();
        byte[] embrulho;
        using (WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(storeImport, amk))
        {
            embrulho = await ring.ImportWorkspaceKeyAsync(Workspace, wkCrua);
            Assert.Equal(VaultKeyRooting.WkRandom, await storeImport.LoadKeyRootingAsync(Workspace));
        }

        // 3) Voltar do servidor (restore do embrulho guardado).
        var storeRestore = new InMemoryWorkspaceKeyStore();
        using (WkWorkspaceKeyRing ring = TeamKeyRingFactory.New(storeRestore, amk))
        {
            Assert.Null(await storeRestore.LoadKeyRootingAsync(Workspace));
            await ring.RestoreWrappedWorkspaceKeyAsync(Workspace, embrulho);
            Assert.Equal(VaultKeyRooting.WkRandom, await storeRestore.LoadKeyRootingAsync(Workspace));
        }
    }

    /// <summary>
    /// O <see cref="InMemoryWorkspaceKeyStore"/> não enumera; a chave de armazenamento é detalhe da
    /// implementação. Este helper procura pelas duas formas plausíveis para que o teste afirme o
    /// COMPORTAMENTO ("existe/não existe blob guardado") sem se prender ao nome da chave.
    /// </summary>
    private static async Task<byte[]?> FindStoredBlobAsync(IWorkspaceKeyStore store) =>
        await store.LoadAsync(Workspace) ?? await store.LoadAsync("wk:" + Workspace);
}
