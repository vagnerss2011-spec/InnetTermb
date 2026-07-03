# SSH avançado (chave privada + perfis de algoritmos) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (ou subagent-driven-development). Steps usam checkbox (`- [ ]`).

**Goal:** Autenticação SSH por chave privada (credencial no Keychain, com passphrase opcional) + perfil de key algorithms por endpoint (Automático/Compatível/Legado) para equipamento antigo e novo.

**Architecture:** 3 unidades — (U1) contratos + Keychain para credencial `privateKey` com 2 envelopes no vault; (U2) `SshAlgorithmPolicy` puro que preenche `ConnectionInfo`; (U3) provider faz dispatch por `CredentialRef.Type` e aplica o perfil no único plug point (`RenciSshConnectionFactory`).

**Tech Stack:** .NET 10, WPF, SSH.NET 2024.2.0 (Renci.SshNet), xUnit.

**Worktree:** `C:\dev\remoteops-ssh-advanced` (branch `feature/ssh-advanced`, base `origin/main` `df34111`). **Spec:** `docs/superpowers/specs/2026-07-03-ssh-advanced-design.md`. **Release alvo:** v1.2.0.

## Global Constraints

- `TreatWarningsAsErrors=true` → 0/0. Nullable/ImplicitUsings enable. Suíte existente (**489**) verde.
- **Antes de push:** `dotnet format` + `dotnet format --verify-no-changes` (exit 0); build+test **Release**.
- Segredo (chave/passphrase) **nunca** em log/UI/auditoria: `char[]`/`VaultSecret` (auto-zero), `Array.Clear`. Padrão `CredentialDialog.GetPassword()`.
- Tipo canônico da credencial de chave: **`"privateKey"`** (constante `CredentialTypes.PrivateKey`); passphrase: `"privateKeyPassphrase"`. O Type entra no AAD do AES-GCM — usar a constante em todo lugar.
- Build: `dotnet build "C:\dev\remoteops-ssh-advanced\RemoteOps.sln" -c Debug --nologo`. Test idem.

## Interfaces existentes reaproveitadas

- `CredentialRef { Id, Name, Type, Scope?, Metadata?, SecretEnvelopeId?, Version }`; `CredentialMetadata { Username?, HasPrivateKey, LastRotatedAt? }` (`Contracts/Assets/CredentialRef.cs`).
- `EndpointProfile { VendorProfile?, TerminalEncoding? }` (`Contracts/Assets/Endpoint.cs:27`); persistido via `profile_json` no SqlCipher.
- `IVault`: `Task<SecretEnvelope> StoreAsync(VaultStoreRequest{WorkspaceId,CredentialId,Type,ActorUserId}, ReadOnlyMemory<char>, ct)`; `Task<VaultSecret> RetrieveAsync(envelopeId, VaultAccessContext, ct)`; `Task<SecretEnvelope> RotateAsync(envelopeId, ReadOnlyMemory<char>, VaultAccessContext, ct)`; `Task RevokeAsync(envelopeId, VaultAccessContext, ct)`. `VaultSecret` (IDisposable): `RevealUtf8()→ReadOnlySpan<byte>`, `RevealString()→string`.
- `KeychainViewModel(ILocalStore, IVault, string workspaceId)`; `ILocalStore.UpdateCredentialRefAsync`/`AddCredentialRefAsync`/`DeleteCredentialRefAsync`/`GetCredentialRefsAsync`.
- `CredentialDialogViewModel(CredentialDialogMode mode, string name="", string username="")`; `CredentialDialog.GetPassword()→char[]` (`Views/CredentialDialog.xaml.cs`).
- SSH: `internal interface ISshConnectionFactory { ISshConnection Create(string host,int port,string username,string password); }` (`Terminal/Ssh/ISshConnectionFactory.cs`); impl `RenciSshConnectionFactory` (`RenciSshConnection.cs:10`); `SshSessionProvider.ConnectWithTofuAsync(host,port,username,password,...)` chama `_factory.Create(...)` em `SshSessionProvider.cs:130`. `RemoteOps.Terminal` tem `InternalsVisibleTo("RemoteOps.UnitTests")`.
- SSH.NET 2024.2.0: `ConnectionInfo.KeyExchangeAlgorithms/HostKeyAlgorithms/Encryptions/HmacAlgorithms` (`IDictionary<string, ...>` ordenados); `new PrivateKeyFile(Stream, string? passphrase)`; `new PrivateKeyAuthenticationMethod(string username, params IPrivateKeySource[])`. Algoritmos disponíveis por default incluem: KEX `diffie-hellman-group14-sha1`, `-group-exchange-sha1`, `-group1-sha1`, `-group14-sha256`, `-group16-sha512`, `-group-exchange-sha256`; HostKey `ssh-rsa`, `rsa-sha2-256/512`, `ssh-ed25519`, `ecdsa-sha2-nistp256/384/521`; Cipher `aes128/192/256-cbc`, `aes128/192/256-ctr`, `aes*-gcm@openssh.com`, `3des-cbc`, `chacha20-poly1305@openssh.com`; HMAC `hmac-sha1`, `hmac-sha2-256/512`.

---

### Task 1: `CredentialTypes` (constante compartilhada)

**Files:** Create `src/RemoteOps.Contracts/Assets/CredentialTypes.cs`; Test `tests/RemoteOps.UnitTests/Contracts/CredentialTypesTests.cs`.

**Produces:** `CredentialTypes.Password="password"`, `CredentialTypes.PrivateKey="privateKey"`, `CredentialTypes.PrivateKeyPassphrase="privateKeyPassphrase"`.

- [ ] **Step 1: Teste**
```csharp
using RemoteOps.Contracts.Assets;
using Xunit;

namespace RemoteOps.UnitTests.Contracts;

public sealed class CredentialTypesTests
{
    [Fact]
    public void Values_AreStable()
    {
        Assert.Equal("password", CredentialTypes.Password);
        Assert.Equal("privateKey", CredentialTypes.PrivateKey);
        Assert.Equal("privateKeyPassphrase", CredentialTypes.PrivateKeyPassphrase);
    }
}
```
- [ ] **Step 2: Rodar → falha de compilação.**
- [ ] **Step 3: Implementar**
```csharp
namespace RemoteOps.Contracts.Assets;

/// <summary>Valores canônicos de <see cref="CredentialRef.Type"/>. O Type entra no AAD do
/// AES-GCM do vault — divergência de grafia entre camadas quebra o decrypt.</summary>
public static class CredentialTypes
{
    public const string Password = "password";
    public const string PrivateKey = "privateKey";
    public const string PrivateKeyPassphrase = "privateKeyPassphrase";
}
```
- [ ] **Step 4: Rodar → passa.**
- [ ] **Step 5: `dotnet format` + commit** — `feat(ssh): CredentialTypes`.

