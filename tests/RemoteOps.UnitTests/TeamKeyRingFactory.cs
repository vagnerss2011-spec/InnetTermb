using RemoteOps.Security.Account;
using RemoteOps.Security.Storage;

namespace RemoteOps.UnitTests;

/// <summary>
/// Monta o chaveiro do time <b>como a produção monta</b>: um store só servindo as DUAS portas — a da
/// chave (<see cref="IWorkspaceKeyStore"/>) e a do marcador de raiz (<see cref="IVaultRootingStore"/>),
/// exatamente como o <c>FileVaultStore</c> faz no app.
///
/// <para><b>Por que uma fábrica e não um <c>new</c> em cada teste:</b> chave e marcador precisam
/// aterrissar no MESMO lugar. Um teste que os separasse estaria exercitando uma montagem que a
/// produção não tem — e passaria a "provar" um comportamento que ninguém vive.</para>
/// </summary>
internal static class TeamKeyRingFactory
{
    internal static WkWorkspaceKeyRing New(ReadOnlySpan<byte> amk) =>
        New(new InMemoryWorkspaceKeyStore(), amk);

    internal static WkWorkspaceKeyRing New(InMemoryWorkspaceKeyStore store, ReadOnlySpan<byte> amk) =>
        new(store, store, amk);
}
