using System;
using System.Collections.Generic;
using System.Text.Json;

using RemoteOps.Security.Account;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Contrato de fio entre o cliente (RemoteOps.Sync) e os endpoints REAIS de conta do backend (T4).
///
/// <para><b>Por que existem DTOs espelhados aqui em vez de referenciar RemoteOps.Cloud.Auth:</b> o
/// backend E2EE (T4/T5/T8) vive noutro branch — <c>feature/cloud-backend</c>, worktree
/// <c>C:/dev/remoteops-cloud</c> — e o <c>src/RemoteOps.Cloud</c> DESTE branch ainda é a cópia
/// pré-E2EE (o <c>LoginRequest</c> antigo, que recebia senha). Referenciar o projeto local aqui
/// provaria o contrato ERRADO. Os records abaixo são cópia literal de
/// <c>remoteops-cloud/src/RemoteOps.Cloud/Auth/AuthModels.cs</c> no commit a94fb1e; quando os dois
/// branches se encontrarem, troque o espelho por um <c>using RemoteOps.Cloud.Auth</c> e apague esta
/// região (ver pendências da T9).</para>
///
/// <para>O teste é de SERIALIZAÇÃO de verdade: monta o tipo do cliente, joga no fio com as mesmas
/// opções do ASP.NET Core (<see cref="JsonSerializerDefaults.Web"/>) e desserializa como o tipo do
/// servidor — do jeito que o Minimal API faria. Um campo renomeado, aninhado ou faltando aparece
/// aqui como null/default, e não como um 400 em campo três meses depois.</para>
/// </summary>
public sealed class AccountContractsWireTests
{
    private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);

    // ── Espelho do backend T4 (remoteops-cloud@a94fb1e, Auth/AuthModels.cs) ──────────────

    private sealed record ServerArgon2Params(int MemoryKib, int Iterations, int Parallelism, int OutputBytes);

    private sealed record ServerLoginRequest(string Email, string? Password, string DeviceId, string DeviceName)
    {
        public string? AuthHash { get; init; }
        public string? TotpCode { get; init; }
    }

    private sealed record ServerMfaEnrollResponse(string SecretBase32, string OtpauthUri);

    private sealed record ServerMfaConfirmRequest(string Code);

    private sealed record ServerMfaDisableRequest(string Code);

    private sealed record ServerWorkspaceSummary(string Id, string Name, string Role, string? Kind = null);

    private sealed record ServerLoginResponse(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string? WrappedAmkPwd = null,
        int? AmkKeyVersion = null,
        IReadOnlyList<ServerWorkspaceSummary>? Workspaces = null);

    private sealed record ServerRegisterRequest(
        string Email,
        string Argon2Salt,
        ServerArgon2Params Argon2Params,
        string AuthHash,
        string WrappedAmkPwd,
        string WrappedAmkRec,
        int AmkKeyVersion,
        string DeviceId,
        string DeviceName,
        string WorkspaceName);

    private sealed record ServerRegisterResponse(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAt,
        string WorkspaceId,
        string? WrappedAmkPwd = null,
        int? AmkKeyVersion = null,
        IReadOnlyList<ServerWorkspaceSummary>? Workspaces = null);

    private sealed record ServerKdfResponse(string Argon2Salt, ServerArgon2Params Argon2Params);

    /// <summary>
    /// ⚠️ <b>O cliente ANTERIOR a esta versão</b>, campo a campo: o <c>AccountWorkspace</c> sem
    /// <c>Kind</c>. Existe para provar a outra metade da janela de deploy — backend novo × PC velho —
    /// sem depender de "System.Text.Json ignora membro desconhecido" como folclore.
    /// </summary>
    private sealed record ClienteVelhoWorkspace(string Id, string Name, string Role);

    // Recuperação de senha (Fase 4) — espelho de Auth/AuthModels.cs deste branch.
    private sealed record ServerForgotPasswordRequest(string Email);
    private sealed record ServerResetContextRequest(string Token);
    private sealed record ServerResetContextResponse(string WrappedAmkRec);
    private sealed record ServerResetPasswordRequest(
        string Token,
        string NewAuthHash,
        string NewArgon2Salt,
        ServerArgon2Params NewArgon2Params,
        string NewWrappedAmkPwd);

    // ── /auth/register ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A request de registro do cliente tem que cair INTEIRA nos campos do servidor. O T7 mandava
    /// <c>firstWorkspace:{name}</c> aninhado e não mandava device nenhum — o backend real quer
    /// <c>workspaceName</c>/<c>deviceId</c>/<c>deviceName</c> na raiz.
    /// </summary>
    [Fact]
    public void RegisterRequest_DeserializesIntoServerShape()
    {
        var client = new RegisterAccountRequest(
            "op@innet.tec.br",
            Argon2Salt: new byte[16],
            Argon2Params.Default,
            AuthHash: new byte[32],
            WrappedAmkPwd: [1, 2, 3],
            WrappedAmkRec: [4, 5, 6],
            AmkKeyVersion: 1,
            DeviceId: "11111111-1111-1111-1111-111111111111",
            DeviceName: "PC-A",
            WorkspaceName: "NOC");

        string wire = JsonSerializer.Serialize(client, s_web);
        ServerRegisterRequest? server = JsonSerializer.Deserialize<ServerRegisterRequest>(wire, s_web);

        Assert.NotNull(server);
        Assert.Equal("op@innet.tec.br", server!.Email);
        Assert.Equal("NOC", server.WorkspaceName);
        Assert.Equal("11111111-1111-1111-1111-111111111111", server.DeviceId);
        Assert.Equal("PC-A", server.DeviceName);
        Assert.Equal(1, server.AmkKeyVersion);

        // Blobs: byte[] no cliente vira string base64 no fio — que é EXATAMENTE o que o backend
        // decodifica com Convert.FromBase64String. Base64 canônico (com padding), sem base64url.
        Assert.Equal(Convert.ToBase64String(new byte[16]), server.Argon2Salt);
        Assert.Equal(Convert.ToBase64String(new byte[32]), server.AuthHash);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), server.WrappedAmkPwd);
        Assert.Equal(Convert.ToBase64String([4, 5, 6]), server.WrappedAmkRec);

        // Params públicos do Argon2id: mesmos nomes dos dois lados.
        Assert.Equal(65536, server.Argon2Params.MemoryKib);
        Assert.Equal(3, server.Argon2Params.Iterations);
        Assert.Equal(1, server.Argon2Params.Parallelism);
        Assert.Equal(32, server.Argon2Params.OutputBytes);
    }

    /// <summary>
    /// O backend valida <c>authHash</c> comparando o PBKDF2 da STRING base64 recebida
    /// (<c>PasswordHasher.Hash(req.AuthHash)</c>), não dos bytes. Se o registro e o login
    /// codificassem o mesmo AuthHash de formas diferentes, o login falharia com a senha certa —
    /// então o encoding tem que ser estável e idêntico nos dois caminhos.
    /// </summary>
    [Fact]
    public void AuthHash_HasIdenticalBase64_InRegisterAndLogin()
    {
        byte[] authHash = [.. System.Linq.Enumerable.Range(0, 32).Select(i => (byte)i)];

        var register = new RegisterAccountRequest(
            "op@innet.tec.br", new byte[16], Argon2Params.Default, authHash,
            [1], [2], 1, "dev-1", "PC-A", "NOC");
        var login = new E2eeLoginRequest("op@innet.tec.br", authHash, "dev-1", "PC-A");

        var serverRegister = JsonSerializer.Deserialize<ServerRegisterRequest>(
            JsonSerializer.Serialize(register, s_web), s_web);
        var serverLogin = JsonSerializer.Deserialize<ServerLoginRequest>(
            JsonSerializer.Serialize(login, s_web), s_web);

        Assert.Equal(serverRegister!.AuthHash, serverLogin!.AuthHash);
        Assert.Equal(Convert.ToBase64String(authHash), serverLogin.AuthHash);
    }

    /// <summary>A resposta de registro do servidor tem que caber no tipo do cliente — inclusive o workspaceId.</summary>
    [Fact]
    public void RegisterResponse_FromServerShape_BindsOnClient()
    {
        var server = new ServerRegisterResponse(
            "access", "refresh", DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
            WorkspaceId: "ws-guid",
            WrappedAmkPwd: Convert.ToBase64String([9, 9]),
            AmkKeyVersion: 1,
            Workspaces: [new ServerWorkspaceSummary("ws-guid", "NOC", "owner")]);

        string wire = JsonSerializer.Serialize(server, s_web);
        RegisterAccountResponse? client = JsonSerializer.Deserialize<RegisterAccountResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal("access", client!.AccessToken);
        Assert.Equal("refresh", client.RefreshToken);
        Assert.Equal("ws-guid", client.WorkspaceId);
        Assert.Equal(1, client.AmkKeyVersion);
        Assert.Equal(new byte[] { 9, 9 }, client.WrappedAmkPwd);

        AccountWorkspace ws = Assert.Single(client.Workspaces!);
        Assert.Equal("ws-guid", ws.Id);
        Assert.Equal("NOC", ws.Name);
        // O papel RBAC vem do servidor desde a T4; o cliente precisa dele pra saber se é owner.
        Assert.Equal("owner", ws.Role);
    }

    // ── /auth/login ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O login E2EE do cliente tem que satisfazer o XOR do backend: exatamente um entre
    /// <c>authHash</c> e <c>password</c>. O cliente nunca tem o campo password — então ele chega
    /// null e o servidor entra no ramo E2EE.
    /// </summary>
    [Fact]
    public void E2eeLoginRequest_SendsAuthHash_AndNoPasswordField()
    {
        var client = new E2eeLoginRequest("op@innet.tec.br", new byte[32], "dev-1", "PC-A");

        string wire = JsonSerializer.Serialize(client, s_web);
        ServerLoginRequest? server = JsonSerializer.Deserialize<ServerLoginRequest>(wire, s_web);

        Assert.NotNull(server);
        Assert.Null(server!.Password);
        Assert.NotNull(server.AuthHash);
        Assert.Equal("op@innet.tec.br", server.Email);
        Assert.Equal("dev-1", server.DeviceId);
        Assert.Equal("PC-A", server.DeviceName);

        // A regra do backend: hasAuthHash != hasPassword. Sem o campo password no fio, passa.
        Assert.DoesNotContain("\"password\"", wire, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// No RE-envio do login (2FA), o <c>totpCode</c> do cliente tem que cair no campo homônimo do
    /// backend — senão o código nunca chegaria e o login travaria em loop de mfa_required.
    /// </summary>
    [Fact]
    public void E2eeLoginRequest_WithTotpCode_LandsInServerField()
    {
        var client = new E2eeLoginRequest("op@innet.tec.br", new byte[32], "dev-1", "PC-A", "123456");

        string wire = JsonSerializer.Serialize(client, s_web);
        ServerLoginRequest? server = JsonSerializer.Deserialize<ServerLoginRequest>(wire, s_web);

        Assert.Equal("123456", server!.TotpCode);
    }

    [Fact]
    public void E2eeLoginRequest_WithoutTotpCode_LeavesServerFieldNull()
    {
        var client = new E2eeLoginRequest("op@innet.tec.br", new byte[32], "dev-1", "PC-A");

        string wire = JsonSerializer.Serialize(client, s_web);
        ServerLoginRequest? server = JsonSerializer.Deserialize<ServerLoginRequest>(wire, s_web);

        Assert.Null(server!.TotpCode);
    }

    // ── /auth/mfa/* (2FA) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void MfaEnrollResponse_FromServerShape_BindsOnClient()
    {
        var server = new ServerMfaEnrollResponse("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "otpauth://totp/RemoteOps:op?secret=X");

        string wire = JsonSerializer.Serialize(server, s_web);
        MfaEnrollResponse? client = JsonSerializer.Deserialize<MfaEnrollResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", client!.SecretBase32);
        Assert.StartsWith("otpauth://totp/", client.OtpauthUri);
    }

    [Fact]
    public void MfaConfirmRequest_DeserializesIntoServerShape()
    {
        var client = new MfaConfirmRequest("123456");

        var server = JsonSerializer.Deserialize<ServerMfaConfirmRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.Equal("123456", server!.Code);
    }

    [Fact]
    public void MfaDisableRequest_DeserializesIntoServerShape()
    {
        var client = new MfaDisableRequest("654321");

        var server = JsonSerializer.Deserialize<ServerMfaDisableRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.Equal("654321", server!.Code);
    }

    /// <summary>Resposta de login E2EE do servidor real → tipo do cliente, campo a campo.</summary>
    [Fact]
    public void LoginResponse_FromServerShape_BindsOnClient()
    {
        var server = new ServerLoginResponse(
            "access", "refresh", DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
            WrappedAmkPwd: Convert.ToBase64String([7, 7, 7]),
            AmkKeyVersion: 1,
            Workspaces: [new ServerWorkspaceSummary("ws-guid", "NOC", "owner")]);

        string wire = JsonSerializer.Serialize(server, s_web);
        E2eeLoginResponse? client = JsonSerializer.Deserialize<E2eeLoginResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal(new byte[] { 7, 7, 7 }, client!.WrappedAmkPwd);
        Assert.Equal(1, client.AmkKeyVersion);
        Assert.Equal("owner", Assert.Single(client.Workspaces!).Role);
    }

    /// <summary>
    /// Conta LEGADA (criada antes do E2EE): o backend devolve 200 com os campos E2EE NULOS. O tipo
    /// do cliente precisa aceitar isso sem estourar na desserialização — quem decide o que fazer é o
    /// authenticator (que rejeita a sessão), não o parser.
    /// </summary>
    [Fact]
    public void LoginResponse_WithNullE2eeFields_StillDeserializes()
    {
        var server = new ServerLoginResponse(
            "access", "refresh", DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"));

        string wire = JsonSerializer.Serialize(server, s_web);
        E2eeLoginResponse? client = JsonSerializer.Deserialize<E2eeLoginResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Null(client!.WrappedAmkPwd);
        Assert.Null(client.AmkKeyVersion);
        Assert.Null(client.Workspaces);
    }

    // ── /auth/kdf ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// O salt do servidor é base64 numa string; o cliente lê como byte[] pra alimentar o Argon2id
    /// direto. Os 16 bytes têm que voltar idênticos — um salt errado deriva a MasterKey errada e o
    /// cofre não abre.
    /// </summary>
    [Fact]
    public void KdfResponse_FromServerShape_BindsOnClient()
    {
        byte[] salt = [.. System.Linq.Enumerable.Range(0, 16).Select(i => (byte)(i * 7))];
        var server = new ServerKdfResponse(
            Convert.ToBase64String(salt), new ServerArgon2Params(65536, 3, 1, 32));

        string wire = JsonSerializer.Serialize(server, s_web);
        KdfResponse? client = JsonSerializer.Deserialize<KdfResponse>(wire, s_web);

        Assert.NotNull(client);
        Assert.Equal(salt, client!.Argon2Salt);
        Assert.Equal(Argon2Params.Default, client.Argon2Params);
    }

    // ── /auth/password/{forgot,reset-context,reset} (Fase 4) ───────────────────────────────

    [Fact]
    public void ForgotPasswordRequest_DeserializesIntoServerShape()
    {
        var client = new ForgotPasswordRequest("op@innet.tec.br");

        var server = JsonSerializer.Deserialize<ServerForgotPasswordRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.Equal("op@innet.tec.br", server!.Email);
    }

    [Fact]
    public void ResetContextRequest_DeserializesIntoServerShape()
    {
        var client = new ResetContextRequest("reset-token-abc");

        var server = JsonSerializer.Deserialize<ServerResetContextRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.Equal("reset-token-abc", server!.Token);
    }

    /// <summary>O escrow de recuperação: string base64 no servidor → byte[] no cliente, byte a byte.</summary>
    [Fact]
    public void ResetContextResponse_FromServerShape_BindsOnClient()
    {
        var server = new ServerResetContextResponse(Convert.ToBase64String([1, 2, 3, 4]));

        var client = JsonSerializer.Deserialize<ResetContextResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, client!.WrappedAmkRec);
    }

    /// <summary>
    /// O reset do cliente tem que cair INTEIRO nos campos do servidor: token + material novo
    /// (authHash/salt/wrappedAmkPwd em base64 + params). Um campo renomeado aqui = 400 em campo.
    /// </summary>
    [Fact]
    public void ResetPasswordRequest_DeserializesIntoServerShape()
    {
        var client = new ResetPasswordRequest(
            Token: "reset-token-abc", // pragma: allowlist secret
            NewAuthHash: new byte[32],
            NewArgon2Salt: new byte[16],
            NewArgon2Params: Argon2Params.Default,
            NewWrappedAmkPwd: [9, 8, 7]);

        var server = JsonSerializer.Deserialize<ServerResetPasswordRequest>(
            JsonSerializer.Serialize(client, s_web), s_web);

        Assert.NotNull(server);
        Assert.Equal("reset-token-abc", server!.Token);
        Assert.Equal(Convert.ToBase64String(new byte[32]), server.NewAuthHash);
        Assert.Equal(Convert.ToBase64String(new byte[16]), server.NewArgon2Salt);
        Assert.Equal(Convert.ToBase64String([9, 8, 7]), server.NewWrappedAmkPwd);
        Assert.Equal(65536, server.NewArgon2Params.MemoryKib);
        Assert.Equal(32, server.NewArgon2Params.OutputBytes);
    }

    // ── workspaces[].kind — o FATO que substitui o palpite por ausência de chave ──────────
    //
    // O app classificava o workspace ativo por um 404 de GET /workspaces/{id}/key. Esse 404 quer
    // dizer "a SUA CONTA não guarda embrulho neste workspace" — e é indistinguível de um 404 de
    // infraestrutura. A coluna `workspaces.kind` já existia no servidor e simplesmente não viajava.

    /// <summary>O <c>kind</c> do servidor chega ao cliente e vira FATO, não string solta.</summary>
    [Theory]
    [InlineData("team", WorkspaceKindFact.Team)]
    [InlineData("personal", WorkspaceKindFact.Personal)]
    public void WorkspaceKind_TravelsFromServer_AndBecomesFact(string kind, WorkspaceKindFact esperado)
    {
        var server = new ServerLoginResponse(
            "access", "refresh", DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
            WrappedAmkPwd: Convert.ToBase64String([7]),
            AmkKeyVersion: 1,
            Workspaces: [new ServerWorkspaceSummary("ws-guid", "Equipe", "owner", kind)]);

        E2eeLoginResponse? client = JsonSerializer.Deserialize<E2eeLoginResponse>(
            JsonSerializer.Serialize(server, s_web), s_web);

        AccountWorkspace ws = Assert.Single(client!.Workspaces!);
        Assert.Equal(kind, ws.Kind);
        Assert.Equal(esperado, WorkspaceKindFacts.From(ws.Kind));
    }

    /// <summary>
    /// ⚠️ <b>Backend VELHO (campo ausente) = NÃO SEI.</b> Nunca "é pessoal" — é exatamente essa
    /// tradução que autorizava gravar o dono do banco dos ~700 com o GUID do time.
    /// </summary>
    [Fact]
    public void BackendVelho_SemOCampoKind_ViraNaoSei_ENaoPessoal()
    {
        var server = new ServerLoginResponse(
            "access", "refresh", DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
            WrappedAmkPwd: Convert.ToBase64String([7]),
            AmkKeyVersion: 1,
            Workspaces: [new ServerWorkspaceSummary("ws-guid", "NOC", "owner")]);

        string wire = JsonSerializer.Serialize(server, s_web);
        E2eeLoginResponse? client = JsonSerializer.Deserialize<E2eeLoginResponse>(wire, s_web);

        AccountWorkspace ws = Assert.Single(client!.Workspaces!);
        Assert.Null(ws.Kind);
        Assert.Equal(WorkspaceKindFact.Unknown, WorkspaceKindFacts.From(ws.Kind));
    }

    /// <summary>
    /// A outra metade da janela de deploy: <b>cliente VELHO × backend NOVO</b>. O campo extra não
    /// pode quebrar a desserialização de quem ainda não atualizou — e o resto do login continua
    /// chegando inteiro.
    /// </summary>
    [Fact]
    public void ClienteVelho_ComOCampoNovo_NaoQuebra()
    {
        var server = new ServerLoginResponse(
            "access", "refresh", DateTimeOffset.Parse("2030-01-01T00:00:00+00:00"),
            WrappedAmkPwd: Convert.ToBase64String([7]),
            AmkKeyVersion: 1,
            Workspaces: [new ServerWorkspaceSummary("ws-guid", "Equipe", "owner", "team")]);

        string wire = JsonSerializer.Serialize(server, s_web);
        var velho = JsonSerializer.Deserialize<IReadOnlyList<ClienteVelhoWorkspace>>(
            JsonSerializer.Deserialize<JsonElement>(wire, s_web)
                .GetProperty("workspaces").GetRawText(),
            s_web);

        ClienteVelhoWorkspace ws = Assert.Single(velho!);
        Assert.Equal("ws-guid", ws.Id);
        Assert.Equal("Equipe", ws.Name);
        Assert.Equal("owner", ws.Role);
    }

    /// <summary>
    /// ⚠️ <b>Lista de RECONHECIMENTO, não de negação.</b> Um valor que este binário não conhece
    /// (versão futura do servidor), vazio ou lixo é "não sei" — não "pessoal". Escrito ao contrário
    /// (<c>!= "team" ⇒ pessoal</c>), o primeiro valor novo do servidor autorizaria adotar o cofre
    /// pessoal do operador para um workspace que ninguém classificou.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Team")]
    [InlineData("PERSONAL")]
    [InlineData("shared")]
    public void KindDesconhecido_ViraNaoSei(string? kind)
        => Assert.Equal(WorkspaceKindFact.Unknown, WorkspaceKindFacts.From(kind));
}
