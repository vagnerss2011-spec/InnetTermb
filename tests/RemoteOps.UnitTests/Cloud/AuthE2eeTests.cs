using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Auth;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

/// <summary>
/// Testes do fluxo E2EE de conta (T4): register → kdf → login → password/change.
///
/// LIMITAÇÃO CONHECIDA: o repo não tem infra de integração com Postgres (sem
/// Testcontainers). Estes testes exercitam os serviços reais contra um
/// AppDbContext InMemory, seguindo o padrão já usado em CloudTestContext.
/// Não cobrem: SQL gerado, constraints/índices únicos do Postgres e concorrência
/// real. Ver §12 do spec — a suíte de integração com Testcontainers fica pendente.
/// </summary>
public sealed class AuthE2eeTests
{
    // Fixtures sintéticas: bytes aleatórios fazem o papel dos blobs que o cliente
    // produz. O servidor os trata como opacos, então o conteúdo é irrelevante.
    private static byte[] Rand(int n) => RandomNumberGenerator.GetBytes(n);

    private static RegisterRequest NewRegister(
        string email,
        byte[] authHash,
        byte[] salt,
        byte[] wrappedPwd,
        byte[] wrappedRec) =>
        new(
            Email: email,
            Argon2Salt: Convert.ToBase64String(salt),
            Argon2Params: new Argon2Params(65536, 3, 1, 32),
            AuthHash: Convert.ToBase64String(authHash),
            WrappedAmkPwd: Convert.ToBase64String(wrappedPwd),
            WrappedAmkRec: Convert.ToBase64String(wrappedRec),
            AmkKeyVersion: 1,
            DeviceId: Guid.NewGuid().ToString(),
            DeviceName: "Device A",
            WorkspaceName: "Meu Workspace");

    // ── Round-trip register → kdf → login ─────────────────────────────────────

