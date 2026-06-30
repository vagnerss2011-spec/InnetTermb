namespace RemoteOps.Sync.Remote;

/// <summary>
/// Conjunto de tokens emitidos pelo Cloud para um device. Nunca é logado nem persistido
/// em texto puro fora do vault (ADR-003/ADR-013).
/// </summary>
public sealed record TokenSet(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