---

### Task 2: `CredentialMetadata.PassphraseEnvelopeId` + `EndpointProfile.SshAlgorithmProfile`

**Files:** Modify `src/RemoteOps.Contracts/Assets/CredentialRef.cs`, `src/RemoteOps.Contracts/Assets/Endpoint.cs`; Test `tests/RemoteOps.UnitTests/Desktop/Infrastructure/CredentialMetadataKeyFieldsTests.cs`.

**Produces:** `CredentialMetadata.PassphraseEnvelopeId` (string?, init); `EndpointProfile.SshAlgorithmProfile` (string?, init).

- [ ] **Step 1: Teste** (round-trip via SqlCipher seria pesado; testa serialização JSON do metadata que o store usa):
```csharp
using System.Text.Json;
using RemoteOps.Contracts.Assets;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class CredentialMetadataKeyFieldsTests
{
    [Fact]
    public void Metadata_PassphraseEnvelopeId_RoundTripsJson()
    {
        var m = new CredentialMetadata { Username = "root", HasPrivateKey = true, PassphraseEnvelopeId = "env-pp" };
        var back = JsonSerializer.Deserialize<CredentialMetadata>(JsonSerializer.Serialize(m))!;
        Assert.True(back.HasPrivateKey);
        Assert.Equal("env-pp", back.PassphraseEnvelopeId);
    }

    [Fact]
    public void EndpointProfile_SshAlgorithmProfile_RoundTripsJson()
    {
        var p = new EndpointProfile { SshAlgorithmProfile = "compat" };
        var back = JsonSerializer.Deserialize<EndpointProfile>(JsonSerializer.Serialize(p))!;
        Assert.Equal("compat", back.SshAlgorithmProfile);
    }
}
```
- [ ] **Step 2: Rodar → falha.**
- [ ] **Step 3: Implementar** — em `CredentialRef.cs`, no `CredentialMetadata`, após `HasPrivateKey`:
```csharp
    /// <summary>Envelope da passphrase da chave privada (quando houver); null = chave sem passphrase.</summary>
    public string? PassphraseEnvelopeId { get; init; }
```
Em `Endpoint.cs`, no `EndpointProfile`, após `TerminalEncoding`:
```csharp
    /// <summary>Perfil de segurança SSH: "auto" (default permissivo) | "strict" (só algoritmos fortes). null = auto.</summary>
    public string? SshAlgorithmProfile { get; init; }
```
- [ ] **Step 4: Rodar → passa.**
- [ ] **Step 5: `dotnet format` + commit** — `feat(ssh): campos PassphraseEnvelopeId + SshAlgorithmProfile`.

---

### Task 3: `PrivateKeyInput.Classify`

**Files:** Create `src/RemoteOps.Desktop/Infrastructure/PrivateKeyInput.cs`; Test `tests/RemoteOps.UnitTests/Desktop/Infrastructure/PrivateKeyInputTests.cs`.

**Produces:** `enum PrivateKeyKind { Valid, PuttyPpk, Invalid }`; `static PrivateKeyKind PrivateKeyInput.Classify(string text)`.

- [ ] **Step 1: Teste**
```csharp
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class PrivateKeyInputTests
{
    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----", PrivateKeyKind.Valid)]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nabc\n-----END RSA PRIVATE KEY-----", PrivateKeyKind.Valid)]
    [InlineData("   -----BEGIN PRIVATE KEY-----\nx", PrivateKeyKind.Valid)]
    [InlineData("PuTTY-User-Key-File-2: ssh-rsa\nEncryption: none", PrivateKeyKind.PuttyPpk)]
    [InlineData("PuTTY-User-Key-File-3: ssh-ed25519", PrivateKeyKind.PuttyPpk)]
    [InlineData("qualquer lixo", PrivateKeyKind.Invalid)]
    [InlineData("", PrivateKeyKind.Invalid)]
    public void Classify_Works(string text, PrivateKeyKind expected)
        => Assert.Equal(expected, PrivateKeyInput.Classify(text));
}
```
- [ ] **Step 2: Rodar → falha.**
- [ ] **Step 3: Implementar**
```csharp
using System;

namespace RemoteOps.Desktop.Infrastructure;

public enum PrivateKeyKind { Valid, PuttyPpk, Invalid }

/// <summary>Classifica o texto de uma chave privada colada/importada. Puro (sem IO).</summary>
public static class PrivateKeyInput
{
    public static PrivateKeyKind Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PrivateKeyKind.Invalid;
        }

        string t = text.TrimStart();
        if (t.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            return PrivateKeyKind.PuttyPpk;
        }

        return t.StartsWith("-----BEGIN", StringComparison.Ordinal)
            ? PrivateKeyKind.Valid
            : PrivateKeyKind.Invalid;
    }
}
```
- [ ] **Step 4: Rodar → passa.**
- [ ] **Step 5: `dotnet format` + commit** — `feat(ssh): PrivateKeyInput.Classify (valida PEM, detecta PPK)`.

---

### Task 4: `SshAlgorithmPolicy` (perfil Estrito remove algoritmos fracos)

**Files:** Create `src/RemoteOps.Terminal/Ssh/SshAlgorithmPolicy.cs`; Test `tests/RemoteOps.UnitTests/Terminal/SshAlgorithmPolicyTests.cs`.

**Produces:** `static class SshAlgorithmPolicy { const string Auto="auto", Strict="strict"; static void Apply(ConnectionInfo info, string? profile); }`.

**Nota de design (verificado rodando a lib):** SSH.NET 2024.2.0 já habilita por default TODOS os algoritmos, fortes e fracos (KEX inclui `group1-sha1`/`group14-sha1`; Cipher inclui `aes*-cbc`/`3des-cbc`; HostKey inclui `ssh-rsa`/`ssh-dss`; HMAC inclui `hmac-sha1`). Logo equipamento antigo já conecta em `auto`. O perfil **Estrito** endurece: `.Remove()` os fracos dos dicionários da `ConnectionInfo`. `.Remove` de chave ausente é no-op → nunca lança.

