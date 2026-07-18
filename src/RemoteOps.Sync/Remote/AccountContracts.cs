using RemoteOps.Security.Account;

namespace RemoteOps.Sync.Remote;

// Espelhos cliente-side das mensagens de conta E2EE (spec §5), no mesmo espírito de AuthContracts:
// RemoteOps.Sync não depende do assembly do servidor; a forma JSON (camelCase, blobs em base64 —
// System.Text.Json serializa byte[] como base64) é o contrato.
//
// CONFERIDO CAMPO A CAMPO contra o backend real da T4 (remoteops-cloud@a94fb1e,
// Auth/AuthModels.cs + Auth/AccountService.cs). O contrato está fixado por
// AccountContractsWireTests, que serializa estes tipos e desserializa nos do servidor — é lá que um
// drift futuro aparece, e não num 400 em campo.
//
// Por que byte[] aqui e string (base64) lá: os dois produzem a MESMA string base64 no fio
// (System.Text.Json usa Convert.ToBase64String — canônico, com padding), então o tipo forte fica do
// lado que faz cripto e o servidor segue tratando tudo como blob opaco. Isso importa além da
// estética: o backend valida o authHash pelo PBKDF2 da STRING base64 recebida, então o encoding
// precisa ser idêntico no registro e no login (provado em AuthHash_HasIdenticalBase64_InRegisterAndLogin).

/// <summary>
/// Workspace que a conta enxerga (devolvido no registro/login). Espelha o <c>WorkspaceSummary</c> do
/// backend — inclusive o <see cref="Role"/> (RBAC), que diz se esta conta é dona do workspace.
/// </summary>
public sealed record AccountWorkspace(string Id, string Name, string Role);

/// <summary>
/// <c>POST /auth/register</c>. TUDO aqui é público ou opaco (spec §4.2): salt/params do Argon2 são
/// públicos, os <c>Wrapped*</c> são a AMK cifrada (inúteis sem a senha/chave de recuperação) e o
/// <see cref="AuthHash"/> só prova a senha — é matematicamente incapaz de derivar a KEK
/// (domain-separation do HKDF). Senha, MasterKey, KEK e AMK NÃO aparecem neste tipo por construção.
///
/// <para>O registro já identifica o device (<see cref="DeviceId"/>/<see cref="DeviceName"/>): o
/// backend cria a conta E EMITE a sessão na mesma chamada, e um refresh token só existe amarrado a
/// um device.</para>
/// </summary>
public sealed record RegisterAccountRequest(
    string Email,
    byte[] Argon2Salt,
    Argon2Params Argon2Params,
    byte[] AuthHash,
    byte[] WrappedAmkPwd,
    byte[] WrappedAmkRec,
    int AmkKeyVersion,
    string DeviceId,
    string DeviceName,
    string WorkspaceName);

/// <summary>
/// Resposta do registro: tokens + o workspace recém-criado. Os campos de escrow vêm preenchidos
/// (o backend reusa o mesmo emissor de sessão do login), mas o device que acabou de registrar já
/// tem a AMK em mãos — quem precisa deles é o login noutro device.
/// </summary>
public sealed record RegisterAccountResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string WorkspaceId,
    byte[]? WrappedAmkPwd = null,
    int? AmkKeyVersion = null,
    IReadOnlyList<AccountWorkspace>? Workspaces = null);

/// <summary>
/// <c>GET /auth/kdf?email=</c> — pré-login: o device precisa do salt/params pra re-derivar a
/// MasterKey da senha. Público por design (spec §4.2); o backend responde de forma uniforme pra
/// e-mail inexistente (params decoy determinísticos, anti-enumeração) e aplica rate-limit.
/// </summary>
public sealed record KdfResponse(byte[] Argon2Salt, Argon2Params Argon2Params);

/// <summary>
/// <c>POST /auth/login</c> na forma E2EE: manda o <see cref="AuthHash"/>, NUNCA a senha (o servidor
/// guarda PBKDF2 dele — nem o AuthHash cru). O backend aceita exatamente UM entre <c>authHash</c> e
/// <c>password</c>; este tipo não tem o campo <c>password</c>, então o servidor sempre entra no ramo
/// E2EE — o caminho legado é inalcançável a partir do cliente por construção.
///
/// <para><see cref="TotpCode"/> só é preenchido no RE-envio, depois de o backend responder
/// <c>mfa_required</c> (conta com 2FA ativa). Fica nulo no 1º envio — o servidor só o exige quando a
/// conta tem 2FA e o AuthHash já validou.</para>
/// </summary>
public sealed record E2eeLoginRequest(
    string Email, byte[] AuthHash, string DeviceId, string DeviceName, string? TotpCode = null);

// ── Recuperação de senha por email (spec Fase 4) ────────────────────────────────────────

/// <summary><c>POST /auth/password/forgot</c>: dispara o email. Resposta sempre 202 (anti-enumeração).</summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>
/// <c>POST /auth/password/reset-context</c>: troca o token do email pelo escrow de recuperação
/// (<c>wrappedAmkRec</c>), pra o cliente abrir a AMK com a chave de recuperação. O blob é opaco.
/// </summary>
public sealed record ResetContextRequest(string Token);

/// <summary>Escrow de recuperação (base64 no fio → byte[] aqui) devolvido a um token de reset válido.</summary>
public sealed record ResetContextResponse(byte[] WrappedAmkRec);

/// <summary>
/// <c>POST /auth/password/reset</c>: conclui o reset. Como o registro, TUDO aqui é público ou opaco —
/// o cliente já abriu a AMK com a chave de recuperação e a re-embrulhou sob a senha nova. O token do
/// email é a autorização (no lugar do AuthHash antigo). A AMK não muda.
/// </summary>
public sealed record ResetPasswordRequest(
    string Token,
    byte[] NewAuthHash,
    byte[] NewArgon2Salt,
    Argon2Params NewArgon2Params,
    byte[] NewWrappedAmkPwd);

// ── 2FA / TOTP (spec Fase 3) — espelhos das mensagens de MFA do backend ─────────────────

/// <summary>
/// Resposta de <c>POST /auth/mfa/enroll</c> (autenticado): o segredo em Base32 (pra digitar no app) +
/// o <c>otpauth://</c> URI (pra montar o QR). O 2FA NÃO fica ativo até o <c>confirm</c>.
/// </summary>
public sealed record MfaEnrollResponse(string SecretBase32, string OtpauthUri);

/// <summary><c>POST /auth/mfa/confirm</c> (autenticado): ativa o 2FA com um código TOTP válido.</summary>
public sealed record MfaConfirmRequest(string Code);

/// <summary><c>POST /auth/mfa/disable</c> (autenticado): desliga o 2FA — exige um código TOTP válido.</summary>
public sealed record MfaDisableRequest(string Code);

/// <summary>
/// Resposta do login E2EE: além dos tokens, devolve o escrow por senha pra o device desembrulhar a
/// AMK LOCALMENTE (o servidor não participa disso — ele não tem a KEK).
///
/// <para>Os campos E2EE são NULÁVEIS porque o backend os devolve nulos para contas LEGADAS (criadas
/// antes da Fase 1, sem escrow). Modelá-los como não-nuláveis fazia a desserialização de
/// <c>amkKeyVersion: null</c> estourar um JsonException cru na cara do operador; agora o parser
/// aceita, e quem rejeita a sessão com uma mensagem em pt-BR é o E2eeAccountAuthenticator.</para>
/// </summary>
public sealed record E2eeLoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    byte[]? WrappedAmkPwd = null,
    int? AmkKeyVersion = null,
    IReadOnlyList<AccountWorkspace>? Workspaces = null);
