# RDP — Sessão RDP/Terminal Server real (MSTSCAX ActiveX em aba) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open a real RDP session from the Inspector — host with an `rdp` endpoint shows "Conectar RDP"; clicking it opens a live tab hosting the Microsoft RDP ActiveX control (MSTSCAX) via `WindowsFormsHost`, connecting with the resolved username + vault password, redirections OFF by default, NLA on, audited start/end — all gated behind a `rdp.enabled` feature flag (default OFF).

**Architecture:** `RemoteOps.Rdp` gets a pure, COM-free configuration layer (`RdpConnectionConfigBuilder`) plus an `IRdpSessionProvider` implementation that does only the non-visual work (resolve endpoint, build config, audit, return `SessionHandle`). `RemoteOps.Desktop` adds a `RdpTabViewModel`/`RdpTabView` pair mirroring the existing `TerminalTabViewModel`/`TerminalTabView` "aba viva" pattern: the ViewModel owns session lifecycle and exposes connection config; the View hosts the MSTSCAX ActiveX control in a `WindowsFormsHost` and triggers `Connect()` once loaded, pulling the password from the vault at that exact moment (mirrors ADR-009 §FIX-3 minimal-lifetime pattern) and never storing it on the ViewModel. A new `IFeatureFlags` abstraction (default OFF) gates the whole path; with the flag off, `rdp` falls back to today's `Tabs.OpenTab(...)` placeholder.

**Tech Stack:** C#/.NET 10, WPF, `Microsoft.Extensions.DependencyInjection` keyed services, MSTSCAX (`mstscax.dll`) via `<COMReference>` + `WindowsFormsHost`, xUnit.

## Global Constraints

- Never write password/secret in plaintext to disk, log, commit, or `ToString()`. Use `using var secret = await vault.RetrieveAsync(...); secret.RevealString()` with the narrowest possible scope (ADR-009 §FIX-3 pattern).
- Never save the credential to the Windows global Credential Manager without explicit policy.
- Never ignore a certificate warning without an audited event.
- Sensitive redirections (clipboard, drive, printer, audio, USB) default OFF; only enabled via explicit policy/profile (none exists yet — MVP ships OFF with no override path).
- New feature → feature flag `rdp.enabled`, default OFF, reviewed by `security-agent` before flip.
- Branch is `feature/integration-rdp`, worktree at `C:/dev/remoteops-int-rdp`. Commit small. Don't touch other modules' folders without saying so.
- `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` must all be green (`TreatWarningsAsErrors` is on via the repo's default analyzers — match existing nullable/style conventions exactly).
- ADR numbering: ADR-013 is reserved by the parallel `feature/integration-sync` front — **use ADR-014**, not 013. Leave the PR's `Depends-on:` field empty (everything RDP needs is already on `main`).
- This repo's CI (`dotnet build`/`test`) runs on `windows-latest` only — `tests/RemoteOps.UnitTests` already targets `net10.0-windows`, so there is no cross-platform constraint to satisfy beyond keeping COM/UI code physically separate from the pure logic (for your own ability to unit-test it without a rendered control).

---

## File Structure

```
src/RemoteOps.Rdp/
  RemoteOps.Rdp.csproj                     [MODIFY] COMReference (MSTSCLib) + UseWindowsForms/UseWPF
  IRdpSessionProvider.cs                   [MODIFY] add GetConnectionConfig(sessionId)
  RdpRedirectionPolicy.cs                  [NEW] pure policy record
  RdpConnectionConfig.cs                   [NEW] pure config record
  RdpConnectionConfigBuilder.cs            [NEW] pure builder (host/port/username/NLA/policy)
  IRdpEndpointResolver.cs                  [NEW]
  IRdpCredentialRefResolver.cs             [NEW] (username metadata only — no vault)
  IRdpCredentialResolver.cs                [NEW] (password via vault — analogous to IWinBoxCredentialResolver)
  IRdpSecurityContext.cs                   [NEW]
  RdpAuditEvent.cs                         [NEW] RdpAuditEvent + IRdpAuditSink + RdpActions
  RdpSessionProvider.cs                    [NEW]

src/RemoteOps.Desktop/
  RemoteOps.Desktop.csproj                 [MODIFY] UseWindowsForms=true; ProjectReference RemoteOps.Rdp
  Infrastructure/IFeatureFlags.cs          [NEW] IFeatureFlags + FeatureFlagNames + EnvironmentFeatureFlags
  Integration/LocalStoreRdpEndpointResolver.cs       [NEW]
  Integration/LocalStoreRdpCredentialRefResolver.cs  [NEW]
  Integration/RdpCredentialResolver.cs               [NEW]
  Integration/StructuredRdpAuditSink.cs              [NEW]
  Integration/AppTerminalSecurityContext.cs          [MODIFY] also implement RemoteOps.Rdp.IRdpSecurityContext
  Integration/AppCompositionRoot.cs                  [MODIFY] register RDP graph + feature flags
  Rdp/RdpTabViewModel.cs                   [NEW]
  Rdp/RdpTabView.xaml                      [NEW]
  Rdp/RdpTabView.xaml.cs                   [NEW]
  ViewModels/TabsViewModel.cs              [MODIFY] OpenRdpTab + CloseTab handling
  ViewModels/MainViewModel.cs              [MODIFY] rdpProvider/credentialResolver/featureFlags + case Rdp
  ViewModels/InspectorViewModel.cs         [MODIFY] CanOpenRdp
  Views/TabsView.xaml                      [MODIFY] DataTemplate RdpTabViewModel→RdpTabView
  Views/InspectorView.xaml                 [MODIFY] RDP button visibility binding

tests/RemoteOps.UnitTests/
  Rdp/RdpConnectionConfigBuilderTests.cs                 [NEW]
  Rdp/RdpSessionProviderTests.cs                         [NEW]
  Rdp/Fakes/InMemoryRdpEndpointResolver.cs               [NEW]
  Rdp/Fakes/InMemoryRdpCredentialRefResolver.cs          [NEW]
  Rdp/Fakes/InMemoryRdpAuditSink.cs                      [NEW]
  Rdp/Fakes/FakeRdpSecurityContext.cs                    [NEW]
  Desktop/Infrastructure/EnvironmentFeatureFlagsTests.cs [NEW]
  Desktop/Integration/RdpCredentialResolverTests.cs      [NEW]
  Desktop/Rdp/Fakes/FakeRdpSessionProvider.cs            [NEW]
  Desktop/Rdp/Fakes/FakeRdpCredentialResolver.cs         [NEW]
  Desktop/Rdp/RdpTabViewModelTests.cs                    [NEW]
  Desktop/TabsViewModelRdpTests.cs                       [NEW]
  Desktop/InspectorViewModelRdpTests.cs                  [NEW]
  Desktop/MainViewModelRdpTests.cs                       [NEW]
  Desktop/CompositionRootSmokeTests.cs                   [MODIFY] add RDP resolutions

adr/ADR-004-rdp-activex-vs-freerdp.md          [MODIFY] Status → Aceita + consequências MVP
adr/ADR-014-rdp-hospedagem-activex-e-politicas.md [NEW]
docs/08-rdp-terminal-server.md                  [MODIFY] fluxo real implementado
CHANGELOG.md                                    [MODIFY]
```

**Interfaces summary (so later tasks don't have to re-derive signatures):**

```csharp
// RemoteOps.Rdp
public sealed record RdpRedirectionPolicy { bool ClipboardRedirectionEnabled; bool DriveRedirectionEnabled; bool PrinterRedirectionEnabled; bool AudioRedirectionEnabled; bool UsbRedirectionEnabled; static RdpRedirectionPolicy Default; }
public sealed record RdpConnectionConfig { string Host; int Port; string Username; bool NlaRequired; RdpRedirectionPolicy Redirection; }
public static class RdpConnectionConfigBuilder {
    public static string ResolveHost(Endpoint endpoint, bool preferIpv6);
    public static RdpConnectionConfig Build(Endpoint endpoint, string username, bool preferIpv6, RdpRedirectionPolicy? redirectionPolicy = null);
}
public interface IRdpEndpointResolver { Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default); }
public interface IRdpCredentialRefResolver { Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default); }
public interface IRdpCredentialResolver { Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default); }
public interface IRdpSecurityContext { string ActorUserId; string? DeviceId; }
public sealed record RdpAuditEvent { string Action; string SessionId; string Host; string? CertificateThumbprint; string? UserId; DateTimeOffset OccurredAt; }
public interface IRdpAuditSink { Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default); }
public static class RdpActions { SessionOpened="rdp.session.opened"; SessionClosed="rdp.session.closed"; CertificateAccepted="rdp.certificate.accepted"; CertificateRejected="rdp.certificate.rejected"; ConnectFailed="rdp.connect.failed"; }
public interface IRdpSessionProvider : IRemoteSessionProvider { RdpConnectionConfig GetConnectionConfig(string sessionId); }
public sealed class RdpSessionProvider : IRdpSessionProvider { /* ctor(IRdpEndpointResolver, IRdpCredentialRefResolver, IRdpSecurityContext, IRdpAuditSink) */ }

// RemoteOps.Desktop
public interface IFeatureFlags { bool IsEnabled(string flagName); }
public static class FeatureFlagNames { const string RdpEnabled = "rdp.enabled"; }
public sealed class EnvironmentFeatureFlags : IFeatureFlags { /* ctor(string? rawFlags = null) */ }
public sealed class RdpTabViewModel : SessionTabViewModel {
    /* ctor(string id, string title, string protocol, IRdpSessionProvider provider, IRdpCredentialResolver credentialResolver, SessionRequest baseRequest) */
    Task<RdpConnectionConfig> PrepareAsync(CancellationToken ct = default);
    Task<string?> ResolvePasswordAsync(CancellationToken ct = default);
    void MarkConnected();
    void MarkDisconnected(string? reason);
    Task CloseAsync();
    bool IsConnected { get; }
    RdpConnectionConfig? ConnectionConfig { get; }
    event Action<string>? ConnectFailed;
}
```

---

## Task 1: Pure RDP connection config (host/port/username/NLA/redirection)

**Files:**
- Create: `src/RemoteOps.Rdp/RdpRedirectionPolicy.cs`
- Create: `src/RemoteOps.Rdp/RdpConnectionConfig.cs`
- Create: `src/RemoteOps.Rdp/RdpConnectionConfigBuilder.cs`
- Test: `tests/RemoteOps.UnitTests/Rdp/RdpConnectionConfigBuilderTests.cs`

**Interfaces:**
- Consumes: `RemoteOps.Contracts.Assets.Endpoint` (existing).
- Produces: `RdpRedirectionPolicy`, `RdpConnectionConfig`, `RdpConnectionConfigBuilder.Build(...)`/`.ResolveHost(...)` — consumed by Task 2 (`RdpSessionProvider`) and Task 5 (`RdpTabViewModel`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RemoteOps.UnitTests/Rdp/RdpConnectionConfigBuilderTests.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Rdp;
using Xunit;

namespace RemoteOps.UnitTests.Rdp;

public sealed class RdpConnectionConfigBuilderTests
{
    private static Endpoint MakeEndpoint(
        string? ipv4 = "10.0.0.5",
        string? ipv6 = null,
        string? fqdn = null,
        int port = 0) => new()
    {
        Id = "ep-1",
        AssetId = "asset-1",
        Protocol = "rdp",
        Ipv4 = ipv4,
        Ipv6 = ipv6,
        Fqdn = fqdn,
        Port = port,
    };

    [Fact]
    public void ResolveHost_PreferIpv6True_AndIpv6Present_ReturnsIpv6()
    {
        var ep = MakeEndpoint(ipv4: "10.0.0.5", ipv6: "fe80::1");
        Assert.Equal("fe80::1", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: true));
    }

    [Fact]
    public void ResolveHost_PreferIpv6True_ButNoIpv6_FallsBackToIpv4()
    {
        var ep = MakeEndpoint(ipv4: "10.0.0.5", ipv6: null);
        Assert.Equal("10.0.0.5", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: true));
    }

    [Fact]
    public void ResolveHost_PreferIpv6False_ReturnsIpv4()
    {
        var ep = MakeEndpoint(ipv4: "10.0.0.5", ipv6: "fe80::1");
        Assert.Equal("10.0.0.5", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: false));
    }

    [Fact]
    public void ResolveHost_NoIps_FallsBackToFqdn()
    {
        var ep = MakeEndpoint(ipv4: null, ipv6: null, fqdn: "host.example.com");
        Assert.Equal("host.example.com", RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: false));
    }

    [Fact]
    public void ResolveHost_NoAddressAtAll_Throws()
    {
        var ep = MakeEndpoint(ipv4: null, ipv6: null, fqdn: null);
        Assert.Throws<InvalidOperationException>(() => RdpConnectionConfigBuilder.ResolveHost(ep, preferIpv6: false));
    }

    [Fact]
    public void Build_PortZero_DefaultsTo3389()
    {
        var ep = MakeEndpoint(port: 0);
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.Equal(3389, config.Port);
    }

    [Fact]
    public void Build_CustomPort_IsPreserved()
    {
        var ep = MakeEndpoint(port: 33890);
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.Equal(33890, config.Port);
    }

    [Fact]
    public void Build_UsernameFromCredentialRef_IsPropagated()
    {
        var ep = MakeEndpoint();
        var config = RdpConnectionConfigBuilder.Build(ep, username: "CORP\\admin", preferIpv6: false);
        Assert.Equal("CORP\\admin", config.Username);
    }

    [Fact]
    public void Build_NlaRequired_DefaultsTrue()
    {
        var ep = MakeEndpoint();
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.True(config.NlaRequired);
    }

    [Fact]
    public void Build_RedirectionPolicy_DefaultsAllOff()
    {
        var ep = MakeEndpoint();
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false);
        Assert.False(config.Redirection.ClipboardRedirectionEnabled);
        Assert.False(config.Redirection.DriveRedirectionEnabled);
        Assert.False(config.Redirection.PrinterRedirectionEnabled);
        Assert.False(config.Redirection.AudioRedirectionEnabled);
        Assert.False(config.Redirection.UsbRedirectionEnabled);
    }

    [Fact]
    public void Build_ExplicitRedirectionPolicy_IsHonored()
    {
        var ep = MakeEndpoint();
        var policy = new RdpRedirectionPolicy { ClipboardRedirectionEnabled = true };
        var config = RdpConnectionConfigBuilder.Build(ep, username: "admin", preferIpv6: false, redirectionPolicy: policy);
        Assert.True(config.Redirection.ClipboardRedirectionEnabled);
        Assert.False(config.Redirection.DriveRedirectionEnabled);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpConnectionConfigBuilderTests`
Expected: build error — `RdpConnectionConfigBuilder`/`RdpRedirectionPolicy`/`RdpConnectionConfig` don't exist yet.

- [ ] **Step 3: Implement**

```csharp
// src/RemoteOps.Rdp/RdpRedirectionPolicy.cs
namespace RemoteOps.Rdp;

/// <summary>
/// Política de redirecionamentos do MSTSCAX. Todos OFF por padrão (requisito de
/// segurança) — só habilitados via política/profile explícito.
/// </summary>
public sealed record RdpRedirectionPolicy
{
    public bool ClipboardRedirectionEnabled { get; init; }
    public bool DriveRedirectionEnabled { get; init; }
    public bool PrinterRedirectionEnabled { get; init; }
    public bool AudioRedirectionEnabled { get; init; }
    public bool UsbRedirectionEnabled { get; init; }

    public static RdpRedirectionPolicy Default { get; } = new();
}
```

```csharp
// src/RemoteOps.Rdp/RdpConnectionConfig.cs
namespace RemoteOps.Rdp;

/// <summary>Configuração de conexão RDP resolvida — sem segredo, sem COM, totalmente testável.</summary>
public sealed record RdpConnectionConfig
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required bool NlaRequired { get; init; }
    public required RdpRedirectionPolicy Redirection { get; init; }
}
```

```csharp
// src/RemoteOps.Rdp/RdpConnectionConfigBuilder.cs
using RemoteOps.Contracts.Assets;