- [ ] **Step 1: Teste**
```csharp
using Renci.SshNet;
using RemoteOps.Terminal.Ssh;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshAlgorithmPolicyTests
{
    private static ConnectionInfo NewInfo()
        => new("h", 22, "u", new PasswordAuthenticationMethod("u", "p"));

    [Fact]
    public void Auto_DoesNotChangeAlgorithms()
    {
        var info = NewInfo();
        int kex = info.KeyExchangeAlgorithms.Count;
        int enc = info.Encryptions.Count;
        SshAlgorithmPolicy.Apply(info, SshAlgorithmPolicy.Auto);
        Assert.Equal(kex, info.KeyExchangeAlgorithms.Count);
        Assert.Equal(enc, info.Encryptions.Count);
    }

    [Fact]
    public void Strict_RemovesWeakAlgorithms()
    {
        var info = NewInfo();
        SshAlgorithmPolicy.Apply(info, SshAlgorithmPolicy.Strict);
        Assert.DoesNotContain("diffie-hellman-group1-sha1", info.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("diffie-hellman-group14-sha1", info.KeyExchangeAlgorithms.Keys);
        Assert.DoesNotContain("ssh-rsa", info.HostKeyAlgorithms.Keys);
        Assert.DoesNotContain("aes256-cbc", info.Encryptions.Keys);
        Assert.DoesNotContain("3des-cbc", info.Encryptions.Keys);
        Assert.DoesNotContain("hmac-sha1", info.HmacAlgorithms.Keys);
    }

    [Fact]
    public void Strict_KeepsStrongAlgorithms()
    {
        var info = NewInfo();
        SshAlgorithmPolicy.Apply(info, SshAlgorithmPolicy.Strict);
        Assert.Contains("curve25519-sha256", info.KeyExchangeAlgorithms.Keys);
        Assert.Contains("aes256-ctr", info.Encryptions.Keys);
        Assert.Contains("ssh-ed25519", info.HostKeyAlgorithms.Keys);
        Assert.Contains("hmac-sha2-256", info.HmacAlgorithms.Keys);
    }

    [Fact]
    public void NullProfile_DoesNotThrow() => SshAlgorithmPolicy.Apply(NewInfo(), null);
}
```

- [ ] **Step 2: Rodar → falha (classe não existe).**
- [ ] **Step 3: Implementar**
```csharp
using System.Collections.Generic;
using Renci.SshNet;

namespace RemoteOps.Terminal.Ssh;

/// <summary>
/// Aplica o perfil de segurança SSH ao <see cref="ConnectionInfo"/>, no único ponto onde a
/// conexão é montada. SSH.NET 2024.2.0 já habilita TODOS os algoritmos (fortes e fracos) por
/// default — então "auto" conecta a equipamento legado sem ação. "strict" REMOVE os fracos
/// (só desabilita — nenhuma criptografia nova). Perfil por host: o hardening não afeta a frota.
/// </summary>
public static class SshAlgorithmPolicy
{
    public const string Auto = "auto";
    public const string Strict = "strict";

    private static readonly string[] WeakKex =
    {
        "diffie-hellman-group1-sha1", "diffie-hellman-group14-sha1", "diffie-hellman-group-exchange-sha1",
    };
    private static readonly string[] WeakHostKey =
    {
        "ssh-rsa", "ssh-dss", "ssh-rsa-cert-v01@openssh.com", "ssh-dss-cert-v01@openssh.com",
    };
    private static readonly string[] WeakCiphers =
    {
        "aes128-cbc", "aes192-cbc", "aes256-cbc", "3des-cbc",
    };
    private static readonly string[] WeakHmac =
    {
        "hmac-sha1", "hmac-sha1-etm@openssh.com",
    };

    public static void Apply(ConnectionInfo info, string? profile)
    {
        if (profile != Strict)
        {
            return; // auto/null: defaults permissivos da lib (conecta a legado)
        }

        Remove(info.KeyExchangeAlgorithms, WeakKex);
        Remove(info.HostKeyAlgorithms, WeakHostKey);
        Remove(info.Encryptions, WeakCiphers);
        Remove(info.HmacAlgorithms, WeakHmac);
    }

    private static void Remove<T>(IDictionary<string, T> algos, string[] names)
    {
        foreach (string name in names)
        {
            algos.Remove(name); // no-op se ausente; nunca lança
        }
    }
}
```
- [ ] **Step 4: Rodar → passa.**
- [ ] **Step 5: `dotnet format` + commit** — `feat(ssh): SshAlgorithmPolicy (perfil Estrito remove algoritmos fracos)`.

---

### Task 5: `SshConnectionOptions` + nova assinatura da factory + auth por chave

**Files:** Modify `src/RemoteOps.Terminal/Ssh/ISshConnectionFactory.cs`, `RenciSshConnection.cs`; Modify `tests/RemoteOps.UnitTests/Terminal/Fakes/FakeSshConnectionFactory.cs`; Test `tests/RemoteOps.UnitTests/Terminal/RenciSshConnectionFactoryTests.cs`.

**Produces:** `internal sealed record SshConnectionOptions { string Host; int Port; string Username; string? Password; byte[]? PrivateKeyUtf8; string? PrivateKeyPassphrase; string? AlgorithmProfile; }`; `ISshConnection Create(SshConnectionOptions options)`.

