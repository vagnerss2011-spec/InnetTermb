# Novidades (changelog) + Reportar problema (bug report) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adicionar duas abas no modal de Configurações — "Novidades" (changelog curado offline) e "Reportar problema" (bug report via `mailto:` com diagnósticos opt-in/preview).

**Architecture:** Estende o modal `SettingsWindow`/`SettingsViewModel` com dois VMs-filho de responsabilidade única (`ChangelogViewModel`, `BugReportViewModel`), cada um sobre um serviço puro (`EmbeddedChangelogSource`, `MailtoBugReportComposer`+`DiagnosticsProvider`). Changelog vem de um JSON embutido no binário; bug report abre `mailto:` + grava cópia local. Zero rede, zero vault, zero tabela SQLCipher.

**Tech Stack:** .NET 10, WPF (`net10.0-windows`), `System.Text.Json`, `Microsoft.Extensions.DependencyInjection`, xUnit.

**Worktree:** `C:\dev\remoteops-changelog-bugreport` (branch `feature/gui-changelog-bugreport`, base `feature/gui-credentials-winbox` `e1886d3`, **Depends-on #44**). Caminhos relativos a essa base.

**Spec:** `docs/superpowers/specs/2026-07-02-changelog-bugreport-design.md`

## Global Constraints

- `TreatWarningsAsErrors=true` → build **0/0**. `Nullable=enable`, `ImplicitUsings=enable`.
- **CI roda `dotnet format --verify-no-changes` em Release.** Antes de cada commit: `dotnet format` (auto-fix) + `dotnet format --verify-no-changes` (exit 0). Object initializers exigem **um membro por linha**.
- Testes xUnit (`[Fact]`/`Assert`); namespace espelha a pasta (`RemoteOps.UnitTests.Desktop.*`); a suíte existente (**437**) fica verde.
- Rótulos destas telas em **pt-BR**.
- **Nenhum segredo** em UI, log, e-mail, arquivo ou commit. Diagnósticos só de fontes secret-free.
- Build: `dotnet build "C:\dev\remoteops-changelog-bugreport\RemoteOps.sln" -c Debug --nologo`. Test: `dotnet test "C:\dev\remoteops-changelog-bugreport\RemoteOps.sln" -c Debug --nologo`.

## Interfaces existentes reaproveitadas

- `AppSettings` (`Infrastructure/AppSettings.cs`) — `sealed record` com `init` props (`Flags`, `Theme`, `WinBoxExePath`, `WinBoxSha256`). Persistido por `JsonSettingsStore` (`%AppData%\RemoteOps\settings.json`); `Load()` fail-safe (defaults em erro); `Save(AppSettings)`.
- `ISettingsStore` — `AppSettings Load()`, `void Save(AppSettings)`.
- `AppVersion` (`Update/AppVersion.cs`) — `readonly record struct : IComparable<AppVersion>`; `static bool TryParse(string?, out AppVersion)`; `int CompareTo(AppVersion)`. SemVer com precedência de prerelease.
- `LogsViewModel` (`ViewModels/LogsViewModel.cs`) — `sealed class : BaseViewModel, IUiLogSink`; `ObservableCollection<string> Events` (mais novo em index 0, via `Emit`). Singleton no DI.
- `BaseViewModel` — `Set(ref, value)`, `RaisePropertyChanged(name)`. `RelayCommand(Action, Func<bool>?)` com `RaiseCanExecuteChanged()`.
- `SettingsViewModel(ISettingsStore store, IUpdateService? updateService = null)` — construído por `WorkspaceViewModel.CreateSettingsViewModel()` → `new(_settingsStore ?? new JsonSettingsStore(), _updateService)`. Tem `Saved` event, `SaveCommand`. `private SettingsViewModel Vm => (SettingsViewModel)DataContext;` já existe em `SettingsWindow.xaml.cs`.
- `WorkspaceViewModel(BrowserViewModel browser, TabsViewModel tabs, ISettingsStore? settingsStore = null, IUpdateService? updateService = null)` — singleton no DI; `Browser` expõe `Logs` (LogsViewModel).
- `BrowserViewModel(HostsViewModel, KeychainViewModel, LogsViewModel)` — singleton no DI (`AddSingleton<BrowserViewModel>()`, auto-ctor). Menu do avatar em `BrowserView.xaml` faz bind em `OpenSettingsCommand`/`CheckUpdatesCommand`/`AboutCommand`.
- `MainWindow.OpenSettings()` (linhas 111-115): `new SettingsWindow(Vm.CreateSettingsViewModel()){Owner=this}; window.ShowDialog();`.
- DI: `AppCompositionRoot.Build(...)`; `ISettingsStore→JsonSettingsStore` (singleton), `LogsViewModel` (singleton), `InternalsVisibleTo("RemoteOps.UnitTests")` já configurado.

---

### Task 1: `AppSettings.LastSeenChangelogVersion`

**Files:**
- Modify: `src/RemoteOps.Desktop/Infrastructure/AppSettings.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/AppSettingsChangelogTests.cs`

**Interfaces:** Produces `AppSettings.LastSeenChangelogVersion` (`string?`, init).

- [ ] **Step 1: Teste que falha** — `AppSettingsChangelogTests.cs`:
```csharp
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class AppSettingsChangelogTests
{
    [Fact]
    public void LastSeenChangelogVersion_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        AppSettings loaded = store.Load();
        Assert.Equal("1.0.0", loaded.LastSeenChangelogVersion);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test "C:\dev\remoteops-changelog-bugreport\RemoteOps.sln" --filter "FullyQualifiedName~AppSettingsChangelogTests" --nologo` → FALHA de compilação.

- [ ] **Step 3: Implementar** — em `AppSettings.cs`, após `WinBoxSha256`:
```csharp
    /// <summary>Última versão de changelog que o operador já viu (badge "novidades"); null = nunca viu.</summary>
    public string? LastSeenChangelogVersion { get; init; }
```

- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: `dotnet format`** (auto-fix) + **`dotnet format --verify-no-changes`** (exit 0).
- [ ] **Step 6: Commit** — `feat(gui): AppSettings.LastSeenChangelogVersion`.

---

### Task 2: Camada de dados do changelog (recurso embutido)

**Files:**
- Create: `src/RemoteOps.Desktop/Changelog/ChangelogEntry.cs`
- Create: `src/RemoteOps.Desktop/Changelog/IChangelogSource.cs`
- Create: `src/RemoteOps.Desktop/Changelog/EmbeddedChangelogSource.cs`
- Create: `src/RemoteOps.Desktop/Changelog/ChangelogVersioning.cs`
- Create: `src/RemoteOps.Desktop/Resources/operator-changelog.json`
- Modify: `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`
- Test: `tests/RemoteOps.UnitTests/Desktop/Changelog/EmbeddedChangelogSourceTests.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Changelog/ChangelogVersioningTests.cs`

**Interfaces:**
- Produces `ChangelogEntry { string Version; string Date; IReadOnlyList<string> Highlights }`.
- Produces `IChangelogSource.Load() -> IReadOnlyList<ChangelogEntry>`.
- Produces `EmbeddedChangelogSource : IChangelogSource` (recurso lógico `operator-changelog.json`).
- Produces `ChangelogVersioning.IsNewer(string version, string? lastSeen) -> bool`; `ChangelogVersioning.Latest(IEnumerable<string>) -> string?`.

- [ ] **Step 1: Seed do JSON** — `src/RemoteOps.Desktop/Resources/operator-changelog.json`:
```json
{
  "entries": [
    {
      "version": "1.0.0",
      "date": "2026-07-02",
      "highlights": [
        "Chaveiro: crie login e senha direto no app (aba Keychain).",
        "WinBox: escolha o executável em Configurações → Ferramentas externas; o app fixa o hash automaticamente.",
        "Anexe uma credencial a um host no editor de endpoints."
      ]
    }
  ]
}
```

- [ ] **Step 2: Embed no csproj** — em `RemoteOps.Desktop.csproj`, adicionar um `<ItemGroup>` (ex.: após o bloco de `Content` do wwwroot, antes de `</Project>`):
```xml
  <ItemGroup>
    <!-- Changelog curado do operador (offline). LogicalName fixo → GetManifestResourceStream determinístico. -->
    <EmbeddedResource Include="Resources\operator-changelog.json">
      <LogicalName>operator-changelog.json</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
```

- [ ] **Step 3: Testes que falham** — `EmbeddedChangelogSourceTests.cs`:
```csharp
using System.Linq;
using RemoteOps.Desktop.Changelog;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Changelog;

public sealed class EmbeddedChangelogSourceTests
{
    [Fact]
    public void Load_ReturnsSeededVersion_WithHighlights()
    {
        var source = new EmbeddedChangelogSource();
        var entries = source.Load();
        var v1 = entries.SingleOrDefault(e => e.Version == "1.0.0");
        Assert.NotNull(v1);
        Assert.NotEmpty(v1!.Highlights);
    }
}
```
`ChangelogVersioningTests.cs`:
```csharp
using RemoteOps.Desktop.Changelog;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Changelog;

public sealed class ChangelogVersioningTests
{
    [Theory]
    [InlineData("1.1.0", null, true)]      // nunca viu → novo
    [InlineData("1.1.0", "1.0.0", true)]   // maior que o visto → novo
    [InlineData("1.0.0", "1.0.0", false)]  // igual → não novo
    [InlineData("1.0.0", "1.1.0", false)]  // menor → não novo
    [InlineData("nao-semver", "1.0.0", false)] // inválido → não novo
    public void IsNewer_Works(string version, string? lastSeen, bool expected)
        => Assert.Equal(expected, ChangelogVersioning.IsNewer(version, lastSeen));

    [Fact]
    public void Latest_PicksHighestSemVer()
        => Assert.Equal("2.0.0", ChangelogVersioning.Latest(new[] { "1.9.0", "2.0.0", "1.10.0" }));

    [Fact]
    public void Latest_EmptyOrAllInvalid_ReturnsNull()
        => Assert.Null(ChangelogVersioning.Latest(new[] { "x", "y" }));
}
```

- [ ] **Step 4: Rodar e ver falhar.**

- [ ] **Step 5: Implementar `ChangelogEntry.cs`:**
```csharp
using System.Collections.Generic;

namespace RemoteOps.Desktop.Changelog;

/// <summary>Uma versão do changelog curado do operador.</summary>
public sealed record ChangelogEntry
{
    public string Version { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public IReadOnlyList<string> Highlights { get; init; } = [];
}
```

- [ ] **Step 6: Implementar `IChangelogSource.cs`:**
```csharp
using System.Collections.Generic;

namespace RemoteOps.Desktop.Changelog;

public interface IChangelogSource
{
    IReadOnlyList<ChangelogEntry> Load();
}
```

- [ ] **Step 7: Implementar `EmbeddedChangelogSource.cs`:**
```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemoteOps.Desktop.Changelog;

/// <summary>Lê o changelog curado embutido no binário (offline, sem rede). Falha → lista vazia.</summary>
public sealed class EmbeddedChangelogSource : IChangelogSource
{
    private const string ResourceName = "operator-changelog.json";
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public IReadOnlyList<ChangelogEntry> Load()
    {
        try
        {
            using Stream? stream = typeof(EmbeddedChangelogSource).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return [];
            }

            ChangelogFile? file = JsonSerializer.Deserialize<ChangelogFile>(stream, Options);
            return file?.Entries ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record ChangelogFile
    {
        public List<ChangelogEntry> Entries { get; init; } = [];
    }
}
```

- [ ] **Step 8: Implementar `ChangelogVersioning.cs`:**
```csharp
using System.Collections.Generic;
using RemoteOps.Desktop.Update;

namespace RemoteOps.Desktop.Changelog;

/// <summary>Comparações SemVer do changelog (DRY entre ChangelogViewModel e BrowserViewModel).</summary>
public static class ChangelogVersioning
{
    /// <summary>True se <paramref name="version"/> é mais nova que <paramref name="lastSeen"/> (ou nunca visto).</summary>
    public static bool IsNewer(string version, string? lastSeen)
    {
        if (!AppVersion.TryParse(version, out AppVersion v))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(lastSeen) || !AppVersion.TryParse(lastSeen, out AppVersion seen))
        {
            return true;
        }

        return v.CompareTo(seen) > 0;
    }

    /// <summary>Maior versão SemVer válida da lista; null se nenhuma parsear.</summary>
    public static string? Latest(IEnumerable<string> versions)
    {
        string? latest = null;
        AppVersion best = default;
        bool has = false;
        foreach (string raw in versions)
        {
            if (!AppVersion.TryParse(raw, out AppVersion v))
            {
                continue;
            }

            if (!has || v.CompareTo(best) > 0)
            {
                best = v;
                latest = raw;
                has = true;
            }
        }

        return latest;
    }
}
```

- [ ] **Step 9: Rodar e ver passar** + `dotnet build` 0/0.
- [ ] **Step 10: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 11: Commit** — `feat(gui): changelog embutido (EmbeddedChangelogSource + versioning)`.

---

### Task 3: `ChangelogViewModel`

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/ChangelogViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/ChangelogViewModelTests.cs`

**Interfaces:**
- Consumes: `IChangelogSource`, `ChangelogVersioning`, `ISettingsStore`, `AppSettings.LastSeenChangelogVersion`.
- Produces: `ChangelogViewModel(IChangelogSource source, ISettingsStore store)`; `ObservableCollection<ChangelogItemViewModel> Entries`; `bool HasEntries`; `void MarkAllSeen()`. `ChangelogItemViewModel(string Version, string Date, IReadOnlyList<string> Highlights, bool IsNew)`.

- [ ] **Step 1: Teste que falha** — `ChangelogViewModelTests.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class ChangelogViewModelTests
{
    private sealed class FakeSource : IChangelogSource
    {
        private readonly IReadOnlyList<ChangelogEntry> _entries;
        public FakeSource(params string[] versions)
            => _entries = versions.Select(v => new ChangelogEntry { Version = v, Date = "2026-01-01", Highlights = new[] { "h" } }).ToList();
        public IReadOnlyList<ChangelogEntry> Load() => _entries;
    }

    private static JsonSettingsStore TempStore()
        => new(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json"));

    [Fact]
    public void NeverSeen_AllEntriesNew()
    {
        var vm = new ChangelogViewModel(new FakeSource("1.0.0", "1.1.0"), TempStore());
        Assert.True(vm.HasEntries);
        Assert.All(vm.Entries, e => Assert.True(e.IsNew));
    }

    [Fact]
    public void LastSeen_MarksOnlyNewerAsNew()
    {
        var store = TempStore();
        store.Save(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        var vm = new ChangelogViewModel(new FakeSource("1.0.0", "1.1.0"), store);
        Assert.False(vm.Entries.Single(e => e.Version == "1.0.0").IsNew);
        Assert.True(vm.Entries.Single(e => e.Version == "1.1.0").IsNew);
    }

    [Fact]
    public void MarkAllSeen_PersistsLatestVersion()
    {
        var store = TempStore();
        var vm = new ChangelogViewModel(new FakeSource("1.0.0", "1.1.0"), store);
        vm.MarkAllSeen();
        Assert.Equal("1.1.0", store.Load().LastSeenChangelogVersion);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar `ChangelogViewModel.cs`:**
```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;

namespace RemoteOps.Desktop.ViewModels;

public sealed record ChangelogItemViewModel(string Version, string Date, IReadOnlyList<string> Highlights, bool IsNew);

/// <summary>Aba "Novidades": lista as versões curadas, marca as novas desde a última visita.</summary>
public sealed class ChangelogViewModel : BaseViewModel
{
    private readonly ISettingsStore _store;

    public ChangelogViewModel(IChangelogSource source, ISettingsStore store)
    {
        _store = store;
        string? lastSeen = store.Load().LastSeenChangelogVersion;
        foreach (ChangelogEntry e in source.Load())
        {
            Entries.Add(new ChangelogItemViewModel(e.Version, e.Date, e.Highlights, ChangelogVersioning.IsNewer(e.Version, lastSeen)));
        }
    }

    public ObservableCollection<ChangelogItemViewModel> Entries { get; } = [];
    public bool HasEntries => Entries.Count > 0;

    /// <summary>Grava a versão mais recente como "vista" (chamado quando a aba Novidades abre).</summary>
    public void MarkAllSeen()
    {
        string? latest = ChangelogVersioning.Latest(Entries.Select(e => e.Version));
        if (latest is null)
        {
            return;
        }

        AppSettings settings = _store.Load();
        _store.Save(settings with { LastSeenChangelogVersion = latest });
    }
}
```

- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 6: Commit** — `feat(gui): ChangelogViewModel (novidades + marca visto)`.

---

### Task 4: Badge de novidades no avatar

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/BrowserViewModel.cs`
- Modify: `src/RemoteOps.Desktop/Views/BrowserView.xaml`
- Modify: `src/RemoteOps.Desktop/MainWindow.xaml.cs:111-115`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/BrowserViewModelChangelogTests.cs`

**Interfaces:**
- Consumes: `IChangelogSource`, `ChangelogVersioning`, `ISettingsStore`.
- Produces: `BrowserViewModel.HasUnreadChangelog` (bool); `BrowserViewModel.RefreshChangelogBadge()`. Ctor ganha dois parâmetros opcionais `IChangelogSource? changelogSource = null, ISettingsStore? settingsStore = null`.

- [ ] **Step 1: Teste que falha** — `BrowserViewModelChangelogTests.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class BrowserViewModelChangelogTests
{
    private sealed class FakeSource : IChangelogSource
    {
        private readonly IReadOnlyList<ChangelogEntry> _e;
        public FakeSource(params string[] v) => _e = v.Select(x => new ChangelogEntry { Version = x }).ToList();
        public IReadOnlyList<ChangelogEntry> Load() => _e;
    }

    private static (BrowserViewModel vm, JsonSettingsStore store) Build(JsonSettingsStore store)
    {
        var logs = new LogsViewModel();
        var hosts = new HostsViewModel(new InMemoryLocalStore(), null!, "ws-local");
        var keychain = new KeychainViewModel(new InMemoryLocalStore(), new RemoteOps.UnitTests.Desktop.ViewModels.FakeVault(), "ws-local");
        var vm = new BrowserViewModel(hosts, keychain, logs, new FakeSource("1.0.0"), store);
        return (vm, store);
    }

    [Fact]
    public void UnreadWhenNeverSeen_ClearsAfterSeen()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json"));
        var (vm, _) = Build(store);
        Assert.True(vm.HasUnreadChangelog);

        store.Save(new AppSettings { LastSeenChangelogVersion = "1.0.0" });
        vm.RefreshChangelogBadge();
        Assert.False(vm.HasUnreadChangelog);
    }
}
```
> `HostsViewModel`/`KeychainViewModel`/`FakeVault` já existem no projeto de teste (usados por outros testes). `SessionLauncher` pode ser `null!` porque o teste não abre sessão.

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar** — em `BrowserViewModel.cs`, trocar ctor e adicionar membros. Novo ctor e campos:
```csharp
    private readonly IChangelogSource? _changelogSource;
    private readonly ISettingsStore? _settingsStore;

    public BrowserViewModel(
        HostsViewModel hosts,
        KeychainViewModel keychain,
        LogsViewModel logs,
        IChangelogSource? changelogSource = null,
        ISettingsStore? settingsStore = null)
    {
        Hosts = hosts;
        Keychain = keychain;
        Logs = logs;
        _changelogSource = changelogSource;
        _settingsStore = settingsStore;
        ShowHostsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Hosts);
        ShowKeychainCommand = new RelayCommand(() => { ActiveSection = BrowserSection.Keychain; _ = keychain.LoadAsync(); });
        ShowLogsCommand = new RelayCommand(() => ActiveSection = BrowserSection.Logs);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke(this, EventArgs.Empty));
        CheckUpdatesCommand = new RelayCommand(() => UpdatesRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
    }
```
Adicionar `using RemoteOps.Desktop.Changelog;` e `using RemoteOps.Desktop.Infrastructure;` no topo. Depois das propriedades `IsHosts/IsKeychain/IsLogs`, adicionar:
```csharp
    /// <summary>Há versão de changelog não vista? (pontinho no avatar).</summary>
    public bool HasUnreadChangelog
    {
        get
        {
            if (_changelogSource is null || _settingsStore is null)
            {
                return false;
            }

            string? latest = ChangelogVersioning.Latest(
                System.Linq.Enumerable.Select(_changelogSource.Load(), e => e.Version));
            return latest is not null && ChangelogVersioning.IsNewer(latest, _settingsStore.Load().LastSeenChangelogVersion);
        }
    }

    /// <summary>Reavalia o badge (chamar quando o modal de Configurações fecha).</summary>
    public void RefreshChangelogBadge() => RaisePropertyChanged(nameof(HasUnreadChangelog));
```

- [ ] **Step 4: Rodar e ver passar** (o teste da unidade).

- [ ] **Step 5: Badge no XAML** — em `BrowserView.xaml`, no `<Button ... Click="AvatarButton_Click">` do avatar (linhas ~54-94), envolver o conteúdo num `Grid` com um pontinho no canto. Substituir o filho `<TextBlock Text="{DynamicResource Icon.Person}" .../>` por:
```xml
                    <Grid>
                        <TextBlock Text="{DynamicResource Icon.Person}"
                                   FontFamily="{DynamicResource Font.Family.Icons}"
                                   Foreground="{DynamicResource Brush.Text.Primary}"
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"/>
                        <Ellipse Width="9" Height="9"
                                 HorizontalAlignment="Right" VerticalAlignment="Top"
                                 Margin="0,-2,-2,0"
                                 Fill="{DynamicResource Brush.Accent.Base}"
                                 Stroke="{DynamicResource Brush.Bg.Surface}" StrokeThickness="1.5"
                                 ToolTip="Há novidades"
                                 Visibility="{Binding HasUnreadChangelog, Converter={StaticResource BoolToVis}}"/>
                    </Grid>
```
> `BoolToVis` já está em `UserControl.Resources` de `BrowserView.xaml`. O `DataContext` do `BrowserView` é o `BrowserViewModel`, então `HasUnreadChangelog` resolve.

- [ ] **Step 6: Refresh ao fechar Configurações** — em `MainWindow.xaml.cs`, método `OpenSettings()`:
```csharp
    private void OpenSettings()
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
        Vm.Browser.RefreshChangelogBadge();
    }
```

- [ ] **Step 7: Build** 0/0 + rodar o teste da unidade de novo.
- [ ] **Step 8: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 9: Commit** — `feat(gui): badge de novidades no avatar`.

---

### Task 5: `DiagnosticsProvider`

**Files:**
- Create: `src/RemoteOps.Desktop/Reporting/IDiagnosticsProvider.cs`
- Create: `src/RemoteOps.Desktop/Reporting/DiagnosticsProvider.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Reporting/DiagnosticsProviderTests.cs`

**Interfaces:**
- Consumes: `LogsViewModel` (`Events`, `Emit`).
- Produces: `IDiagnosticsProvider.BuildDiagnostics() -> string`; `DiagnosticsProvider(LogsViewModel logs, string appVersion, string osDescription, string? deviceId)`.

- [ ] **Step 1: Teste que falha** — `DiagnosticsProviderTests.cs`:
```csharp
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Reporting;

public sealed class DiagnosticsProviderTests
{
    [Fact]
    public void BuildDiagnostics_IncludesVersionOsDeviceAndRecentLogs()
    {
        var logs = new LogsViewModel();
        logs.Emit("evento A");
        logs.Emit("evento B");
        var provider = new DiagnosticsProvider(logs, "1.2.3", "Windows 11", "dev-42");

        string text = provider.BuildDiagnostics();

        Assert.Contains("1.2.3", text);
        Assert.Contains("Windows 11", text);
        Assert.Contains("dev-42", text);
        Assert.Contains("evento A", text);
        Assert.Contains("evento B", text);
    }

    [Fact]
    public void BuildDiagnostics_NoDeviceId_OmitsDeviceLine()
    {
        var provider = new DiagnosticsProvider(new LogsViewModel(), "1.0.0", "Windows", deviceId: null);
        Assert.DoesNotContain("Device:", provider.BuildDiagnostics());
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar `IDiagnosticsProvider.cs`:**
```csharp
namespace RemoteOps.Desktop.Reporting;

public interface IDiagnosticsProvider
{
    /// <summary>Bloco de diagnóstico secret-free (versão, SO, device id, últimas N linhas de log).</summary>
    string BuildDiagnostics();
}
```

- [ ] **Step 4: Implementar `DiagnosticsProvider.cs`:**
```csharp
using System.Linq;
using System.Text;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Reporting;

/// <summary>Monta o diagnóstico anexável ao bug report. Só fontes secret-free.</summary>
public sealed class DiagnosticsProvider : IDiagnosticsProvider
{
    private const int MaxLogLines = 30;
    private readonly LogsViewModel _logs;
    private readonly string _appVersion;
    private readonly string _osDescription;
    private readonly string? _deviceId;

    public DiagnosticsProvider(LogsViewModel logs, string appVersion, string osDescription, string? deviceId)
    {
        _logs = logs;
        _appVersion = appVersion;
        _osDescription = osDescription;
        _deviceId = deviceId;
    }

    public string BuildDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"App: RemoteOps Desktop {_appVersion}");
        sb.AppendLine($"SO: {_osDescription}");
        if (!string.IsNullOrWhiteSpace(_deviceId))
        {
            sb.AppendLine($"Device: {_deviceId}");
        }

        sb.AppendLine();
        sb.AppendLine($"Últimas {MaxLogLines} linhas de log:");
        foreach (string line in _logs.Events.Take(MaxLogLines))
        {
            sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 5: Rodar e ver passar.**
- [ ] **Step 6: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 7: Commit** — `feat(gui): DiagnosticsProvider (bloco secret-free)`.

---

### Task 6: Bug report composer (`mailto:` + cópia local)

**Files:**
- Create: `src/RemoteOps.Desktop/Reporting/SupportContact.cs`
- Create: `src/RemoteOps.Desktop/Reporting/BugReport.cs`
- Create: `src/RemoteOps.Desktop/Reporting/IBugReportComposer.cs`
- Create: `src/RemoteOps.Desktop/Reporting/MailtoBugReportComposer.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Reporting/MailtoBugReportComposerTests.cs`

**Interfaces:**
- Consumes: `IDiagnosticsProvider`.
- Produces: `SupportContact.Email` (const); `BugReport(string Title, string Description, bool IncludeDiagnostics)`; `IBugReportComposer` com `string BuildPreview(BugReport)`, `Uri BuildMailtoUri(BugReport)`, `Task<string> SaveLocalCopyAsync(BugReport)`; `MailtoBugReportComposer(IDiagnosticsProvider diagnostics, string? bugReportsDir = null)` + `internal string BuildMailtoBody(BugReport)`.

- [ ] **Step 1: Teste que falha** — `MailtoBugReportComposerTests.cs`:
```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using RemoteOps.Desktop.Reporting;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Reporting;

public sealed class MailtoBugReportComposerTests
{
    private sealed class FakeDiagnostics : IDiagnosticsProvider
    {
        private readonly string _text;
        public FakeDiagnostics(string text) => _text = text;
        public string BuildDiagnostics() => _text;
    }

    [Fact]
    public void BuildPreview_IncludesDescription_AndDiagnosticsWhenOptedIn()
    {
        var c = new MailtoBugReportComposer(new FakeDiagnostics("DIAG-BLOCK"));
        string withDiag = c.BuildPreview(new BugReport("t", "minha descrição", IncludeDiagnostics: true));
        string without = c.BuildPreview(new BugReport("t", "minha descrição", IncludeDiagnostics: false));
        Assert.Contains("minha descrição", withDiag);
        Assert.Contains("DIAG-BLOCK", withDiag);
        Assert.DoesNotContain("DIAG-BLOCK", without);
    }

    [Fact]
    public void BuildMailtoUri_IsMailto_ToSupport_WithEncodedSubject()
    {
        var c = new MailtoBugReportComposer(new FakeDiagnostics(""));
        Uri uri = c.BuildMailtoUri(new BugReport("Falha no WinBox", "x", IncludeDiagnostics: false));
        Assert.StartsWith("mailto:" + SupportContact.Email, uri.ToString());
        Assert.Contains(Uri.EscapeDataString("[RemoteOps] Falha no WinBox"), uri.ToString());
    }

    [Fact]
    public void BuildMailtoBody_TruncatesDiagnostics_ButKeepsDescription()
    {
        var c = new MailtoBugReportComposer(new FakeDiagnostics(new string('D', 5000)));
        string body = c.BuildMailtoBody(new BugReport("t", "DESCRICAO-INTACTA", IncludeDiagnostics: true));
        Assert.Contains("DESCRICAO-INTACTA", body);
        Assert.Contains("diagnóstico truncado", body);
        Assert.True(body.Length < 2000);
    }

    [Fact]
    public async Task SaveLocalCopyAsync_WritesFileWithTitleAndDescription()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var c = new MailtoBugReportComposer(new FakeDiagnostics("D"), dir);
        string path = await c.SaveLocalCopyAsync(new BugReport("meu-titulo", "meu-corpo", IncludeDiagnostics: true));
        Assert.True(File.Exists(path));
        string content = await File.ReadAllTextAsync(path);
        Assert.Contains("meu-titulo", content);
        Assert.Contains("meu-corpo", content);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar `SupportContact.cs`:**
```csharp
namespace RemoteOps.Desktop.Reporting;

/// <summary>Endereço de suporte para onde os bug reports são abertos (mailto:).</summary>
public static class SupportContact
{
    public const string Email = "suporte@innet.tec.br";
}
```

- [ ] **Step 4: Implementar `BugReport.cs`:**
```csharp
namespace RemoteOps.Desktop.Reporting;

public sealed record BugReport(string Title, string Description, bool IncludeDiagnostics);
```

- [ ] **Step 5: Implementar `IBugReportComposer.cs`:**
```csharp
using System;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Reporting;

public interface IBugReportComposer
{
    /// <summary>Texto completo do report (o que o operador vê no preview e o que é salvo localmente).</summary>
    string BuildPreview(BugReport report);

    /// <summary>URI mailto: para o suporte (diagnóstico truncado se estourar o limite; descrição intacta).</summary>
    Uri BuildMailtoUri(BugReport report);

    /// <summary>Salva a cópia completa em %AppData%\RemoteOps\bug-reports\; devolve o caminho.</summary>
    Task<string> SaveLocalCopyAsync(BugReport report);
}
```

- [ ] **Step 6: Implementar `MailtoBugReportComposer.cs`:**
```csharp
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Reporting;

/// <summary>Compõe o bug report como e-mail pré-preenchido (mailto:) + cópia local. Sem rede.</summary>
public sealed class MailtoBugReportComposer : IBugReportComposer
{
    private const int MaxMailtoBodyChars = 1500;
    private const string TruncationNote = "\n[diagnóstico truncado — cópia completa salva localmente]";
    private readonly IDiagnosticsProvider _diagnostics;
    private readonly string _bugReportsDir;

    public MailtoBugReportComposer(IDiagnosticsProvider diagnostics, string? bugReportsDir = null)
    {
        _diagnostics = diagnostics;
        _bugReportsDir = bugReportsDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteOps",
            "bug-reports");
    }

    public string BuildPreview(BugReport report) => BuildFullBody(report);

    public Uri BuildMailtoUri(BugReport report)
    {
        string subject = "[RemoteOps] " + report.Title;
        string body = BuildMailtoBody(report);
        string url = $"mailto:{SupportContact.Email}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";
        return new Uri(url);
    }

    public async Task<string> SaveLocalCopyAsync(BugReport report)
    {
        Directory.CreateDirectory(_bugReportsDir);
        string fileName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt";
        string path = Path.Combine(_bugReportsDir, fileName);
        string content = $"Título: {report.Title}\n\n{BuildFullBody(report)}";
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    /// <summary>Corpo completo (descrição + diagnóstico, sem truncar). Preview e cópia local usam isto.</summary>
    private string BuildFullBody(BugReport report)
    {
        var sb = new StringBuilder();
        sb.Append(report.Description);
        if (report.IncludeDiagnostics)
        {
            sb.Append("\n\n--- Diagnósticos ---\n");
            sb.Append(_diagnostics.BuildDiagnostics());
        }

        return sb.ToString();
    }

    /// <summary>Corpo do mailto: descrição nunca truncada; só o diagnóstico é cortado ao orçamento.</summary>
    internal string BuildMailtoBody(BugReport report)
    {
        string head = report.Description;
        if (!report.IncludeDiagnostics)
        {
            return head;
        }

        string diagSection = "\n\n--- Diagnósticos ---\n" + _diagnostics.BuildDiagnostics();
        int budget = MaxMailtoBodyChars - head.Length;
        if (budget <= 0)
        {
            return head;
        }

        if (diagSection.Length > budget)
        {
            return head + diagSection[..budget] + TruncationNote;
        }

        return head + diagSection;
    }
}
```

- [ ] **Step 7: Rodar e ver passar.**
- [ ] **Step 8: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 9: Commit** — `feat(gui): MailtoBugReportComposer (mailto + copia local)`.

---

### Task 7: `BugReportViewModel`

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/BugReportViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/BugReportViewModelTests.cs`

**Interfaces:**
- Consumes: `IBugReportComposer`, `BugReport`.
- Produces: `BugReportViewModel(IBugReportComposer composer, Action<Uri>? openMailto = null, Action<string>? copyToClipboard = null)`; props `Title`, `Description`, `IncludeDiagnostics`, `PreviewText`, `StatusMessage`; `SubmitCommand`, `PreviewCommand`, `CopyCommand`.

- [ ] **Step 1: Teste que falha** — `BugReportViewModelTests.cs`:
```csharp
using System;
using System.Threading.Tasks;
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class BugReportViewModelTests
{
    private sealed class FakeComposer : IBugReportComposer
    {
        public bool ThrowOnSave;
        public string BuildPreview(BugReport r) => "PREVIEW:" + r.Description;
        public Uri BuildMailtoUri(BugReport r) => new("mailto:suporte@innet.tec.br?subject=x");
        public Task<string> SaveLocalCopyAsync(BugReport r)
            => ThrowOnSave ? throw new System.IO.IOException("disk") : Task.FromResult("C:/tmp/r.txt");
    }

    [Fact]
    public void CanSubmit_RequiresTitleAndDescription()
    {
        var vm = new BugReportViewModel(new FakeComposer());
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.Title = "t";
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.Description = "d";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Submit_OpensMailto_EvenIfSaveFails()
    {
        Uri? opened = null;
        var composer = new FakeComposer { ThrowOnSave = true };
        var vm = new BugReportViewModel(composer, uri => opened = uri, _ => { })
        {
            Title = "t",
            Description = "d",
        };
        await vm.SubmitForTestAsync();
        Assert.NotNull(opened);
        Assert.StartsWith("mailto:", opened!.ToString());
    }

    [Fact]
    public void Copy_RefreshesPreview_AndCopies()
    {
        string? copied = null;
        var vm = new BugReportViewModel(new FakeComposer(), _ => { }, text => copied = text)
        {
            Description = "abc",
        };
        vm.CopyCommand.Execute(null);
        Assert.Equal("PREVIEW:abc", copied);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: Implementar `BugReportViewModel.cs`:**
```csharp
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using RemoteOps.Desktop.Reporting;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>Aba "Reportar problema": compõe o report e abre um e-mail pré-preenchido ao suporte.</summary>
public sealed class BugReportViewModel : BaseViewModel
{
    private readonly IBugReportComposer _composer;
    private readonly Action<Uri> _openMailto;
    private readonly Action<string> _copyToClipboard;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _previewText = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _includeDiagnostics = true;

    public BugReportViewModel(
        IBugReportComposer composer,
        Action<Uri>? openMailto = null,
        Action<string>? copyToClipboard = null)
    {
        _composer = composer;
        _openMailto = openMailto ?? (uri => Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }));
        _copyToClipboard = copyToClipboard ?? (text => System.Windows.Clipboard.SetText(text));
        SubmitCommand = new RelayCommand(() => _ = SubmitForTestAsync(), CanSubmit);
        PreviewCommand = new RelayCommand(RefreshPreview);
        CopyCommand = new RelayCommand(() =>
        {
            RefreshPreview();
            _copyToClipboard(PreviewText);
            StatusMessage = "Copiado para a área de transferência.";
        });
    }

    public string Title
    {
        get => _title;
        set { Set(ref _title, value); SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public string Description
    {
        get => _description;
        set { Set(ref _description, value); SubmitCommand.RaiseCanExecuteChanged(); }
    }

    public bool IncludeDiagnostics { get => _includeDiagnostics; set => Set(ref _includeDiagnostics, value); }
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public RelayCommand SubmitCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand CopyCommand { get; }

    private bool CanSubmit() => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(Description);
    private BugReport Report => new(Title.Trim(), Description.Trim(), IncludeDiagnostics);
    private void RefreshPreview() => PreviewText = _composer.BuildPreview(Report);

    /// <summary>Salva a cópia local e abre o mailto. Público para teste; a UI chama via SubmitCommand.</summary>
    public async Task SubmitForTestAsync()
    {
        if (!CanSubmit())
        {
            return;
        }

        BugReport report = Report;
        try
        {
            await _composer.SaveLocalCopyAsync(report);
        }
        catch (Exception)
        {
            // Cópia local é best-effort; o e-mail ainda pode ser enviado.
        }

        try
        {
            _openMailto(_composer.BuildMailtoUri(report));
            StatusMessage = "Abrindo seu e-mail…";
        }
        catch (Exception)
        {
            StatusMessage = "Não foi possível abrir o e-mail. Use “Copiar” e cole no seu cliente.";
        }
    }
}
```

- [ ] **Step 4: Rodar e ver passar.**
- [ ] **Step 5: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 6: Commit** — `feat(gui): BugReportViewModel (submit independente + preview)`.

---

### Task 8: Integração — SettingsViewModel expõe os VMs + DI

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/SettingsViewModel.cs`
- Modify: `src/RemoteOps.Desktop/ViewModels/WorkspaceViewModel.cs`
- Modify: `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/SettingsViewModelChildrenTests.cs`

**Interfaces:**
- Consumes: `ChangelogViewModel`, `BugReportViewModel`, `IChangelogSource`, `IBugReportComposer`, `IDiagnosticsProvider`, `DiagnosticsProvider`, `MailtoBugReportComposer`, `EmbeddedChangelogSource`.
- Produces: `SettingsViewModel.Changelog` (`ChangelogViewModel?`), `SettingsViewModel.BugReport` (`BugReportViewModel?`); ctor ganha dois opcionais no fim. `WorkspaceViewModel` ctor ganha `IChangelogSource? changelogSource = null, IBugReportComposer? bugReportComposer = null`.

- [ ] **Step 1: Teste que falha** — `SettingsViewModelChildrenTests.cs`:
```csharp
using System.IO;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelChildrenTests
{
    private sealed class FakeDiag : IDiagnosticsProvider { public string BuildDiagnostics() => "d"; }

    [Fact]
    public void Exposes_Changelog_And_BugReport_WhenProvided()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "s.json"));
        var changelog = new ChangelogViewModel(new EmbeddedChangelogSource(), store);
        var bug = new BugReportViewModel(new MailtoBugReportComposer(new FakeDiag()));
        var vm = new SettingsViewModel(store, updateService: null, changelog: changelog, bugReport: bug);
        Assert.Same(changelog, vm.Changelog);
        Assert.Same(bug, vm.BugReport);
    }

    [Fact]
    public void OldCtor_StillWorks_ChildrenNull()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "s.json"));
        var vm = new SettingsViewModel(store);
        Assert.Null(vm.Changelog);
        Assert.Null(vm.BugReport);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar.**

- [ ] **Step 3: `SettingsViewModel.cs`** — trocar a assinatura do ctor e adicionar as propriedades. Ctor:
```csharp
    public SettingsViewModel(
        ISettingsStore store,
        IUpdateService? updateService = null,
        ChangelogViewModel? changelog = null,
        BugReportViewModel? bugReport = null)
    {
        _store = store;
        _updateService = updateService;
        Changelog = changelog;
        BugReport = bugReport;
        _settings = store.Load();
```
(resto do corpo do ctor inalterado). Adicionar as propriedades junto das demais:
```csharp
    /// <summary>Aba "Novidades" (pode ser null em testes que não injetam os filhos).</summary>
    public ChangelogViewModel? Changelog { get; }

    /// <summary>Aba "Reportar problema" (pode ser null em testes que não injetam os filhos).</summary>
    public BugReportViewModel? BugReport { get; }
```

- [ ] **Step 4: `WorkspaceViewModel.cs`** — threading. Novo ctor + campos:
```csharp
    private readonly ISettingsStore? _settingsStore;
    private readonly IUpdateService? _updateService;
    private readonly IChangelogSource? _changelogSource;
    private readonly IBugReportComposer? _bugReportComposer;
    private string _syncStatus = "Offline";

    public WorkspaceViewModel(
        BrowserViewModel browser,
        TabsViewModel tabs,
        ISettingsStore? settingsStore = null,
        IUpdateService? updateService = null,
        IChangelogSource? changelogSource = null,
        IBugReportComposer? bugReportComposer = null)
    {
        Browser = browser;
        Tabs = tabs;
        _settingsStore = settingsStore;
        _updateService = updateService;
        _changelogSource = changelogSource;
        _bugReportComposer = bugReportComposer;
    }
```
Adicionar `using RemoteOps.Desktop.Changelog;` e `using RemoteOps.Desktop.Reporting;`. Trocar `CreateSettingsViewModel`:
```csharp
    public SettingsViewModel CreateSettingsViewModel()
    {
        ISettingsStore store = _settingsStore ?? new JsonSettingsStore();
        ChangelogViewModel? changelog = _changelogSource is null ? null : new ChangelogViewModel(_changelogSource, store);
        BugReportViewModel? bugReport = _bugReportComposer is null ? null : new BugReportViewModel(_bugReportComposer);
        return new SettingsViewModel(store, _updateService, changelog, bugReport);
    }
```

- [ ] **Step 5: `AppCompositionRoot.cs`** — registrar os serviços. Junto ao registro de `LogsViewModel` (após linha 122), adicionar:
```csharp
        // Changelog (offline) + bug report (mailto) — Fase 1, sem rede.
        services.AddSingleton<Changelog.IChangelogSource, Changelog.EmbeddedChangelogSource>();
        services.AddSingleton<Reporting.IDiagnosticsProvider>(sp => new Reporting.DiagnosticsProvider(
            sp.GetRequiredService<ViewModels.LogsViewModel>(),
            typeof(AppCompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "?",
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ReadDeviceId()));
        services.AddSingleton<Reporting.IBugReportComposer, Reporting.MailtoBugReportComposer>();
```
E adicionar um helper privado na classe (perto de `BuildWinBoxManifest`):
```csharp
    private static string? ReadDeviceId()
    {
        try
        {
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RemoteOps", "device.id");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path).Trim() : null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }
```
> `WorkspaceViewModel` e `BrowserViewModel` são registrados por auto-ctor (`AddSingleton<...>()`), então os novos parâmetros são resolvidos automaticamente a partir dos serviços acima + `ISettingsStore` (já registrado). `MailtoBugReportComposer` também é auto-ctor (só depende de `IDiagnosticsProvider`).

- [ ] **Step 6: Rodar e ver passar** + `dotnet build` 0/0 + rodar `CompositionRootSmokeTests` (`dotnet test … --filter "FullyQualifiedName~CompositionRootSmokeTests" --nologo`) para confirmar que o grafo resolve.
- [ ] **Step 7: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 8: Commit** — `feat(gui): SettingsViewModel expoe Changelog/BugReport + DI`.

---

### Task 9: `SettingsWindow` — abas "Novidades" e "Reportar problema"

**Files:**
- Modify: `src/RemoteOps.Desktop/Views/SettingsWindow.xaml`
- Modify: `src/RemoteOps.Desktop/Views/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: `SettingsViewModel.Changelog` (`Entries`, `HasEntries`, `MarkAllSeen()`), `SettingsViewModel.BugReport` (`Title`, `Description`, `IncludeDiagnostics`, `PreviewText`, `SubmitCommand`, `PreviewCommand`, `CopyCommand`, `StatusMessage`).

- [ ] **Step 1: XAML das abas** — em `SettingsWindow.xaml`, dentro do `<TabControl>`, **antes** da `<TabItem Header="Atualização">`, inserir as duas abas:
```xml
            <TabItem Header="Novidades">
                <ScrollViewer VerticalScrollBarVisibility="Auto" Margin="12">
                    <StackPanel>
                        <TextBlock Text="Sem novidades para mostrar."
                                   Foreground="{DynamicResource Brush.Text.Secondary}"
                                   Visibility="{Binding Changelog.HasEntries, Converter={StaticResource InverseBoolToVis}}"/>
                        <ItemsControl ItemsSource="{Binding Changelog.Entries}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Border Margin="0,0,0,12" Padding="12"
                                            Background="{DynamicResource Brush.Bg.Canvas}"
                                            BorderBrush="{DynamicResource Brush.Border.Subtle}"
                                            BorderThickness="1"
                                            CornerRadius="{DynamicResource Radius.Control}">
                                        <StackPanel>
                                            <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                                                <TextBlock Text="{Binding Version}" FontWeight="SemiBold"/>
                                                <TextBlock Text="{Binding Date}" Margin="8,0,0,0"
                                                           Foreground="{DynamicResource Brush.Text.Tertiary}"/>
                                                <Border Margin="8,0,0,0" Padding="6,1"
                                                        Background="{DynamicResource Brush.Accent.Muted}"
                                                        CornerRadius="8"
                                                        Visibility="{Binding IsNew, Converter={StaticResource BoolToVis}}">
                                                    <TextBlock Text="novo" FontSize="10"
                                                               Foreground="{DynamicResource Brush.Accent.Base}"/>
                                                </Border>
                                            </StackPanel>
                                            <ItemsControl ItemsSource="{Binding Highlights}">
                                                <ItemsControl.ItemTemplate>
                                                    <DataTemplate>
                                                        <TextBlock Text="{Binding}" TextWrapping="Wrap" Margin="0,1"
                                                                   Foreground="{DynamicResource Brush.Text.Secondary}"/>
                                                    </DataTemplate>
                                                </ItemsControl.ItemTemplate>
                                            </ItemsControl>
                                        </StackPanel>
                                    </Border>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
            <TabItem Header="Reportar problema">
                <ScrollViewer VerticalScrollBarVisibility="Auto" Margin="12">
                    <StackPanel DataContext="{Binding BugReport}">
                        <TextBlock Text="Título" FontWeight="SemiBold" Margin="0,0,0,4"/>
                        <TextBox Text="{Binding Title, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,10"/>
                        <TextBlock Text="Descrição" FontWeight="SemiBold" Margin="0,0,0,4"/>
                        <TextBox Text="{Binding Description, UpdateSourceTrigger=PropertyChanged}"
                                 AcceptsReturn="True" TextWrapping="Wrap" MinHeight="90"
                                 VerticalScrollBarVisibility="Auto" Margin="0,0,0,10"/>
                        <CheckBox Content="Incluir diagnósticos (versão, sistema, últimas linhas de log)"
                                  IsChecked="{Binding IncludeDiagnostics}" Margin="0,0,0,8"/>
                        <Expander x:Name="Preview" Header="Ver o que será anexado" Margin="0,0,0,10"
                                  Expanded="Preview_Expanded">
                            <TextBox Text="{Binding PreviewText, Mode=OneWay}" IsReadOnly="True"
                                     FontFamily="Consolas" TextWrapping="Wrap"
                                     MinHeight="80" MaxHeight="160" VerticalScrollBarVisibility="Auto"/>
                        </Expander>
                        <StackPanel Orientation="Horizontal">
                            <Button Content="Enviar por e-mail" Command="{Binding SubmitCommand}"
                                    Style="{DynamicResource Button.Primary}" Padding="10,4" Margin="0,0,8,0"/>
                            <Button Content="Copiar" Command="{Binding CopyCommand}" Padding="10,4"/>
                        </StackPanel>
                        <TextBlock Text="{Binding StatusMessage}" Margin="0,8,0,0"
                                   Foreground="{DynamicResource Brush.Text.Secondary}" TextWrapping="Wrap"/>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
```
> `Expander` não tem `Command`: o preview é atualizado pelo handler `Preview_Expanded` do code-behind (Step 3). `Button.Primary` já existe como recurso do tema (usado em `HostEditorDialog.xaml`/`CredentialDialog.xaml`).

- [ ] **Step 2: Converter `InverseBoolToVis`** — o empty-state usa um conversor de bool invertido. Verificar se já existe um recurso `InverseBoolToVis` no tema; se **não** existir, trocar a linha do empty-state por um `Style` com `DataTrigger` (sem conversor novo):
```xml
                        <TextBlock Text="Sem novidades para mostrar."
                                   Foreground="{DynamicResource Brush.Text.Secondary}">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Visibility" Value="Collapsed"/>
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Changelog.HasEntries}" Value="False">
                                            <Setter Property="Visibility" Value="Visible"/>
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
```
> Usar esta variante com `DataTrigger` (não depende de `InverseBoolToVis`). `BoolToVis` padrão do WPF (`BooleanToVisibilityConverter`) precisa estar declarado nos `Window.Resources` do `SettingsWindow.xaml`; se não estiver, adicionar `<BooleanToVisibilityConverter x:Key="BoolToVis"/>` em `<Window.Resources>` (criar o bloco se não existir).

- [ ] **Step 3: Code-behind** — em `SettingsWindow.xaml.cs`, marcar visto ao abrir a aba Novidades e atualizar o preview ao expandir. Dar `x:Name="Tabs"` ao `<TabControl>` no XAML e `x:Name="Preview"` ao `<Expander>`; adicionar handlers:
```csharp
    private void Tabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0
            && e.AddedItems[0] is System.Windows.Controls.TabItem { Header: "Novidades" })
        {
            Vm.Changelog?.MarkAllSeen();
        }
    }

    private void Preview_Expanded(object sender, System.Windows.RoutedEventArgs e)
        => Vm.BugReport?.PreviewCommand.Execute(null);
```
No XAML, ligar: `<TabControl x:Name="Tabs" SelectionChanged="Tabs_SelectionChanged">` e `<Expander x:Name="Preview" Header="Ver o que será anexado" Expanded="Preview_Expanded" …>`.

- [ ] **Step 4: Build** 0/0. Smoke manual opcional: abrir Configurações → aba Novidades mostra a 1.0.0; aba Reportar problema compõe e o botão abre o cliente de e-mail.
- [ ] **Step 5: `dotnet format` + `--verify-no-changes`.**
- [ ] **Step 6: Commit** — `feat(gui): abas Novidades e Reportar problema no modal de Configuracoes`.

---

### Task 10: Validação final

**Files:** nenhuma alteração de código.

- [ ] **Step 1: Suíte completa** — `dotnet test "C:\dev\remoteops-changelog-bugreport\RemoteOps.sln" -c Release --nologo` → 0/0, verde (437 + novos).
- [ ] **Step 2: Format gate** — `dotnet format --verify-no-changes --verbosity diagnostic` → exit 0 (mesmo gate do CI).
- [ ] **Step 3: Smoke manual** — app abre; **Configurações → Novidades** mostra a 1.0.0 com chip "novo" na primeira vez e o pontinho no avatar some após visitar; **Configurações → Reportar problema** compõe título/descrição, o checkbox de diagnósticos alterna o preview, **Enviar por e-mail** abre o cliente com `suporte@innet.tec.br` preenchido e uma cópia aparece em `%AppData%\RemoteOps\bug-reports\`.
- [ ] **Step 4: finishing-a-development-branch** — usar o skill; push + PR (base `main`, **Depends-on #44**); o usuário faz o merge.

---

## Self-Review (executada na escrita)

**Cobertura da spec:** §Novidades (dado embutido → Task 2; VM → Task 3; badge → Task 4; aba → Task 9) · §Reportar problema (diagnóstico → Task 5; composer mailto+cópia → Task 6; VM → Task 7; aba → Task 9) · §AppSettings.LastSeenChangelogVersion → Task 1 · §Colocação (abas no modal) → Task 9 · §DI/integração → Task 8 · §segurança (opt-in/preview, sem segredo) → Tasks 5,6,7,9 · §testes → cada task TDD + Task 10.

**Placeholders:** os únicos pontos de verificação-em-runtime são: (a) a existência de um `BoolToVis`/bloco `<Window.Resources>` em `SettingsWindow.xaml` (Task 9 Step 2 dá a instrução exata de criar se faltar) e (b) `Button.Primary` como recurso do tema (já usado em `HostEditorDialog.xaml`/`CredentialDialog.xaml`, confirmado existir). Não há TODO/TBD.

**Consistência de tipos:** `ChangelogVersioning.IsNewer/Latest` (Task 2) usados em Tasks 3,4 · `IChangelogSource.Load()` (Task 2) em Tasks 3,4,8 · `ChangelogViewModel(IChangelogSource, ISettingsStore)` (Task 3) em Task 8 · `IBugReportComposer.{BuildPreview,BuildMailtoUri,SaveLocalCopyAsync}` (Task 6) em Task 7 · `BugReportViewModel(IBugReportComposer, Action<Uri>?, Action<string>?)` (Task 7) em Task 8 · `SettingsViewModel(… , ChangelogViewModel?, BugReportViewModel?)` (Task 8) casa com `CreateSettingsViewModel` (Task 8) e com o XAML (Task 9) · `AppSettings.LastSeenChangelogVersion` (Task 1) consumido em Tasks 3,4.