namespace RemoteOps.Rdp;

/// <summary>
/// Monta a configuração de conexão RDP a partir de Endpoint + usuário resolvido.
/// Classe pura — sem COM, sem UI — testável em qualquer plataforma.
/// </summary>
public static class RdpConnectionConfigBuilder
{
    private const int DefaultPort = 3389;

    public static RdpConnectionConfig Build(
        Endpoint endpoint,
        string username,
        bool preferIpv6,
        RdpRedirectionPolicy? redirectionPolicy = null)
    {
        string host = ResolveHost(endpoint, preferIpv6);
        int port = endpoint.Port > 0 ? endpoint.Port : DefaultPort;

        return new RdpConnectionConfig
        {
            Host = host,
            Port = port,
            Username = username,
            // NLA é obrigatório no MVP — sem opção de desligar (requisito de segurança).
            NlaRequired = true,
            Redirection = redirectionPolicy ?? RdpRedirectionPolicy.Default,
        };
    }

    public static string ResolveHost(Endpoint endpoint, bool preferIpv6)
    {
        if (preferIpv6 && !string.IsNullOrWhiteSpace(endpoint.Ipv6)) return endpoint.Ipv6;
        if (!string.IsNullOrWhiteSpace(endpoint.Ipv4)) return endpoint.Ipv4;
        if (!string.IsNullOrWhiteSpace(endpoint.Fqdn)) return endpoint.Fqdn;
        if (!string.IsNullOrWhiteSpace(endpoint.Ipv6)) return endpoint.Ipv6;
        throw new InvalidOperationException($"Endpoint '{endpoint.Id}' não tem endereço resolvível.");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpConnectionConfigBuilderTests`
Expected: 11 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Rdp/RdpRedirectionPolicy.cs src/RemoteOps.Rdp/RdpConnectionConfig.cs src/RemoteOps.Rdp/RdpConnectionConfigBuilder.cs tests/RemoteOps.UnitTests/Rdp/RdpConnectionConfigBuilderTests.cs
git commit -m "feat(rdp): config de conexão RDP pura (host/porta/usuário/políticas)"
```

---

## Task 2: RDP resolver/audit contracts + RdpSessionProvider

**Files:**
- Modify: `src/RemoteOps.Rdp/IRdpSessionProvider.cs`
- Create: `src/RemoteOps.Rdp/IRdpEndpointResolver.cs`
- Create: `src/RemoteOps.Rdp/IRdpCredentialRefResolver.cs`
- Create: `src/RemoteOps.Rdp/IRdpCredentialResolver.cs`
- Create: `src/RemoteOps.Rdp/IRdpSecurityContext.cs`
- Create: `src/RemoteOps.Rdp/RdpAuditEvent.cs`
- Create: `src/RemoteOps.Rdp/RdpSessionProvider.cs`
- Create: `tests/RemoteOps.UnitTests/Rdp/Fakes/InMemoryRdpEndpointResolver.cs`
- Create: `tests/RemoteOps.UnitTests/Rdp/Fakes/InMemoryRdpCredentialRefResolver.cs`
- Create: `tests/RemoteOps.UnitTests/Rdp/Fakes/InMemoryRdpAuditSink.cs`
- Create: `tests/RemoteOps.UnitTests/Rdp/Fakes/FakeRdpSecurityContext.cs`
- Test: `tests/RemoteOps.UnitTests/Rdp/RdpSessionProviderTests.cs`

**Interfaces:**
- Consumes: `RdpConnectionConfigBuilder` (Task 1), `RemoteOps.Contracts.Sessions.{SessionRequest,SessionHandle,RemoteProtocol}`, `RemoteOps.Contracts.Assets.{Endpoint,CredentialRef}`.
- Produces: `IRdpSessionProvider` (with `GetConnectionConfig`), `RdpSessionProvider` — consumed by Task 5 (`RdpTabViewModel`), Task 6 (DI wiring).

**Design note:** `RdpSessionProvider` never touches the vault. It only resolves `Endpoint` (host/port) and `CredentialRef.Metadata.Username` (not secret) to build the `RdpConnectionConfig`, then audits. The password is resolved later, by `RdpTabViewModel`/`RdpTabView` at actual `Connect()` time (Task 5), via the separate `IRdpCredentialResolver` (Task 3/4) — this keeps the password's lifetime as short as possible and structurally impossible to leak through `RdpSessionProvider`'s audit path.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RemoteOps.UnitTests/Rdp/Fakes/InMemoryRdpEndpointResolver.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Rdp.Fakes;

internal sealed class InMemoryRdpEndpointResolver : IRdpEndpointResolver
{
    private readonly Dictionary<string, Endpoint> _endpoints = [];

    public void Add(Endpoint endpoint) => _endpoints[endpoint.Id] = endpoint;

    public Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default)
        => _endpoints.TryGetValue(endpointId, out var ep)
            ? Task.FromResult(ep)
            : Task.FromException<Endpoint>(new KeyNotFoundException($"Endpoint '{endpointId}' não encontrado."));
}
```

```csharp
// tests/RemoteOps.UnitTests/Rdp/Fakes/InMemoryRdpCredentialRefResolver.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Rdp.Fakes;

internal sealed class InMemoryRdpCredentialRefResolver : IRdpCredentialRefResolver
{
    private readonly Dictionary<string, CredentialRef> _refs = [];

    public void Add(CredentialRef credRef) => _refs[credRef.Id] = credRef;

    public Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default)
        => _refs.TryGetValue(credentialRefId, out var cr)
            ? Task.FromResult(cr)
            : Task.FromException<CredentialRef>(new KeyNotFoundException($"CredentialRef '{credentialRefId}' não encontrada."));
}
```

```csharp
// tests/RemoteOps.UnitTests/Rdp/Fakes/InMemoryRdpAuditSink.cs
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Rdp.Fakes;

internal sealed class InMemoryRdpAuditSink : IRdpAuditSink
{
    private readonly List<RdpAuditEvent> _events = [];

    public IReadOnlyList<RdpAuditEvent> Events => _events;

    public Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default)
    {
        _events.Add(auditEvent);
        return Task.CompletedTask;
    }
}
```

```csharp
// tests/RemoteOps.UnitTests/Rdp/Fakes/FakeRdpSecurityContext.cs
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Rdp.Fakes;

internal sealed class FakeRdpSecurityContext : IRdpSecurityContext
{
    public string ActorUserId { get; init; } = "test-user";
    public string? DeviceId { get; init; }
}
```

```csharp
// tests/RemoteOps.UnitTests/Rdp/RdpSessionProviderTests.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Rdp;
using RemoteOps.UnitTests.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Rdp;

public sealed class RdpSessionProviderTests
{
    private const string EndpointId = "ep-1";
    private const string CredRefId = "cr-1";

    private static (RdpSessionProvider provider, InMemoryRdpEndpointResolver eps,
        InMemoryRdpCredentialRefResolver creds, InMemoryRdpAuditSink audit) Build()
    {
        var eps = new InMemoryRdpEndpointResolver();
        var creds = new InMemoryRdpCredentialRefResolver();
        var audit = new InMemoryRdpAuditSink();
        var secCtx = new FakeRdpSecurityContext();
        var provider = new RdpSessionProvider(eps, creds, secCtx, audit);
        return (provider, eps, creds, audit);
    }

    private static void SeedFixtures(InMemoryRdpEndpointResolver eps, InMemoryRdpCredentialRefResolver creds)
    {
        eps.Add(new Endpoint
        {
            Id = EndpointId,
            AssetId = "asset-1",
            Protocol = RemoteProtocol.Rdp,
            Ipv4 = "10.0.0.5",
            Port = 0,
        });

        creds.Add(new CredentialRef
        {
            Id = CredRefId,
            Name = "Test Cred",
            Type = "password",
            SecretEnvelopeId = "env-1",
            Metadata = new CredentialMetadata { Username = "CORP\\admin" },
        });
    }