    [Fact]
    public async Task Register_Kdf_Login_RoundTrip()
    {
        using var ctx = new CloudTestContext();
        var authHash = Rand(32);
        var salt = Rand(16);
        var wrappedPwd = Rand(60);
        var wrappedRec = Rand(60);
        var email = "operador@test.local";

        var reg = await ctx.Accounts.RegisterAsync(
            NewRegister(email, authHash, salt, wrappedPwd, wrappedRec), "1.2.3.4", default);

        Assert.NotNull(reg);
        Assert.False(string.IsNullOrEmpty(reg.AccessToken));
        Assert.False(string.IsNullOrEmpty(reg.RefreshToken));
        Assert.False(string.IsNullOrEmpty(reg.WorkspaceId));

        // O device deriva a MasterKey com os params que o servidor devolve no /auth/kdf.
        var kdf = await ctx.Accounts.GetKdfAsync(email, default);
        Assert.Equal(Convert.ToBase64String(salt), kdf.Argon2Salt);
        Assert.Equal(65536, kdf.Argon2Params.MemoryKib);
        Assert.Equal(3, kdf.Argon2Params.Iterations);
        Assert.Equal(1, kdf.Argon2Params.Parallelism);
        Assert.Equal(32, kdf.Argon2Params.OutputBytes);

        // Device 2: loga só com o AuthHash e recebe o escrow para desembrulhar a AMK.
        var login = await ctx.Tokens.LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "Device B")
            {
                AuthHash = Convert.ToBase64String(authHash),
            }, "5.6.7.8", default);

        Assert.NotNull(login);
        Assert.Equal(Convert.ToBase64String(wrappedPwd), login.WrappedAmkPwd);
        Assert.Equal(1, login.AmkKeyVersion);
        Assert.NotNull(login.Workspaces);
        var ws = Assert.Single(login.Workspaces!);
        Assert.Equal("Meu Workspace", ws.Name);
        Assert.Equal(reg.WorkspaceId, ws.Id);
        Assert.Equal("Owner", ws.Role);
    }

    // ── AuthHash errado falha ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_Fails_WhenAuthHashWrong()
    {
        using var ctx = new CloudTestContext();
        var email = "operador@test.local";
        await ctx.Accounts.RegisterAsync(
            NewRegister(email, Rand(32), Rand(16), Rand(60), Rand(60)), "1.2.3.4", default);

        var login = await ctx.Tokens.LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "Device B")
            {
                AuthHash = Convert.ToBase64String(Rand(32)), // AuthHash de outra senha
            }, "5.6.7.8", default);

        Assert.Null(login);
    }

    [Fact]
    public async Task Login_Fails_WhenPasswordUsedOnE2eeAccount()
    {
        using var ctx = new CloudTestContext();
        var email = "operador@test.local";
        await ctx.Accounts.RegisterAsync(
            NewRegister(email, Rand(32), Rand(16), Rand(60), Rand(60)), "1.2.3.4", default);

        // Conta E2EE não tem PasswordHash legado — o caminho antigo não pode virar bypass.
        var login = await ctx.Tokens.LoginAsync(
            new LoginRequest(email, "qualquer-coisa", Guid.NewGuid().ToString(), "Device B"),
            "5.6.7.8", default);

        Assert.Null(login);
    }

    // ── Servidor nunca persiste plaintext ─────────────────────────────────────

    [Fact]
    public async Task Register_NeverPersistsAuthHashInPlaintext()
    {
        using var ctx = new CloudTestContext();
        var authHash = Rand(32);
        var authHashB64 = Convert.ToBase64String(authHash);
        var email = "operador@test.local";

        await ctx.Accounts.RegisterAsync(
            NewRegister(email, authHash, Rand(16), Rand(60), Rand(60)), "1.2.3.4", default);

        var user = Assert.Single(ctx.Db.Users);

        // O servidor guarda PBKDF2 DO AuthHash — nunca o AuthHash cru.
        Assert.NotNull(user.AuthHashHash);
        Assert.NotEqual(authHashB64, user.AuthHashHash);
        Assert.DoesNotContain(authHashB64, user.AuthHashHash);
        Assert.StartsWith("v1:", user.AuthHashHash);

        // Nenhuma coluna byte[] pode conter os bytes do AuthHash.
        Assert.False(Contains(user.WrappedAmkPwd, authHash));
        Assert.False(Contains(user.WrappedAmkRec, authHash));
        Assert.False(Contains(user.Argon2Salt, authHash));

        // Conta E2EE não tem senha legada nenhuma.
        Assert.Null(user.PasswordHash);
    }

    private static bool Contains(byte[]? haystack, byte[] needle)
    {
        if (haystack is null || needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var hit = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }

    // ── Anti-enumeração no /auth/kdf ──────────────────────────────────────────

    [Fact]
    public async Task Kdf_ReturnsSameShape_ForUnknownEmail()
    {
        using var ctx = new CloudTestContext();
        var salt = Rand(16);
        await ctx.Accounts.RegisterAsync(
            NewRegister("existe@test.local", Rand(32), salt, Rand(60), Rand(60)), "1.2.3.4", default);

        var real = await ctx.Accounts.GetKdfAsync("existe@test.local", default);
        var decoy = await ctx.Accounts.GetKdfAsync("naoexiste@test.local", default);

        // Mesmo shape: salt base64 de 16 bytes e params idênticos aos default.
        Assert.Equal(16, Convert.FromBase64String(decoy.Argon2Salt).Length);
        Assert.Equal(real.Argon2Params, decoy.Argon2Params);

        // E o salt fake não pode coincidir com o real nem ser vazio.
        Assert.NotEqual(real.Argon2Salt, decoy.Argon2Salt);
    }

    [Fact]
    public async Task Kdf_DecoyIsDeterministic_AcrossCalls()
    {
        using var ctx = new CloudTestContext();

        // Salt aleatório por request denunciaria a conta inexistente (basta chamar 2x).
        // O decoy é HMAC(segredo do servidor, email) — estável.
        var a = await ctx.Accounts.GetKdfAsync("naoexiste@test.local", default);
        var b = await ctx.Accounts.GetKdfAsync("naoexiste@test.local", default);

        Assert.Equal(a.Argon2Salt, b.Argon2Salt);
        Assert.Equal(a.Argon2Params, b.Argon2Params);
    }

    [Fact]
    public async Task Kdf_DecoyDiffersPerEmail()
    {
        using var ctx = new CloudTestContext();

        var a = await ctx.Accounts.GetKdfAsync("um@test.local", default);
        var b = await ctx.Accounts.GetKdfAsync("dois@test.local", default);

        Assert.NotEqual(a.Argon2Salt, b.Argon2Salt);
    }

    [Fact]
    public async Task Kdf_IsCaseInsensitive_LikeRegistration()
    {
        using var ctx = new CloudTestContext();
        var salt = Rand(16);
        await ctx.Accounts.RegisterAsync(
            NewRegister("Operador@Test.Local", Rand(32), salt, Rand(60), Rand(60)), "1.2.3.4", default);

        var kdf = await ctx.Accounts.GetKdfAsync("operador@test.local", default);

        // Se o lookup fosse case-sensitive, o e-mail real cairia no decoy e o
        // login legítimo derivaria a MasterKey com o salt errado.
        Assert.Equal(Convert.ToBase64String(salt), kdf.Argon2Salt);
    }

    // ── Registro duplicado ────────────────────────────────────────────────────

    [Fact]
    public async Task Register_Fails_WhenEmailAlreadyExists()
    {
        using var ctx = new CloudTestContext();
        var req = NewRegister("operador@test.local", Rand(32), Rand(16), Rand(60), Rand(60));
        await ctx.Accounts.RegisterAsync(req, "1.2.3.4", default);

        var second = await ctx.Accounts.RegisterAsync(
            NewRegister("operador@test.local", Rand(32), Rand(16), Rand(60), Rand(60)), "1.2.3.4", default);

        Assert.Null(second);
    }

    // ── Troca de senha (re-embrulha a AMK) ────────────────────────────────────

    [Fact]
    public async Task ChangePassword_RewrapsAmk_WithoutTouchingRecoveryEscrow()
    {
        using var ctx = new CloudTestContext();
        var oldAuthHash = Rand(32);
        var wrappedRec = Rand(60);
        var email = "operador@test.local";

        await ctx.Accounts.RegisterAsync(
            NewRegister(email, oldAuthHash, Rand(16), Rand(60), wrappedRec), "1.2.3.4", default);
        var user = Assert.Single(ctx.Db.Users);

        var newAuthHash = Rand(32);
        var newSalt = Rand(16);
        var newWrappedPwd = Rand(60);

        var ok = await ctx.Accounts.ChangePasswordAsync(user.Id, new ChangePasswordRequest(
            OldAuthHash: Convert.ToBase64String(oldAuthHash),
            NewAuthHash: Convert.ToBase64String(newAuthHash),
            NewArgon2Salt: Convert.ToBase64String(newSalt),
            NewArgon2Params: new Argon2Params(65536, 4, 1, 32),
            NewWrappedAmkPwd: Convert.ToBase64String(newWrappedPwd)), default);

        Assert.True(ok);

        var updated = ctx.Db.Users.Single();
        Assert.Equal(newSalt, updated.Argon2Salt);
        Assert.Equal(newWrappedPwd, updated.WrappedAmkPwd);
        Assert.Equal(4, updated.Argon2Iterations);

        // A AMK não muda: o escrow de recuperação continua válido e a versão da chave é a mesma.
        Assert.Equal(wrappedRec, updated.WrappedAmkRec);
        Assert.Equal(1, updated.AmkKeyVersion);

        // A senha antiga deixa de autenticar; a nova autentica.
        Assert.Null(await ctx.Tokens.LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D")
            { AuthHash = Convert.ToBase64String(oldAuthHash) }, "1.2.3.4", default));
        Assert.NotNull(await ctx.Tokens.LoginAsync(
            new LoginRequest(email, null, Guid.NewGuid().ToString(), "D")
            { AuthHash = Convert.ToBase64String(newAuthHash) }, "1.2.3.4", default));
    }

    [Fact]
    public async Task ChangePassword_Fails_WhenOldAuthHashWrong()
    {
        using var ctx = new CloudTestContext();
        var oldAuthHash = Rand(32);
        var originalWrapped = Rand(60);

        await ctx.Accounts.RegisterAsync(
            NewRegister("operador@test.local", oldAuthHash, Rand(16), originalWrapped, Rand(60)),
            "1.2.3.4", default);
        var user = Assert.Single(ctx.Db.Users);

        var ok = await ctx.Accounts.ChangePasswordAsync(user.Id, new ChangePasswordRequest(
            OldAuthHash: Convert.ToBase64String(Rand(32)), // errado
            NewAuthHash: Convert.ToBase64String(Rand(32)),
            NewArgon2Salt: Convert.ToBase64String(Rand(16)),
            NewArgon2Params: new Argon2Params(65536, 3, 1, 32),
            NewWrappedAmkPwd: Convert.ToBase64String(Rand(60))), default);

        Assert.False(ok);

        // Nada pode ter sido gravado.
        var untouched = ctx.Db.Users.Single();
        Assert.Equal(originalWrapped, untouched.WrappedAmkPwd);
    }

    // ── Compatibilidade com o login legado por senha ──────────────────────────

    [Fact]
    public async Task Login_StillWorks_ForLegacyPasswordAccount()
    {
        using var ctx = new CloudTestContext();
        var (_, _, user, _) = await ctx.SeedActiveUserAsync();

        // Conta legada: PasswordHash preenchido, sem nenhum campo E2EE.
        user.PasswordHash = PasswordHasher.Hash("segredo-legado-do-teste");
        await ctx.Db.SaveChangesAsync();

        var login = await ctx.Tokens.LoginAsync(
            new LoginRequest(user.Email, "segredo-legado-do-teste", Guid.NewGuid().ToString(), "Device Legado"),
            "1.2.3.4", default);

        Assert.NotNull(login);
        // Conta sem escrow não devolve AMK — o cliente cai no fluxo antigo.
        Assert.Null(login.WrappedAmkPwd);
    }

    [Fact]
    public async Task Login_Fails_WhenAuthHashUsedOnLegacyAccount()
    {
        using var ctx = new CloudTestContext();
        var (_, _, user, _) = await ctx.SeedActiveUserAsync();
        user.PasswordHash = PasswordHasher.Hash("segredo-legado-do-teste");
        await ctx.Db.SaveChangesAsync();

        var login = await ctx.Tokens.LoginAsync(
            new LoginRequest(user.Email, null, Guid.NewGuid().ToString(), "D")
            { AuthHash = Convert.ToBase64String(Rand(32)) }, "1.2.3.4", default);

        Assert.Null(login);
    }

    // ── Anti-enumeração por timing no /auth/login (FIX 2) ─────────────────────

    /// <summary>
    /// Prova ESTRUTURAL (sem medir tempo → sem flake) de que o login de e-mail existente e
    /// o de e-mail inexistente invocam o PBKDF2 o MESMO número de vezes. Antes do fix, o
    /// e-mail inexistente dava short-circuit (0 PBKDF2, resposta sub-ms) e o existente rodava
    /// 1 (dezenas de ms), vazando a existência da conta por timing.
    /// O contador é injetado POR INSTÂNCIA via TokenService.ProofVerifier, então não há estado
    /// global compartilhado entre os testes paralelos do xUnit.
    /// </summary>
    [Fact]
    public async Task Login_InvokesPbkdf2_SameCount_ForExistingAndUnknownEmail()
    {
        using var ctx = new CloudTestContext();
        var config = CloudTestContext.TestConfig();

        const string existingEmail = "existe@test.local";
        await ctx.Accounts.RegisterAsync(
            NewRegister(existingEmail, Rand(32), Rand(16), Rand(60), Rand(60)), "1.2.3.4", default);

        // Conta existente, AuthHash errado → falha, mas roda o PBKDF2 real.
        var existingCount = 0;
        var svcExisting = new TokenService(ctx.Db, config, NullLogger<TokenService>.Instance)
        {
            ProofVerifier = (v, h) => { Interlocked.Increment(ref existingCount); return PasswordHasher.Verify(v, h); },
        };
        var existingLogin = await svcExisting.LoginAsync(
            new LoginRequest(existingEmail, null, Guid.NewGuid().ToString(), "D")
            { AuthHash = Convert.ToBase64String(Rand(32)) }, "1.2.3.4", default);

        // Conta inexistente → tem que rodar o MESMO número de PBKDF2 (contra o decoy).
        var unknownCount = 0;
        var svcUnknown = new TokenService(ctx.Db, config, NullLogger<TokenService>.Instance)
        {
            ProofVerifier = (v, h) => { Interlocked.Increment(ref unknownCount); return PasswordHasher.Verify(v, h); },
        };
        var unknownLogin = await svcUnknown.LoginAsync(
            new LoginRequest("naoexiste@test.local", null, Guid.NewGuid().ToString(), "D")
            { AuthHash = Convert.ToBase64String(Rand(32)) }, "1.2.3.4", default);

        Assert.Null(existingLogin);
        Assert.Null(unknownLogin);
        Assert.Equal(1, existingCount);
        Assert.Equal(existingCount, unknownCount);
    }
}
