using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Storage;

/// <summary>
/// Onde fica registrado <b>em que raiz</b> o cofre de cada workspace está — a "versão de key-rooting".
///
/// <para><b>Por que é uma interface própria, separada da migração:</b> quem escreve o marcador não é
/// só o migrador. A raiz do TIME grava <see cref="VaultKeyRooting.WkRandom"/> na MESMA operação em
/// que a WK aterrissa (nascer, chegar pelo convite ou voltar do servidor) — e é justamente essa
/// coincidência de lugar que impede chave e marcador de divergirem. Obrigar a raiz do time a
/// depender do contrato inteiro da migração (backup, enumeração de envelopes) só para gravar um
/// inteiro seria acoplamento sem contrapartida.</para>
///
/// <para>Quem lê o marcador é o resolvedor de escopo do boot: ele precisa responder "este workspace
/// é de time?" mesmo quando a chave NÃO está aqui — e nesse caso a resposta certa é recusar alto, e
/// não silenciosamente cair no cofre pessoal.</para>
/// </summary>
public interface IVaultRootingStore
{
    /// <summary>Raiz registrada para o workspace, ou <c>null</c> se nunca foi registrada.</summary>
    Task<VaultKeyRooting?> LoadKeyRootingAsync(string workspaceId, CancellationToken ct = default);

    Task SaveKeyRootingAsync(string workspaceId, VaultKeyRooting rooting, CancellationToken ct = default);
}
