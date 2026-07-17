namespace RemoteOps.Sync.Remote;

// Espelhos cliente-side das mensagens de auth do Cloud (RemoteOps.Cloud.Auth). Mantidos aqui
// para que RemoteOps.Sync não dependa do assembly do servidor; a forma JSON (camelCase) é a
// mesma validada pelo endpoint /auth/refresh.
//
// O LoginRequest/LoginResponse que mandava SENHA saiu na Fase 1 (E2EE): a senha do operador não
// pode chegar ao servidor nem por engano, e o backend só a aceitaria para contas legadas
// (pré-escrow), que a Fase 1 não cria mais. O login do cliente é o E2eeLoginRequest (authHash) em
// AccountContracts.cs — deixar o caminho antigo compilando seria manter viva a única porta pela
// qual a senha sairia deste processo.

public sealed record RefreshRequest(string RefreshToken, string DeviceId);

public sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
