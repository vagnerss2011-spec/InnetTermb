using System.Linq;
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
/// UM cofre, VÁRIAS raízes. O <see cref="RoutedWorkspaceKeyRing"/> responde "qual raiz serve este
/// workspace?" por PREFIXO do id — lei de nomenclatura, decidida sem I/O e idêntica em todo device —
/// e não por estado de sessão: uma resolução que variasse entre duas chamadas selaria com a chave de
/// uma raiz e o envelope só falharia ao ser aberto, meses depois.
///
/// <para><b>O que estes testes guardam:</b> que o CARIMBO de cada envelope sai da MESMA decisão que
/// entregou a CHAVE. Com o cofre atendendo duas raízes, um carimbo pedido em separado pode vir da
/// OUTRA raiz — e o erro não aparece no dia em que é cometido, e sim meses depois, no computador do
/// colega, como "a senha não abre".</para>
/// </summary>
public sealed class RoutedWorkspaceKeyRingTests
{
    private const string TeamPrefix = "time:";
    private const string TeamWorkspace = TeamPrefix + "8f3b6f4a-0000-4000-8000-000000000001";

    private static byte[] Amk() => RandomNumberGenerator.GetBytes(32);

    private static VaultStoreRequest Req(string workspaceId, string credentialId) => new()
    {
        WorkspaceId = workspaceId,
        CredentialId = credentialId,
        ActorUserId = "operador",
    };

    /// <summary>
    /// <b>A montagem de produção, inteira.</b> Um <c>CredentialVault</c> só, atendendo:
    /// <c>ws-local</c> (credenciais do operador) e <c>local</c> (chave do banco SQLCipher) pela raiz
    /// da AMK, e <c>time:{W}</c> pela raiz da WK. Cada envelope sai carimbado com a raiz que
    /// realmente entregou a chave — e é isto que fica vermelho se alguém devolver o carimbo para o
    /// chaveiro (que, atendendo duas raízes, não tem UM algoritmo para declarar).
    ///
    /// <para>A consequência que é o núcleo do desenho: a chave do banco e os tokens NUNCA ganham o
    /// prefixo <c>time:</c>, então nunca são procurados no cofre do time. Era essa a objeção do 1d
    /// contra trocar a raiz do cofre inteiro — e o roteamento a dissolve.</para>
    /// </summary>
    [Fact]
    public async Task Roteador_CarimbaCadaWorkspaceComARaizQueDevolveuAChave()
    {
        byte[] amk = Amk();
        var keyStore = new InMemoryWorkspaceKeyStore();
        using var amkRing = new AmkWorkspaceKeyRing(amk);
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(keyStore, amk);

        // A WK precisa NASCER pelo caminho do convite (fundar o time). O cofre não alcança isto.
        (await teamRing.MintWorkspaceKeyAsync(TeamWorkspace)).Dispose();

        var routed = new RoutedWorkspaceKeyRing(amkRing, [(TeamPrefix, teamRing)]);
        var vault = new CredentialVault(
            new InMemoryCredentialStore(), routed, new InMemoryVaultAuditSink());

        SecretEnvelope credenciais = await vault.StoreAsync(Req("ws-local", "c1"), "senha-do-host".AsMemory());
        SecretEnvelope chaveDoBanco = await vault.StoreAsync(Req("local", "c2"), "hex-da-chave".AsMemory());
        SecretEnvelope doTime = await vault.StoreAsync(Req(TeamWorkspace, "c3"), "senha-do-cliente".AsMemory());

        Assert.Equal(VaultAlgorithms.AmkRootedV1, credenciais.Algorithm);
        Assert.Equal(VaultAlgorithms.AmkRootedV1, chaveDoBanco.Algorithm);
        Assert.Equal(VaultAlgorithms.WkRootedV1, doTime.Algorithm);

        // E os três ABREM pelo mesmo cofre: carimbo certo e chave certa andando juntos.
        var ctx = new VaultAccessContext { ActorUserId = "operador" };
        using (VaultSecret s = await vault.RetrieveAsync(credenciais.EnvelopeId, ctx))
        {
            Assert.Equal("senha-do-host", s.RevealString());
        }

        using (VaultSecret s = await vault.RetrieveAsync(chaveDoBanco.EnvelopeId, ctx))
        {
            Assert.Equal("hex-da-chave", s.RevealString());
        }

        using (VaultSecret s = await vault.RetrieveAsync(doTime.EnvelopeId, ctx))
        {
            Assert.Equal("senha-do-cliente", s.RevealString());
        }
    }