- [ ] **Step 1: Teste** (a factory real com chave inválida deve lançar mensagem legível; sem rede):
```csharp
using System.Text;
using RemoteOps.Terminal.Ssh;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class RenciSshConnectionFactoryTests
{
    [Fact]
    public void Create_WithInvalidPrivateKey_ThrowsReadableError()
    {
        var factory = new RenciSshConnectionFactory();
        var opts = new SshConnectionOptions
        {
            Host = "h", Port = 22, Username = "u",
            PrivateKeyUtf8 = Encoding.UTF8.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----\nnotarealkey\n-----END OPENSSH PRIVATE KEY-----"),
        };
        var ex = Assert.ThrowsAny<System.Exception>(() => factory.Create(opts));
        Assert.Contains("chave", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_WithPassword_DoesNotThrow()
    {
        var factory = new RenciSshConnectionFactory();
        using var conn = factory.Create(new SshConnectionOptions { Host = "h", Port = 22, Username = "u", Password = "p" });
        Assert.NotNull(conn);
    }
}
```
- [ ] **Step 2: Rodar → falha (assinatura antiga).**
- [ ] **Step 3: `ISshConnectionFactory.cs`** — substituir a assinatura + adicionar o record:
```csharp
/// <summary>Opções de conexão SSH: senha OU chave privada, mais o perfil de algoritmos.</summary>
internal sealed record SshConnectionOptions
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public string? Password { get; init; }
    public byte[]? PrivateKeyUtf8 { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
    public string? AlgorithmProfile { get; init; }
}

internal interface ISshConnectionFactory
{
    ISshConnection Create(SshConnectionOptions options);
}
```
- [ ] **Step 4: `RenciSshConnection.cs`** — nova `Create`:
```csharp
    public ISshConnection Create(SshConnectionOptions options)
    {
        AuthenticationMethod authMethod;
        if (options.PrivateKeyUtf8 is { } keyBytes)
        {
            try
            {
                using var ms = new MemoryStream(keyBytes);
                var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                    ? new PrivateKeyFile(ms)
                    : new PrivateKeyFile(ms, options.PrivateKeyPassphrase);
                authMethod = new PrivateKeyAuthenticationMethod(options.Username, keyFile);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Chave privada inválida ou passphrase incorreta.", ex);
            }
        }
        else
        {
            authMethod = new PasswordAuthenticationMethod(options.Username, options.Password ?? string.Empty);
        }

        var info = new ConnectionInfo(options.Host, options.Port, options.Username, authMethod)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        SshAlgorithmPolicy.Apply(info, options.AlgorithmProfile);

        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        return new RenciSshConnection(client);
    }
```
Adicionar `using System.IO;` no topo se faltar.
- [ ] **Step 5: `FakeSshConnectionFactory.cs`** — nova assinatura + capturar as opções:
```csharp
    public SshConnectionOptions? LastOptions { get; private set; }

    public ISshConnection Create(SshConnectionOptions options)
    {
        LastOptions = options;
        var conn = new FakeSshConnection(SimulatedFingerprint, ForceValidatorResult);
        Created.Add(conn);
        return conn;
    }
```
(remover a assinatura antiga `Create(string,int,string,string)`).
- [ ] **Step 6: Rodar** os testes de factory + `SshSessionProviderTests` (vão quebrar na chamada — corrigidos na Task 6). Confirmar que `RenciSshConnectionFactoryTests` passa.
- [ ] **Step 7: `dotnet format` + commit** — `feat(ssh): factory por SshConnectionOptions (chave + perfil)`.

---

### Task 6: `SshSessionProvider` — dispatch por Type + recupera chave/passphrase + perfil

**Files:** Modify `src/RemoteOps.Terminal/Ssh/SshSessionProvider.cs`; Modify `tests/RemoteOps.UnitTests/Terminal/SshSessionProviderTests.cs` (ajustar chamadas + novos casos); Test novo `tests/RemoteOps.UnitTests/Terminal/SshKeyAuthTests.cs`.

**Consumes:** `SshConnectionOptions`, `CredentialTypes`, `IVault.RetrieveAsync`, `CredentialMetadata.PassphraseEnvelopeId`, `EndpointProfile.SshAlgorithmProfile`.

- [ ] **Step 1: Teste novo** — `SshKeyAuthTests.cs` (usa os fakes existentes; a `FakeSshConnectionFactory` agora captura `LastOptions`, e a conexão fake não conecta de verdade). Precisa que o endpoint tenha profile e a credencial seja privateKey:
```csharp
using System.Text;
using RemoteOps.Contracts.Assets;
using RemoteOps.Contracts.Sessions;
using RemoteOps.Terminal.Ssh;
using RemoteOps.UnitTests.Terminal.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class SshKeyAuthTests
{
    [Fact]
    public async Task PrivateKeyCredential_PassesKeyAndProfile_NotPassword()
    {
        var factory = new FakeSshConnectionFactory { ForceValidatorResult = true };
        var vault = new FakeVault();
        // credencial privateKey: envelope da chave + envelope da passphrase
        string keyPem = "-----BEGIN OPENSSH PRIVATE KEY-----\nKEYBODY\n-----END OPENSSH PRIVATE KEY-----";
        string keyEnv = await vault.StoreForTestAsync("privateKey", keyPem);
        string ppEnv = await vault.StoreForTestAsync("privateKeyPassphrase", "s3nha");
        var cred = new CredentialRef
        {
            Id = "c1", Name = "k", Type = CredentialTypes.PrivateKey, SecretEnvelopeId = keyEnv,
            Metadata = new CredentialMetadata { Username = "root", HasPrivateKey = true, PassphraseEnvelopeId = ppEnv },
        };
        var endpoint = new Endpoint { Id = "e1", AssetId = "a1", Protocol = "ssh", Ipv4 = "10.0.0.1", Port = 22, CredentialRefId = "c1", Profile = new EndpointProfile { SshAlgorithmProfile = "strict" } };

        var provider = SshTestHarness.CreateProvider(factory, vault, endpoint, cred);
        await provider.OpenAsync(new SessionRequest { SessionId = "s1", Protocol = "ssh", EndpointId = "e1", CredentialRefId = "c1" }, default);

        var opts = factory.LastOptions!;
        Assert.NotNull(opts.PrivateKeyUtf8);
        Assert.Equal(Encoding.UTF8.GetString(opts.PrivateKeyUtf8!), keyPem);
        Assert.Equal("s3nha", opts.PrivateKeyPassphrase);
        Assert.Null(opts.Password);
        Assert.Equal("strict", opts.AlgorithmProfile);
    }

    [Fact]
    public async Task PasswordCredential_PassesPassword_NotKey()
    {
        var factory = new FakeSshConnectionFactory { ForceValidatorResult = true };
        var vault = new FakeVault();
        string env = await vault.StoreForTestAsync("password", "p4ss");
        var cred = new CredentialRef { Id = "c1", Name = "p", Type = CredentialTypes.Password, SecretEnvelopeId = env, Metadata = new CredentialMetadata { Username = "root" } };
        var endpoint = new Endpoint { Id = "e1", AssetId = "a1", Protocol = "ssh", Ipv4 = "10.0.0.1", Port = 22, CredentialRefId = "c1" };

        var provider = SshTestHarness.CreateProvider(factory, vault, endpoint, cred);
        await provider.OpenAsync(new SessionRequest { SessionId = "s1", Protocol = "ssh", EndpointId = "e1", CredentialRefId = "c1" }, default);

        var opts = factory.LastOptions!;
        Assert.Equal("p4ss", opts.Password);
        Assert.Null(opts.PrivateKeyUtf8);
    }
}
```
> **Pré-requisito de teste:** `FakeVault` precisa de um helper `StoreForTestAsync(type, plaintext) → envelopeId` que guarde por tipo e permita `RetrieveAsync`. E um `SshTestHarness.CreateProvider(...)` que monte o `SshSessionProvider` com os fakes (resolver de endpoint/credencial in-memory, security-context/audit/hostkey fakes já existentes). Ver Step 3/4 — reutiliza os fakes de `SshSessionProviderTests.cs` (ler esse arquivo e extrair a montagem para o harness ou inline no teste). Se preferir, montar o provider inline no teste com os `InMemoryEndpointResolver`/`InMemoryCredentialRefResolver` já existentes.

