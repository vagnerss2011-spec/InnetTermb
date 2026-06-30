namespace RemoteOps.Cloud.Auth;

public sealed record LoginRequest(string Email, string Password, string DeviceId, string DeviceName);

public sealed record LoginResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public sealed record RefreshRequest(string RefreshToken, string DeviceId);

public sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