    private static SessionRequest MakeRequest(string? sessionId = null) => new()
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString("n"),
        Protocol = RemoteProtocol.Rdp,
        EndpointId = EndpointId,
        CredentialRefId = CredRefId,
        PreferIpv6 = false,
    };

    [Fact]
    public void Protocol_ReturnsRdp()
    {
        var (provider, _, _, _) = Build();
        Assert.Equal(RemoteProtocol.Rdp, provider.Protocol);
    }

    [Fact]
    public async Task OpenAsync_ReturnsOpenHandle()
    {
        var (provider, eps, creds, _) = Build();
        SeedFixtures(eps, creds);

        var handle = await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        Assert.True(handle.IsOpen);
        Assert.Equal(RemoteProtocol.Rdp, handle.Protocol);
        Assert.Equal(EndpointId, handle.EndpointId);
    }

    [Fact]
    public async Task OpenAsync_BuildsConnectionConfig_WithDefaultPortAndResolvedUsername()
    {
        var (provider, eps, creds, _) = Build();
        SeedFixtures(eps, creds);
        var request = MakeRequest();

        await provider.OpenAsync(request, CancellationToken.None);
        var config = provider.GetConnectionConfig(request.SessionId);

        Assert.Equal("10.0.0.5", config.Host);
        Assert.Equal(3389, config.Port);
        Assert.Equal("CORP\\admin", config.Username);
        Assert.True(config.NlaRequired);
        Assert.False(config.Redirection.ClipboardRedirectionEnabled);
    }

    [Fact]
    public async Task OpenAsync_EmitsSessionOpenedAudit()
    {
        var (provider, eps, creds, audit) = Build();
        SeedFixtures(eps, creds);
        var request = MakeRequest();

        await provider.OpenAsync(request, CancellationToken.None);

        var ev = Assert.Single(audit.Events);
        Assert.Equal(RdpActions.SessionOpened, ev.Action);
        Assert.Equal(request.SessionId, ev.SessionId);
        Assert.Equal("10.0.0.5", ev.Host);
        Assert.Equal("test-user", ev.UserId);
    }

    [Fact]
    public async Task OpenAsync_MissingUsername_Throws()
    {
        var (provider, eps, creds, _) = Build();
        eps.Add(new Endpoint { Id = EndpointId, AssetId = "a", Protocol = RemoteProtocol.Rdp, Ipv4 = "10.0.0.5" });
        creds.Add(new CredentialRef { Id = CredRefId, Name = "x", Type = "password", SecretEnvelopeId = "env-1" });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAsync(MakeRequest(), CancellationToken.None));
    }

    [Fact]
    public void GetConnectionConfig_BeforeOpen_Throws()
    {
        var (provider, _, _, _) = Build();
        Assert.Throws<InvalidOperationException>(() => provider.GetConnectionConfig("never-opened"));
    }

    [Fact]
    public async Task CloseAsync_ClosesHandleAndEmitsSessionClosedAudit()
    {
        var (provider, eps, creds, audit) = Build();
        SeedFixtures(eps, creds);
        var request = MakeRequest();
        var handle = await provider.OpenAsync(request, CancellationToken.None);

        await provider.CloseAsync(handle, CancellationToken.None);

        Assert.False(handle.IsOpen);
        Assert.Equal(2, audit.Events.Count);
        Assert.Equal(RdpActions.SessionClosed, audit.Events[1].Action);
        Assert.Equal("10.0.0.5", audit.Events[1].Host);
    }

    [Fact]
    public async Task CloseAsync_UnknownHandle_DoesNotThrowOrAudit()
    {
        var (provider, _, _, audit) = Build();
        var fakeHandle = new SessionHandle
        {
            SessionId = "never-opened",
            Protocol = RemoteProtocol.Rdp,
            EndpointId = EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };

        await provider.CloseAsync(fakeHandle, CancellationToken.None);

        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task RdpSessionProvider_HasNoVaultDependency_AuditNeverContainsSecretLikeFields()
    {
        // Regressão estrutural: RdpSessionProvider não recebe IRdpCredentialResolver/IVault
        // no construtor — não há rota possível para um segredo entrar no RdpAuditEvent.
        var (provider, eps, creds, audit) = Build();
        SeedFixtures(eps, creds);

        await provider.OpenAsync(MakeRequest(), CancellationToken.None);

        foreach (var ev in audit.Events)
        {
            Assert.DoesNotContain("s3cr3t", ev.ToString());
            Assert.DoesNotContain("s3cr3t", ev.UserId ?? string.Empty);
            Assert.DoesNotContain("s3cr3t", ev.CertificateThumbprint ?? string.Empty);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpSessionProviderTests`
Expected: build error — types don't exist.

- [ ] **Step 3: Implement**

```csharp
// src/RemoteOps.Rdp/IRdpEndpointResolver.cs
using RemoteOps.Contracts.Assets;

namespace RemoteOps.Rdp;

/// <summary>Resolve um EndpointId para os dados reais de conexão. Implementado pelo Desktop.</summary>
public interface IRdpEndpointResolver
{
    Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default);
}
```

```csharp
// src/RemoteOps.Rdp/IRdpCredentialRefResolver.cs
using RemoteOps.Contracts.Assets;

namespace RemoteOps.Rdp;

/// <summary>
/// Resolve metadados da credencial (usuário) — NUNCA toca o vault. O segredo é
/// obtido separadamente via <see cref="IRdpCredentialResolver"/> apenas no momento
/// de conectar (lifetime mínimo).
/// </summary>
public interface IRdpCredentialRefResolver
{
    Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default);
}
```

```csharp
// src/RemoteOps.Rdp/IRdpCredentialResolver.cs
namespace RemoteOps.Rdp;

/// <summary>
/// Resolve a senha de uma credencial via vault. Análogo a IWinBoxCredentialResolver:
/// retorna a senha em texto puro apenas para a fronteira que exige (ClearTextPassword
/// do MSTSCAX) — o chamador deve usar e descartar a string imediatamente.
/// </summary>
public interface IRdpCredentialResolver
{
    Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default);
}
```

```csharp
// src/RemoteOps.Rdp/IRdpSecurityContext.cs
namespace RemoteOps.Rdp;

/// <summary>Contexto de segurança do usuário corrente para auditoria RDP.</summary>
public interface IRdpSecurityContext
{
    string ActorUserId { get; }
    string? DeviceId { get; }
}
```

```csharp
// src/RemoteOps.Rdp/RdpAuditEvent.cs
namespace RemoteOps.Rdp;

/// <summary>
/// Evento de auditoria de sessão RDP. Por construção NÃO contém credencial, senha
/// ou conteúdo de tela — apenas identificadores e metadados inócuos.
/// </summary>
public sealed record RdpAuditEvent
{
    public required string Action { get; init; }
    public required string SessionId { get; init; }
    public required string Host { get; init; }

    /// <summary>Thumbprint SHA-256/SHA-1 do certificado do servidor (hex). Não é um segredo.</summary>
    public string? CertificateThumbprint { get; init; }

    public string? UserId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    public override string ToString() => $"{Action} rdp://{Host} [{SessionId}]";
}

public interface IRdpAuditSink
{
    Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default);
}

/// <summary>Ações de auditoria RDP. Alinhadas a docs/08-rdp-terminal-server.md.</summary>
public static class RdpActions
{
    public const string SessionOpened = "rdp.session.opened";
    public const string SessionClosed = "rdp.session.closed";
    public const string CertificateAccepted = "rdp.certificate.accepted";
    public const string CertificateRejected = "rdp.certificate.rejected";
    public const string ConnectFailed = "rdp.connect.failed";
}
```

```csharp
// src/RemoteOps.Rdp/IRdpSessionProvider.cs (replaces existing placeholder)
using RemoteOps.Contracts.Sessions;

namespace RemoteOps.Rdp;

public interface IRdpSessionProvider : IRemoteSessionProvider
{
    /// <summary>Config resolvida (host/porta/usuário/políticas) para a sessão aberta por OpenAsync.</summary>
    RdpConnectionConfig GetConnectionConfig(string sessionId);
}
```

```csharp
// src/RemoteOps.Rdp/RdpSessionProvider.cs
using System.Collections.Concurrent;
using RemoteOps.Contracts.Sessions;

namespace RemoteOps.Rdp;

/// <summary>
/// Trabalho não-visual da sessão RDP: resolve endpoint/usuário, monta a config,
/// audita início/fim, devolve SessionHandle. A conexão visual (Connect do MSTSCAX)
/// é disparada pela View — este provider NUNCA toca o vault (ver IRdpCredentialResolver).
/// </summary>
public sealed class RdpSessionProvider : IRdpSessionProvider
{
    private readonly IRdpEndpointResolver _endpointResolver;
    private readonly IRdpCredentialRefResolver _credentialRefResolver;
    private readonly IRdpSecurityContext _securityContext;
    private readonly IRdpAuditSink _auditSink;
    private readonly ConcurrentDictionary<string, SessionHandle> _sessions = new();
    private readonly ConcurrentDictionary<string, RdpConnectionConfig> _configs = new();

    public string Protocol => RemoteProtocol.Rdp;

    public RdpSessionProvider(
        IRdpEndpointResolver endpointResolver,
        IRdpCredentialRefResolver credentialRefResolver,
        IRdpSecurityContext securityContext,
        IRdpAuditSink auditSink)
    {
        _endpointResolver = endpointResolver;
        _credentialRefResolver = credentialRefResolver;
        _securityContext = securityContext;
        _auditSink = auditSink;
    }

    public async Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        var endpoint = await _endpointResolver.ResolveAsync(request.EndpointId, ct);
        var credRef = await _credentialRefResolver.ResolveAsync(request.CredentialRefId, ct);
        string username = credRef.Metadata?.Username
            ?? throw new InvalidOperationException(
                $"CredentialRef '{request.CredentialRefId}' não tem username em Metadata.");

        var config = RdpConnectionConfigBuilder.Build(endpoint, username, request.PreferIpv6);
        _configs[request.SessionId] = config;