    /// <summary>
    /// <b>Chave e carimbo saem da MESMA instância.</b> Aqui o roteamento está INVERTIDO de propósito
    /// — o padrão é a raiz do time e a rota é a da AMK. Um carimbo que viesse do chaveiro (uma
    /// constante por objeto), do padrão, ou de uma segunda consulta ao roteador erraria em um dos
    /// dois workspaces; só o carimbo que viaja preso ao material acerta nos dois.
    ///
    /// <para>Reforço estrutural, no mesmo teste: a interface que o <c>CredentialVault</c> enxerga NÃO
    /// tem nenhum membro que responda "qual é o algoritmo". Enquanto isso valer, não existe segunda
    /// fonte da verdade para o carimbo divergir da chave — a impossibilidade é do TIPO, e não uma
    /// concordância feliz entre duas consultas que hoje batem.</para>
    /// </summary>
    [Fact]
    public async Task ChaveECarimbo_SaemDaMESMAInstancia()
    {
        byte[] amk = Amk();
        var keyStore = new InMemoryWorkspaceKeyStore();
        using var amkRing = new AmkWorkspaceKeyRing(amk);
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(keyStore, amk);
        (await teamRing.MintWorkspaceKeyAsync("qualquer-time")).Dispose();

        // INVERTIDO: padrão = WK, rota = AMK.
        var routed = new RoutedWorkspaceKeyRing(teamRing, [("pessoal:", amkRing)]);
        var vault = new CredentialVault(
            new InMemoryCredentialStore(), routed, new InMemoryVaultAuditSink());

        SecretEnvelope peloPadrao = await vault.StoreAsync(Req("qualquer-time", "c1"), "a".AsMemory());
        SecretEnvelope pelaRota = await vault.StoreAsync(Req("pessoal:ws-local", "c2"), "b".AsMemory());

        Assert.Equal(VaultAlgorithms.WkRootedV1, peloPadrao.Algorithm);
        Assert.Equal(VaultAlgorithms.AmkRootedV1, pelaRota.Algorithm);

        // A interface do cofre não tem de onde tirar um carimbo que não seja a própria chave.
        Assert.DoesNotContain(
            typeof(IWorkspaceKeyRing).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            m => m.Name.Contains("Algorithm", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sem rota que case, vale o PADRÃO. É o que mantém o boot pessoal byte a byte igual ao de hoje:
    /// nenhum id do app (<c>ws-local</c>, <c>local</c>, o GUID dos tokens) começa com <c>time:</c>,
    /// então nada muda de caminho por causa desta fatia.
    /// </summary>
    [Fact]
    public async Task SemRotaQueCase_ValeOPadrao()
    {
        byte[] amk = Amk();
        using var amkRing = new AmkWorkspaceKeyRing(amk);
        using WkWorkspaceKeyRing teamRing = TeamKeyRingFactory.New(amk);
        var routed = new RoutedWorkspaceKeyRing(amkRing, [(TeamPrefix, teamRing)]);

        using WorkspaceKey pelaRaizPadrao = await routed.GetOrCreateWorkspaceKeyAsync("local");
        Assert.Equal(
            AmkKeyDerivation.DeriveWorkspaceKey(amk, "local"), pelaRaizPadrao.Key.ToArray());
    }

    /// <summary>
    /// O roteador não é dono dos anéis: quem os construiu (o <c>VaultRootActivator</c>) é quem os
    /// descarta. Um <c>IDisposable</c> aqui faria o cofre zerar a AMK de um anel que o fluxo de
    /// convite ainda usa — e a falha apareceria só na próxima operação, sem relação aparente.
    /// </summary>
    [Fact]
    public void Roteador_NaoEhDonoDosAneis()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(RoutedWorkspaceKeyRing)));
    }
}
