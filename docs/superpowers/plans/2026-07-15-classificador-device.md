# Classificador de Device — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Classificar cada host por papel (roteador/switch/servidor/OLT…) auto-sugerido do Vendor/Model, com logo do vendor colado ao nome (fallback pro glifo de papel), coluna "Tipo" e chips de filtro na lista.

**Architecture:** Um classificador puro (`DeviceClassifier`) infere `(role, vendorKey, badge)` de strings; um catálogo (`DeviceCatalog`) mapeia isso pra glifo/cor/logo; um `DeviceIcon` (UserControl) renderiza logo-ou-glifo; a lista e o editor consomem os dois. Persistência ganha 1 coluna nullable. Sem rede.

**Tech Stack:** .NET 10 / WPF (net10.0-windows), MVVM (`BaseViewModel.Set`, `RelayCommand`), xUnit, SQLCipher (Microsoft.Data.Sqlite), Segoe MDL2 Assets (glifos).

## Global Constraints

- `Nullable enable`, `ImplicitUsings enable`, warnings-as-errors — código tem que compilar limpo.
- CI gates (rodar local antes de push): `dotnet build -c Release` (0 warn/err) + `dotnet format --verify-no-changes` + suíte completa verde.
- XAML novo/alterado tem que ter **teste de render STA** (bugs de runtime de XAML passam no build; screenshot não funciona nesta máquina).
- Strings de UI em **pt-BR**.
- **Logos de vendor**: uso interno; assets em `src/RemoteOps.Desktop/assets/logos/` no `.gitignore` (repo público). Fallback pro glifo garante que nada fica sem ícone. Formato **PNG**.
- Retrocompatibilidade: `DeviceRole` nullable; hosts antigos = "Sem tipo".

## File Structure

- **Novo** `src/RemoteOps.Contracts/Assets/DeviceRoles.cs` — constantes dos papéis.
- **Novo** `src/RemoteOps.Desktop/Domain/DeviceClassifier.cs` — heurística pura.
- **Novo** `src/RemoteOps.Desktop/Domain/DeviceCatalog.cs` — role→glifo/rótulo, vendorKey→cor/logo.
- **Novo** `src/RemoteOps.Desktop/Views/DeviceIcon.xaml(.cs)` — UserControl logo-ou-glifo.
- **Novo** `src/RemoteOps.Desktop/assets/logos/.gitkeep` (pasta gitignored).
- **Alterado** `Asset.cs`, `AddAssetRequest.cs`, `InMemoryLocalStore.cs`, `SqlCipherLocalStore.cs`, `HostEditorViewModel.cs`, `HostEditorDialog.xaml`, `HostsViewModel.cs`, `HostsView.xaml`, `Themes/Tokens/Icons.xaml`, `.gitignore`, `RemoteOps.Desktop.csproj`.
- **Testes** em `tests/RemoteOps.UnitTests/`: `Domain/DeviceClassifierTests.cs`, `Domain/DeviceCatalogTests.cs`, `Desktop/Views/DeviceIconRenderTests.cs`, `Desktop/ViewModels/HostEditorClassificationTests.cs`, `Desktop/ViewModels/HostsFilterTests.cs`, + round-trip nos testes de store existentes.

---

### Task 1: Modelo — `DeviceRoles` + `Asset.DeviceRole`

**Files:**
- Create: `src/RemoteOps.Contracts/Assets/DeviceRoles.cs`
- Modify: `src/RemoteOps.Contracts/Assets/Asset.cs`
- Test: `tests/RemoteOps.UnitTests/Contracts/DeviceRolesTests.cs`

**Interfaces — Produces:** `DeviceRoles.{Router,Switch,ServerLinux,ServerWindows,Olt,Firewall,LoadBalancer,Wireless,Other}` (const string), `DeviceRoles.All` (IReadOnlyList<string>); `Asset.DeviceRole` (string?).