        var handle = new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = RemoteProtocol.Rdp,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        };
        _sessions[request.SessionId] = handle;

        await _auditSink.EmitAsync(new RdpAuditEvent
        {
            Action = RdpActions.SessionOpened,
            SessionId = request.SessionId,
            Host = config.Host,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);

        return handle;
    }

    public RdpConnectionConfig GetConnectionConfig(string sessionId) =>
        _configs.TryGetValue(sessionId, out var config)
            ? config
            : throw new InvalidOperationException($"Sessão RDP '{sessionId}' não encontrada ou ainda não aberta.");

    public async Task CloseAsync(SessionHandle handle, CancellationToken ct)
    {
        if (!_sessions.TryRemove(handle.SessionId, out _)) return;
        _configs.TryGetValue(handle.SessionId, out var config);
        _configs.TryRemove(handle.SessionId, out _);
        handle.IsOpen = false;

        await _auditSink.EmitAsync(new RdpAuditEvent
        {
            Action = RdpActions.SessionClosed,
            SessionId = handle.SessionId,
            Host = config?.Host ?? handle.EndpointId,
            UserId = _securityContext.ActorUserId,
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "Rdp.RdpSessionProviderTests"`
Expected: 9 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Rdp/ tests/RemoteOps.UnitTests/Rdp/
git commit -m "feat(rdp): RdpSessionProvider — resolve, audita, sem acesso ao vault"
```

---

## Task 3: Feature flag `rdp.enabled` (default OFF)

**Files:**
- Create: `src/RemoteOps.Desktop/Infrastructure/IFeatureFlags.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/EnvironmentFeatureFlagsTests.cs`

**Interfaces:**
- Produces: `IFeatureFlags`, `FeatureFlagNames.RdpEnabled`, `EnvironmentFeatureFlags` — consumed by Task 6 (DI), Task 7 (MainViewModel), Task 8 (InspectorViewModel).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RemoteOps.UnitTests/Desktop/Infrastructure/EnvironmentFeatureFlagsTests.cs
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class EnvironmentFeatureFlagsTests
{
    [Fact]
    public void IsEnabled_NoFlagsConfigured_ReturnsFalse()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "");
        Assert.False(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void IsEnabled_FlagListed_ReturnsTrue()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "rdp.enabled");
        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void IsEnabled_MultipleFlags_CommaSeparated_ParsesAll()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "foo.bar, rdp.enabled ,baz");
        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
        Assert.True(flags.IsEnabled("foo.bar"));
        Assert.True(flags.IsEnabled("baz"));
    }

    [Fact]
    public void IsEnabled_IsCaseInsensitive()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "RDP.ENABLED");
        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void IsEnabled_UnknownFlag_ReturnsFalse()
    {
        var flags = new EnvironmentFeatureFlags(rawFlags: "rdp.enabled");
        Assert.False(flags.IsEnabled("ndesk.enabled"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter EnvironmentFeatureFlagsTests`
Expected: build error — `IFeatureFlags`/`EnvironmentFeatureFlags`/`FeatureFlagNames` don't exist.

- [ ] **Step 3: Implement**

```csharp
// src/RemoteOps.Desktop/Infrastructure/IFeatureFlags.cs
namespace RemoteOps.Desktop.Infrastructure;

public interface IFeatureFlags
{
    bool IsEnabled(string flagName);
}

/// <summary>Nomes de feature flags conhecidos pelo Desktop.</summary>
public static class FeatureFlagNames
{
    /// <summary>Habilita a sessão RDP real (MSTSCAX). Default OFF até o MVP ser validado.</summary>
    public const string RdpEnabled = "rdp.enabled";
}

/// <summary>
/// Lê flags habilitadas da variável de ambiente REMOTEOPS_FEATURE_FLAGS (lista
/// separada por vírgula). Sem a variável (ou vazia), nenhuma flag está habilitada.
/// </summary>
public sealed class EnvironmentFeatureFlags : IFeatureFlags
{
    private readonly HashSet<string> _enabled;

    public EnvironmentFeatureFlags(string? rawFlags = null)
    {
        rawFlags ??= Environment.GetEnvironmentVariable("REMOTEOPS_FEATURE_FLAGS");
        _enabled = new HashSet<string>(
            (rawFlags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled(string flagName) => _enabled.Contains(flagName);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter EnvironmentFeatureFlagsTests`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/Infrastructure/IFeatureFlags.cs tests/RemoteOps.UnitTests/Desktop/Infrastructure/EnvironmentFeatureFlagsTests.cs
git commit -m "feat(desktop): feature flag rdp.enabled (default OFF)"
```

---

## Task 4: Desktop Integration — resolvers, credential resolver, audit sink

**Files:**
- Create: `src/RemoteOps.Desktop/Integration/LocalStoreRdpEndpointResolver.cs`
- Create: `src/RemoteOps.Desktop/Integration/LocalStoreRdpCredentialRefResolver.cs`
- Create: `src/RemoteOps.Desktop/Integration/RdpCredentialResolver.cs`
- Create: `src/RemoteOps.Desktop/Integration/StructuredRdpAuditSink.cs`
- Modify: `src/RemoteOps.Desktop/Integration/AppTerminalSecurityContext.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Integration/RdpCredentialResolverTests.cs`

**Interfaces:**
- Consumes: `ILocalStore` (existing), `IVault`/`VaultSecret`/`VaultAccessContext` (existing), `RemoteOps.Rdp.{IRdpEndpointResolver,IRdpCredentialRefResolver,IRdpCredentialResolver,IRdpAuditSink,IRdpSecurityContext}` (Task 2).
- Produces: concrete implementations registered in Task 6.

**Note:** `RdpCredentialResolverTests` reuses `RemoteOps.UnitTests.Terminal.Fakes.FakeVault` (already in the test assembly — `internal` types are assembly-visible) and the production `InMemoryLocalStore` (already used by `AppCompositionRoot`'s test path) rather than introducing new fakes.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/RemoteOps.UnitTests/Desktop/Integration/RdpCredentialResolverTests.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Integration;
using RemoteOps.Terminal;
using RemoteOps.UnitTests.Terminal.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

public sealed class RdpCredentialResolverTests
{
    private sealed class FixedSecurityContext : ITerminalSecurityContext
    {
        public string ActorUserId { get; init; } = "test-user";
        public string? DeviceId { get; init; }
    }

    [Fact]
    public async Task ResolvePasswordAsync_ReturnsRevealedSecret()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var envelopeId = await vault.SetupAsync("s3cr3t-rdp", "cred-rdp");
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cr-rdp-1",
            Name = "RDP cred",
            Type = "password",
            SecretEnvelopeId = envelopeId,
            Metadata = new CredentialMetadata { Username = "CORP\\admin" },
        });

        var resolver = new RdpCredentialResolver(store, vault, new FixedSecurityContext());

        string? password = await resolver.ResolvePasswordAsync("cr-rdp-1");

        Assert.Equal("s3cr3t-rdp", password);
    }

    [Fact]
    public async Task ResolvePasswordAsync_UnknownCredentialRef_ReturnsNull()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var resolver = new RdpCredentialResolver(store, vault, new FixedSecurityContext());

        string? password = await resolver.ResolvePasswordAsync("does-not-exist");

        Assert.Null(password);
    }

    [Fact]
    public async Task ResolvePasswordAsync_NoSecretEnvelope_ReturnsNull()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        await store.AddCredentialRefAsync(new CredentialRef
        {
            Id = "cr-no-secret",
            Name = "No secret",
            Type = "password",
            Metadata = new CredentialMetadata { Username = "admin" },
        });
        var resolver = new RdpCredentialResolver(store, vault, new FixedSecurityContext());

        string? password = await resolver.ResolvePasswordAsync("cr-no-secret");

        Assert.Null(password);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpCredentialResolverTests`
Expected: build error — `RdpCredentialResolver` doesn't exist.

- [ ] **Step 3: Implement**

```csharp
// src/RemoteOps.Desktop/Integration/LocalStoreRdpEndpointResolver.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Integration;

internal sealed class LocalStoreRdpEndpointResolver : IRdpEndpointResolver
{
    private readonly ILocalStore _store;

    public LocalStoreRdpEndpointResolver(ILocalStore store) => _store = store;

    public async Task<Endpoint> ResolveAsync(string endpointId, CancellationToken ct = default)
    {
        var endpoint = await _store.GetEndpointAsync(endpointId, ct).ConfigureAwait(false);
        return endpoint ?? throw new InvalidOperationException(
            $"Endpoint '{endpointId}' não encontrado no store local.");
    }
}
```

```csharp
// src/RemoteOps.Desktop/Integration/LocalStoreRdpCredentialRefResolver.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Integration;

internal sealed class LocalStoreRdpCredentialRefResolver : IRdpCredentialRefResolver
{
    private readonly ILocalStore _store;

    public LocalStoreRdpCredentialRefResolver(ILocalStore store) => _store = store;

    public async Task<CredentialRef> ResolveAsync(string credentialRefId, CancellationToken ct = default)
    {
        var credRef = await _store.GetCredentialRefAsync(credentialRefId, ct).ConfigureAwait(false);
        return credRef ?? throw new InvalidOperationException(
            $"CredentialRef '{credentialRefId}' não encontrada no store local.");
    }
}
```

```csharp
// src/RemoteOps.Desktop/Integration/RdpCredentialResolver.cs
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Rdp;
using RemoteOps.Security.Vault;
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

/// <summary>Análogo a StoreWinBoxCredentialResolver — resolve a senha via vault, lifetime mínimo.</summary>
internal sealed class RdpCredentialResolver : IRdpCredentialResolver
{
    private readonly ILocalStore _store;
    private readonly IVault _vault;
    private readonly ITerminalSecurityContext _securityContext;

    public RdpCredentialResolver(ILocalStore store, IVault vault, ITerminalSecurityContext securityContext)
    {
        _store = store;
        _vault = vault;
        _securityContext = securityContext;
    }

    public async Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default)
    {
        var credRef = await _store.GetCredentialRefAsync(credentialRefId, ct).ConfigureAwait(false);
        if (credRef?.SecretEnvelopeId is null) return null;

        var vaultCtx = new VaultAccessContext
        {
            ActorUserId = _securityContext.ActorUserId,
            DeviceId = _securityContext.DeviceId,
        };

        // Lifetime mínimo: `using` zera o buffer imediatamente após RevealString (ADR-009 §FIX-3).
        using var secret = await _vault.RetrieveAsync(credRef.SecretEnvelopeId, vaultCtx, ct).ConfigureAwait(false);
        return secret.RevealString();
    }
}
```

```csharp
// src/RemoteOps.Desktop/Integration/StructuredRdpAuditSink.cs
using System.Diagnostics;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Integration;

internal sealed class StructuredRdpAuditSink : IRdpAuditSink
{
    public Task EmitAsync(RdpAuditEvent auditEvent, CancellationToken ct = default)
    {
        var line = auditEvent.CertificateThumbprint is not null
            ? $"[AUDIT][rdp] action={auditEvent.Action} session={auditEvent.SessionId} " +
              $"host={auditEvent.Host} cert={auditEvent.CertificateThumbprint} " +
              $"user={auditEvent.UserId} at={auditEvent.OccurredAt:O}"
            : $"[AUDIT][rdp] action={auditEvent.Action} session={auditEvent.SessionId} " +
              $"host={auditEvent.Host} user={auditEvent.UserId} at={auditEvent.OccurredAt:O}";
        Trace.WriteLine(line);
        return Task.CompletedTask;
    }
}
```

Modify `AppTerminalSecurityContext` to also satisfy `RemoteOps.Rdp.IRdpSecurityContext` (same two properties — no new logic needed):

```csharp
// src/RemoteOps.Desktop/Integration/AppTerminalSecurityContext.cs
using RemoteOps.Terminal;

namespace RemoteOps.Desktop.Integration;

internal sealed class AppTerminalSecurityContext : ITerminalSecurityContext, RemoteOps.Rdp.IRdpSecurityContext
{
    public string ActorUserId { get; } = "local-user";
    public string? DeviceId { get; } = Environment.MachineName;
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpCredentialResolverTests`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/Integration/LocalStoreRdpEndpointResolver.cs src/RemoteOps.Desktop/Integration/LocalStoreRdpCredentialRefResolver.cs src/RemoteOps.Desktop/Integration/RdpCredentialResolver.cs src/RemoteOps.Desktop/Integration/StructuredRdpAuditSink.cs src/RemoteOps.Desktop/Integration/AppTerminalSecurityContext.cs tests/RemoteOps.UnitTests/Desktop/Integration/RdpCredentialResolverTests.cs
git commit -m "feat(desktop): resolvers/audit RDP — Desktop→RemoteOps.Rdp adapters"
```

---

## Task 5: RdpTabViewModel

**Files:**
- Create: `src/RemoteOps.Desktop/Rdp/RdpTabViewModel.cs`
- Create: `tests/RemoteOps.UnitTests/Desktop/Rdp/Fakes/FakeRdpSessionProvider.cs`
- Create: `tests/RemoteOps.UnitTests/Desktop/Rdp/Fakes/FakeRdpCredentialResolver.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Rdp/RdpTabViewModelTests.cs`

**Interfaces:**
- Consumes: `RemoteOps.Rdp.{IRdpSessionProvider,IRdpCredentialResolver,RdpConnectionConfig}` (Task 2), `RemoteOps.Desktop.ViewModels.SessionTabViewModel` (existing), `RemoteOps.Contracts.Sessions.SessionRequest` (existing).
- Produces: `RdpTabViewModel` — consumed by Task 6 (`MainViewModel`), Task 7 (`TabsViewModel`), Task 11 (`RdpTabView`).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/RemoteOps.UnitTests/Desktop/Rdp/Fakes/FakeRdpSessionProvider.cs
using RemoteOps.Contracts.Sessions;
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Desktop.Rdp.Fakes;

internal sealed class FakeRdpSessionProvider : IRdpSessionProvider
{
    public string Protocol => RemoteProtocol.Rdp;
    public List<SessionRequest> OpenedRequests { get; } = [];
    public int CloseCount { get; private set; }
    public bool ShouldThrowOnOpen { get; set; }
    public RdpConnectionConfig ConfigToReturn { get; set; } = new()
    {
        Host = "10.0.0.5",
        Port = 3389,
        Username = "admin",
        NlaRequired = true,
        Redirection = RdpRedirectionPolicy.Default,
    };

    public Task<SessionHandle> OpenAsync(SessionRequest request, CancellationToken ct)
    {
        if (ShouldThrowOnOpen) throw new InvalidOperationException("Fake provider: open failed");

        OpenedRequests.Add(request);
        return Task.FromResult(new SessionHandle
        {
            SessionId = request.SessionId,
            Protocol = request.Protocol,
            EndpointId = request.EndpointId,
            OpenedAt = DateTimeOffset.UtcNow,
            IsOpen = true,
        });
    }

    public RdpConnectionConfig GetConnectionConfig(string sessionId) => ConfigToReturn;

    public Task CloseAsync(SessionHandle handle, CancellationToken ct)
    {
        CloseCount++;
        return Task.CompletedTask;
    }
}
```

```csharp
// tests/RemoteOps.UnitTests/Desktop/Rdp/Fakes/FakeRdpCredentialResolver.cs
using RemoteOps.Rdp;

namespace RemoteOps.UnitTests.Desktop.Rdp.Fakes;

internal sealed class FakeRdpCredentialResolver : IRdpCredentialResolver
{
    public string? PasswordToReturn { get; set; } = "s3cr3t-rdp";
    public List<string> RequestedCredentialRefIds { get; } = [];

    public Task<string?> ResolvePasswordAsync(string credentialRefId, CancellationToken ct = default)
    {
        RequestedCredentialRefIds.Add(credentialRefId);
        return Task.FromResult(PasswordToReturn);
    }
}
```

```csharp
// tests/RemoteOps.UnitTests/Desktop/Rdp/RdpTabViewModelTests.cs
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Rdp;
using RemoteOps.UnitTests.Desktop.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Rdp;

public sealed class RdpTabViewModelTests
{
    private static SessionRequest MakeRequest() => new()
    {
        SessionId = Guid.NewGuid().ToString("n"),
        Protocol = RemoteProtocol.Rdp,
        EndpointId = "ep-1",
        CredentialRefId = "cr-1",
    };

    [Fact]
    public async Task PrepareAsync_OpensSessionAndExposesConfig()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var request = MakeRequest();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, request);

        var config = await vm.PrepareAsync();

        Assert.Single(provider.OpenedRequests);
        Assert.Equal("10.0.0.5", config.Host);
        Assert.Equal("10.0.0.5", vm.ConnectionConfig!.Host);
    }

    [Fact]
    public async Task PrepareAsync_CalledTwiceConcurrently_OnlyOpensOnce()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await Task.WhenAll(vm.PrepareAsync(), vm.PrepareAsync());

        Assert.Single(provider.OpenedRequests);
    }

    [Fact]
    public async Task PrepareAsync_OnFailure_ResetsStateForRetry()
    {
        var provider = new FakeRdpSessionProvider { ShouldThrowOnOpen = true };
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.PrepareAsync());

        provider.ShouldThrowOnOpen = false;
        await vm.PrepareAsync(); // não deve lançar "já conectando"
        Assert.Single(provider.OpenedRequests);
    }

    [Fact]
    public async Task ResolvePasswordAsync_DelegatesToCredentialResolver_WithRequestCredentialRefId()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver { PasswordToReturn = "s3cr3t-rdp" };
        var request = MakeRequest();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, request);

        string? password = await vm.ResolvePasswordAsync();

        Assert.Equal("s3cr3t-rdp", password);
        Assert.Equal([request.CredentialRefId], credResolver.RequestedCredentialRefIds);
    }

    [Fact]
    public void MarkConnected_SetsIsConnectedTrue()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        vm.MarkConnected();

        Assert.True(vm.IsConnected);
    }

    [Fact]
    public void MarkDisconnected_SetsIsConnectedFalse_AndRaisesConnectFailed()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());
        vm.MarkConnected();

        string? reason = null;
        vm.ConnectFailed += r => reason = r;
        vm.MarkDisconnected("network error");

        Assert.False(vm.IsConnected);
        Assert.Equal("network error", reason);
    }

    [Fact]
    public async Task CloseAsync_BeforePrepare_DoesNothing()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());

        await vm.CloseAsync();

        Assert.Equal(0, provider.CloseCount);
    }

    [Fact]
    public async Task CloseAsync_AfterPrepare_ClosesProviderSession()
    {
        var provider = new FakeRdpSessionProvider();
        var credResolver = new FakeRdpCredentialResolver();
        var vm = new RdpTabViewModel("id1", "Host (RDP)", "rdp", provider, credResolver, MakeRequest());
        await vm.PrepareAsync();

        await vm.CloseAsync();

        Assert.Equal(1, provider.CloseCount);
        Assert.False(vm.IsConnected);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpTabViewModelTests`
Expected: build error — `RdpTabViewModel` doesn't exist.

- [ ] **Step 3: Implement**

```csharp
// src/RemoteOps.Desktop/Rdp/RdpTabViewModel.cs
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Rdp;

namespace RemoteOps.Desktop.Rdp;

/// <summary>
/// ViewModel de uma aba RDP. Espelha o ciclo de vida de TerminalTabViewModel
/// (CAS idle→connecting→connected, CloseAsync), mas a "conexão" real (MSTSCAX
/// Connect()) é disparada pela View — esta classe só prepara a config e expõe
/// a senha sob demanda, sem nunca retê-la em campo.
/// </summary>
public sealed class RdpTabViewModel : SessionTabViewModel
{
    private readonly IRdpSessionProvider _provider;
    private readonly IRdpCredentialResolver _credentialResolver;
    private readonly SessionRequest _baseRequest;

    private SessionHandle? _handle;
    // 0 = idle, 1 = connecting/prepared, 2 = connected (ActiveX OnConnected)
    private int _connectionState;

    public RdpTabViewModel(
        string id,
        string title,
        string protocol,
        IRdpSessionProvider provider,
        IRdpCredentialResolver credentialResolver,
        SessionRequest baseRequest)
        : base(id, title, protocol)
    {
        _provider = provider;
        _credentialResolver = credentialResolver;
        _baseRequest = baseRequest;
    }

    public bool IsConnected => Volatile.Read(ref _connectionState) == 2;

    public RdpConnectionConfig? ConnectionConfig { get; private set; }

    /// <summary>Disparado quando o ActiveX reporta desconexão/erro. View repassa o motivo.</summary>
    public event Action<string>? ConnectFailed;

    /// <summary>
    /// Resolve endpoint/usuário e audita início. Chamado pela View ao carregar o
    /// WindowsFormsHost, ANTES de aplicar Server/UserName no controle MSTSCAX.
    /// </summary>
    public async Task<RdpConnectionConfig> PrepareAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _connectionState, 1, 0) != 0)
            return ConnectionConfig!;

        try
        {
            _handle = await _provider.OpenAsync(_baseRequest, ct);
            ConnectionConfig = _provider.GetConnectionConfig(_baseRequest.SessionId);
            return ConnectionConfig;
        }
        catch
        {
            Interlocked.Exchange(ref _connectionState, 0);
            throw;
        }
    }

    /// <summary>
    /// Resolve a senha do vault sob demanda. Chame imediatamente antes de
    /// AdvancedSettings.ClearTextPassword e descarte a string assim que usada
    /// (mitigação ADR-009 §FIX-3 — a senha nunca fica em campo deste ViewModel).
    /// </summary>
    public Task<string?> ResolvePasswordAsync(CancellationToken ct = default) =>
        _credentialResolver.ResolvePasswordAsync(_baseRequest.CredentialRefId, ct);

    /// <summary>Chamado pela View quando o ActiveX dispara OnConnected/OnLoginComplete.</summary>
    public void MarkConnected() => Interlocked.Exchange(ref _connectionState, 2);

    /// <summary>Chamado pela View quando o ActiveX dispara OnDisconnected ou erro.</summary>
    public void MarkDisconnected(string? reason)
    {
        Interlocked.Exchange(ref _connectionState, 0);
        if (reason != null) ConnectFailed?.Invoke(reason);
    }

    /// <summary>Encerra a sessão. Chamado ao fechar a aba.</summary>
    public async Task CloseAsync()
    {
        if (_handle == null) return;

        await _provider.CloseAsync(_handle, CancellationToken.None);
        _handle = null;
        Interlocked.Exchange(ref _connectionState, 0);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter RdpTabViewModelTests`
Expected: 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/Rdp/RdpTabViewModel.cs tests/RemoteOps.UnitTests/Desktop/Rdp/
git commit -m "feat(desktop): RdpTabViewModel — ciclo de vida de aba RDP, senha sob demanda"
```

---

## Task 6: AppCompositionRoot wiring

**Files:**
- Modify: `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs`
- Modify: `tests/RemoteOps.UnitTests/Desktop/CompositionRootSmokeTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–4.
- Produces: DI registrations consumed by Task 7 (`MainViewModel`).

- [ ] **Step 1: Write the failing tests**

Add to `CompositionRootSmokeTests.cs` (append before the final `}`):

```csharp
    // Feature flags ---------------------------------------------------------

    [Fact]
    public void Resolve_IFeatureFlags() =>
        Assert.NotNull(_provider.GetRequiredService<IFeatureFlags>());

    // RDP -------------------------------------------------------------------

    [Fact]
    public void Resolve_IRdpEndpointResolver() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpEndpointResolver>());

    [Fact]
    public void Resolve_IRdpCredentialRefResolver() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpCredentialRefResolver>());

    [Fact]
    public void Resolve_IRdpCredentialResolver() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpCredentialResolver>());

    [Fact]
    public void Resolve_IRdpAuditSink() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpAuditSink>());

    [Fact]
    public void Resolve_IRdpSecurityContext() =>
        Assert.NotNull(_provider.GetRequiredService<RemoteOps.Rdp.IRdpSecurityContext>());

    [Fact]
    public void Resolve_RdpSessionProvider_ByKey() =>
        Assert.NotNull(_provider.GetRequiredKeyedService<RemoteOps.Rdp.IRdpSessionProvider>("rdp"));
