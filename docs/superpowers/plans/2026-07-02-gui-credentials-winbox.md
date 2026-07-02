# Config no GUI — Keychain CRUD + anexar ao host + WinBox — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permitir, pela GUI, criar/gerenciar credenciais login+senha (no vault cifrado), anexá-las a um host, e apontar o executável do WinBox (o app calcula/fixa o SHA-256).

**Architecture:** Estender os VMs/views existentes: `KeychainViewModel` ganha CRUD via `ICredentialVault` (já no DI) + `CredentialRef` no `ILocalStore` (novo `UpdateCredentialRefAsync`); `HostEditor` ganha seletor de credencial; `Settings` ganha aba "Ferramentas externas" com o WinBox; `BuildWinBoxManifest` passa a ler do `AppSettings`.

**Tech Stack:** .NET 10, WPF (net10.0-windows), Microsoft.Extensions.DependencyInjection, xUnit 2.9.3.

**Worktree:** `C:\dev\remoteops-cred-winbox` (branch `feature/gui-credentials-winbox`, a partir de `origin/main` `5384728`). Caminhos relativos a essa base.

**Spec:** `docs/superpowers/specs/2026-07-02-gui-credentials-winbox-design.md`

## Global Constraints

- `TreatWarningsAsErrors=true` — build 0/0. `Nullable=enable`, `ImplicitUsings=enable`.
- Testes xUnit (`[Fact]`/`Assert`), namespace espelha a pasta (`RemoteOps.UnitTests.Desktop.*`); a suíte existente (**425**) deve ficar verde.
- **Rótulos do Keychain em inglês** (Keychain, Credentials, Username, Password, Add credential, Change password, Rotate, Delete); resto do app em pt-BR.
- Segredos NUNCA na UI: senha entra por `PasswordBox`, é passada como `char[]`/`ReadOnlyMemory<char>` ao vault e nunca exibida depois.
- WorkspaceId local fixo: `"ws-local"`. Actor de auditoria: `"local-user"`.
- Build: `dotnet build "C:\dev\remoteops-cred-winbox\RemoteOps.sln" -c Debug --nologo`. Test: `dotnet test "C:\dev\remoteops-cred-winbox\RemoteOps.sln" -c Debug --nologo`.

## Interfaces existentes reaproveitadas

- `ICredentialVault`/`CredentialVault` (DI singleton): `Task<SecretEnvelope> StoreAsync(VaultStoreRequest, ReadOnlyMemory<char> secret, ct)`; `Task<SecretEnvelope> RotateAsync(string envelopeId, ReadOnlyMemory<char> newSecret, VaultAccessContext, ct)`; `Task RevokeAsync(string envelopeId, VaultAccessContext, ct)`.
- `VaultStoreRequest { string WorkspaceId; string CredentialId; string Type="secret"; string ActorUserId; string? DeviceId }`. `VaultAccessContext { string ActorUserId; string? DeviceId }`.
- `SecretEnvelope` — a propriedade do id é **`EnvelopeId`** (não `Id`).
- `ILocalStore`: `AddCredentialRefAsync(CredentialRef)`, `GetCredentialRefsAsync(ws)`, `GetCredentialRefAsync(id)`, `DeleteCredentialRefAsync(id)`.
- `CredentialRef { string Id; string Name; string Type; string? Scope; CredentialMetadata? Metadata; string? SecretEnvelopeId; int Version }`. `CredentialMetadata { string? Username; bool HasPrivateKey; DateTimeOffset? LastRotatedAt }`.
- `AppSettings { Dictionary<string,bool> Flags; string Theme }` (record). `ISettingsStore.Load()/Save(AppSettings)`.
- `AppCompositionRoot.BuildWinBoxManifest(IServiceProvider)` — hoje lê `WINBOX_EXE_PATH`/`WINBOX_SHA256` (env) + default `C:\Tools\WinBox\winbox64.exe`; produz `WinBoxToolManifest { Tool, Version, File, Sha256, ExecutablePath }`.
- `HostEditorViewModel` (endpoints com `CredentialRefId`), `KeychainViewModel(ILocalStore, string workspaceId)`, `SettingsViewModel(ISettingsStore, IUpdateService?)`, `RelayCommand`, `BaseViewModel`.