- [ ] **Step 1: Teste** — `DeviceRolesTests.cs`:
```csharp
using RemoteOps.Contracts.Assets;
using Xunit;
namespace RemoteOps.UnitTests.Contracts;
public class DeviceRolesTests
{
    [Fact] public void All_ContainsEveryRole_AndNoDuplicates()
    {
        Assert.Equal(9, DeviceRoles.All.Count);
        Assert.Equal(DeviceRoles.All.Count, DeviceRoles.All.Distinct().Count());
        Assert.Contains(DeviceRoles.Router, DeviceRoles.All);
        Assert.Contains(DeviceRoles.Other, DeviceRoles.All);
    }
}
```
- [ ] **Step 2: Rodar** — `dotnet test --filter DeviceRolesTests` → FAIL (não compila: `DeviceRoles` não existe).
- [ ] **Step 3: Implementar** — `DeviceRoles.cs`:
```csharp
namespace RemoteOps.Contracts.Assets;

/// <summary>Papéis normalizados de um device (classificação). Ver docs .../classificador-device-design.md.</summary>
public static class DeviceRoles
{
    public const string Router = "router";
    public const string Switch = "switch";
    public const string ServerLinux = "server-linux";
    public const string ServerWindows = "server-windows";
    public const string Olt = "olt";
    public const string Firewall = "firewall";
    public const string LoadBalancer = "loadbalancer";
    public const string Wireless = "wireless";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        [Router, Switch, ServerLinux, ServerWindows, Olt, Firewall, LoadBalancer, Wireless, Other];
}
```
- [ ] **Step 4:** Em `Asset.cs`, adicionar após `Model`: `public string? DeviceRole { get; init; }`
- [ ] **Step 5: Rodar** → PASS.
- [ ] **Step 6: Commit** — `git commit -am "feat(model): Asset.DeviceRole + DeviceRoles"`

---

### Task 2: `DeviceClassifier` (heurística pura)

**Files:**
- Create: `src/RemoteOps.Desktop/Domain/DeviceClassifier.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Domain/DeviceClassifierTests.cs`

**Interfaces — Consumes:** `DeviceRoles.*` (Task 1). **Produces:** `record DeviceClassification(string Role, string? VendorKey, string? BadgeLabel, int Confidence)`; `DeviceClassifier.Suggest(string? vendor, string? model, string? protocol) → DeviceClassification` (nunca lança, sem IO).

