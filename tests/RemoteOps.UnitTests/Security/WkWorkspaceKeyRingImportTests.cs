using System;
using System.Security.Cryptography;
using System.Threading.Tasks;

using RemoteOps.Security.Account;
using RemoteOps.Security.Audit;
using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

using Xunit;

namespace RemoteOps.UnitTests.Security;

/// <summary>
/// O buraco que o estágio 1b deixou de propósito para o 1d: o ring SORTEIA, mas não sabia RECEBER
/// uma WK de fora. É por aqui que a chave do time entra na máquina do convidado.
///
/// <para><b>O risco número um da fatia</b> mora neste arquivo: o <c>CredentialVault</c> chama
/// <c>GetOrCreateWorkspaceKeyAsync</c> em TODA operação. Se qualquer coisa tocar o cofre do time
/// ANTES de a WK do convite ser importada, o ring sorteia uma chave aleatória e o cofre BIFURCA em
/// silêncio — o convidado cadastra senhas que ninguém do time abre, e ninguém descobre por semanas.
/// Por isso o cofre do time roda com criação NEGADA: sem WK importada ele recusa alto.</para>
/// </summary>
public sealed class WkWorkspaceKeyRingImportTests
{
    private const string TeamWorkspace = "team:9f1c";

    private static byte[] Amk() => RandomNumberGenerator.GetBytes(32);

    private static byte[] Wk() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Importada, a WK vira a chave daquele workspace — os mesmos bytes, não um sorteio.</summary>
    [Fact]
    public async Task WkImportada_EhAChaveDoWorkspace()
    {
        byte[] wk = Wk();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        await ring.ImportWorkspaceKeyAsync(TeamWorkspace, wk);

        using WorkspaceKey? lida = await ring.TryGetWorkspaceKeyAsync(TeamWorkspace);
        Assert.NotNull(lida);
        Assert.Equal(wk, lida.Key.ToArray());
    }

    /// <summary>
    /// A importação devolve o embrulho sob a AMK — que é EXATAMENTE o <c>WrappedWk</c> que sobe para
    /// a membership. Devolver daqui (em vez de o chamador montar) mantém UMA definição do AAD: uma
    /// constante de cripto copiada é o tipo de coisa que diverge em silêncio e só aparece como
    /// "o cofre não abre" no PC do colega.
    /// </summary>
    [Fact]
    public async Task ImportacaoDevolveOEmbrulhoQueSobeParaAMembership()
    {
        byte[] amk = Amk();
        byte[] wk = Wk();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk);

        byte[] wrapped = await ring.ImportWorkspaceKeyAsync(TeamWorkspace, wk);

        Assert.NotEmpty(wrapped);
        Assert.NotEqual(wk, wrapped);