- [ ] **Step 2: Rodar → falha.**
- [ ] **Step 3: `FakeVault`** — garantir `StoreForTestAsync` (se não existir, adicionar) que cria envelope e permite `RetrieveAsync` devolver o plaintext certo por envelopeId, respeitando o Type. (Ler `Fakes/FakeVault.cs`; muitos testes já o usam — estender sem quebrar.)
- [ ] **Step 4: Implementar em `SshSessionProvider.OpenAsync`** — trocar o bloco de recuperação de segredo (linhas ~68-92) por dispatch:
```csharp
        string username = credRef.Metadata?.Username
            ?? throw new InvalidOperationException($"CredentialRef '{request.CredentialRefId}' não tem username em Metadata.");
        string envelopeId = credRef.SecretEnvelopeId
            ?? throw new InvalidOperationException($"CredentialRef '{request.CredentialRefId}' não tem SecretEnvelopeId.");

        int cols = request.Terminal?.Cols ?? 80;
        int rows = request.Terminal?.Rows ?? 24;
        string termType = "xterm-256color";
        string? algorithmProfile = endpoint.Profile?.SshAlgorithmProfile;

        var vaultCtx = new VaultAccessContext { ActorUserId = _securityContext.ActorUserId, DeviceId = _securityContext.DeviceId };

        SshConnectionOptions options;
        if (credRef.Type == CredentialTypes.PrivateKey)
        {
            using var keySecret = await _vault.RetrieveAsync(envelopeId, vaultCtx, ct);
            byte[] keyBytes = keySecret.RevealUtf8().ToArray();
            string? passphrase = null;
            if (credRef.Metadata?.PassphraseEnvelopeId is { } ppId)
            {
                using var ppSecret = await _vault.RetrieveAsync(ppId, vaultCtx, ct);
                passphrase = ppSecret.RevealString();
            }
            options = new SshConnectionOptions { Host = host, Port = port, Username = username, PrivateKeyUtf8 = keyBytes, PrivateKeyPassphrase = passphrase, AlgorithmProfile = algorithmProfile };
        }
        else
        {
            using var secret = await _vault.RetrieveAsync(envelopeId, vaultCtx, ct);
            options = new SshConnectionOptions { Host = host, Port = port, Username = username, Password = secret.RevealString(), AlgorithmProfile = algorithmProfile };
        }

        var (connection, shell, channel, readerCts) =
            await ConnectWithTofuAsync(options, cols, rows, termType, request.SessionId, ct);
```
E trocar a assinatura de `ConnectWithTofuAsync` para receber `SshConnectionOptions options` no lugar de `host,port,username,password` (usar `options.Host` no lugar de `host` nos audits/host-key; `_factory.Create(options)` no lugar de `_factory.Create(host,port,username,password)`).
> **Higiene:** `keyBytes` é cópia gerenciada — após o connect, `Array.Clear(keyBytes)` no `finally` do fluxo de conexão (registrar como parte do Step; o PEM não deve persistir além do necessário).
- [ ] **Step 5: Ajustar `SshSessionProviderTests.cs`** — as chamadas antigas de `ConnectWithTofuAsync`/factory já passam por `OpenAsync`, então os testes existentes que usam senha continuam via o branch `else`. Só ajustar o que referencia a assinatura antiga da factory (se algum). Rodar todos os testes de `Terminal`.
- [ ] **Step 6: Build 0/0 + testes verdes.**
- [ ] **Step 7: `dotnet format` + commit** — `feat(ssh): provider autentica por chave + aplica perfil do endpoint`.

---

### Task 7: `KeychainViewModel` — criar/rotacionar credencial de chave

**Files:** Modify `src/RemoteOps.Desktop/ViewModels/KeychainViewModel.cs`; Test `tests/RemoteOps.UnitTests/Desktop/ViewModels/KeychainKeyCredentialTests.cs`.

**Produces:** `CreateKeyAsync(name, username, char[] privateKey, char[]? passphrase)`; `ReplaceKeyAsync(cred, char[] newKey)`; `ChangePassphraseAsync(cred, char[] newPassphrase)`; `ChangePasswordAsync` guardado por tipo; `DeleteAsync` revoga passphrase.