- [ ] **Step 1: Teste** — matriz cobrindo cada regra + negativos:
```csharp
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using Xunit;
namespace RemoteOps.UnitTests.Desktop.Domain;
public class DeviceClassifierTests
{
    [Theory]
    [InlineData("MikroTik", "CCR2004", "ssh", DeviceRoles.Router, "mikrotik", "ROS")]
    [InlineData(null, null, "mikrotik", DeviceRoles.Router, "mikrotik", "ROS")]
    [InlineData("MikroTik", "CRS328", "ssh", DeviceRoles.Switch, "mikrotik", "ROS")]
    [InlineData("Huawei", "NE8000", "ssh", DeviceRoles.Router, "huawei", "VRP8")]
    [InlineData("Huawei", "S5720", "telnet", DeviceRoles.Switch, "huawei", "VRP5")]
    [InlineData("Huawei", "MA5800", "telnet", DeviceRoles.Olt, "huawei", "OLT")]
    [InlineData("Debian", null, "ssh", DeviceRoles.ServerLinux, "debian", "DEB")]
    [InlineData("Ubuntu", "22.04", "ssh", DeviceRoles.ServerLinux, "ubuntu", "UBU")]
    [InlineData("A10", "Thunder", "ssh", DeviceRoles.LoadBalancer, "a10", "A10")]
    [InlineData(null, null, "rdp", DeviceRoles.ServerWindows, "windows", "WIN")]
    public void Suggest_MapsKnownDevices(string? v, string? m, string? p, string role, string vk, string badge)
    {
        var c = DeviceClassifier.Suggest(v, m, p);
        Assert.Equal(role, c.Role);
        Assert.Equal(vk, c.VendorKey);
        Assert.Equal(badge, c.BadgeLabel);
    }

    [Fact] public void Suggest_Unknown_ReturnsOther_ZeroConfidence()
    {
        var c = DeviceClassifier.Suggest("AcmeCorp", "X1", "ssh");
        Assert.Equal(DeviceRoles.Other, c.Role);
        Assert.Equal(0, c.Confidence);
    }

    [Fact] public void Suggest_Empty_DoesNotThrow()
        => Assert.Equal(DeviceRoles.Other, DeviceClassifier.Suggest(null, null, null).Role);
}
```
- [ ] **Step 2: Rodar** → FAIL (não compila).
- [ ] **Step 3: Implementar** — `DeviceClassifier.cs`:
```csharp
using System.Text.RegularExpressions;
using RemoteOps.Contracts.Assets;

namespace RemoteOps.Desktop.Domain;

/// <summary>Classificação sugerida de um device. BadgeLabel/glifo só são usados no fallback sem logo.</summary>
public sealed record DeviceClassification(string Role, string? VendorKey, string? BadgeLabel, int Confidence);

/// <summary>
/// Heurística LOCAL (sem rede) que sugere papel + vendor + selo a partir do que o operador digitou.
/// Ponto único de classificação — a detecção ATIVA futura (banner SSH/RouterOS/SNMP) entra aqui.
/// Regras ordenadas: a primeira que casar vence. Nunca lança.
/// </summary>
public static class DeviceClassifier
{
    private static readonly Func<string, string, string, DeviceClassification?>[] Rules =
    [
        (v, m, p) => p == "mikrotik" || v.Contains("mikrotik") || v.Contains("routeros")
            ? new(m.Contains("crs") || m.Contains("css") ? DeviceRoles.Switch : DeviceRoles.Router, "mikrotik", "ROS", 90) : null,
        (v, m, p) => v.Contains("huawei") && Regex.IsMatch(m, "^(ne|atn|ar|cx)|netengine")
            ? new(DeviceRoles.Router, "huawei", "VRP8", 90) : null,
        (v, m, p) => v.Contains("huawei") && Regex.IsMatch(m, "^(s[0-9]|ce|cloudengine)")
            ? new(DeviceRoles.Switch, "huawei", "VRP5", 85) : null,
        (v, m, p) => v.Contains("huawei") && Regex.IsMatch(m, "ma5|ea5|olt")
            ? new(DeviceRoles.Olt, "huawei", "OLT", 85) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "debian") ? new(DeviceRoles.ServerLinux, "debian", "DEB", 80) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "ubuntu") ? new(DeviceRoles.ServerLinux, "ubuntu", "UBU", 80) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "centos|rhel|red ?hat|rocky|almalinux|(^| )linux")
            ? new(DeviceRoles.ServerLinux, "linux", "LNX", 70) : null,
        (v, m, p) => Regex.IsMatch(v + " " + m, "windows|win ?server") ? new(DeviceRoles.ServerWindows, "windows", "WIN", 75) : null,
        (v, m, p) => v.Contains("a10") ? new(DeviceRoles.LoadBalancer, "a10", "A10", 80) : null,
        (v, m, p) => Regex.IsMatch(v, "cisco|ios|nx-os")
            ? new(Regex.IsMatch(m, "catalyst|nexus") ? DeviceRoles.Switch : DeviceRoles.Router, "cisco", "CSC", 70) : null,
        (v, m, p) => Regex.IsMatch(v, "juniper|junos") ? new(DeviceRoles.Router, "juniper", "JNP", 70) : null,
        (v, m, p) => p == "rdp" ? new(DeviceRoles.ServerWindows, "windows", "WIN", 50) : null,
    ];

    public static DeviceClassification Suggest(string? vendor, string? model, string? protocol)
    {
        string v = (vendor ?? string.Empty).ToLowerInvariant();
        string m = (model ?? string.Empty).ToLowerInvariant();
        string p = (protocol ?? string.Empty).ToLowerInvariant();
        foreach (var rule in Rules)
        {
            if (rule(v, m, p) is { } c) return c;
        }
        string? slug = string.IsNullOrWhiteSpace(vendor) ? null : Regex.Replace(v, "[^a-z0-9]+", "-").Trim('-');
        return new DeviceClassification(DeviceRoles.Other, slug, null, 0);
    }
}
```
- [ ] **Step 4: Rodar** → PASS (ajustar regex se algum caso falhar).
- [ ] **Step 5: Commit** — `git commit -am "feat(domain): DeviceClassifier heurístico"`

---

### Task 3: `DeviceCatalog` (glifo/rótulo/cor/logo) + glifos no tema