---

### Task 1: `HashUtil.Sha256File`

**Files:**
- Create: `src/RemoteOps.Desktop/Infrastructure/HashUtil.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/HashUtilTests.cs`

**Interfaces:** Produces `HashUtil.Sha256File(string path) -> string` (hex minúsculo).

- [ ] **Step 1: Teste que falha**
```csharp
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class HashUtilTests
{
    [Fact]
    public void Sha256File_KnownContent_ReturnsKnownHash()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(path, "abc");
        // SHA-256("abc") conhecido
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", HashUtil.Sha256File(path));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test "C:\dev\remoteops-cred-winbox\RemoteOps.sln" --filter "FullyQualifiedName~HashUtilTests" --nologo` → FALHA de compilação.

- [ ] **Step 3: Implementar**
```csharp
using System;
using System.IO;
using System.Security.Cryptography;

namespace RemoteOps.Desktop.Infrastructure;

public static class HashUtil
{
    public static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): HashUtil.Sha256File`.

---

### Task 2: `AppSettings` — campos do WinBox

**Files:**
- Modify: `src/RemoteOps.Desktop/Infrastructure/AppSettings.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/AppSettingsWinBoxTests.cs`

**Interfaces:** Produces `AppSettings.WinBoxExePath` (string?), `AppSettings.WinBoxSha256` (string?).

- [ ] **Step 1: Teste que falha** — round-trip via `JsonSettingsStore`:
```csharp
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class AppSettingsWinBoxTests
{
    [Fact]
    public void WinBoxFields_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { WinBoxExePath = @"C:\wb\winbox64.exe", WinBoxSha256 = "abcd" });
        AppSettings loaded = store.Load();
        Assert.Equal(@"C:\wb\winbox64.exe", loaded.WinBoxExePath);
        Assert.Equal("abcd", loaded.WinBoxSha256);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** — em `AppSettings.cs`, adicionar ao record:
```csharp
    /// <summary>Caminho do executável do WinBox configurado pela GUI (Configurações → Ferramentas externas).</summary>
    public string? WinBoxExePath { get; init; }

    /// <summary>SHA-256 fixado do executável do WinBox (validação fail-closed no launch).</summary>
    public string? WinBoxSha256 { get; init; }
```
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): AppSettings ganha WinBoxExePath/WinBoxSha256`.

---

### Task 3: `ILocalStore.UpdateCredentialRefAsync`

**Files:**
- Modify: `src/RemoteOps.Desktop/Infrastructure/ILocalStore.cs`, `InMemoryLocalStore.cs`, `SqlCipherLocalStore.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/InMemoryLocalStoreCredentialTests.cs`

**Interfaces:** Produces `Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef, CancellationToken ct = default)`.

- [ ] **Step 1: Teste que falha**
```csharp
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class InMemoryLocalStoreCredentialTests
{
    [Fact]
    public async Task UpdateCredentialRef_ChangesNameKeepsId()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef { Id = "c1", Name = "old", Type = "password", SecretEnvelopeId = "e1" });
        await store.UpdateCredentialRefAsync(new CredentialRef { Id = "c1", Name = "new", Type = "password", SecretEnvelopeId = "e1" });
        CredentialRef? got = await store.GetCredentialRefAsync("c1");
        Assert.Equal("new", got!.Name);
        Assert.Equal("e1", got.SecretEnvelopeId);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar.** Em `ILocalStore.cs`, ao lado de `AddCredentialRefAsync`:
```csharp
    Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default);
