using RemoteOps.Contracts.ExternalTools;

namespace RemoteOps.MikroTik;

// TODO: Implementar na frente feature/mikrotik-winbox (ver ADR-006 e docs/21).
// Valida hash do executável, monta argumentos de forma segura, audita sem logar senhas.
public interface IWinBoxRunner
{
    Task<string> LaunchAsync(ExternalToolLaunchRequest request, CancellationToken ct = default);
}