**Files:**
- Create: `src/RemoteOps.Desktop/Domain/DeviceCatalog.cs`
- Modify: `src/RemoteOps.Desktop/Themes/Tokens/Icons.xaml` (glifos de papel)
- Test: `tests/RemoteOps.UnitTests/Desktop/Domain/DeviceCatalogTests.cs`

**Interfaces — Consumes:** `DeviceRoles.*`. **Produces:** `DeviceCatalog.RoleGlyph(string?)`, `RoleLabel(string?)`, `VendorColorHex(string?)`, `LogoFileName(string?)`.

- [ ] **Step 1: Teste**:
```csharp
using RemoteOps.Contracts.Assets;
using RemoteOps.Desktop.Domain;
using Xunit;
namespace RemoteOps.UnitTests.Desktop.Domain;
public class DeviceCatalogTests
{
    [Fact] public void EveryRole_HasGlyphAndLabel()
    {
        foreach (var r in DeviceRoles.All)
        {
            Assert.False(string.IsNullOrEmpty(DeviceCatalog.RoleGlyph(r)));
            Assert.False(string.IsNullOrWhiteSpace(DeviceCatalog.RoleLabel(r)));
        }
    }
    [Fact] public void NullRole_FallsBackToGeneric() => Assert.Equal(DeviceCatalog.RoleLabel(DeviceRoles.Other), DeviceCatalog.RoleLabel(null));
    [Fact] public void KnownVendor_HasColor() => Assert.StartsWith("#", DeviceCatalog.VendorColorHex("huawei"));
    [Fact] public void LogoFileName_UsesVendorKey() => Assert.Equal("huawei.png", DeviceCatalog.LogoFileName("huawei"));
    [Fact] public void LogoFileName_NullVendor_IsNull() => Assert.Null(DeviceCatalog.LogoFileName(null));
}
```
- [ ] **Step 2: Rodar** → FAIL.
- [ ] **Step 3: Implementar** — `DeviceCatalog.cs` (glifos MDL2; **verificar visualmente no render test da Task 6** e trocar por Path se algum glifo não existir/ficar ruim):
```csharp
using RemoteOps.Contracts.Assets;

namespace RemoteOps.Desktop.Domain;

/// <summary>Fonte única de aparência por papel/vendor. Glifos são codepoints da Segoe MDL2 Assets.</summary>
public static class DeviceCatalog
{
    // AÇÃO NA IMPLEMENTAÇÃO: preencher cada arm com o codepoint MDL2 correto (ex.: ""
    // NetworkTower pra Switch, conhecido) e VALIDAR no render test (Task 6). Não deixar vazio no
    // código final; se um glifo sair como "caixa"/tofu, trocar o codepoint OU desenhar um Path
    // vetorial (como o check/chevron do tema). Os comentários por arm indicam o glifo pretendido.
    public static string RoleGlyph(string? role) => role switch
    {
        DeviceRoles.Router => "",        // NetworkTower-ish
        DeviceRoles.Switch => "",        // Network
        DeviceRoles.ServerLinux => "",   // Server (DeveloperTools-ish) — verificar
        DeviceRoles.ServerWindows => "",
        DeviceRoles.Olt => "",           // Streaming/broadcast — verificar
        DeviceRoles.Firewall => "",      // Lock/Shield
        DeviceRoles.LoadBalancer => "",  // BranchFork — verificar
        DeviceRoles.Wireless => "",      // Wifi
        _ => "",                          // Devices genérico
    };

    public static string RoleLabel(string? role) => role switch
    {
        DeviceRoles.Router => "Roteador",
        DeviceRoles.Switch => "Switch",
        DeviceRoles.ServerLinux => "Servidor Linux",
        DeviceRoles.ServerWindows => "Servidor Windows",
        DeviceRoles.Olt => "OLT",
        DeviceRoles.Firewall => "Firewall",
        DeviceRoles.LoadBalancer => "Load Balancer",
        DeviceRoles.Wireless => "Wireless",
        _ => "Sem tipo",
    };

    public static string VendorColorHex(string? vendorKey) => vendorKey switch
    {
        "huawei" => "#C0392B",
        "mikrotik" => "#2D6FB8",
        "debian" => "#97C459",
        "ubuntu" => "#E56B2E",
        "a10" => "#EF9F27",
        "cisco" => "#1BA0D7",
        "juniper" => "#3FA34D",
        _ => "#647085", // Text.Tertiary
    };

    /// <summary>Nome do arquivo de logo (em assets/logos/) ou null. O arquivo pode não existir → fallback pro glifo.</summary>
    public static string? LogoFileName(string? vendorKey)
        => string.IsNullOrWhiteSpace(vendorKey) ? null : $"{vendorKey}.png";
}
```
- [ ] **Step 4:** Em `Icons.xaml`, adicionar chaves `Icon.Role*` correspondentes (opcional — o DeviceCatalog já entrega o glifo direto; só adicione se quiser referenciar por DynamicResource). Pode pular se o `DeviceIcon` usar `DeviceCatalog.RoleGlyph` direto.
- [ ] **Step 5: Rodar** → PASS.
- [ ] **Step 6: Commit** — `git commit -am "feat(domain): DeviceCatalog (glifo/rótulo/cor/logo)"`