```

Add `using RemoteOps.Desktop.Infrastructure;` to the top of the file (already needed for `IFeatureFlags`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter CompositionRootSmokeTests`
Expected: failures — services not registered (`GetRequiredService` throws).

- [ ] **Step 3: Implement**

In `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs`, add to the `using` block:

```csharp
using RemoteOps.Rdp;
```

In `BuildInternal`, after the existing `// Adaptadores Desktop→Terminal (ADR-011)` block, add:

```csharp
        // Feature flags (default OFF — REMOTEOPS_FEATURE_FLAGS env var)
        services.AddSingleton<IFeatureFlags, EnvironmentFeatureFlags>();

        // Adaptadores Desktop→RDP (ADR-014)
        services.AddSingleton<IRdpEndpointResolver, LocalStoreRdpEndpointResolver>();
        services.AddSingleton<IRdpCredentialRefResolver, LocalStoreRdpCredentialRefResolver>();
        services.AddSingleton<IRdpCredentialResolver, RdpCredentialResolver>();
        services.AddSingleton<IRdpAuditSink, StructuredRdpAuditSink>();
        services.AddSingleton<IRdpSecurityContext, AppTerminalSecurityContext>();
```

After the existing `services.AddKeyedSingleton<ITerminalSessionProvider, TelnetSessionProvider>(RemoteProtocol.Telnet);` line, add:

```csharp
        services.AddKeyedSingleton<IRdpSessionProvider, RdpSessionProvider>(RemoteProtocol.Rdp);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter CompositionRootSmokeTests`
Expected: all tests (existing + 7 new) PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs tests/RemoteOps.UnitTests/Desktop/CompositionRootSmokeTests.cs
git commit -m "feat(desktop): registra grafo RDP no AppCompositionRoot (keyed + feature flag)"
```

---

## Task 7: MainViewModel — case RemoteProtocol.Rdp

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/MainViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/MainViewModelRdpTests.cs`

**Interfaces:**
- Consumes: `RdpTabViewModel` (Task 5), `IFeatureFlags` (Task 3), `IRdpSessionProvider`/`IRdpCredentialResolver` (Task 2/4), `TabsViewModel.OpenRdpTab` (Task 8 — write this task's test against the Task-8 signature; Task 8 lands first in execution order if using subagent-driven dispatch, or implement Task 8's `OpenRdpTab` stub before this test if executed strictly in order. This plan executes Task 8 before relying on it here — see ordering note below).
- Produces: `MainViewModel` ctor with `rdpProvider`/`rdpCredentialResolver`/`featureFlags` params; `OnSessionRequested` branches to RDP.

