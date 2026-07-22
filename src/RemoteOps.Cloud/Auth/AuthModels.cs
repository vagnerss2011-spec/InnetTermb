namespace RemoteOps.Cloud.Auth;

/// <summary>
/// Parâmetros públicos do Argon2id. O servidor só os transporta — quem deriva a
/// MasterKey é o device (spec §4.1).
/// </summary>
public sealed record Argon2Params(int MemoryKib, int Iterations, int Parallelism, int OutputBytes);

/// <summary>
/// Login. Contas E2EE mandam <see cref="AuthHash"/>; contas legadas mandam
/// <see cref="Password"/>. Exatamente um dos dois — nunca os dois.
/// </summary>
public sealed record LoginRequest(string Email, string? Password, string DeviceId, string DeviceName)
{
    /// <summary>Prova de senha derivada no device (base64). O servidor nunca recebe a senha.</summary>
    public string? AuthHash { get; init; }

    /// <summary>
    /// Código TOTP de 6 dígitos. Só exigido quando a conta tem 2FA ativa (<c>MfaRequired</c>). É
    /// PEDIDO só DEPOIS de o AuthHash validar (ver TokenService) — não vira oráculo de enumeração.
    /// </summary>
    public string? TotpCode { get; init; }
}

/// <summary>Resultado do login: sucesso (com payload), credencial inválida, ou 2FA pendente.</summary>
public enum LoginOutcome
{
    Success,
    InvalidCredentials,

    /// <summary>Senha OK, mas falta (ou está errado) o código TOTP — a UI deve pedir o código.</summary>
    MfaRequired,
}

/// <summary>
/// Envelope do login. <see cref="LoginOutcome.MfaRequired"/> é DISTINTO de
/// <see cref="LoginOutcome.InvalidCredentials"/> de propósito: só a UI que já provou a senha recebe o
/// sinal de pedir o TOTP. As duas viram 401 no endpoint, mas com corpo estruturado diferente.
/// </summary>
public sealed record LoginResult(LoginOutcome Outcome, LoginResponse? Response)
{
    public static readonly LoginResult InvalidCredentials = new(LoginOutcome.InvalidCredentials, null);
    public static readonly LoginResult MfaChallenge = new(LoginOutcome.MfaRequired, null);
    public static LoginResult Success(LoginResponse response) => new(LoginOutcome.Success, response);
}

/// <summary>
/// Resposta de login. Os campos E2EE vêm nulos para contas legadas — por isso são
/// opcionais, e o construtor de 3 argumentos continua válido.
/// </summary>
public sealed record LoginResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string? WrappedAmkPwd = null,
    int? AmkKeyVersion = null,
    IReadOnlyList<WorkspaceSummary>? Workspaces = null);

/// <summary>
/// Um workspace que a conta enxerga, na resposta de login/registro.
/// </summary>
/// <param name="Kind">
/// ⚠️ <b><c>workspaces.kind</c> (<see cref="Data.Entities.WorkspaceKinds"/>) VIAJANDO até o
/// cliente.</b> A coluna existe desde o G1 e era usada só aqui dentro; o cliente não a recebia e,
/// sem ela, classificava o workspace ativo pela AUSÊNCIA de embrulho de chave — um 404 de
/// <c>GET /workspaces/{id}/key</c>, que significa "esta CONTA não guarda embrulho aqui" e é
/// indistinguível de um 404 de infraestrutura. Lido como "não é time", ele fazia o banco local com
/// os equipamentos do operador ser adotado pelo workspace do TIME.
///
/// <para><b>Obrigatório de propósito</b> (sem valor default): o servidor SEMPRE sabe. Um default
/// aqui seria herdado em silêncio por qualquer construção nova, e o valor errado nesta linha é a
/// decisão de cofre do cliente. Quem constrói tem de DIZER.</para>
/// </param>
public sealed record WorkspaceSummary(string Id, string Name, string Role, string Kind);

public sealed record RefreshRequest(string RefreshToken, string DeviceId);

public sealed record RefreshResponse(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Registro E2EE. Todo material sensível já chega embrulhado: o servidor recebe
/// AuthHash (prova, não senha) e os dois escrows opacos da AMK.
/// </summary>
public sealed record RegisterRequest(
    string Email,
    string Argon2Salt,
    Argon2Params Argon2Params,
    string AuthHash,
    string WrappedAmkPwd,
    string WrappedAmkRec,
    int AmkKeyVersion,
    string DeviceId,
    string DeviceName,
    string WorkspaceName);

/// <summary>Mesmo formato do login + o workspace recém-criado.</summary>
public sealed record RegisterResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string WorkspaceId,
    string? WrappedAmkPwd = null,
    int? AmkKeyVersion = null,
    IReadOnlyList<WorkspaceSummary>? Workspaces = null);

/// <summary>Params públicos de KDF para o device derivar a MasterKey antes do login.</summary>
public sealed record KdfResponse(string Argon2Salt, Argon2Params Argon2Params);

// ── 2FA / TOTP (spec Fase 3) ────────────────────────────────────────────────

/// <summary>
/// Resposta do <c>/auth/mfa/enroll</c>: o segredo em Base32 (pra digitar) + o otpauth URI (pro QR).
/// O 2FA ainda NÃO está ativo aqui — só depois do <c>/auth/mfa/confirm</c>.
/// </summary>
public sealed record MfaEnrollResponse(string SecretBase32, string OtpauthUri);

/// <summary>Confirma o enroll (ativa o 2FA) com um código TOTP válido do app do usuário.</summary>
public sealed record MfaConfirmRequest(string Code);

/// <summary>Desliga o 2FA — exige um código TOTP válido (não basta estar logado).</summary>
public sealed record MfaDisableRequest(string Code);

/// <summary>
/// Troca de senha: re-embrulha a AMK sob a nova KEK. A AMK não muda, então os
/// segredos do cofre continuam decifráveis e o escrow de recuperação segue válido.
/// </summary>
public sealed record ChangePasswordRequest(
    string OldAuthHash,
    string NewAuthHash,
    string NewArgon2Salt,
    Argon2Params NewArgon2Params,
    string NewWrappedAmkPwd);

// ── Recuperação de senha por email (spec Fase 4) ────────────────────────────

/// <summary>
/// "Esqueci a senha": dispara o email de recuperação. A resposta é SEMPRE 202, exista ou não a conta
/// (anti-enumeração) — este record não carrega nada de volta.
/// </summary>
public sealed record ForgotPasswordRequest(string Email);

/// <summary>
/// Troca o token do email pelo escrow de recuperação (<c>wrappedAmkRec</c>), para o cliente
/// desembrulhar a AMK com a chave de recuperação. O blob é opaco: inútil sem a chave de recuperação.
/// </summary>
public sealed record ResetContextRequest(string Token);

/// <summary>Escrow de recuperação (base64) devolvido ao portador de um token de reset válido.</summary>
public sealed record ResetContextResponse(string WrappedAmkRec);

/// <summary>
/// Conclui o reset: como o <c>password/change</c>, mas autorizado pelo TOKEN do email em vez do
/// AuthHash antigo. O cliente já abriu a AMK com a chave de recuperação e a re-embrulhou sob a senha
/// nova — o servidor só valida o token e grava o material novo. A AMK não muda.
/// </summary>
public sealed record ResetPasswordRequest(
    string Token,
    string NewAuthHash,
    string NewArgon2Salt,
    Argon2Params NewArgon2Params,
    string NewWrappedAmkPwd);