```
Em `InMemoryLocalStore.cs`, ao lado de `AddCredentialRefAsync`:
```csharp
    public Task<CredentialRef> UpdateCredentialRefAsync(CredentialRef credentialRef, CancellationToken ct = default)
    {
        _credentialRefs[credentialRef.Id] = credentialRef;
        return Task.FromResult(credentialRef);
    }
```
Em `SqlCipherLocalStore.cs`: implementar espelhando **exatamente** o padrão de `AddCredentialRefAsync` já existente nesse arquivo (upsert da linha de credential ref por Id) — ler o método `AddCredentialRefAsync` de `SqlCipherLocalStore.cs` e seguir a mesma abordagem (mesma tabela/colunas), garantindo update por Id. Build deve compilar 0/0.

- [ ] **Step 4: Rodar e ver passar** + `dotnet build` 0/0 (confirma que SqlCipher implementa a interface).
- [ ] **Step 5: Commit** — `feat(gui): ILocalStore.UpdateCredentialRefAsync (InMemory + SqlCipher)`.

---

### Task 4: `KeychainViewModel` — CRUD via vault

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/KeychainViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/KeychainViewModelCrudTests.cs`

**Interfaces:**
- Consumes: `ICredentialVault`, `ILocalStore`, `VaultStoreRequest`, `VaultAccessContext`, `SecretEnvelope`, `CredentialRef`, `CredentialMetadata`.
- Produces: `KeychainViewModel(ILocalStore store, ICredentialVault vault, string workspaceId)`; `Task CreateAsync(string name, string username, char[] password)`; `Task ChangePasswordAsync(CredentialRef cred, char[] newPassword)`; `Task UpdateAsync(CredentialRef cred, string name, string username)`; `Task DeleteAsync(CredentialRef cred)`; `ObservableCollection<CredentialRef> Credentials`; `Task LoadAsync()`; `AssetViewModel`-style `SelectedCredential`.

