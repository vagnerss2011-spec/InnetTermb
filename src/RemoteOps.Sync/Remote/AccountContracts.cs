using RemoteOps.Security.Account;

namespace RemoteOps.Sync.Remote;

// Espelhos cliente-side das mensagens de conta E2EE (spec §5), no mesmo espírito de AuthContracts:
// RemoteOps.Sync não depende do assembly do servidor; a forma JSON (camelCase, blobs em base64 —
// System.Text.Json serializa byte[] como base64) é o contrato.
//
// TODO(Fase1 T4/T5): os endpoints /auth/register, /auth/kdf e o /auth/login com authHash AINDA NÃO
// EXISTEM no backend (remoteops-cloud) — hoje o /auth/login recebe a SENHA (ver LoginRequest em
// AuthContracts.cs, pré-E2EE). Estes contratos seguem a spec §5 e são o alvo da T4; quando ela
// aterrissar, conferir campo a campo (nomes/casing/tipos) e remover este TODO. Nada aqui foi
// inventado além do que a spec define.

/// <summary>Workspace criado junto com a conta (o operador dá o nome no registro).</summary>
public sealed record FirstWorkspaceRequest(string Name);

/// <summary>Workspace que a conta enxerga (devolvido no registro/login).</summary>
public sealed record AccountWorkspace(string Id, string Name);

/// <summary>
/// <c>POST /auth/register</c>. TUDO aqui é público ou opaco (spec §4.2): salt/params do Argon2 são
/// públicos, os <c>Wrapped*</c> são a AMK cifrada (inúteis sem a senha/chave de recuperação) e o
/// <see cref="AuthHash"/> só prova a senha — é matematicamente incapaz de derivar a KEK
/// (domain-separation do HKDF). Senha, MasterKey, KEK e AMK NÃO aparecem neste tipo por construção.
/// </summary>
public sealed record RegisterAccountRequest(
    string Email,
    byte[] Argon2Salt,
    Argon2Params Argon2Params,
    byte[] AuthHash,
    byte[] WrappedAmkPwd,
    byte[] WrappedAmkRec,
    int AmkKeyVersion,
    FirstWorkspaceRequest FirstWorkspace);

public sealed record RegisterAccountResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<AccountWorkspace> Workspaces);

/// <summary>
/// <c>GET /auth/kdf?email=</c> — pré-login: o device precisa do salt/params pra re-derivar a
/// MasterKey da senha. Público por design (spec §4.2); o backend responde de forma uniforme pra
/// e-mail inexistente (anti-enumeração) e aplica rate-limit.
/// </summary>
public sealed record KdfResponse(byte[] Argon2Salt, Argon2Params Argon2Params);

/// <summary>
/// <c>POST /auth/login</c> na forma E2EE: manda o <see cref="AuthHash"/>, NUNCA a senha (o servidor
/// guarda PBKDF2 dele — nem o AuthHash cru).
/// </summary>
public sealed record E2eeLoginRequest(string Email, byte[] AuthHash, string DeviceId, string DeviceName);

/// <summary>
/// Resposta do login E2EE: além dos tokens, devolve o escrow por senha pra o device desembrulhar a
/// AMK LOCALMENTE (o servidor não participa disso — ele não tem a KEK).
/// </summary>
public sealed record E2eeLoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    byte[] WrappedAmkPwd,
    int AmkKeyVersion,
    IReadOnlyList<AccountWorkspace> Workspaces);