        // O SEGUNDO device: mesma conta (mesma AMK), disco vazio, só o blob que veio do servidor.
        using var outroDevice = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk);
        await outroDevice.RestoreWrappedWorkspaceKeyAsync(TeamWorkspace, wrapped);

        using WorkspaceKey? restaurada = await outroDevice.TryGetWorkspaceKeyAsync(TeamWorkspace);
        Assert.NotNull(restaurada);
        Assert.Equal(wk, restaurada.Key.ToArray());
    }

    /// <summary>
    /// Reimportar a MESMA WK é no-op (o aceite pode ser repetido depois de uma queda de rede). Já
    /// importar OUTRA por cima tem de estourar: seria trocar a chave por baixo de segredos já
    /// selados, que é perda de cofre disfarçada de sucesso.
    /// </summary>
    [Fact]
    public async Task Reimportar_AMesmaEhNoOp_OutraEstoura()
    {
        byte[] wk = Wk();
        using var ring = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        byte[] primeiro = await ring.ImportWorkspaceKeyAsync(TeamWorkspace, wk);
        byte[] segundo = await ring.ImportWorkspaceKeyAsync(TeamWorkspace, wk);
        Assert.NotEmpty(segundo);

        // O embrulho pode ter nonce novo, mas a chave guardada continua sendo a mesma.
        Assert.NotEmpty(primeiro);
        using WorkspaceKey? lida = await ring.TryGetWorkspaceKeyAsync(TeamWorkspace);
        Assert.Equal(wk, lida!.Key.ToArray());

        await Assert.ThrowsAsync<VaultException>(
            () => ring.ImportWorkspaceKeyAsync(TeamWorkspace, Wk()));
    }

    /// <summary>
    /// <b>Fail-closed.</b> No cofre do TIME o ring não pode sortear: quem não tem a WK do convite
    /// ainda não é do time. Recusar alto é a única saída que o operador enxerga — sortear produziria
    /// senhas que ninguém abre.
    /// </summary>
    [Fact]
    public async Task CofreDoTime_SemWkImportada_RecusaAlto_EmVezDeSortear()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(store, Amk(), allowKeyCreation: false);

        var erro = await Assert.ThrowsAsync<VaultException>(
            () => ring.GetOrCreateWorkspaceKeyAsync(TeamWorkspace));

        Assert.Contains("convite", erro.Message, StringComparison.OrdinalIgnoreCase);

        // E nada foi gravado: uma WK sorteada "só desta vez" é o começo da bifurcação.
        Assert.Null(await store.LoadAsync("wk:" + TeamWorkspace));
        Assert.Null(await ring.TryGetWorkspaceKeyAsync(TeamWorkspace));
    }

    /// <summary>
    /// <b>A guarda não é vácua.</b> O MESMO cenário com criação permitida — que é o comportamento
    /// que existiria sem o fail-closed — sorteia uma WK, grava e devolve como se nada houvesse: sem
    /// exceção, sem log, sem nada na tela. Este teste fixa a diferença entre os dois modos para que
    /// "simplificar" o parâmetro um dia fique vermelho aqui, e não em campo, semanas depois, na
    /// forma de senhas que ninguém do time consegue abrir.
    /// </summary>
    [Fact]
    public async Task ComCriacaoPermitida_OMesmoCaminho_SORTEIA_EmSilencio()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(store, Amk(), allowKeyCreation: true);

        using WorkspaceKey sorteada = await ring.GetOrCreateWorkspaceKeyAsync(TeamWorkspace);

        Assert.Equal(32, sorteada.Key.Length);
        Assert.NotEqual(new byte[32], sorteada.Key.ToArray());
        Assert.NotNull(await store.LoadAsync("wk:" + TeamWorkspace));
    }

    /// <summary>
    /// A ORDEM ERRADA não pode passar despercebida: o <see cref="CredentialVault"/> tocando o cofre
    /// do time ANTES da importação tem de FALHAR, e não gravar um segredo sob uma chave aleatória.
    /// Depois da importação, a mesma operação funciona — e o segredo abre com a WK do time.
    /// </summary>
    [Fact]
    public async Task OrdemErrada_CofreAntesDaImportacao_Falha_EDepoisDaImportacaoFunciona()
    {
        byte[] wkDoTime = Wk();
        var store = new InMemoryCredentialStore();
        var keyStore = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(keyStore, Amk(), allowKeyCreation: false);
        var vault = new CredentialVault(store, ring, NullVaultAuditSink.Instance);

        var request = new VaultStoreRequest
        {
            WorkspaceId = TeamWorkspace,
            CredentialId = "cred-1",
            Type = "password",
            ActorUserId = "convidado",
        };

        await Assert.ThrowsAsync<VaultException>(
            () => vault.StoreAsync(request, "senha-do-time".AsMemory()));

        // Nada de chave sorteada por baixo do pano: é ISSO que faria o cofre bifurcar em silêncio.
        Assert.Null(await keyStore.LoadAsync("wk:" + TeamWorkspace));

        await ring.ImportWorkspaceKeyAsync(TeamWorkspace, wkDoTime);

        SecretEnvelope envelope = await vault.StoreAsync(request, "senha-do-time".AsMemory());
        Assert.Equal(VaultAlgorithms.WkRootedV1, envelope.Algorithm);

        // A prova de que a chave usada foi a do TIME (e não um sorteio): outro device, outra conta,
        // com a MESMA WK importada, abre o envelope.
        var outroStore = new InMemoryCredentialStore();
        await outroStore.SaveAsync(envelope);
        using var outroRing = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk(), allowKeyCreation: false);
        await outroRing.ImportWorkspaceKeyAsync(TeamWorkspace, wkDoTime);
        var outroVault = new CredentialVault(outroStore, outroRing, NullVaultAuditSink.Instance);

        using VaultSecret aberto = await outroVault.RetrieveAsync(
            envelope.EnvelopeId, new VaultAccessContext { ActorUserId = "colega" });
        Assert.Equal("senha-do-time", aberto.RevealString());
    }

    /// <summary>
    /// A WK importada persiste EMBRULHADA: o que vai para o disco não é a chave do time. Sem isto, o
    /// notebook do colega carrega a chave do cofre inteiro em claro.
    /// </summary>
    [Fact]
    public async Task WkImportada_VaiEmbrulhadaParaODisco()
    {
        byte[] wk = Wk();
        var store = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(store, Amk());

        await ring.ImportWorkspaceKeyAsync(TeamWorkspace, wk);

        byte[]? blob = await store.LoadAsync("wk:" + TeamWorkspace);
        Assert.NotNull(blob);
        Assert.NotEqual(wk, blob);
    }

    /// <summary>
    /// O ring entrega o embrulho <b>como está no disco</b> — é ele que sobe no
    /// <c>PUT /workspaces/{id}/key</c>. Sem chave guardada, devolve <c>null</c> (nada a publicar), e
    /// o que ele devolve tem de ser o MESMO blob byte a byte: é a igualdade de bytes que faz o
    /// servidor reconhecer uma republicação como no-op em vez de conflito.
    /// </summary>
    [Fact]
    public async Task EmbrulhoGuardado_SaiComoEsta_OuNuloQuandoNaoHa()
    {
        var store = new InMemoryWorkspaceKeyStore();
        using var ring = new WkWorkspaceKeyRing(store, Amk());

        Assert.Null(await ring.TryGetWrappedWorkspaceKeyAsync(TeamWorkspace));

        byte[] wrapped = await ring.ImportWorkspaceKeyAsync(TeamWorkspace, Wk());

        Assert.Equal(wrapped, await ring.TryGetWrappedWorkspaceKeyAsync(TeamWorkspace));
        Assert.Equal(await store.LoadAsync("wk:" + TeamWorkspace), await ring.TryGetWrappedWorkspaceKeyAsync(TeamWorkspace));
    }

    /// <summary>
    /// <b>Restaurar guarda o blob DO SERVIDOR, byte a byte</b> — e não um re-embrulho com nonce novo.
    /// Não é economia de CPU: é o que faz a republicação daquele device ser reconhecida como
    /// idempotente. Re-embrulhando, o disco divergiria do servidor no primeiro boot e todo reparo
    /// seguinte bateria em 409 — um alarme de bifurcação disparando por rotina, que é o jeito mais
    /// rápido de ensinar o operador a ignorar o alarme de verdade.
    /// </summary>
    [Fact]
    public async Task Restaurar_GuardaOBlobDoServidorInalterado()
    {
        byte[] amk = Amk();
        byte[] wk = Wk();
        using var origem = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk);
        byte[] doServidor = await origem.ImportWorkspaceKeyAsync(TeamWorkspace, wk);

        using var outroDevice = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), amk, allowKeyCreation: false);
        await outroDevice.RestoreWrappedWorkspaceKeyAsync(TeamWorkspace, doServidor);

        Assert.Equal(doServidor, await outroDevice.TryGetWrappedWorkspaceKeyAsync(TeamWorkspace));

        using WorkspaceKey? restaurada = await outroDevice.TryGetWorkspaceKeyAsync(TeamWorkspace);
        Assert.Equal(wk, restaurada!.Key.ToArray());
    }

    /// <summary>Blob de outra conta não restaura: a AMK errada faz o tag GCM falhar, alto.</summary>
    [Fact]
    public async Task RestaurarBlobDeOutraConta_Estoura()
    {
        using var dona = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());
        byte[] wrapped = await dona.ImportWorkspaceKeyAsync(TeamWorkspace, Wk());

        using var intrusa = new WkWorkspaceKeyRing(new InMemoryWorkspaceKeyStore(), Amk());

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => intrusa.RestoreWrappedWorkspaceKeyAsync(TeamWorkspace, wrapped));
    }
}