- [ ] **Step 1: Teste que falha** — `KeychainViewModelCrudTests.cs`:
```csharp
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Security.Vault;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class KeychainViewModelCrudTests
{
    private sealed class FakeVault : ICredentialVault
    {
        public string? LastStoredSecret; public string? LastRotatedId; public string? LastRevokedId;
        public Task<SecretEnvelope> StoreAsync(VaultStoreRequest r, System.ReadOnlyMemory<char> secret, CancellationToken ct = default)
        {
            LastStoredSecret = new string(secret.Span);
            return Task.FromResult(new SecretEnvelope { EnvelopeId = "env-" + r.CredentialId, WorkspaceId = r.WorkspaceId, CredentialId = r.CredentialId, Type = r.Type, Version = 1, Algorithm = "test", WrappedCek = [], CekNonce = [], CekTag = [] });
        }
        public Task<SecretEnvelope> RotateAsync(string envelopeId, System.ReadOnlyMemory<char> s, VaultAccessContext c, CancellationToken ct = default) { LastRotatedId = envelopeId; return Task.FromResult(new SecretEnvelope { EnvelopeId = envelopeId, WorkspaceId = "ws-local", CredentialId = "c", Type = "password", Version = 2, Algorithm = "test", WrappedCek = [], CekNonce = [], CekTag = [] }); }
        public Task RevokeAsync(string envelopeId, VaultAccessContext c, CancellationToken ct = default) { LastRevokedId = envelopeId; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Create_StoresSecretInVault_AndAddsRef()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var vm = new KeychainViewModel(store, vault, "ws-local");
        await vm.CreateAsync("root@r1", "root", "s3cr3t".ToCharArray());
        var refs = await store.GetCredentialRefsAsync("ws-local");
        var cred = refs.Single();
        Assert.Equal("root@r1", cred.Name);
        Assert.Equal("root", cred.Metadata!.Username);
        Assert.StartsWith("env-", cred.SecretEnvelopeId);
        Assert.Equal("s3cr3t", vault.LastStoredSecret);
    }

    [Fact]
    public async Task Delete_RevokesEnvelope_AndRemovesRef()
    {
        var store = new InMemoryLocalStore();
        var vault = new FakeVault();
        var vm = new KeychainViewModel(store, vault, "ws-local");
        await vm.CreateAsync("c", "u", "p".ToCharArray());
        var cred = (await store.GetCredentialRefsAsync("ws-local")).Single();
        await vm.DeleteAsync(cred);
        Assert.Empty(await store.GetCredentialRefsAsync("ws-local"));
        Assert.Equal(cred.SecretEnvelopeId, vault.LastRevokedId);
    }
}
```
> Confirmar que a interface `ICredentialVault` declara `StoreAsync/RotateAsync/RevokeAsync` (senão o `FakeVault` implementa a interface real — ajustar o fake às assinaturas reais lidas de `ICredentialVault.cs`).

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar `KeychainViewModel.cs`** (substituir por):
```csharp
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Security.Vault;

namespace RemoteOps.Desktop.ViewModels;

public sealed class KeychainViewModel : BaseViewModel
{
    private const string Actor = "local-user";
    private readonly ILocalStore _store;
    private readonly ICredentialVault _vault;
    private readonly string _workspaceId;
    private CredentialRef? _selected;

    public KeychainViewModel(ILocalStore store, ICredentialVault vault, string workspaceId)
    {
        _store = store;
        _vault = vault;
        _workspaceId = workspaceId;
    }

    public ObservableCollection<CredentialRef> Credentials { get; } = [];

    public CredentialRef? SelectedCredential
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public async Task LoadAsync()
    {
        Credentials.Clear();
        foreach (var c in await _store.GetCredentialRefsAsync(_workspaceId))
            Credentials.Add(c);
    }

    public async Task CreateAsync(string name, string username, char[] password)
    {
        string credId = Guid.NewGuid().ToString("n");
        SecretEnvelope env = await _vault.StoreAsync(
            new VaultStoreRequest { WorkspaceId = _workspaceId, CredentialId = credId, Type = "password", ActorUserId = Actor },
            password);
        Array.Clear(password);
        await _store.AddCredentialRefAsync(new CredentialRef
        {
            Id = credId,
            Name = name.Trim(),
            Type = "password",
            Metadata = new CredentialMetadata { Username = username.Trim() },
            SecretEnvelopeId = env.EnvelopeId,
        });
        await LoadAsync();
    }

    public async Task UpdateAsync(CredentialRef cred, string name, string username)
    {
        await _store.UpdateCredentialRefAsync(cred with
        {
            Name = name.Trim(),
            Metadata = new CredentialMetadata { Username = username.Trim(), HasPrivateKey = cred.Metadata?.HasPrivateKey ?? false },
        });
        await LoadAsync();
    }

    public async Task ChangePasswordAsync(CredentialRef cred, char[] newPassword)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RotateAsync(envId, newPassword, new VaultAccessContext { ActorUserId = Actor });
        Array.Clear(newPassword);
    }

    public async Task DeleteAsync(CredentialRef cred)
    {
        if (cred.SecretEnvelopeId is { } envId)
            await _vault.RevokeAsync(envId, new VaultAccessContext { ActorUserId = Actor });
        await _store.DeleteCredentialRefAsync(cred.Id);
        await LoadAsync();
    }
}
```
> `CredentialRef` é record (`with` funciona). Se `ICredentialVault` não expuser algum dos 3 métodos (só o `CredentialVault` concreto), injetar `CredentialVault` no lugar de `ICredentialVault` (ambos no DI) — verificar contra `ICredentialVault.cs` no Step 1.

- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: Commit** — `feat(gui): KeychainViewModel CRUD (create/update/rotate/delete via vault)`.

---