- [ ] **Step 1: Teste** — `KeychainKeyCredentialTests.cs` (usa `FakeVault` + `InMemoryLocalStore`):
```csharp
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.UnitTests.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class KeychainKeyCredentialTests
{
    private static (KeychainViewModel vm, InMemoryLocalStore store, FakeVault vault) Build()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        return (new KeychainViewModel(store, vault, "ws-local"), store, vault);
    }

    [Fact]
    public async Task CreateKey_WithPassphrase_StoresTwoEnvelopes_AndMetadata()
    {
        var (vm, store, _) = Build();
        await vm.CreateKeyAsync("router-key", "root", "PRIVATEKEY".ToCharArray(), "pass".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        Assert.Equal(CredentialTypes.PrivateKey, cred.Type);
        Assert.True(cred.Metadata!.HasPrivateKey);
        Assert.Equal("root", cred.Metadata.Username);
        Assert.NotNull(cred.SecretEnvelopeId);
        Assert.NotNull(cred.Metadata.PassphraseEnvelopeId);
    }

    [Fact]
    public async Task CreateKey_NoPassphrase_LeavesPassphraseEnvelopeNull()
    {
        var (vm, store, _) = Build();
        await vm.CreateKeyAsync("k", "root", "PRIVATEKEY".ToCharArray(), passphrase: null);
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        Assert.Null(cred.Metadata!.PassphraseEnvelopeId);
    }

    [Fact]
    public async Task ChangePassword_OnKeyCredential_DoesNothing()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("k", "root", "PK".ToCharArray(), null);
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        vault.RotatedEnvelopeIds.Clear();
        await vm.ChangePasswordAsync(cred, "x".ToCharArray());
        Assert.Empty(vault.RotatedEnvelopeIds);
    }

    [Fact]
    public async Task Delete_KeyWithPassphrase_RevokesBothEnvelopes()
    {
        var (vm, store, vault) = Build();
        await vm.CreateKeyAsync("k", "root", "PK".ToCharArray(), "pp".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.DeleteAsync(cred);
        Assert.Contains(cred.SecretEnvelopeId, vault.RevokedEnvelopeIds);
        Assert.Contains(cred.Metadata!.PassphraseEnvelopeId, vault.RevokedEnvelopeIds);
    }
}
```
> **Pré-requisito:** `FakeVault` deve expor `RotatedEnvelopeIds`/`RevokedEnvelopeIds` (listas). Se não existirem, adicionar no `FakeVault` (Step 2 revela). `ChangePasswordAsync` guard por tipo: hoje roda `RotateAsync` incondicionalmente — passa a checar `cred.Type == CredentialTypes.Password`.
- [ ] **Step 2: Rodar → falha.**
- [ ] **Step 3: Implementar** — em `KeychainViewModel.cs`, adicionar (e trocar `"password"` literais por `CredentialTypes.Password`):
```csharp
    public async Task CreateKeyAsync(string name, string username, char[] privateKey, char[]? passphrase)
    {
        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope keyEnv = await _vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId, Type = CredentialTypes.PrivateKey, ActorUserId = Actor },
            privateKey);
        Array.Clear(privateKey);

        string? passphraseEnvelopeId = null;
        if (passphrase is { Length: > 0 })
        {
            SecretEnvelope ppEnv = await _vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId + "-pp", Type = CredentialTypes.PrivateKeyPassphrase, ActorUserId = Actor },
                passphrase);
            passphraseEnvelopeId = ppEnv.EnvelopeId;
        }
        if (passphrase is not null) Array.Clear(passphrase);

        await _store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = name.Trim(),
            Type = CredentialTypes.PrivateKey,
            Metadata = new CredentialMetadata { Username = username.Trim(), HasPrivateKey = true, PassphraseEnvelopeId = passphraseEnvelopeId },
            SecretEnvelopeId = keyEnv.EnvelopeId,
        });
        await LoadAsync();
    }

    public async Task ReplaceKeyAsync(CredentialRef cred, char[] newKey)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RotateAsync(envId, newKey, new VaultAccessContext { ActorUserId = Actor });
        Array.Clear(newKey);
    }

    public async Task ChangePassphraseAsync(CredentialRef cred, char[] newPassphrase)
    {
        if (cred.Metadata?.PassphraseEnvelopeId is { } ppId)
        {
            await _vault.RotateAsync(ppId, newPassphrase, new VaultAccessContext { ActorUserId = Actor });
        }
        else
        {
            SecretEnvelope ppEnv = await _vault.StoreAsync(
                new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = cred.Id + "-pp", Type = CredentialTypes.PrivateKeyPassphrase, ActorUserId = Actor },
                newPassphrase);
            await _store.UpdateCredentialRefAsync(new CredentialRef
            {
                Id = cred.Id, Name = cred.Name, Type = cred.Type, Scope = cred.Scope,
                Metadata = new CredentialMetadata { Username = cred.Metadata?.Username, HasPrivateKey = true, PassphraseEnvelopeId = ppEnv.EnvelopeId },
                SecretEnvelopeId = cred.SecretEnvelopeId, Version = cred.Version,
            });
        }
        Array.Clear(newPassphrase);
        await LoadAsync();
    }
```
Trocar `ChangePasswordAsync` para guardar por tipo:
```csharp
    public async Task ChangePasswordAsync(CredentialRef cred, char[] newPassword)
    {
        if (cred.Type == CredentialTypes.Password && cred.SecretEnvelopeId is { } envId)
            await _vault.RotateAsync(envId, newPassword, new VaultAccessContext { ActorUserId = Actor });
        Array.Clear(newPassword);
    }
```
Trocar `DeleteAsync` para revogar passphrase:
```csharp
    public async Task DeleteAsync(CredentialRef cred)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RevokeAsync(envId, new VaultAccessContext { ActorUserId = Actor });
        if (cred.Metadata?.PassphraseEnvelopeId is { } ppId)
            await _vault.RevokeAsync(ppId, new VaultAccessContext { ActorUserId = Actor });
        await _store.DeleteCredentialRefAsync(cred.Id);
        await LoadAsync();
    }
```
E `CreateAsync`/`UpdateAsync`: trocar literais `"password"` por `CredentialTypes.Password`.
- [ ] **Step 4: Rodar → passa** + suíte Keychain existente verde.
- [ ] **Step 5: `dotnet format` + commit** — `feat(ssh): KeychainViewModel cria/rotaciona credencial de chave`.

---

### Task 8: `CredentialDialog` — tipo, chave (arquivo+colar), passphrase

**Files:** Modify `src/RemoteOps.Desktop/ViewModels/CredentialDialogViewModel.cs`, `src/RemoteOps.Desktop/Views/CredentialDialog.xaml(.cs)`, `src/RemoteOps.Desktop/Views/KeychainView.xaml(.cs)`; Test `tests/RemoteOps.UnitTests/Desktop/ViewModels/CredentialDialogKeyTests.cs`.

**Produces:** `CredentialDialogViewModel.IsKeyType` (bool, default false), `ShowTypePicker`, `ShowPrivateKey`; `CredentialDialog.GetPrivateKey()→char[]`, `GetPassphrase()→char[]`, `PrivateKeyText` set no code-behind.