---

### Task 4: Persistência — `device_role` (SqlCipher + InMemory + AddAssetRequest)

**Files:**
- Modify: `src/RemoteOps.Desktop/Domain/AddAssetRequest.cs`, `src/RemoteOps.Desktop/Infrastructure/InMemoryLocalStore.cs`, `src/RemoteOps.Desktop/Infrastructure/SqlCipherLocalStore.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/DeviceRolePersistenceTests.cs`

**Interfaces — Consumes:** `Asset.DeviceRole`, `AddAssetRequest`. **Produces:** stores persistem/leem `DeviceRole`.

- [ ] **Step 1: Teste** (InMemory + SqlCipher round-trip + migração sobre banco antigo):
```csharp
// round-trip: AddAssetAsync com Vendor/Model + UpdateAssetAsync setando DeviceRole → GetAssetsAsync devolve DeviceRole.
// migração: abrir SqlCipher sobre um assets sem a coluna device_role não deve lançar (ALTER idempotente).
```
(Escrever asserts concretos espelhando os testes de store existentes em `Desktop/Infrastructure/`.)
- [ ] **Step 2: Rodar** → FAIL.
- [ ] **Step 3:** `AddAssetRequest.cs` — adicionar `public string? DeviceRole { get; init; }`.
- [ ] **Step 4:** `InMemoryLocalStore.cs` — nos pontos que copiam `Vendor` (≈ linhas 75, 123, 147) adicionar `DeviceRole = request.DeviceRole` / `DeviceRole = asset.DeviceRole`.
- [ ] **Step 5:** `SqlCipherLocalStore.cs`:
  - Schema (≈ linha 50): adicionar `device_role TEXT,` após `vendor TEXT,`.
  - **Migração idempotente** logo após criar as tabelas: `PRAGMA table_info(assets)`; se não houver coluna `device_role`, `ALTER TABLE assets ADD COLUMN device_role TEXT`.
  - SELECTs (linhas 178/182/242): incluir `device_role` na lista.
  - INSERT (270/271) + `$device_role` param; UPDATE (316) + `device_role = $device_role`.
  - Mapper (657/664/679): ler `device_role` (índice novo) → `DeviceRole`.
- [ ] **Step 6: Rodar** → PASS.
- [ ] **Step 7: Commit** — `git commit -am "feat(store): persistir Asset.DeviceRole (+ migração)"`

---

### Task 5: Editor — Vendor/Model + "Tipo" com auto-sugestão

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/HostEditorViewModel.cs`, `src/RemoteOps.Desktop/Views/HostEditorDialog.xaml`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostEditorClassificationTests.cs`

**Interfaces — Consumes:** `DeviceClassifier.Suggest`, `AddAssetRequest.DeviceRole`. **Produces:** `HostEditorViewModel.{Vendor, Model, DeviceRole, DeviceRoleOptions}`; SaveAsync persiste Vendor/Model/DeviceRole.

> Nota: hoje o editor **não** captura Vendor/Model nem os persiste (SaveAsync só passa Name). Esta task fecha esse gap — é pré-requisito do classificador.