### Task 5: `CredentialDialog` (form Name/Username/Password)

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/CredentialDialogViewModel.cs`, `src/RemoteOps.Desktop/Views/CredentialDialog.xaml(.cs)`

**Interfaces:** Produces `CredentialDialogViewModel { string Name; string Username; bool IsEdit; RelayCommand SaveCommand; event Saved }`; `CredentialDialog(CredentialDialogViewModel vm)` — expõe a senha digitada via `char[] GetPassword()` do code-behind (PasswordBox).

- [ ] **Step 1: `CredentialDialogViewModel.cs`:**
```csharp
using System;

namespace RemoteOps.Desktop.ViewModels;

public sealed class CredentialDialogViewModel : BaseViewModel
{
    private string _name = string.Empty;
    private string _username = string.Empty;

    public CredentialDialogViewModel(bool isEdit, string name = "", string username = "")
    {
        IsEdit = isEdit; _name = name; _username = username;
        SaveCommand = new RelayCommand(() => Saved?.Invoke(this, EventArgs.Empty), () => !string.IsNullOrWhiteSpace(Name));
    }

    public bool IsEdit { get; }
    public string Title => IsEdit ? "Edit credential" : "Add credential";
    public string Name { get => _name; set { Set(ref _name, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string Username { get => _username; set => Set(ref _username, value); }
    public RelayCommand SaveCommand { get; }
    public event EventHandler? Saved;
}
```

- [ ] **Step 2: `CredentialDialog.xaml`** — Window modal tematizada (tokens `Brush.*`/`Text.*`), campos: `Name` (TextBox → `Name`), `Username` (TextBox → `Username`), **`Password`** (`PasswordBox x:Name="PasswordField"`), botões Save (`SaveCommand`)/Cancel (`IsCancel=True`). Labels em inglês.
- [ ] **Step 3: `CredentialDialog.xaml.cs`:**
```csharp
using System.Windows;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class CredentialDialog : Window
{
    public CredentialDialog(CredentialDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Saved += (_, _) => { DialogResult = true; Close(); };
    }

    public char[] GetPassword()
    {
        var secure = PasswordField.SecurePassword;
        // Converte para char[] só no ato de salvar; o chamador zera após uso.
        var chars = new char[secure.Length];
        var bstr = System.Runtime.InteropServices.Marshal.SecureStringToBSTR(secure);
        try { System.Runtime.InteropServices.Marshal.Copy(bstr, chars, 0, chars.Length); }
        finally { System.Runtime.InteropServices.Marshal.ZeroFreeBSTR(bstr); }
        return chars;
    }
}
```
- [ ] **Step 4: Build** 0/0.
- [ ] **Step 5: Commit** — `feat(gui): CredentialDialog (Name/Username/Password)`.

---

### Task 6: `KeychainView` — botões CRUD (inglês) + fiação

**Files:**
- Modify: `src/RemoteOps.Desktop/Views/KeychainView.xaml(.cs)`

- [ ] **Step 1: `KeychainView.xaml`** — acima/junto da lista de `Credentials`, um botão **"Add credential"**; a lista (DataGrid/ItemsControl) ganha, por item, **Edit / Change password / Delete**. `SelectedCredential` TwoWay. Manter o banner read-only removido (agora é editável). Labels em inglês.
- [ ] **Step 2: `KeychainView.xaml.cs`** — handlers que abrem o `CredentialDialog` e chamam o VM:
  - `Add`: `var vm = new CredentialDialogViewModel(false); var dlg = new CredentialDialog(vm){Owner=Window.GetWindow(this)}; if (dlg.ShowDialog()==true) await Keychain.CreateAsync(vm.Name, vm.Username, dlg.GetPassword());` (onde `Keychain` = `(KeychainViewModel)DataContext`).
  - `Edit`: abre `CredentialDialogViewModel(true, cred.Name, cred.Metadata?.Username ?? "")`; no Save → `await Keychain.UpdateAsync(cred, vm.Name, vm.Username)`.
  - `Change password`: abre um diálogo só com PasswordBox (reusar `CredentialDialog` ou um mínimo) → `await Keychain.ChangePasswordAsync(cred, pwd)`.
  - `Delete`: confirmação (`MessageBox`) → `await Keychain.DeleteAsync(cred)`.
- [ ] **Step 3: Build** 0/0. Smoke: aba Keychain mostra "Add credential"; criar/editar/excluir funciona.
- [ ] **Step 4: Commit** — `feat(gui): KeychainView com CRUD (labels em ingles)`.

---

### Task 7: `HostEditorViewModel` — seletor de credencial + `HostEditorDialog`

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/HostEditorViewModel.cs`, `src/RemoteOps.Desktop/Views/HostEditorDialog.xaml`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostEditorCredentialTests.cs`

**Interfaces:** Produces `HostEditorViewModel.AvailableCredentials` (`ObservableCollection<CredentialRef>`), `HostEditorViewModel.NewEndpointCredentialId` (string?); ao adicionar endpoint, seta `Endpoint.CredentialRefId`. Ctor passa a receber a lista (via `store.GetCredentialRefsAsync`).

- [ ] **Step 1: Teste que falha**
```csharp
using System.Linq;
using System.Threading.Tasks;
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostEditorCredentialTests
{
    [Fact]
    public async Task AddEndpoint_WithCredential_SetsCredentialRefId()
    {
        var store = new InMemoryLocalStore();
        await store.AddCredentialRefAsync(new CredentialRef { Id = "c1", Name = "root", Type = "password", SecretEnvelopeId = "e1" });
        var vm = new HostEditorViewModel(store, "ws-local", existing: null, groupId: null);
        await vm.LoadCredentialsAsync();
        vm.NewEndpointProtocol = "ssh"; vm.NewEndpointAddress = "10.0.0.1"; vm.NewEndpointPort = 22;
        vm.NewEndpointCredentialId = "c1";
        vm.AddEndpointCommand.Execute(null);
        Assert.Equal("c1", vm.Endpoints.Single().CredentialRefId);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** — em `HostEditorViewModel.cs`: adicionar `public ObservableCollection<CredentialRef> AvailableCredentials { get; } = [];`, `public string? NewEndpointCredentialId { get; set; }`, `public async Task LoadCredentialsAsync() { AvailableCredentials.Clear(); foreach (var c in await _store.GetCredentialRefsAsync(_workspaceId)) AvailableCredentials.Add(c); }`; no método `AddEndpoint`, setar `CredentialRefId = NewEndpointCredentialId` no `Endpoint` criado (e limpar `NewEndpointCredentialId` depois). Chamar `LoadCredentialsAsync()` no ctor ou quando o diálogo abre.
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: `HostEditorDialog.xaml`** — na linha "adicionar endpoint", um `ComboBox` de credencial (`ItemsSource=AvailableCredentials`, `DisplayMemberPath=Name`, `SelectedValuePath=Id`, `SelectedValue=NewEndpointCredentialId`) + botão **+ New credential** (abre `CredentialDialog`, cria via um `KeychainViewModel` ou um callback, recarrega e seleciona). Build 0/0.
- [ ] **Step 6: Commit** — `feat(gui): seletor de credencial no editor de host`.

---

### Task 8: `SettingsViewModel` + `SettingsWindow` — aba "Ferramentas externas" (WinBox)

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/SettingsViewModel.cs`, `src/RemoteOps.Desktop/Views/SettingsWindow.xaml(.cs)`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/SettingsViewModelWinBoxTests.cs`

**Interfaces:** Produces `SettingsViewModel.WinBoxExePath` (string?), `SettingsViewModel.WinBoxSha256` (string?), `SetWinBox(string path, string sha256)`; `Save()` persiste os dois no `AppSettings`.

- [ ] **Step 1: Teste que falha**
```csharp
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelWinBoxTests
{
    [Fact]
    public void Save_PersistsWinBoxPathAndHash()
    {
        string p = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(p);
        var vm = new SettingsViewModel(store);
        vm.SetWinBox(@"C:\wb\winbox64.exe", "deadbeef");
        vm.SaveCommand.Execute(null);
        AppSettings loaded = store.Load();
        Assert.Equal(@"C:\wb\winbox64.exe", loaded.WinBoxExePath);
        Assert.Equal("deadbeef", loaded.WinBoxSha256);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar** — em `SettingsViewModel.cs`: carregar `_winBoxExePath = _settings.WinBoxExePath; _winBoxSha256 = _settings.WinBoxSha256;` no ctor; props `WinBoxExePath`/`WinBoxSha256` (com `Set`); `public void SetWinBox(string path, string sha256) { WinBoxExePath = path; WinBoxSha256 = sha256; }`; no `Save()`, incluir no `_settings with { ..., WinBoxExePath = WinBoxExePath, WinBoxSha256 = WinBoxSha256 }` antes do `_store.Save`.
- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: `SettingsWindow.xaml`** — nova `TabItem` **"Ferramentas externas"** (pt-BR): label do caminho (`WinBoxExePath`), botão **Procurar…** (`Click="BrowseWinBox_Click"`), o hash fixado (curto) e um aviso; botão **Re-fixar hash** (`Click="RepinWinBox_Click"`). `SettingsWindow.xaml.cs`: `BrowseWinBox_Click` abre `OpenFileDialog{Filter="WinBox|*.exe"}` → `string sha = HashUtil.Sha256File(dlg.FileName); ((SettingsViewModel)DataContext).SetWinBox(dlg.FileName, sha);`. `RepinWinBox_Click`: recomputa `HashUtil.Sha256File(vm.WinBoxExePath)` se houver caminho. Build 0/0.
- [ ] **Step 6: Commit** — `feat(gui): aba Ferramentas externas (WinBox) nas Configuracoes`.

---

### Task 9: `BuildWinBoxManifest` lê settings + DI (Keychain com vault)

**Files:**
- Modify: `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs`
- Create: `src/RemoteOps.Desktop/Integration/WinBoxManifestResolver.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Integration/WinBoxManifestResolverTests.cs`

**Interfaces:** Produces `WinBoxManifestResolver.Resolve(string? settingsPath, string? settingsHash, string? envPath, string? envHash) -> WinBoxToolManifest`.

- [ ] **Step 1: Teste que falha**
```csharp
using RemoteOps.Desktop.Integration;
using RemoteOps.MikroTik;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

public sealed class WinBoxManifestResolverTests
{
    [Fact]
    public void Settings_TakePrecedence_OverEnvAndDefault()
    {
        WinBoxToolManifest m = WinBoxManifestResolver.Resolve(@"C:\s\wb.exe", "aaa", @"C:\e\wb.exe", "bbb");
        Assert.Equal(@"C:\s\wb.exe", m.ExecutablePath);
        Assert.Equal("aaa", m.Sha256);
    }

    [Fact]
    public void Env_UsedWhenSettingsEmpty_ElseDefaultPath()
    {
        WinBoxToolManifest e = WinBoxManifestResolver.Resolve(null, null, @"C:\e\wb.exe", "bbb");
        Assert.Equal(@"C:\e\wb.exe", e.ExecutablePath);
        Assert.Equal("bbb", e.Sha256);
        WinBoxToolManifest d = WinBoxManifestResolver.Resolve(null, null, null, null);
        Assert.Equal(@"C:\Tools\WinBox\winbox64.exe", d.ExecutablePath);
        Assert.Null(d.Sha256);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**
- [ ] **Step 3: Implementar `WinBoxManifestResolver.cs`:**
```csharp
using RemoteOps.MikroTik;

namespace RemoteOps.Desktop.Integration;

public static class WinBoxManifestResolver
{
    public static WinBoxToolManifest Resolve(string? settingsPath, string? settingsHash, string? envPath, string? envHash)
    {
        string exePath = !string.IsNullOrWhiteSpace(settingsPath) ? settingsPath
            : !string.IsNullOrWhiteSpace(envPath) ? envPath
            : @"C:\Tools\WinBox\winbox64.exe";
        string? sha = !string.IsNullOrWhiteSpace(settingsHash) ? settingsHash
            : !string.IsNullOrWhiteSpace(envHash) ? envHash
            : null;
        return new WinBoxToolManifest { Tool = "winbox", Version = "unknown", File = "winbox64.exe", Sha256 = sha, ExecutablePath = exePath };
    }
}
```
- [ ] **Step 4: Fiação em `AppCompositionRoot.cs`.** Trocar o corpo de `BuildWinBoxManifest(IServiceProvider sp)` para resolver settings + env via o resolver:
```csharp
    private static WinBoxToolManifest BuildWinBoxManifest(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<ISettingsStore>().Load();
        return WinBoxManifestResolver.Resolve(
            settings.WinBoxExePath, settings.WinBoxSha256,
            Environment.GetEnvironmentVariable("WINBOX_EXE_PATH"),
            Environment.GetEnvironmentVariable("WINBOX_SHA256"));
    }
```
E atualizar o registro do `KeychainViewModel` para receber `ICredentialVault` (ou `CredentialVault`): onde ele é construído (DI ou dentro do `BrowserViewModel`), passar o vault resolvido. Garantir `ISettingsStore` já registrado (está, da frente de update).
- [ ] **Step 5: Rodar e ver passar** + `dotnet build` 0/0.
- [ ] **Step 6: Commit** — `feat(gui): WinBox manifest le settings (path+hash) + Keychain recebe vault`.

---

### Task 10: Validação final

**Files:** nenhuma alteração de código.

- [ ] **Step 1: Suíte completa** — `dotnet test "C:\dev\remoteops-cred-winbox\RemoteOps.sln" -c Debug --nologo` → 0/0, verde (425 + novos).
- [ ] **Step 2: Smoke manual** — app abre; **Keychain → Add credential** (login+senha) salva; **editar host → anexar a credencial** ao endpoint; **Configurações → Ferramentas externas → Procurar WinBox** (hash calculado) → **abrir WinBox** pelo menu de contexto MikroTik funciona; conectar SSH usando a credencial criada.
- [ ] **Step 3: Commit final (se houver ajuste)** — `chore(gui): valida Config no GUI (Keychain + WinBox)`.

---

## Self-Review (executada na escrita)

**Cobertura da spec:** §3.1 Keychain CRUD → Tasks 4,5,6 (+3 UpdateCredentialRefAsync) · §3.2 anexar ao host → Task 7 · §3.3 WinBox settings → Tasks 1,2,8,9 · §3.4 segurança (PasswordBox/char[]/zeramento) → Tasks 4,5 · §5 testes → cada task TDD + Task 10 · componentes §4 → Tasks 1-9.

**Placeholders:** o único ponto "mirror existing" é o `SqlCipherLocalStore.UpdateCredentialRefAsync` (Task 3) — instrução concreta de espelhar o `AddCredentialRefAsync` já existente no arquivo, verificável no build. Os "confirmar contra ICredentialVault.cs" (Tasks 4) são checagens de assinatura, não TODOs.

**Consistência de tipos:** `SecretEnvelope.EnvelopeId` (não `.Id`) usado consistentemente; `KeychainViewModel(ILocalStore, ICredentialVault, string)` (Task 4) casa com a fiação (Task 9); `HashUtil.Sha256File` (Task 1) usado nas Tasks 8/9-adjacentes; `WinBoxManifestResolver.Resolve` (Task 9) assinatura única; `AppSettings.WinBoxExePath/WinBoxSha256` (Task 2) consumidos nas Tasks 8,9; `UpdateCredentialRefAsync` (Task 3) usado na Task 4.

## Execution Handoff
Ver a mensagem de handoff após salvar.