**Ordering note:** `TabsViewModel.OpenRdpTab` (Task 8) is a small, self-contained addition. Do Task 8 immediately before this task if executing strictly sequentially, OR implement both together — they're independent of each other's internals and only need to compile together. The step order below assumes `TabsViewModel.OpenRdpTab` already exists; if running tasks in plan order, swap Task 7 and Task 8.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/RemoteOps.UnitTests/Desktop/MainViewModelRdpTests.cs
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class MainViewModelRdpTests
{
    private sealed class FixedFeatureFlags : IFeatureFlags
    {
        private readonly bool _enabled;
        public FixedFeatureFlags(bool enabled) => _enabled = enabled;
        public bool IsEnabled(string flagName) => flagName == FeatureFlagNames.RdpEnabled && _enabled;
    }

    [Fact]
    public void OnSessionRequested_RdpWithFlagOn_OpensRdpTab()
    {
        var store = new RemoteOps.Desktop.Infrastructure.InMemoryLocalStore();
        var rdpProvider = new FakeRdpSessionProvider();
        var rdpCredResolver = new FakeRdpCredentialResolver();
        var vm = new MainViewModel(
            store,
            featureFlags: new FixedFeatureFlags(enabled: true),
            rdpProvider: rdpProvider,
            rdpCredentialResolver: rdpCredResolver);

        InvokeSessionRequested(vm, "rdp", endpointId: "ep-1", credentialRefId: "cr-1");

        Assert.IsType<RdpTabViewModel>(vm.Tabs.ActiveTab);
    }

    [Fact]
    public void OnSessionRequested_RdpWithFlagOff_FallsBackToPlaceholder()
    {
        var store = new RemoteOps.Desktop.Infrastructure.InMemoryLocalStore();
        var rdpProvider = new FakeRdpSessionProvider();
        var rdpCredResolver = new FakeRdpCredentialResolver();
        var vm = new MainViewModel(
            store,
            featureFlags: new FixedFeatureFlags(enabled: false),
            rdpProvider: rdpProvider,
            rdpCredentialResolver: rdpCredResolver);

        InvokeSessionRequested(vm, "rdp", endpointId: "ep-1", credentialRefId: "cr-1");

        Assert.IsNotType<RdpTabViewModel>(vm.Tabs.ActiveTab);
        Assert.NotNull(vm.Tabs.ActiveTab);
    }

    [Fact]
    public void OnSessionRequested_RdpMissingProvider_FallsBackToPlaceholder()
    {
        var store = new RemoteOps.Desktop.Infrastructure.InMemoryLocalStore();
        var vm = new MainViewModel(store, featureFlags: new FixedFeatureFlags(enabled: true));

        InvokeSessionRequested(vm, "rdp", endpointId: "ep-1", credentialRefId: "cr-1");

        Assert.IsNotType<RdpTabViewModel>(vm.Tabs.ActiveTab);
    }

    private static void InvokeSessionRequested(MainViewModel vm, string protocol, string? endpointId, string? credentialRefId)
    {
        var method = typeof(MainViewModel).GetMethod("OnSessionRequested",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        method.Invoke(vm, [vm, new OpenSessionRequest
        {
            AssetId = "asset-1",
            AssetName = "Host1",
            Protocol = protocol,
            EndpointId = endpointId,
            CredentialRefId = credentialRefId,
        }]);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter MainViewModelRdpTests`
Expected: build error — `MainViewModel` ctor has no `featureFlags`/`rdpProvider`/`rdpCredentialResolver` params.

- [ ] **Step 3: Implement**

In `src/RemoteOps.Desktop/ViewModels/MainViewModel.cs`, add to the `using` block:

```csharp
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Rdp;
```

Replace the existing fields/ctor:

```csharp
    private readonly ITerminalSessionProvider? _sshProvider;
    private readonly ITerminalSessionProvider? _telnetProvider;
    private readonly IRdpSessionProvider? _rdpProvider;
    private readonly IRdpCredentialResolver? _rdpCredentialResolver;
    private readonly IFeatureFlags? _featureFlags;

    private string _syncStatus = "Offline";
    private string _searchText = string.Empty;

    public MainViewModel(
        ILocalStore store,
        IWinBoxRunner? winBoxRunner = null,
        IFeatureFlags? featureFlags = null,
        [FromKeyedServices(RemoteProtocol.Ssh)] ITerminalSessionProvider? sshProvider = null,
        [FromKeyedServices(RemoteProtocol.Telnet)] ITerminalSessionProvider? telnetProvider = null,
        [FromKeyedServices(RemoteProtocol.Rdp)] IRdpSessionProvider? rdpProvider = null,
        IRdpCredentialResolver? rdpCredentialResolver = null)
    {
        _sshProvider = sshProvider;
        _telnetProvider = telnetProvider;
        _rdpProvider = rdpProvider;
        _rdpCredentialResolver = rdpCredentialResolver;
        _featureFlags = featureFlags;

        Sidebar = new SidebarViewModel(store, DefaultWorkspaceId);
        HostList = new HostListViewModel(store, DefaultWorkspaceId);
        Inspector = new InspectorViewModel(store, winBoxRunner, featureFlags);
        Tabs = new TabsViewModel();

        Sidebar.GroupSelected += (_, groupVm) =>
            _ = HostList.LoadAsync(groupVm?.Id);

        HostList.AssetSelected += (_, assetVm) =>
            Inspector.Asset = assetVm;

        Inspector.SessionRequested += OnSessionRequested;
    }
```

Replace `OnSessionRequested`:

```csharp
    private void OnSessionRequested(object? sender, OpenSessionRequest req)
    {
        bool rdpEnabled = _featureFlags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false;

        if (req.Protocol == RemoteProtocol.Rdp
            && rdpEnabled
            && _rdpProvider != null
            && _rdpCredentialResolver != null
            && req.EndpointId != null
            && req.CredentialRefId != null)
        {
            var rdpSessionRequest = new SessionRequest
            {
                SessionId = Guid.NewGuid().ToString("n"),
                Protocol = req.Protocol,
                EndpointId = req.EndpointId,
                CredentialRefId = req.CredentialRefId,
            };

            var rdpTab = new RdpTabViewModel(
                id: rdpSessionRequest.SessionId,
                title: $"{req.AssetName} ({req.Protocol.ToUpperInvariant()})",
                protocol: req.Protocol,
                provider: _rdpProvider,
                credentialResolver: _rdpCredentialResolver,
                baseRequest: rdpSessionRequest);

            Tabs.OpenRdpTab(rdpTab);
            return;
        }

        var provider = req.Protocol switch
        {
            RemoteProtocol.Ssh => _sshProvider,
            RemoteProtocol.Telnet => _telnetProvider,
            _ => null,
        };

        if (provider != null && req.EndpointId != null && req.CredentialRefId != null)
        {
            var sessionRequest = new SessionRequest
            {
                SessionId = Guid.NewGuid().ToString("n"),
                Protocol = req.Protocol,
                EndpointId = req.EndpointId,
                CredentialRefId = req.CredentialRefId,
            };

            var tab = new TerminalTabViewModel(
                id: sessionRequest.SessionId,
                title: $"{req.AssetName} ({req.Protocol.ToUpperInvariant()})",
                protocol: req.Protocol,
                provider: provider,
                baseRequest: sessionRequest);

            Tabs.OpenTerminalTab(tab);
        }
        else
        {
            // Fallback: placeholder tab for RDP (flag off / missing wiring), MikroTik, ou endpoint ausente
            Tabs.OpenTab(req.AssetName, req.Protocol);
        }
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter MainViewModelRdpTests`
Expected: 3 tests PASS. Also re-run the full Desktop suite to confirm no regression: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~Desktop"`.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/MainViewModel.cs tests/RemoteOps.UnitTests/Desktop/MainViewModelRdpTests.cs
git commit -m "feat(desktop): MainViewModel abre RdpTabViewModel quando rdp.enabled"
```

---

## Task 8: TabsViewModel.OpenRdpTab + CloseTab handling

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/TabsViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/TabsViewModelRdpTests.cs`

**Interfaces:**
- Consumes: `RdpTabViewModel` (Task 5).
- Produces: `TabsViewModel.OpenRdpTab(RdpTabViewModel)` — consumed by Task 7.

**Note:** If executing tasks strictly in plan order, do this task before Task 7 (Task 7's implementation step calls `Tabs.OpenRdpTab`, which must exist to compile). Both tasks' tests are independent of each other.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/RemoteOps.UnitTests/Desktop/TabsViewModelRdpTests.cs
using RemoteOps.Contracts.Sessions;
using RemoteOps.Desktop.Rdp;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.Rdp.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class TabsViewModelRdpTests
{
    private static RdpTabViewModel MakeTab(FakeRdpSessionProvider provider, FakeRdpCredentialResolver credResolver) =>
        new("id1", "Host (RDP)", "rdp", provider, credResolver, new SessionRequest
        {
            SessionId = "id1",
            Protocol = RemoteProtocol.Rdp,
            EndpointId = "ep-1",
            CredentialRefId = "cr-1",
        });

    [Fact]
    public void OpenRdpTab_AddsAndActivatesTab()
    {
        var tabs = new TabsViewModel();
        var tab = MakeTab(new FakeRdpSessionProvider(), new FakeRdpCredentialResolver());

        tabs.OpenRdpTab(tab);

        Assert.Contains(tab, tabs.Tabs);
        Assert.Same(tab, tabs.ActiveTab);
        Assert.True(tabs.HasTabs);
    }

    [Fact]
    public async Task CloseTab_OnRdpTab_CallsCloseAsync()
    {
        var tabs = new TabsViewModel();
        var provider = new FakeRdpSessionProvider();
        var tab = MakeTab(provider, new FakeRdpCredentialResolver());
        tabs.OpenRdpTab(tab);
        await tab.PrepareAsync();

        tabs.CloseTabCommand.Execute(tab);
        await Task.Delay(50); // CloseAsync é fire-and-forget, igual ao caminho Terminal

        Assert.Equal(1, provider.CloseCount);
        Assert.DoesNotContain(tab, tabs.Tabs);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter TabsViewModelRdpTests`
Expected: build error — `OpenRdpTab` doesn't exist.

- [ ] **Step 3: Implement**

In `src/RemoteOps.Desktop/ViewModels/TabsViewModel.cs`, add to `using`:

```csharp
using RemoteOps.Desktop.Rdp;
```

Add after `OpenTerminalTab`:

```csharp
    /// <summary>Adiciona uma aba RDP pré-construída e a ativa.</summary>
    public void OpenRdpTab(RdpTabViewModel tab)
    {
        Tabs.Add(tab);
        ActiveTab = tab;
        RaisePropertyChanged(nameof(HasTabs));
    }
```

Update `CloseTab`:

```csharp
    private void CloseTab(SessionTabViewModel? tab)
    {
        if (tab == null || tab.IsPinned)
        {
            return;
        }

        // Close the underlying session (fire-and-forget; pump cancellation is fast)
        if (tab is TerminalTabViewModel ttvm)
            _ = ttvm.CloseAsync();
        else if (tab is RdpTabViewModel rtvm)
            _ = rtvm.CloseAsync();

        int idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ActiveTab == tab)
        {
            ActiveTab = Tabs.Count > 0
                ? Tabs[Math.Max(0, idx - 1)]
                : null;
        }

        RaisePropertyChanged(nameof(HasTabs));
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter TabsViewModelRdpTests`
Expected: 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/TabsViewModel.cs tests/RemoteOps.UnitTests/Desktop/TabsViewModelRdpTests.cs
git commit -m "feat(desktop): TabsViewModel.OpenRdpTab + fecha sessão RDP ao fechar aba"
```

---

## Task 9: InspectorViewModel — "Conectar RDP" visível atrás da flag

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/InspectorViewModel.cs`
- Modify: `src/RemoteOps.Desktop/Views/InspectorView.xaml`
- Test: `tests/RemoteOps.UnitTests/Desktop/InspectorViewModelRdpTests.cs`

**Interfaces:**
- Consumes: `IFeatureFlags` (Task 3).
- Produces: `InspectorViewModel(ILocalStore, IWinBoxRunner?, IFeatureFlags?)` ctor; `CanOpenRdp` bool property — consumed by Task 7 (`MainViewModel` already passes `featureFlags` to the Inspector ctor) and XAML binding.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/RemoteOps.UnitTests/Desktop/InspectorViewModelRdpTests.cs
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop;

public sealed class InspectorViewModelRdpTests
{
    private sealed class FixedFeatureFlags : IFeatureFlags
    {
        private readonly bool _enabled;
        public FixedFeatureFlags(bool enabled) => _enabled = enabled;
        public bool IsEnabled(string flagName) => flagName == FeatureFlagNames.RdpEnabled && _enabled;
    }

    private static AssetViewModel MakeAssetWithRdpEndpoint() => new(new Asset
    {
        Id = "asset-1",
        WorkspaceId = "ws-1",
        Name = "Host1",
        Endpoints = [new Endpoint { Id = "ep-1", AssetId = "asset-1", Protocol = "rdp", Port = 3389 }],
    });

    [Fact]
    public void CanOpenRdp_FlagOnAndHasRdpEndpoint_IsTrue()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore(), featureFlags: new FixedFeatureFlags(true))
        {
            Asset = MakeAssetWithRdpEndpoint(),
        };

        Assert.True(vm.CanOpenRdp);
    }

    [Fact]
    public void CanOpenRdp_FlagOff_IsFalse()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore(), featureFlags: new FixedFeatureFlags(false))
        {
            Asset = MakeAssetWithRdpEndpoint(),
        };

        Assert.False(vm.CanOpenRdp);
    }

    [Fact]
    public void CanOpenRdp_NoRdpEndpoint_IsFalse()
    {
        var assetVm = new AssetViewModel(new Asset { Id = "a", WorkspaceId = "ws", Name = "Host2", Endpoints = [] });
        var vm = new InspectorViewModel(new InMemoryLocalStore(), featureFlags: new FixedFeatureFlags(true))
        {
            Asset = assetVm,
        };

        Assert.False(vm.CanOpenRdp);
    }

    [Fact]
    public void CanOpenRdp_NoFeatureFlagsProvided_IsFalse()
    {
        var vm = new InspectorViewModel(new InMemoryLocalStore())
        {
            Asset = MakeAssetWithRdpEndpoint(),
        };

        Assert.False(vm.CanOpenRdp);
    }
}
```

(`Asset`/`Endpoint` are `RemoteOps.Contracts.Assets` types — confirmed against `src/RemoteOps.Desktop/ViewModels/AssetViewModel.cs` and `src/RemoteOps.Contracts/Assets/Asset.cs`: `Asset.Endpoints` is `List<Endpoint>`, `Id`/`WorkspaceId`/`Name` are required.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter InspectorViewModelRdpTests`
Expected: build error — ctor has no `featureFlags` param; `CanOpenRdp` doesn't exist.

- [ ] **Step 3: Implement**

In `src/RemoteOps.Desktop/ViewModels/InspectorViewModel.cs`, add to `using`:

```csharp
using RemoteOps.Desktop.Infrastructure;
```

Update field/ctor:

```csharp
    private readonly ILocalStore _store;
    private readonly IWinBoxRunner? _winBoxRunner;
    private readonly IFeatureFlags? _featureFlags;
    private AssetViewModel? _asset;
    private string _newEndpointProtocol = RemoteProtocol.Ssh;
    private string _newEndpointAddress = string.Empty;
    private int _newEndpointPort = 22;
    private bool _isBusy;
    private string? _winBoxError;

    public InspectorViewModel(ILocalStore store, IWinBoxRunner? winBoxRunner = null, IFeatureFlags? featureFlags = null)
    {
        _store = store;
        _winBoxRunner = winBoxRunner;
        _featureFlags = featureFlags;

        AddEndpointCommand = new RelayCommand(
            () => _ = AddEndpointAsync(),
            () => !IsBusy && Asset != null && !string.IsNullOrWhiteSpace(NewEndpointAddress));

        OpenSessionCommand = new RelayCommand(
            obj => RequestOpenSession(obj as string ?? NewEndpointProtocol),
            _ => Asset != null);

        OpenWinBoxCommand = new RelayCommand(
            () => _ = OpenWinBoxAsync(),
            () => _winBoxRunner != null && IsMikroTikHost && !IsBusy);
    }
```

Update the `Asset` setter and add `CanOpenRdp`:

```csharp
    public AssetViewModel? Asset
    {
        get => _asset;
        set
        {
            Set(ref _asset, value);
            WinBoxError = null;
            RaisePropertyChanged(nameof(HasAsset));
            RaisePropertyChanged(nameof(IsMikroTikHost));
            RaisePropertyChanged(nameof(CanOpenRdp));
            AddEndpointCommand.RaiseCanExecuteChanged();
            OpenSessionCommand.RaiseCanExecuteChanged();
            OpenWinBoxCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasAsset => _asset != null;

    public bool IsMikroTikHost =>
        _asset?.Asset.Endpoints.Any(e => e.Protocol == RemoteProtocol.MikroTik) ?? false;

    /// <summary>RDP real disponível: flag rdp.enabled ligada E o host tem endpoint rdp.</summary>
    public bool CanOpenRdp =>
        (_featureFlags?.IsEnabled(FeatureFlagNames.RdpEnabled) ?? false)
        && (_asset?.Asset.Endpoints.Any(e => e.Protocol == RemoteProtocol.Rdp) ?? false);
```

In `src/RemoteOps.Desktop/Views/InspectorView.xaml`, wrap the RDP button with a visibility trigger on `CanOpenRdp` (mirroring the WinBox button pattern):

```xml
                <Button Content="RDP"
                        Command="{Binding OpenSessionCommand}"
                        CommandParameter="rdp"
                        Margin="0,0,4,4" Padding="8,2">
                    <Button.Style>
                        <Style TargetType="Button">
                            <Setter Property="Visibility" Value="Collapsed"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding CanOpenRdp}" Value="True">
                                    <Setter Property="Visibility" Value="Visible"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Button.Style>
                </Button>
```

(replaces the old always-visible `<Button Content="RDP" .../>` line; SSH/Telnet buttons stay unconditional, unchanged).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter InspectorViewModelRdpTests`
Expected: 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/InspectorViewModel.cs src/RemoteOps.Desktop/Views/InspectorView.xaml tests/RemoteOps.UnitTests/Desktop/InspectorViewModelRdpTests.cs
git commit -m "feat(desktop): botão Conectar RDP visível só com rdp.enabled + endpoint rdp"
```

---

## Task 10: TabsView.xaml — DataTemplate RdpTabViewModel→RdpTabView

**Files:**
- Modify: `src/RemoteOps.Desktop/Views/TabsView.xaml`

**Interfaces:**
- Consumes: `RdpTabViewModel` (Task 5), `RdpTabView` (Task 13 — created after this task; the XAML reference compiles once Task 13 lands. If executing strictly in order, do this task's XAML edit together with Task 13, or leave a `TODO` placeholder type stub `RdpTabView` from Task 13 first).

**Note:** This task only edits XAML (no C# logic, no automated test — WPF `DataTemplate` resolution is verified by building and by manual smoke-test in Task 14). Sequence this task immediately before or together with Task 13 so the project compiles (`RdpTabView` must exist).

- [ ] **Step 1: Edit `src/RemoteOps.Desktop/Views/TabsView.xaml`**

Add the `rdp` namespace and a `DataTemplate` for `RdpTabViewModel`, placed before the generic `SessionTabViewModel` fallback (WPF picks the most specific matching implicit template, but keep ordering consistent with the existing terminal entry for readability):

```xml
<UserControl x:Class="RemoteOps.Desktop.Views.TabsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:RemoteOps.Desktop.ViewModels"
             xmlns:terminal="clr-namespace:RemoteOps.Desktop.Terminal"
             xmlns:rdp="clr-namespace:RemoteOps.Desktop.Rdp">

    <UserControl.Resources>
        <!-- Implicit DataTemplate: WPF applies this automatically for TerminalTabViewModel -->
        <DataTemplate DataType="{x:Type terminal:TerminalTabViewModel}">
            <terminal:TerminalTabView />
        </DataTemplate>
        <!-- Implicit DataTemplate: WPF applies this automatically for RdpTabViewModel -->
        <DataTemplate DataType="{x:Type rdp:RdpTabViewModel}">
            <rdp:RdpTabView />
        </DataTemplate>
        <!-- Fallback for future non-terminal/non-rdp tab types -->
        <DataTemplate DataType="{x:Type vm:SessionTabViewModel}">
            <TextBlock Text="{Binding Title}"
                       VerticalAlignment="Center"
                       HorizontalAlignment="Center"
                       Foreground="#bbb"
                       FontSize="13"
                       FontStyle="Italic"/>
        </DataTemplate>
    </UserControl.Resources>
    <!-- ... rest of file unchanged ... -->
```

(Only the `UserControl.Resources` block changes — the `Grid`/`TabControl` body below is untouched.)

- [ ] **Step 2: Defer verification to Task 13**

This template only compiles/renders correctly once `RdpTabView` (Task 13) exists. Verification: full solution build in Task 14.

- [ ] **Step 3: Commit (combine with Task 13's commit if doing them together)**

```bash
git add src/RemoteOps.Desktop/Views/TabsView.xaml
git commit -m "feat(desktop): TabsView registra DataTemplate RdpTabViewModel→RdpTabView"
```

---

## Task 11: RemoteOps.Rdp.csproj — COMReference MSTSCAX + RemoteOps.Desktop.csproj wiring

**Files:**
- Modify: `src/RemoteOps.Rdp/RemoteOps.Rdp.csproj`
- Modify: `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`

**Interfaces:**
- No C# interface changes — build configuration only. This unblocks Task 13 (the actual MSTSCAX-hosting view).

- [ ] **Step 1: Edit `src/RemoteOps.Rdp/RemoteOps.Rdp.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>RemoteOps.Rdp</RootNamespace>
    <UseWindowsForms>true</UseWindowsForms>
    <UseWPF>true</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\RemoteOps.Contracts\RemoteOps.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Microsoft Terminal Services Client Control (mstscax.dll) — ADR-004/ADR-014.
         GUID é a typelib "MSTSCLib" registrada com o RDP client do Windows.
         CONFIRME contra o registro da máquina de build se o restore do COMReference falhar:
         reg query "HKCR\TypeLib\{8c11efa1-92c3-11d1-bc1e-00c04fa31489}" /s -->
    <COMReference Include="MSTSCLib">
      <Guid>8c11efa1-92c3-11d1-bc1e-00c04fa31489</Guid>
      <VersionMajor>1</VersionMajor>
      <VersionMinor>0</VersionMinor>
      <Lcid>0</Lcid>
      <WrapperTool>tlbimp</WrapperTool>
      <Isolated>false</Isolated>
      <EmbedInteropTypes>true</EmbedInteropTypes>
    </COMReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Edit `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`**

Add `UseWindowsForms` (required alongside `UseWPF` for `WindowsFormsIntegration`/`WindowsFormsHost`) and the project reference to `RemoteOps.Rdp`:

```xml
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <RootNamespace>RemoteOps.Desktop</RootNamespace>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
```

```xml
  <ItemGroup>
    <ProjectReference Include="..\RemoteOps.Contracts\RemoteOps.Contracts.csproj" />
    <ProjectReference Include="..\RemoteOps.Security\RemoteOps.Security.csproj" />
    <ProjectReference Include="..\RemoteOps.Terminal\RemoteOps.Terminal.csproj" />
    <ProjectReference Include="..\RemoteOps.MikroTik\RemoteOps.MikroTik.csproj" />
    <ProjectReference Include="..\RemoteOps.Sync\RemoteOps.Sync.csproj" />
    <ProjectReference Include="..\RemoteOps.Rdp\RemoteOps.Rdp.csproj" />
  </ItemGroup>
```

- [ ] **Step 3: Build to verify the COM interop resolves**

Run: `dotnet build src/RemoteOps.Rdp/RemoteOps.Rdp.csproj`
Expected: build succeeds and generates `obj/.../Interop.MSTSCLib.dll`. If it fails with "type library not registered" or similar, RDP client (`mstscax.dll`) may not be installed/registered on this machine — note that in the commit/PR and treat as a CI-environment risk to flag to `devops-agent` (Windows GitHub-hosted runners ship `mstscax.dll`; a local dev box might not have it registered as a typelib if Remote Desktop Connection was never launched).

- [ ] **Step 4: Commit**

```bash
git add src/RemoteOps.Rdp/RemoteOps.Rdp.csproj src/RemoteOps.Desktop/RemoteOps.Desktop.csproj
git commit -m "build(rdp): COMReference MSTSCAX + UseWindowsForms para hospedar o ActiveX"
```

---

## Task 12: RdpTabView — WindowsFormsHost + MSTSCAX (spike, manual verification)

**Files:**
- Create: `src/RemoteOps.Desktop/Rdp/RdpTabView.xaml`
- Create: `src/RemoteOps.Desktop/Rdp/RdpTabView.xaml.cs`

**Interfaces:**
- Consumes: `RdpTabViewModel` (Task 5), the COM interop types generated in Task 11.
- Produces: the visual tab. **Not unit-testable headless** (per `.claude/agents/rdp-agent.md` and this plan's Global Constraints) — verification is build + manual connect against a real Windows host, documented as the SPIKE deliverable in the PR per `docs/08-rdp-terminal-server.md` §Spikes obrigatórios.

**This is the one task in the plan where exact API surface is confirmed empirically, not from memory.** The generated interop type name depends on the `mstscax.dll` version on the build machine (commonly `AxMSTSCLib.AxMsRdpClient9NotSafeForScripting` or a newer `MsRdpClient1x`/`11` variant). Before writing the final property/event names, inspect the generated interop assembly:

```powershell
dotnet build src/RemoteOps.Rdp/RemoteOps.Rdp.csproj
# Then inspect generated types, e.g. via ildasm/ILSpy on:
#   src/RemoteOps.Rdp/obj/Debug/net10.0-windows/Interop.MSTSCLib.dll
```

- [ ] **Step 1: Write `RdpTabView.xaml`**

```xml
<UserControl x:Class="RemoteOps.Desktop.Rdp.RdpTabView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:wf="clr-namespace:System.Windows.Forms.Integration;assembly=WindowsFormsIntegration">
    <Grid Background="#1e1e1e">
        <TextBlock x:Name="_statusText"
                   Text="Conectando..."
                   Foreground="#888"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   FontSize="13"/>
        <wf:WindowsFormsHost x:Name="_formsHost" Visibility="Collapsed"/>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Write `RdpTabView.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using AxMSTSCLib;
using MSTSCLib;

namespace RemoteOps.Desktop.Rdp;

/// <summary>
/// Aba RDP real: WindowsFormsHost hospedando o controle ActiveX MSTSCAX (mstscax.dll),
/// ligado ao RdpTabViewModel. Camada fina não testável em headless — coberta por
/// verificação manual (spike) documentada no PR/docs/08.
/// </summary>
public partial class RdpTabView : UserControl
{
    private RdpTabViewModel? _vm;
    private AxMsRdpClient9NotSafeForScripting? _client;
    private bool _connectStarted;

    public RdpTabView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as RdpTabViewModel;
        if (_vm != null && !_connectStarted)
        {
            _connectStarted = true;
            _ = InitAndConnectAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_client != null)
        {
            _client.OnConnected -= OnAxConnected;
            _client.OnDisconnected -= OnAxDisconnected;
            try
            {
                if (_client.Connected == 1) _client.Disconnect();
            }
            catch
            {
                // Controle pode já ter sido finalizado pelo runtime COM — ignorar no shutdown.
            }
        }

        _connectStarted = false;
    }

    private async Task InitAndConnectAsync()
    {
        try
        {
            var config = await _vm!.PrepareAsync();

            _client = new AxMsRdpClient9NotSafeForScripting();
            ((System.ComponentModel.ISupportInitialize)_client).BeginInit();
            _formsHost.Child = _client;
            ((System.ComponentModel.ISupportInitialize)_client).EndInit();

            _client.Server = config.Host;
            _client.UserName = config.Username;
            _client.AdvancedSettings9.RDPPort = config.Port;

            // NLA + nível de autenticação — nunca ignorar certificado sem auditoria (ADR-014).
            _client.AdvancedSettings9.EnableCredSspSupport = config.NlaRequired;
            _client.AdvancedSettings9.AuthenticationLevel = 2; // exige autenticação de servidor

            // Redirecionamentos — todos OFF por padrão (requisito de segurança).
            _client.AdvancedSettings9.RedirectClipboard = config.Redirection.ClipboardRedirectionEnabled;
            _client.AdvancedSettings9.RedirectDrives = config.Redirection.DriveRedirectionEnabled;
            _client.AdvancedSettings9.RedirectPrinters = config.Redirection.PrinterRedirectionEnabled;
            _client.AdvancedSettings9.AudioRedirectionMode =
                config.Redirection.AudioRedirectionEnabled ? 0 /* redirect */ : 2 /* do not play */;

            // Senha: resolvida do vault só agora, aplicada e imediatamente fora de escopo
            // (mitigação ADR-009 §FIX-3 — a string persiste apenas até o GC coletar este frame).
            string? password = await _vm.ResolvePasswordAsync();
            if (password != null)
            {
                _client.AdvancedSettings9.ClearTextPassword = password;
            }

            _client.OnConnected += OnAxConnected;
            _client.OnDisconnected += OnAxDisconnected;

            _formsHost.Visibility = Visibility.Visible;
            _statusText.Visibility = Visibility.Collapsed;

            _client.Connect();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Erro ao conectar RDP: {ex.Message}";
            _vm?.MarkDisconnected(ex.Message);
        }
    }

    private void OnAxConnected(object? sender, EventArgs e) => _vm?.MarkConnected();

    private void OnAxDisconnected(object? sender, IMsTscAxEvents_OnDisconnectedEvent e) =>
        _vm?.MarkDisconnected($"disconnect reason code {e.discReason}");
}
```

> **Confirm before merging:** the exact class name (`AxMsRdpClient9NotSafeForScripting` vs. a newer/older `AxMsRdpClientXNotSafeForScripting`), the `AdvancedSettings9` interface version, and the `OnDisconnected` event arg type (`IMsTscAxEvents_OnDisconnectedEvent.discReason`) against what `dotnet build` actually generates in `Interop.MSTSCLib.dll`/`AxInterop.MSTSCLib.dll` on the build machine — IntelliSense/Object Browser after a successful build is the source of truth, not this plan. Adjust property/event names to match; the surrounding structure (prepare → apply config → resolve password just-in-time → connect → wire events → cleanup on unload) stays the same regardless of the exact interface version.

- [ ] **Step 3: Build and manually verify (SPIKE-RDP-001/002)**

Run: `dotnet build src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`

Manual verification checklist (document results in the PR description per `docs/08-rdp-terminal-server.md` §Spikes obrigatórios):
- Open a host with an `rdp` endpoint (flag `REMOTEOPS_FEATURE_FLAGS=rdp.enabled` set), click "Conectar RDP".
- Tab opens, control connects to a real/test RDP host, `OnConnected` fires (status text disappears, control becomes visible).
- Resize the tab/window — control resizes without losing the session.
- Switch away and back to the tab — connection survives (View may be re-templated; if MSTSCAX disconnects on re-template, note this as a known limitation, mirroring the terminal's "tolerant to view re-creation" comment, and decide in ADR-014 whether to pin the tab or accept reconnect-on-revisit for MVP).
- Close the tab — `OnUnloaded` calls `Disconnect()`, no orphaned process/handle.
- Disconnect from the server end — `OnDisconnected` fires, `MarkDisconnected` is called, tab shows a clear state (not just blank).
- Certificate mismatch scenario (self-signed/changed cert lab host) — confirm the native cert prompt still appears (we do not suppress it) and decide where to hook `RdpActions.CertificateAccepted/Rejected` auditing once the actual MSTSCAX certificate-event surface is confirmed (may require `IMsTscAxEvents4.OnFatalError`/`OnPocReady`-equivalent inspection during this spike — track as `docs/08` open item if not implemented in this PR).

- [ ] **Step 4: Commit**

```bash
git add src/RemoteOps.Desktop/Rdp/RdpTabView.xaml src/RemoteOps.Desktop/Rdp/RdpTabView.xaml.cs
git commit -m "feat(desktop): RdpTabView — WindowsFormsHost + MSTSCAX, senha sob demanda"
```

---

## Task 13: ADR-004 update, ADR-014, docs/08, CHANGELOG

**Files:**
- Modify: `adr/ADR-004-rdp-activex-vs-freerdp.md`
- Create: `adr/ADR-014-rdp-hospedagem-activex-e-politicas.md`
- Modify: `docs/08-rdp-terminal-server.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Update `adr/ADR-004-rdp-activex-vs-freerdp.md`**

Change `## Status` from `Proposta inicial.` to:

```markdown
## Status

Aceita. Implementada em `feature/integration-rdp` (ver ADR-014 para detalhes de
hospedagem, políticas e feature flag).
```

Append a new section at the end:

```markdown
## Implementação MVP

- Controle hospedado via `WindowsFormsHost` em `RdpTabView` (WPF), análogo ao
  WebView2 do terminal (`TerminalTabView`).
- Lógica de configuração de conexão (host/porta/usuário/políticas) isolada em
  `RdpConnectionConfigBuilder` — pura, sem COM, testável em CI.
- Spike FreeRDP **não executado neste PR** — critério de troca definido no ADR
  original permanece válido; reavaliar se ActiveX falhar em produção (foco,
  resize, distribuição).
- Atrás da feature flag `rdp.enabled` (default OFF) até validação em laboratório
  (ver docs/08 §Spikes obrigatórios e ADR-014).
```

- [ ] **Step 2: Write `adr/ADR-014-rdp-hospedagem-activex-e-politicas.md`**

```markdown
# ADR-014 — RDP: hospedagem ActiveX em aba, políticas de redirecionamento e feature flag

## Status

Aceita.

## Contexto

A frente `feature/integration-rdp` implementa a sessão RDP real descrita em
ADR-004 e docs/08-rdp-terminal-server.md: abrir o controle Microsoft RDP
(MSTSCAX/mstscax.dll) numa aba viva do Desktop, com credencial resolvida do
vault, redirecionamentos sensíveis desligados por padrão, NLA obrigatório e
auditoria de início/fim de sessão.

## Decisão 1 — Separação lógica pura / COM-UI

`RdpConnectionConfigBuilder` (host/porta/usuário/políticas) é uma classe pura
em `RemoteOps.Rdp`, sem dependência de COM ou UI — testável em qualquer
plataforma/CI. `RdpSessionProvider` faz apenas o trabalho não-visual (resolve
endpoint+usuário, monta config, audita, devolve `SessionHandle`) e **nunca
toca o vault**. A camada COM (`RdpTabView`, `AxMsRdpClient9NotSafeForScripting`)
fica isolada no Desktop, não testável em headless — coberta por verificação
manual (spike) documentada no PR.

## Decisão 2 — Senha com lifetime mínimo, resolvida no momento de conectar

Ao contrário de SSH (que resolve a senha dentro de `OpenAsync`, pois essa
chamada já é o connect), RDP separa "abrir handle/auditar" (`RdpSessionProvider.
OpenAsync`, sem vault) de "conectar visualmente" (`RdpTabView`, dispara quando o
`WindowsFormsHost` carrega). A senha só é lida do vault em `RdpTabView.
InitAndConnectAsync`, imediatamente antes de `AdvancedSettings.ClearTextPassword`,
via `IRdpCredentialResolver.ResolvePasswordAsync` (análogo a
`StoreWinBoxCredentialResolver`/ADR-009 §FIX-3: `using var secret = ...;
secret.RevealString()`). A `string` resultante sai de escopo assim que atribuída
ao controle — mesma mitigação e mesma limitação documentada em ADR-009 §FIX-3
(a string gerenciada persiste até o GC; não há alternativa sem mudar a API do
MSTSCAX).

## Decisão 3 — Redirecionamentos OFF por padrão

`RdpRedirectionPolicy.Default` desliga clipboard, drive, impressora, áudio e USB.
Não há perfil/política de override nesta PR — habilitar exige uma decisão de
produto futura (RBAC por grupo, como recomendado em docs/08 §Políticas
recomendadas), tratada como trabalho subsequente.

## Decisão 4 — NLA obrigatório, certificado nunca ignorado silenciosamente

`RdpConnectionConfig.NlaRequired` é sempre `true` no MVP (sem opção de
desligar). O prompt nativo de certificado inválido do MSTSCAX não é suprimido.
`RdpActions.CertificateAccepted/CertificateRejected` existem em `RdpAuditEvent`
para auditar a decisão assim que o gancho de evento de certificado do MSTSCAX
for confirmado durante o spike (ver Pendências).

## Decisão 5 — Feature flag `rdp.enabled`, default OFF

`IFeatureFlags` (Desktop, lido de `REMOTEOPS_FEATURE_FLAGS`) gateia tanto a
visibilidade do botão "Conectar RDP" (`InspectorViewModel.CanOpenRdp`) quanto o
roteamento em `MainViewModel.OnSessionRequested`. Com a flag OFF, o protocolo
`rdp` cai no placeholder pré-existente (`Tabs.OpenTab`), comportamento idêntico
ao de antes desta PR. Habilitar em produção requer revisão do `security-agent`
(CLAUDE.md §Atualizações de arquitetura v2).

## Consequências

- **Positivas:** lógica de config 100% testável sem Windows/COM; senha nunca
  retida em campo de ViewModel; rollout controlado por flag.
- **Negativas:** `RdpTabView`/COM não tem cobertura automatizada — depende de
  verificação manual em laboratório a cada mudança relevante de MSTSCAX.
- **Pendências (rastrear como issue após merge):** confirmar o evento MSTSCAX
  correto para auditar aceitação/rejeição de certificado (não identificado com
  certeza sem inspecionar a interop gerada); decidir se a aba RDP deve ser
  "pinned" para sobreviver a re-template de tab switch, ou se reconectar é
  aceitável no MVP.
```

- [ ] **Step 3: Update `docs/08-rdp-terminal-server.md`**

Append a new section at the end:

```markdown
## Status de implementação (feature/integration-rdp)

- Implementado atrás da feature flag `rdp.enabled` (default OFF). Ver ADR-014.
- `RdpConnectionConfigBuilder`/`RdpSessionProvider` cobrem host/porta/usuário/
  auditoria de forma testável (sem COM).
- `RdpTabView` hospeda o MSTSCAX via `WindowsFormsHost` — camada manual/spike,
  não coberta por teste automatizado headless.
- Redirecionamentos (clipboard/drive/printer/audio/USB) implementados como
  OFF-only no MVP — não há ainda UI/política para habilitá-los por grupo.
- Auditoria de certificado (aceitar/rejeitar) modelada em `RdpAuditEvent` mas
  o gancho de evento exato do MSTSCAX fica pendente de confirmação (ver ADR-014
  §Pendências).
```

- [ ] **Step 4: Update `CHANGELOG.md`**

Add an `## [Unreleased]` (or the repo's current in-progress version heading — check the top of `CHANGELOG.md` first and match its existing heading style) entry:

```markdown
### Adicionado

- Sessão RDP real (MSTSCAX/ActiveX) em aba viva, atrás da feature flag `rdp.enabled` (default OFF) — INT-RDP.
- `RdpConnectionConfigBuilder`, `RdpSessionProvider`, `RdpTabViewModel`/`RdpTabView`.
- ADR-014: hospedagem ActiveX, políticas de redirecionamento (default OFF), NLA obrigatório, feature flag.

### Alterado

- ADR-004 status: Proposta inicial → Aceita.
- `InspectorViewModel`: botão "Conectar RDP" agora condicionado a `rdp.enabled` + endpoint `rdp` presente.
```

- [ ] **Step 5: Commit**

```bash
git add adr/ADR-004-rdp-activex-vs-freerdp.md adr/ADR-014-rdp-hospedagem-activex-e-politicas.md docs/08-rdp-terminal-server.md CHANGELOG.md
git commit -m "docs(rdp): ADR-004 aceita, ADR-014, docs/08 e CHANGELOG"
```

---

## Task 14: Final verification

- [ ] **Step 1: Format**

Run: `dotnet format RemoteOps.sln --verify-no-changes --verbosity diagnostic`
If it reports changes: run `dotnet format RemoteOps.sln`, review the diff, then re-run `--verify-no-changes`.

- [ ] **Step 2: Full build**

Run: `dotnet build RemoteOps.sln --configuration Release`
Expected: 0 errors, 0 new warnings (COM interop may emit `CS0618`-style warnings from generated code — check whether the existing `TreatWarningsAsErrors` setup already excludes generated files; if a new warning blocks the build, fix it rather than suppress).

- [ ] **Step 3: Full test suite**

Run: `dotnet test RemoteOps.sln --configuration Release --logger trx`
Expected: all tests pass, including every test added in Tasks 1–9 plus the full pre-existing suite (no regressions in Terminal/MikroTik/Sync/Security suites).

- [ ] **Step 4: Secret-scan self-check**

Run: `git grep -niE "s3cr3t|password\s*=\s*\"|clearTextPassword\s*=\s*\"[^\"]+\"" -- src/ tests/` and confirm the only matches are the test fixtures' deliberately-fake `"s3cr3t-rdp"`/`"s3cr3t"` strings (never a real credential) and the `ClearTextPassword = password` assignment in `RdpTabView.xaml.cs` (a variable reference, not a literal).

- [ ] **Step 5: Manual RDP spike verification**

Re-run the Task 12 manual checklist end-to-end with `REMOTEOPS_FEATURE_FLAGS=rdp.enabled` against a real/lab RDP host. Record the outcome (works / known limitations) in the PR description per CLAUDE.md's "spike result must be documented" requirement.

- [ ] **Step 6: Generate test plan and security review**

Run the `/test-plan` skill (focus: pure config/credential/policy tests already in place — call out any gap found) and the `/security-review` skill (focus: password handling/lifetime, redirection defaults, cert/NLA handling, no-secret-in-log) against this branch's diff. Fold any findings into the working tree before opening the PR, or note them explicitly as follow-up issues in the PR description.

- [ ] **Step 7: Push and open PR**

```bash
git push -u origin feature/integration-rdp
```

Open the PR using the repo's template. Leave `Depends-on:` empty (everything RDP depends on is already on `main`). In the PR description:
- Summarize the feature-flagged rollout (OFF by default).
- Link ADR-004 (updated) and ADR-014 (new).
- Document the Task 12 spike results (connect/resize/disconnect/cert-prompt behavior, any limitations found).
- Tag `security-agent` for review (touches credential resolution and a new external COM surface) and `qa-agent` for the test plan.

Notify the orchestrating session that `feature/integration-rdp` is ready for review/merge.