- [ ] **Step 1: Teste** — mudar Vendor/Model auto-sugere DeviceRole; operador pode sobrescrever; salvar persiste:
```csharp
[Fact] public void SettingVendorModel_SuggestsRole()
{
    var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws", null, null);
    vm.Vendor = "Huawei"; vm.Model = "NE8000";
    Assert.Equal(DeviceRoles.Router, vm.DeviceRole);
}
[Fact] public void OperatorOverride_IsKept_WhenVendorUnchanged()
{
    var vm = new HostEditorViewModel(new InMemoryLocalStore(), "ws", null, null);
    vm.Vendor = "Huawei"; vm.Model = "NE8000";
    vm.DeviceRole = DeviceRoles.Switch;          // override manual
    Assert.Equal(DeviceRoles.Switch, vm.DeviceRole);
}
```
- [ ] **Step 2: Rodar** → FAIL.
- [ ] **Step 3: Implementar VM** — campos `_vendor/_model/_deviceRole` + `_roleTouched` (flag de override):
```csharp
public IReadOnlyList<string> DeviceRoleOptions => DeviceRoles.All;
public string? Vendor { get => _vendor; set { Set(ref _vendor, value); AutoSuggestRole(); } }
public string? Model  { get => _model;  set { Set(ref _model, value);  AutoSuggestRole(); } }
public string? DeviceRole { get => _deviceRole; set { _roleTouched = true; Set(ref _deviceRole, value); RaisePropertyChanged(nameof(DeviceRolePreview)); } }
public string DeviceRolePreview => DeviceCatalog.RoleLabel(_deviceRole);

private void AutoSuggestRole()
{
    if (_roleTouched) return; // não sobrescreve escolha manual
    var c = DeviceClassifier.Suggest(_vendor, _model, NewEndpointProtocol);
    _deviceRole = c.Role == DeviceRoles.Other ? _deviceRole : c.Role;
    RaisePropertyChanged(nameof(DeviceRole));
    RaisePropertyChanged(nameof(DeviceRolePreview));
}
```
  - No ctor com `existing`: `_vendor = existing.Vendor; _model = existing.Model; _deviceRole = existing.DeviceRole; _roleTouched = existing.DeviceRole != null;`
  - `NewEndpointProtocol` setter: chamar `AutoSuggestRole()` também (protocolo influencia).
  - `SaveAsync`: no `AddAssetRequest` adicionar `Vendor = _vendor, Model = _model, DeviceRole = _deviceRole`; no `UpdateAssetAsync(new Asset{...})` adicionar `Vendor = _vendor, Model = _model, DeviceRole = _deviceRole` (hoje esses três não são setados — corrigir).
- [ ] **Step 4: XAML** — em `HostEditorDialog.xaml`, adicionar (perto do Nome) uma linha Vendor + Model (TextBox) e "Tipo" (ComboBox `ItemsSource=DeviceRoleOptions`, `SelectedItem=DeviceRole`) com um `DeviceIcon` (Task 6) de preview.
- [ ] **Step 5: Rodar** → PASS. Rodar `HostEditorDialogRenderTests` (existente) → ainda verde.
- [ ] **Step 6: Commit** — `git commit -am "feat(editor): captura Vendor/Model + Tipo auto-sugerido"`

---

### Task 6: `DeviceIcon` UserControl (logo-ou-glifo) + assets gitignored

**Files:**
- Create: `src/RemoteOps.Desktop/Views/DeviceIcon.xaml` (+ `.cs`), `src/RemoteOps.Desktop/assets/logos/.gitkeep`
- Modify: `.gitignore`, `RemoteOps.Desktop.csproj`
- Test: `tests/RemoteOps.UnitTests/Desktop/Views/DeviceIconRenderTests.cs`

**Interfaces — Consumes:** `DeviceCatalog`. **Produces:** `DeviceIcon` com DPs `Role` (string?) + `VendorKey` (string?); mostra `assets/logos/<vendorKey>.png` se existir, senão glifo de papel tingido por `VendorColorHex`.

