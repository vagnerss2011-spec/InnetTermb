using System.Diagnostics;
using System.Security.Cryptography;

using RemoteOps.Contracts.Audit;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.MikroTik;

using Xunit;

namespace RemoteOps.UnitTests.MikroTik;

public sealed class WinBoxRunnerTests : IDisposable
{
    private readonly FakeAuditSink _audit = new();
    private readonly FakeProcessLauncher _launcher = new();
    private readonly List<string> _tempFiles = [];

    // ── Manifesto ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task LaunchAsync_ManifestWithoutSha256_ThrowsValidationExceptionAndAudits()
    {
        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = null, // ausente → fail-closed
            ExecutablePath = "C:\\nonexistent\\winbox.exe",
        };
        var runner = BuildRunner(manifest);

        var ex = await Assert.ThrowsAsync<WinBoxValidationException>(
            () => runner.LaunchAsync(MakeRequest()));

        Assert.Contains("sha256", ex.Message, StringComparison.OrdinalIgnoreCase);

        var validatedEvent = Assert.Single(_audit.Events, e => e.Action == "winbox_tool_validated");
        Assert.Equal(false, validatedEvent.Metadata["validated"]);
        Assert.Empty(_launcher.Calls); // nenhum processo iniciado
    }

    [Fact]
    public async Task LaunchAsync_ManifestWithPlaceholderSha256_ThrowsAndAudits()
    {
        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = "...", // placeholder → fail-closed
            ExecutablePath = "winbox.exe",
        };
        var runner = BuildRunner(manifest);

        await Assert.ThrowsAsync<WinBoxValidationException>(() => runner.LaunchAsync(MakeRequest()));

        var validatedEvent = Assert.Single(_audit.Events, e => e.Action == "winbox_tool_validated");
        Assert.Equal(false, validatedEvent.Metadata["validated"]);
    }

    [Fact]
    public async Task LaunchAsync_ManifestValidationNeverThrowsNRE()
    {
        // Garante que sha256 null não causa NullReferenceException
        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = null,
            ExecutablePath = "winbox.exe",
        };
        var runner = BuildRunner(manifest);

        var ex = await Record.ExceptionAsync(() => runner.LaunchAsync(MakeRequest()));

        Assert.IsType<WinBoxValidationException>(ex);
        Assert.IsNotType<NullReferenceException>(ex);
    }

    // ── Política ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LaunchAsync_PolicyDeniesHost_ThrowsAndAudits()
    {
        var policy = new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            DeniedHostIds = ["host-bloqueado"],
        });
        var runner = BuildRunner(policyProvider: policy);

        var ex = await Assert.ThrowsAsync<WinBoxValidationException>(
            () => runner.LaunchAsync(MakeRequest(hostId: "host-bloqueado")));

        Assert.Contains("Política", ex.Message);

        var failEvent = Assert.Single(_audit.Events, e => e.Action == "winbox_open_failed");
        Assert.Equal("policy_denied", failEvent.Metadata["reason"]);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task LaunchAsync_PolicyDeniesWorkspace_ThrowsAndAudits()
    {
        var policy = new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            DeniedWorkspaceIds = ["ws-negado"],
        });
        var runner = BuildRunner(policyProvider: policy);

        var ex = await Assert.ThrowsAsync<WinBoxValidationException>(
            () => runner.LaunchAsync(MakeRequest(workspaceId: "ws-negado")));

        Assert.Contains("Política", ex.Message);
        Assert.Single(_audit.Events, e => e.Action == "winbox_open_failed");
    }

    [Fact]
    public async Task LaunchAsync_PolicyDeniesPasswordArg_ThrowsWithoutResolvingCredential()
    {
        var policy = new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            PasswordArgumentAllowed = false, // Modo A — padrão
        });
        var credentialResolved = false;
        var credResolver = new FakeCredentialResolver(() =>
        {
            credentialResolved = true;
            return "S3cret";
        });
        var runner = BuildRunner(policyProvider: policy, credentialResolver: credResolver);

        var ex = await Assert.ThrowsAsync<WinBoxValidationException>(
            () => runner.LaunchAsync(MakeRequest(includePasswordArg: true, credentialRefId: "cred-01")));

        Assert.Contains("negada pela política", ex.Message);
        Assert.False(credentialResolved, "Credencial não deve ser resolvida quando política nega senha.");

        var failEvent = Assert.Single(_audit.Events, e => e.Action == "winbox_open_failed");
        Assert.Equal("password_arg_denied_by_policy", failEvent.Metadata["reason"]);
    }

    // ── RoMON ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LaunchAsync_RoMonEnabled_ThrowsAndAudits()
    {
        var runner = BuildRunner();
        var request = MakeRequest(romon: new ExternalToolRomon { Enabled = true, Agent = "10.0.0.1" });

        var ex = await Assert.ThrowsAsync<WinBoxValidationException>(
            () => runner.LaunchAsync(request));

        Assert.Contains("RoMON", ex.Message);
        Assert.Contains("ADR-006", ex.Message);

        var failEvent = Assert.Single(_audit.Events, e => e.Action == "winbox_open_failed");
        Assert.Equal("romon_not_confirmed_official_cli", failEvent.Metadata["reason"]);
        Assert.Empty(_launcher.Calls);
    }

    [Fact]
    public async Task LaunchAsync_RoMonDisabled_DoesNotBlock()
    {
        var (manifest, _) = CreateValidManifest();
        var runner = BuildRunner(manifest, AllowAllPolicy());
        var request = MakeRequest(romon: new ExternalToolRomon { Enabled = false });

        // Não deve lançar por causa do RoMON desabilitado
        var launchId = await runner.LaunchAsync(request);

        Assert.NotEmpty(launchId);
        Assert.Single(_launcher.Calls);
    }

    // ── Auditoria sem segredo ───────────────────────────────────────────────────

    [Fact]
    public async Task LaunchAsync_NeverLogsPasswordInAuditEvents()
    {
        const string secretPassword = "SuperSecr3t!@#$";
        var (manifest, _) = CreateValidManifest();

        var credResolver = new FakeCredentialResolver(() => secretPassword);
        var runner = BuildRunner(
            manifest,
            AllowPasswordPolicy(),
            credentialResolver: credResolver);

        await runner.LaunchAsync(MakeRequest(
            login: "admin",
            includePasswordArg: true,
            credentialRefId: "cred-01"));

        foreach (var evt in _audit.Events)
        {
            foreach (var (key, value) in evt.Metadata)
            {
                var valueStr = value?.ToString() ?? string.Empty;
                Assert.DoesNotContain(secretPassword, valueStr,
                    $"Evento '{evt.Action}' chave '{key}' contém a senha.");
            }
        }
    }

    [Fact]
    public async Task LaunchAsync_IPv6Target_EmitsIpv6AuditEvent()
    {
        var (manifest, _) = CreateValidManifest();
        var runner = BuildRunner(manifest, AllowAllPolicy());

        await runner.LaunchAsync(MakeRequest(address: "2001:db8::10", port: 8292));

        Assert.Contains(_audit.Events, e => e.Action == "winbox_ipv6_target_used");
    }

    [Fact]
    public async Task LaunchAsync_Ipv4Target_NoIpv6AuditEvent()
    {
        var (manifest, _) = CreateValidManifest();
        var runner = BuildRunner(manifest, AllowAllPolicy());

        await runner.LaunchAsync(MakeRequest(address: "10.0.0.1", port: 0));

        Assert.DoesNotContain(_audit.Events, e => e.Action == "winbox_ipv6_target_used");
    }

    [Fact]
    public async Task LaunchAsync_PasswordArgumentUsed_EmitsPasswordAuditEvent()
    {
        const string password = "P@ss123";
        var (manifest, _) = CreateValidManifest();
        var credResolver = new FakeCredentialResolver(() => password);
        var runner = BuildRunner(manifest, AllowPasswordPolicy(), credentialResolver: credResolver);

        await runner.LaunchAsync(MakeRequest(login: "admin", includePasswordArg: true, credentialRefId: "cred-01"));

        Assert.Contains(_audit.Events, e => e.Action == "winbox_password_argument_used");
    }

    // ── WinBoxToolManifest (testes unitários diretos) ──────────────────────────

    [Fact]
    public void WinBoxToolManifest_NullSha256_ThrowsValidationException()
    {
        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = null,
            ExecutablePath = "winbox.exe",
        };

        Assert.Throws<WinBoxValidationException>(manifest.Validate);
    }

    [Fact]
    public void WinBoxToolManifest_PlaceholderSha256_ThrowsValidationException()
    {
        foreach (var placeholder in new[] { "...", "<sha256>", "abc", string.Empty, "   " })
        {
            var manifest = new WinBoxToolManifest
            {
                Tool = "winbox",
                Version = "4.x",
                File = "winbox.exe",
                Sha256 = placeholder,
                ExecutablePath = "winbox.exe",
            };
            Assert.Throws<WinBoxValidationException>(manifest.Validate);
        }
    }

    [Fact]
    public void WinBoxToolManifest_ValidSha256_FileNotFound_ThrowsValidationException()
    {
        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = new string('a', 64), // 64 hex chars mas arquivo não existe
            ExecutablePath = "C:\\nonexistent\\winbox.exe",
        };

        Assert.Throws<WinBoxValidationException>(manifest.Validate);
    }

    [Fact]
    public void WinBoxToolManifest_WrongHash_ThrowsValidationException()
    {
        var tempFile = Path.GetTempFileName();
        _tempFiles.Add(tempFile);
        File.WriteAllText(tempFile, "content");

        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = new string('0', 64), // hash errado
            ExecutablePath = tempFile,
        };

        Assert.Throws<WinBoxValidationException>(manifest.Validate);
    }

    [Fact]
    public void WinBoxToolManifest_CorrectHash_PassesValidation()
    {
        var (manifest, _) = CreateValidManifest();
        var ex = Record.Exception(manifest.Validate);
        Assert.Null(ex);
    }

    // ── LocalWinBoxPolicyProvider (testes unitários diretos) ───────────────────

    [Fact]
    public async Task PolicyProvider_Default_AllowsWithNoPasswordArg()
    {
        var provider = new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig());
        var decision = await provider.EvaluateAsync("ws-01", "host-01", "user-01");

        Assert.True(decision.Allowed);
        Assert.False(decision.PasswordArgumentAllowed); // Modo A por padrão
    }

    [Fact]
    public async Task PolicyProvider_DeniedHost_ReturnsDenied()
    {
        var provider = new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            DeniedHostIds = ["host-X"],
        });
        var decision = await provider.EvaluateAsync("ws-01", "host-X", "user-01");

        Assert.False(decision.Allowed);
        Assert.NotNull(decision.DenyReason);
    }

    [Fact]
    public async Task PolicyProvider_DeniedWorkspace_ReturnsDenied()
    {
        var provider = new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            DeniedWorkspaceIds = ["ws-restricted"],
        });
        var decision = await provider.EvaluateAsync("ws-restricted", null, "user-01");

        Assert.False(decision.Allowed);
        Assert.NotNull(decision.DenyReason);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private WinBoxRunner BuildRunner(
        WinBoxToolManifest? manifest = null,
        IWinBoxPolicyProvider? policyProvider = null,
        IWinBoxCredentialResolver? credentialResolver = null)
    {
        manifest ??= BadManifest();
        policyProvider ??= new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig());
        credentialResolver ??= new FakeCredentialResolver();
        return new WinBoxRunner(manifest, policyProvider, _audit, credentialResolver, _launcher);
    }

    private static WinBoxToolManifest BadManifest() =>
        new()
        {
            Tool = "winbox",
            Version = "4.x",
            File = "winbox.exe",
            Sha256 = null,
            ExecutablePath = "winbox.exe",
        };

    private (WinBoxToolManifest manifest, string tempPath) CreateValidManifest()
    {
        var tempFile = Path.GetTempFileName();
        _tempFiles.Add(tempFile);
        File.WriteAllBytes(tempFile, "fake-winbox-binary"u8.ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tempFile))).ToLowerInvariant();
        var manifest = new WinBoxToolManifest
        {
            Tool = "winbox",
            Version = "4.x-test",
            File = "winbox.exe",
            Sha256 = hash,
            ExecutablePath = tempFile,
            ApprovedAt = DateTimeOffset.UtcNow,
            ApprovedBy = "test",
        };
        return (manifest, tempFile);
    }

    private static IWinBoxPolicyProvider AllowAllPolicy() =>
        new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            PasswordArgumentAllowed = false,
        });

    private static IWinBoxPolicyProvider AllowPasswordPolicy() =>
        new LocalWinBoxPolicyProvider(new WinBoxPolicyConfig
        {
            PasswordArgumentAllowed = true,
        });

    private static ExternalToolLaunchRequest MakeRequest(
        string address = "10.0.0.1",
        int port = 0,
        string? hostId = null,
        string? workspaceId = null,
        string? login = null,
        bool includePasswordArg = false,
        string? credentialRefId = null,
        ExternalToolRomon? romon = null)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId ?? "ws-01",
            Tool = "winbox",
            HostId = hostId,
            Target = new ExternalToolTarget { Address = address, Port = port },
            Login = login,
            IncludePasswordArgument = includePasswordArg,
            CredentialRefId = credentialRefId,
            Romon = romon,
            RequestedBy = "user-01",
            RequestedAt = DateTimeOffset.UtcNow,
        };

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
    }

    // ── Fakes ───────────────────────────────────────────────────────────────────

    private sealed class FakeAuditSink : IWinBoxAuditSink
    {
        public List<AuditEvent> Events { get; } = [];

        public Task EmitAsync(AuditEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessLauncher : IWinBoxProcessLauncher
    {
        public List<ProcessStartInfo> Calls { get; } = [];

        public Task<string> StartAsync(ProcessStartInfo psi, CancellationToken ct)
        {
            Calls.Add(psi);
            return Task.FromResult("fake-pid-1234");
        }
    }

    private sealed class FakeCredentialResolver : IWinBoxCredentialResolver
    {
        private readonly Func<string?> _resolve;

        public FakeCredentialResolver(Func<string?>? resolve = null)
        {
            _resolve = resolve ?? (() => null);
        }

        public Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default)
            => Task.FromResult(_resolve());
    }
}
