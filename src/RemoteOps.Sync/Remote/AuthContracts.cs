namespace RemoteOps.Sync.Remote;

// Espelhos cliente-side das mensagens de auth do Cloud (RemoteOps.Cloud.Auth). Mantidos aqui
// para que RemoteOps.Sync não dependa do assembly do servidor; a forma JSON (camelCase) é a
// mesma validada pelos endpoints /auth/login e /auth/refresh.

public sealed record LoginRequest(string Email, string Password, string DeviceId, string DeviceName);

public sealed record LoginResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed record RefreshRequest(string RefreshToken, string DeviceId);

public sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