- [ ] **Step 1:** `.gitignore` — adicionar `src/RemoteOps.Desktop/assets/logos/*` com `!src/RemoteOps.Desktop/assets/logos/.gitkeep`.
- [ ] **Step 2:** `.csproj` — `<Content Include="assets\logos\**\*.png"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>` (não falha se vazio).
- [ ] **Step 3: Teste render STA** — `DeviceIcon` com Role="router" sem logo (fallback glifo) e com VendorKey="huawei" (arquivo ausente → fallback) renderizam sem lançar (padrão `StaThreadRunner`).
- [ ] **Step 4: Rodar** → FAIL.
- [ ] **Step 5: Implementar** — `DeviceIcon.xaml.cs`: DPs `Role`/`VendorKey`; no change, resolver caminho `AppContext.BaseDirectory + assets/logos/<file>`; se `File.Exists` → `<Image>` (try/catch → fallback); senão `TextBlock` com `DeviceCatalog.RoleGlyph(Role)` FontFamily Segoe MDL2, Foreground = `VendorColorHex`. ~16px.
- [ ] **Step 6: Rodar** → PASS. **Verificar glifos** (Task 3) no output — ajustar codepoints se algum sair como "caixa".
- [ ] **Step 7: Commit** — `git commit -am "feat(ui): DeviceIcon (logo-ou-glifo) + assets gitignored"`

---

### Task 7: Lista — ícone no nome + coluna "Tipo" + chips de filtro

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/HostsViewModel.cs`, `src/RemoteOps.Desktop/ViewModels/AssetViewModel.cs`, `src/RemoteOps.Desktop/Views/HostsView.xaml`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostsFilterTests.cs`

**Interfaces — Consumes:** `DeviceClassifier`/`DeviceCatalog`, `DeviceIcon`. **Produces:** `AssetViewModel.{DeviceRole, VendorKey, RoleLabel}`; `HostsViewModel.{DeviceFilters (chips), SelectedFilter, ApplyFilterCommand}`.

- [ ] **Step 1:** `AssetViewModel` — expor `DeviceRole => Asset.DeviceRole`, `VendorKey => DeviceClassifier.Suggest(Asset.Vendor, Asset.Model, PrimaryProtocol).VendorKey`, `RoleLabel => DeviceCatalog.RoleLabel(Asset.DeviceRole)`. Incluir no `Refresh`.
- [ ] **Step 2: Teste filtro** — `HostsFilterTests`: com hosts de papéis variados, `SelectedFilter="router"` deixa só roteadores em `Hosts`; `"__all__"` mostra todos; `DeviceFilters` só lista papéis/vendors presentes.
- [ ] **Step 3: Rodar** → FAIL.
- [ ] **Step 4: Implementar filtro** — em `HostsViewModel`: campo `_selectedFilter` (default "__all__"); `LoadHostsAsync` aplica o predicado (por role OU vendorKey) além do search; `DeviceFilters` (ObservableCollection) reconstruída a partir dos papéis/vendors distintos dos assets carregados; `RefreshAsync` re-aplica.
- [ ] **Step 5: XAML** — `HostsView.xaml`: (a) coluna "Nome" vira `DataGridTemplateColumn` com `DeviceIcon` (Role/VendorKey) + `TextBlock Name`; (b) nova `DataGridTemplateColumn` "Tipo" com glifo + `RoleLabel`; (c) `ItemsControl` de chips acima do DataGrid (bind `DeviceFilters`, cada chip = ToggleButton/RadioButton com `ApplyFilterCommand`).
- [ ] **Step 6: Rodar** → PASS. Rodar `ShellRenderSmokeTests` → verde.
- [ ] **Step 7: Commit** — `git commit -am "feat(lista): ícone no nome + coluna Tipo + chips de filtro"`

---

### Task 8: Validação final + PR

- [ ] **Step 1:** `dotnet build RemoteOps.sln -c Release` → 0 warn / 0 err.
- [ ] **Step 2:** `dotnet test RemoteOps.sln -c Release` → tudo verde (novos + regressão).
- [ ] **Step 3:** `dotnet format RemoteOps.sln --verify-no-changes` → exit 0.
- [ ] **Step 4:** Auto-review do diff (altitude, nomes, sem placeholder). Rodar `superpowers:finishing-a-development-branch`.
- [ ] **Step 5:** Preview visual (widget) do editor + lista pro operador conferir (screenshot não funciona nesta máquina).
- [ ] **Step 6:** PR `feature/device-classifier` → main; após aprovação/CI verde, squash-merge (não taggar release ainda — feature acumula até o operador querer cortar versão).