- [ ] **Step 1: Teste (VM)** — `CredentialDialogKeyTests.cs`:
```csharp
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class CredentialDialogKeyTests
{
    [Fact]
    public void Add_DefaultsToPassword_TypePickerVisible()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.Add);
        Assert.False(vm.IsKeyType);
        Assert.True(vm.ShowTypePicker);
        Assert.True(vm.ShowPassword);
        Assert.False(vm.ShowPrivateKey);
    }

    [Fact]
    public void SwitchToKey_TogglesPanels()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.Add) { IsKeyType = true };
        Assert.True(vm.ShowPrivateKey);
        Assert.False(vm.ShowPassword);
    }

    [Fact]
    public void ChangePasswordMode_HidesTypePicker()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.ChangePassword);
        Assert.False(vm.ShowTypePicker);
    }
}
```
- [ ] **Step 2: Rodar → falha.**
- [ ] **Step 3: `CredentialDialogViewModel.cs`** — adicionar:
```csharp
    private bool _isKeyType;

    public bool IsKeyType
    {
        get => _isKeyType;
        set { Set(ref _isKeyType, value); RaisePropertyChanged(nameof(ShowPassword)); RaisePropertyChanged(nameof(ShowPrivateKey)); }
    }

    /// <summary>Só o modo Add oferece escolher o tipo (Edit/ChangePassword operam sobre um existente).</summary>
    public bool ShowTypePicker => Mode == CredentialDialogMode.Add;
    public bool ShowPrivateKey => Mode == CredentialDialogMode.Add && IsKeyType;
```
Trocar `ShowPassword` para: `public bool ShowPassword => Mode != CredentialDialogMode.Edit && !(Mode == CredentialDialogMode.Add && IsKeyType);`
- [ ] **Step 4: Rodar → passa** (VM).
- [ ] **Step 5: `CredentialDialog.xaml`** — largura 460; adicionar, antes do painel Password, um seletor de tipo e o painel de chave:
```xml
        <StackPanel Visibility="{Binding ShowTypePicker, Converter={StaticResource BoolToVis}}" Margin="0,0,0,10">
            <TextBlock Text="Type" Foreground="{DynamicResource Brush.Text.Secondary}" Margin="0,0,0,2"/>
            <ComboBox x:Name="TypePicker" SelectedIndex="0" SelectionChanged="TypePicker_SelectionChanged">
                <ComboBoxItem Content="Password"/>
                <ComboBoxItem Content="SSH key"/>
            </ComboBox>
        </StackPanel>

        <StackPanel Visibility="{Binding ShowPrivateKey, Converter={StaticResource BoolToVis}}">
            <TextBlock Text="Private key (OpenSSH/PEM)" Foreground="{DynamicResource Brush.Text.Secondary}" Margin="0,0,0,2"/>
            <TextBox x:Name="PrivateKeyField" AcceptsReturn="True" TextWrapping="NoWrap" FontFamily="Consolas"
                     MinHeight="90" MaxHeight="150" VerticalScrollBarVisibility="Auto" Margin="0,0,0,4"/>
            <Button Content="Browse…" Click="BrowseKey_Click" HorizontalAlignment="Left" Padding="10,3" Margin="0,0,0,10"/>
            <TextBlock Text="Passphrase (optional)" Foreground="{DynamicResource Brush.Text.Secondary}" Margin="0,0,0,2"/>
            <PasswordBox x:Name="PassphraseField" Height="28"
                         Background="{DynamicResource Brush.Bg.Surface}" Foreground="{DynamicResource Brush.Text.Primary}"
                         BorderBrush="{DynamicResource Brush.Border.Default}" Margin="0,0,0,10"/>
        </StackPanel>
```
- [ ] **Step 6: `CredentialDialog.xaml.cs`** — handlers + getters:
```csharp
    private void TypePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is CredentialDialogViewModel vm && sender is System.Windows.Controls.ComboBox cb)
            vm.IsKeyType = cb.SelectedIndex == 1;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar chave privada",
            Filter = "Chaves (*.pem;*.key;*.openssh)|*.pem;*.key;*.openssh|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true)
            PrivateKeyField.Text = System.IO.File.ReadAllText(dlg.FileName);
    }

    /// <summary>Chave como char[] (o chamador zera). Nunca prop bindável de string.</summary>
    public char[] GetPrivateKey() => PrivateKeyField.Text.ToCharArray();

    public char[] GetPassphrase()
    {
        var secure = PassphraseField.SecurePassword;
        var chars = new char[secure.Length];
        nint bstr = Marshal.SecureStringToBSTR(secure);
        try { Marshal.Copy(bstr, chars, 0, chars.Length); }
        finally { Marshal.ZeroFreeBSTR(bstr); }
        return chars;
    }

    /// <summary>Kind da chave digitada (validação antes de salvar).</summary>
    public RemoteOps.Desktop.Infrastructure.PrivateKeyKind PrivateKeyKind()
        => RemoteOps.Desktop.Infrastructure.PrivateKeyInput.Classify(PrivateKeyField.Text);
```
- [ ] **Step 7: `KeychainView.xaml(.cs)`** — no `Add_Click`, ramificar por tipo (validando a chave):
```csharp
    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var dvm = new CredentialDialogViewModel(CredentialDialogMode.Add);
        var dlg = new CredentialDialog(dvm) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        if (dvm.IsKeyType)
        {
            switch (dlg.PrivateKeyKind())
            {
                case Infrastructure.PrivateKeyKind.PuttyPpk:
                    MessageBox.Show(Window.GetWindow(this),
                        "Chave no formato PuTTY (.ppk). Converta no PuTTYgen: Conversions → Export OpenSSH key, e importe o arquivo gerado.",
                        "Chave SSH", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                case Infrastructure.PrivateKeyKind.Invalid:
                    MessageBox.Show(Window.GetWindow(this),
                        "Cole ou importe uma chave privada OpenSSH/PEM (começa com -----BEGIN …).",
                        "Chave SSH", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
            }
            var key = dlg.GetPrivateKey();
            var pp = dlg.GetPassphrase();
            await Vm.CreateKeyAsync(dvm.Name, dvm.Username, key, pp.Length > 0 ? pp : null);
            if (pp.Length == 0) System.Array.Clear(pp);
        }
        else
        {
            await Vm.CreateAsync(dvm.Name, dvm.Username, dlg.GetPassword());
        }
    }
```
Adicionar botões **Replace key**/**Change passphrase** na toolbar (Click handlers abrindo `CredentialDialog` em modo dedicado — reusar ChangePassword-like; para MVP, um diálogo mínimo que capture só a chave/passphrase). Habilitar por `SelectedCredential?.Type`.
> Nota: os botões Replace key/Change passphrase podem reusar um `CredentialDialogMode.ChangePassword`-like; para o MVP, adicionar `Replace_Click` que abre o diálogo com `IsKeyType=true` forçado (só painel de chave) → `Vm.ReplaceKeyAsync`; `ChangePassphrase_Click` → PasswordBox → `Vm.ChangePassphraseAsync`. Manter simples; sem novos modos de enum se possível.
- [ ] **Step 8: Build 0/0.** Smoke opcional.
- [ ] **Step 9: `dotnet format` + commit** — `feat(ssh): CredentialDialog com tipo, chave (arquivo+colar) e passphrase`.

---

### Task 9: `HostEditor` — ComboBox "Compat. SSH"

**Files:** Modify `src/RemoteOps.Desktop/ViewModels/HostEditorViewModel.cs`, `src/RemoteOps.Desktop/Views/HostEditorDialog.xaml`; Test `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostEditorSshProfileTests.cs`.

**Produces:** `HostEditorViewModel.NewEndpointSshProfile` (string, default "auto"); ao adicionar endpoint ssh com perfil ≠ auto → `Endpoint.Profile.SshAlgorithmProfile`.

- [ ] **Step 1: Teste**
```csharp
using System.Linq;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorSshProfileTests
{
    private static HostEditorViewModel Vm() => new(new InMemoryLocalStore(), "ws-local", existing: null, groupId: null);

    [Fact]
    public void AddSshEndpoint_WithStrict_SetsProfile()
    {
        var vm = Vm();
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointSshProfile = "strict";
        vm.AddEndpointCommand.Execute(null);
        Assert.Equal("strict", vm.Endpoints.Single().Profile!.SshAlgorithmProfile);
    }

    [Fact]
    public void AddSshEndpoint_Auto_LeavesProfileNull()
    {
        var vm = Vm();
        vm.NewEndpointProtocol = "ssh";
        vm.NewEndpointAddress = "10.0.0.1";
        vm.NewEndpointSshProfile = "auto";
        vm.AddEndpointCommand.Execute(null);
        Assert.Null(vm.Endpoints.Single().Profile);
    }
}
```
- [ ] **Step 2: Rodar → falha.**
- [ ] **Step 3: Implementar** — em `HostEditorViewModel.cs`: campo `private string _newEndpointSshProfile = "auto";` + prop `public string NewEndpointSshProfile { get => _newEndpointSshProfile; set => Set(ref _newEndpointSshProfile, value); }`. No `AddEndpoint()`, ao montar o `Endpoint`, adicionar:
```csharp
            Profile = (NewEndpointProtocol == "ssh" && _newEndpointSshProfile == "strict")
                ? new EndpointProfile { SshAlgorithmProfile = "strict" }
                : null,
```
(adicionar `using RemoteOps.Contracts.Assets;` se faltar). Resetar `NewEndpointSshProfile = "auto";` no fim do AddEndpoint.
- [ ] **Step 4: Rodar → passa.**
- [ ] **Step 5: XAML** — em `HostEditorDialog.xaml`, adicionar uma coluna "Segurança SSH" na grid de adicionar endpoint (janela → 800px), ComboBox ligado a `NewEndpointSshProfile`. Build 0/0.
> Concretamente: adicionar `<ColumnDefinition Width="120"/>`, no header row `<TextBlock ... Text="Segurança SSH"/>`, e o ComboBox (ajustar `Grid.Column` dos elementos seguintes — credencial e botão — para as colunas à direita):
```xml
            <ComboBox Grid.Row="1" Grid.Column="4"
                      SelectedValuePath="Content"
                      SelectedValue="{Binding NewEndpointSshProfile, Mode=TwoWay}"
                      ToolTip="Automático: conecta em qualquer equipamento (inclusive antigo). Estrito: exige algoritmos modernos (hardening; use em hosts novos)."
                      Margin="0,0,8,0" VerticalAlignment="Center">
                <ComboBoxItem Content="auto"/>
                <ComboBoxItem Content="strict"/>
            </ComboBox>
```
- [ ] **Step 6: `dotnet format` + commit** — `feat(ssh): perfil de compatibilidade SSH por endpoint no editor de host`.

---

### Task 10: Validação final + release v1.2.0

**Files:** `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj` (`<Version>1.2.0</Version>`), `src/RemoteOps.Desktop/Resources/operator-changelog.json`, `CHANGELOG.md`.

- [ ] **Step 1: Suíte completa Release** — `dotnet test "…\RemoteOps.sln" -c Release --nologo` → 0/0 verde (489 + novos).
- [ ] **Step 2: Format gate** — `dotnet format --verify-no-changes` exit 0.
- [ ] **Step 3: Bump versão** — csproj `1.1.2` → `1.2.0`; entrada **1.2.0** em `operator-changelog.json` (destaques: "Conecte por chave SSH (OpenSSH/PEM) com passphrase — importe o arquivo ou cole" · "Segurança SSH por host: Automático conecta em qualquer equipamento; Estrito exige algoritmos modernos"); seção `## [1.2.0]` no `CHANGELOG.md`.
- [ ] **Step 4: Commit + push + PR** (base main). Após CI verde: **merge (autorizado) + tag v1.2.0** (autorização standing) → release.yml publica.
- [ ] **Step 5: Smoke manual** — criar credencial de chave (colar PEM + passphrase); anexar a um host; endpoint ssh com perfil Compatível; conectar num MikroTik antigo.

---

## Self-Review

**Cobertura da spec:** §U1 credencial chave → Tasks 1,2,7,8 · §U2 perfis → Task 4 · §U3 provider → Tasks 5,6 · §HostEditor perfil → Task 9 · §validação PEM/PPK → Task 3 · §release v1.2.0 → Task 10 · §segurança (char[]/Array.Clear/2 envelopes) → Tasks 5,6,7,8.

**Placeholders:** `SshAlgorithmPolicy.Apply(strict)` faz `.Remove()` real dos fracos (verificado que 2024.2.0 os traz por default) — sem no-op fake. Os botões Replace key/Change passphrase da Task 8 têm caminho concreto (reusar diálogo) sem enum novo. Nenhum "TODO/TBD".

**Consistência de tipos:** `CredentialTypes.PrivateKey/Password/PrivateKeyPassphrase` (T1) usados em T5,6,7 · `SshConnectionOptions` (T5) consumido em T6 · `CredentialMetadata.PassphraseEnvelopeId` (T2) em T6,7 · `EndpointProfile.SshAlgorithmProfile` valores `"auto"`/`"strict"` (T2) em T6,9 · `PrivateKeyInput.Classify`/`PrivateKeyKind` (T3) em T8 · `SshAlgorithmPolicy.Auto/Strict` (T4) em T9 (e o provider passa o valor cru do endpoint) · `KeychainViewModel.CreateKeyAsync/ReplaceKeyAsync/ChangePassphraseAsync` (T7) em T8.
