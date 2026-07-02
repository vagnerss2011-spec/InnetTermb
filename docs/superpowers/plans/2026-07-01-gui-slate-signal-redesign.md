# GUI RemoteOps "Slate Signal" + menus e Configurações — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Portar o design system dark "Slate Signal" para o RemoteOps.Desktop de release e adicionar barra de menus, menus de contexto e uma janela de Configurações com feature flags persistidas, mantendo build e testes verdes.

**Architecture:** Camada aditiva — copiar `Themes/` (tokens + estilos de controle) e ligar em `App.xaml`; re-skinar as views trocando cores fixas por `DynamicResource`; adicionar comandos de menu/contexto reusando o fluxo `OpenSessionRequest` existente (fonte única no Inspector); persistir settings em JSON com um `CompositeFeatureFlags` que mantém a variável de ambiente como override forte.

**Tech Stack:** .NET 10, WPF (net10.0-windows), Microsoft.Extensions.DependencyInjection 10, Velopack 1.2.0, xUnit 2.9.3, System.Text.Json.

**Worktree base:** `C:\dev\remoteops-gui-slate` (branch `feature/gui-slate-signal`, a partir de `feature/packaging-velopack-update`). Todos os caminhos abaixo são relativos a essa base salvo indicação.

**Spec:** `docs/superpowers/specs/2026-07-01-gui-slate-signal-redesign-design.md`

## Global Constraints

- Directory.Build.props (repo-wide): `Nullable=enable`, `LangVersion=latest`, **`TreatWarningsAsErrors=true`**, `ImplicitUsings=enable`, `EnforceCodeStyleInBuild=true`. Qualquer warning quebra o build.
- `RemoteOps.Desktop.csproj`: `UseWPF=true` + `UseWindowsForms=true`; `<Using Remove="System.Windows.Forms" />` (tipos WinForms exigem qualificação total); `App.xaml` é `<Page>` com `Main()` explícito (`StartupObject=RemoteOps.Desktop.App`); já referencia `Microsoft.Extensions.DependencyInjection` 10.0.0.
- Brushes do tema consumidos via **`DynamicResource`** (nunca `StaticResource`).
- Feature flags de ambiente (`REMOTEOPS_FEATURE_FLAGS`) permanecem **override forte**: settings só podem HABILITAR, nunca desabilitar o que o env habilitou.
- Copy/UI em **pt-BR**.
- Diretório de dados existente: `%AppData%\RemoteOps` (Roaming, onde já vive `vault.json`). `settings.json` vai no MESMO diretório.
- Testes: `tests/RemoteOps.UnitTests` (xUnit, `[Fact]`, `Assert.*`), namespaces espelham a pasta (`RemoteOps.UnitTests.Desktop.*`). `InternalsVisibleTo` já habilitado. Os **428 testes existentes devem continuar verdes**.
- Comando de build canônico: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`. Comando de teste: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`.

---

### Task 1: Portar o design system "Slate Signal"

**Files:**
- Create (copy): `src/RemoteOps.Desktop/Themes/**` (de `C:\dev\remoteops-gui-design\src\RemoteOps.Desktop\Themes`)
- Modify: `src/RemoteOps.Desktop/App.xaml`

**Interfaces:**
- Produces: os brushes/estilos `Brush.Bg.App`, `Brush.Bg.Surface`, `Brush.Bg.SurfaceRaised`, `Brush.Bg.Canvas`, `Brush.Border.Subtle`, `Brush.Border.Default`, `Brush.Text.Primary`, `Brush.Text.Secondary`, `Brush.Text.Tertiary`, `Brush.Status.Online/Pending/Error/Idle`, `Brush.Accent.Base`, e os estilos `Text.TitleLg`, `Text.Caption`, `Font.Family.Base`, `Font.Size.Body` — consumidos por todas as views nas tasks seguintes.

- [ ] **Step 1: Copiar a pasta de temas**

Run (PowerShell):
```powershell
Copy-Item -Recurse -Force `
  "C:\dev\remoteops-gui-design\src\RemoteOps.Desktop\Themes" `
  "C:\dev\remoteops-gui-slate\src\RemoteOps.Desktop\Themes"
```
Expected: cria `src/RemoteOps.Desktop/Themes/DarkTheme.xaml`, `Themes/Tokens/*.xaml` (Colors, Typography, Spacing, Icons), `Themes/Controls/*.xaml` (Buttons, TextInputs, TabControl, DataGrid, TreeView, ScrollBar, Misc).

- [ ] **Step 2: Ligar o tema no App.xaml**

Substituir o conteúdo de `src/RemoteOps.Desktop/App.xaml` por:
```xml
<Application x:Class="RemoteOps.Desktop.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Themes/DarkTheme.xaml"/>
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 3: Build para validar que os temas compilam como Page**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: `Compilação com êxito`, 0 erro / 0 aviso. (Se algum `.xaml` do tema não for incluído como Page, o SDK WPF já faz isso por padrão — não deve exigir mudança no csproj.)

- [ ] **Step 4: Commit**

```bash
git add src/RemoteOps.Desktop/Themes src/RemoteOps.Desktop/App.xaml
git commit -m "feat(gui): porta o design system Slate Signal (Themes/ + App.xaml)"
```

---

### Task 2: Re-skin do MainWindow (tokens)

**Files:**
- Modify: `src/RemoteOps.Desktop/MainWindow.xaml`

Troca as cores fixas por tokens. O layout (grid de 3 painéis) e os `Grid.Row/Column` permanecem.

- [ ] **Step 1: Aplicar os tokens no MainWindow**

No `<Window …>` adicionar os atributos de tema e trocar os backgrounds. Alterações exatas:

Na tag `<Window …>` (após `MinHeight/MinWidth`), acrescentar:
```xml
        Background="{DynamicResource Brush.Bg.App}"
        Foreground="{DynamicResource Brush.Text.Primary}"
        FontFamily="{DynamicResource Font.Family.Base}"
        FontSize="{DynamicResource Font.Size.Body}"
```

Barra superior `<Border Grid.Row="0" …>`: trocar `Background="#2D2D30" Padding="8,0"` por:
```xml
                Background="{DynamicResource Brush.Bg.SurfaceRaised}"
                BorderBrush="{DynamicResource Brush.Border.Subtle}"
                BorderThickness="0,0,0,1"
                Padding="8,0"
```

Título: trocar `Foreground="White" FontWeight="SemiBold" FontSize="14"` por `Style="{DynamicResource Text.TitleLg}"`.

Ponto de status (`<Ellipse … Fill="#999">`) e texto (`Foreground="#ccc" FontSize="11"`): substituir pelo bloco com `DataTrigger` de `Brush.Status.*` já existente no MainWindow do gui-design (`C:\dev\remoteops-gui-design\src\RemoteOps.Desktop\MainWindow.xaml` linhas 57-84) — copiar o `<StackPanel Grid.Column="2">…</StackPanel>` inteiro.

Sidebar: `Background="#F5F5F5" BorderBrush="#DDD"` → `Background="{DynamicResource Brush.Bg.Surface}" BorderBrush="{DynamicResource Brush.Border.Subtle}"`.

`HostListView`: adicionar `Background="{DynamicResource Brush.Bg.Surface}"`.

Todos os `GridSplitter` `Background="#DDD"` → `Background="{DynamicResource Brush.Border.Default}"`.

Área de abas `TabsView` `Background="#1E1E1E"` → `Background="{DynamicResource Brush.Bg.Canvas}"`.

Inspector `Background="#FAFAFA" BorderBrush="#DDD"` → `Background="{DynamicResource Brush.Bg.Surface}" BorderBrush="{DynamicResource Brush.Border.Subtle}"`.

- [ ] **Step 2: Build**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso, 0/0.

- [ ] **Step 3: Smoke visual**

Run:
```powershell
& "C:\dev\remoteops-gui-slate\src\RemoteOps.Desktop\bin\Debug\net10.0-windows\RemoteOps.Desktop.exe"
```
Expected: janela abre 100% dark coesa (sem áreas claras). Fechar em seguida.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteOps.Desktop/MainWindow.xaml
git commit -m "feat(gui): re-skin do MainWindow com tokens Slate Signal"
```

---

### Task 3: Re-skin das views (Sidebar, HostList, Inspector, Tabs)

**Files:**
- Modify: `src/RemoteOps.Desktop/Views/SidebarView.xaml`
- Modify: `src/RemoteOps.Desktop/Views/InspectorView.xaml`
- Modify: `src/RemoteOps.Desktop/Views/TabsView.xaml`
- Modify: `src/RemoteOps.Desktop/Views/HostListView.xaml` (só se tiver cor fixa; hoje não tem — deixar como está)

Mapa de substituição (aplicar a QUALQUER cor fixa encontrada):

| Cor fixa | Token |
|---|---|
| `#555` (títulos "Grupos"/"Inspector") | `{DynamicResource Brush.Text.Secondary}` |
| `#888` / `#888` itálico (placeholder, credencial) | `{DynamicResource Brush.Text.Tertiary}` |
| `#aaa` / `#bbb` (placeholders de aba) | `{DynamicResource Brush.Text.Tertiary}` |
| `Red` (WinBoxError) | `{DynamicResource Brush.Status.Error}` |
| `Transparent` (TreeView bg) | manter `Transparent` |

- [ ] **Step 1: SidebarView** — trocar `Foreground="#555"` (título "Grupos") por `Foreground="{DynamicResource Brush.Text.Secondary}"`. Demais controles (TextBox, Button, TreeView) já herdam os estilos implícitos do tema.

- [ ] **Step 2: InspectorView** — trocar: título "Inspector" `Foreground="#555"` → `Brush.Text.Secondary`; placeholder "Selecione um host…" `Foreground="#888"` → `Brush.Text.Tertiary`; `WinBoxError` `Foreground="Red"` → `Brush.Status.Error`; credencial `Foreground="#888"` (2 ocorrências) → `Brush.Text.Tertiary`.

- [ ] **Step 3: TabsView** — trocar: fallback template `Foreground="#bbb"` → `Brush.Text.Tertiary`; placeholder "Nenhuma sessão ativa…" `Foreground="#aaa"` → `Brush.Text.Tertiary`; label de protocolo `Foreground="#888"` → `Brush.Text.Tertiary`; botão "✕" mantém `Background="Transparent" BorderThickness="0"`.

- [ ] **Step 4: Session tab views** — abrir `src/RemoteOps.Desktop/Terminal/TerminalTabView.xaml`, `src/RemoteOps.Desktop/Rdp/RdpTabView.xaml`, `src/RemoteOps.Desktop/NDesk/NDeskTabView.xaml`; aplicar o mesmo mapa a qualquer cor fixa (`#…`, `Red`, `Gray`, etc.). Superfícies de fundo → `Brush.Bg.Canvas`; textos secundários → `Brush.Text.Secondary/Tertiary`.

- [ ] **Step 5: Build + smoke**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso 0/0. Lançar o exe e confirmar que sidebar/inspector/abas estão coesos.

- [ ] **Step 6: Commit**

```bash
git add src/RemoteOps.Desktop/Views src/RemoteOps.Desktop/Terminal src/RemoteOps.Desktop/Rdp src/RemoteOps.Desktop/NDesk
git commit -m "feat(gui): re-skin das views e abas de sessao com tokens Slate Signal"
```

---

### Task 4: Ícone do aplicativo

**Files:**
- Create: `src/RemoteOps.Desktop/assets/appicon.svg` (fonte)
- Create: `src/RemoteOps.Desktop/assets/appicon.ico` (gerado)
- Modify: `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`

- [ ] **Step 1: Criar o SVG da marca** em `src/RemoteOps.Desktop/assets/appicon.svg`:
```xml
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <rect width="256" height="256" rx="48" fill="#12151A"/>
  <circle cx="128" cy="128" r="86" fill="none" stroke="#33B7D1" stroke-width="14"/>
  <circle cx="128" cy="128" r="26" fill="#33B7D1"/>
  <path d="M128 42 V16 M128 240 V214 M42 128 H16 M240 128 H214" stroke="#33B7D1" stroke-width="14" stroke-linecap="round"/>
</svg>
```

- [ ] **Step 2: Converter SVG→ICO.** Se houver ImageMagick (`magick -version`):
```powershell
magick "C:\dev\remoteops-gui-slate\src\RemoteOps.Desktop\assets\appicon.svg" `
  -define icon:auto-resize=16,32,48,256 `
  "C:\dev\remoteops-gui-slate\src\RemoteOps.Desktop\assets\appicon.ico"
```
Sem ImageMagick: gerar o `.ico` com o Velopack (aceita PNG) ou com o pacote `dotnet tool install -g Ravu.IconGen` (fallback documentado). O objetivo é um `.ico` multi-resolução (16/32/48/256).

- [ ] **Step 3: Referenciar o ícone no csproj.** Em `RemoteOps.Desktop.csproj`, dentro do primeiro `<PropertyGroup>` (após `<StartupObject>`), adicionar:
```xml
    <ApplicationIcon>assets\appicon.ico</ApplicationIcon>
```

- [ ] **Step 4: Build + verificar ícone**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso. Lançar o exe → ícone aparece na janela e na barra de tarefas.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/assets src/RemoteOps.Desktop/RemoteOps.Desktop.csproj
git commit -m "feat(gui): icone do app (resolve branding do instalador ADR-019)"
```

---

### Task 5: Persistência de settings (`ISettingsStore` + `AppSettings` + `JsonSettingsStore`)

**Files:**
- Create: `src/RemoteOps.Desktop/Infrastructure/AppSettings.cs`
- Create: `src/RemoteOps.Desktop/Infrastructure/ISettingsStore.cs`
- Create: `src/RemoteOps.Desktop/Infrastructure/JsonSettingsStore.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/JsonSettingsStoreTests.cs`

**Interfaces:**
- Produces: `AppSettings` (record com `Dictionary<string,bool> Flags`, `string Theme`), `ISettingsStore` (`AppSettings Load()`, `void Save(AppSettings)`), `JsonSettingsStore` (ctor `()` = caminho default; ctor `(string path)` = teste).

- [ ] **Step 1: Escrever o teste que falha** — `tests/RemoteOps.UnitTests/Desktop/Infrastructure/JsonSettingsStoreTests.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class JsonSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);

        AppSettings settings = store.Load();

        Assert.Empty(settings.Flags);
        Assert.Equal("slate-signal-dark", settings.Theme);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsFlags()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);

        store.Save(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.RdpEnabled] = true },
        });

        AppSettings loaded = store.Load();
        Assert.True(loaded.Flags[FeatureFlagNames.RdpEnabled]);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not valid json ");
        var store = new JsonSettingsStore(path);

        AppSettings settings = store.Load();
        Assert.Empty(settings.Flags);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~JsonSettingsStoreTests" --nologo`
Expected: FALHA de compilação (tipos não existem).

- [ ] **Step 3: Implementar `AppSettings.cs`:**
```csharp
using System.Collections.Generic;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>Configurações persistidas do usuário (%AppData%\RemoteOps\settings.json).</summary>
public sealed record AppSettings
{
    /// <summary>Feature flags habilitadas pelo usuário, por nome (ver <see cref="FeatureFlagNames"/>).</summary>
    public Dictionary<string, bool> Flags { get; init; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Identificador do tema ativo. Único por ora.</summary>
    public string Theme { get; init; } = "slate-signal-dark";
}
```

- [ ] **Step 4: Implementar `ISettingsStore.cs`:**
```csharp
namespace RemoteOps.Desktop.Infrastructure;

/// <summary>Lê/grava as <see cref="AppSettings"/> do usuário. Nunca lança em Load (defaults em erro).</summary>
public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
```

- [ ] **Step 5: Implementar `JsonSettingsStore.cs`:**
```csharp
using System;
using System.IO;
using System.Text.Json;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Persiste as settings em <c>%AppData%\RemoteOps\settings.json</c> (mesmo diretório do
/// vault). Load é fail-safe: arquivo ausente ou corrompido devolve os defaults.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonSettingsStore() : this(DefaultPath()) { }

    public JsonSettingsStore(string path) => _path = path;

    private static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RemoteOps",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        string dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(_path, json);
    }
}
```

- [ ] **Step 6: Rodar e ver passar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~JsonSettingsStoreTests" --nologo`
Expected: 3 passed.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteOps.Desktop/Infrastructure/AppSettings.cs src/RemoteOps.Desktop/Infrastructure/ISettingsStore.cs src/RemoteOps.Desktop/Infrastructure/JsonSettingsStore.cs tests/RemoteOps.UnitTests/Desktop/Infrastructure/JsonSettingsStoreTests.cs
git commit -m "feat(gui): store de settings persistido em JSON (%AppData%/RemoteOps)"
```

---

### Task 6: `CompositeFeatureFlags` (settings OU env, env como override)

**Files:**
- Create: `src/RemoteOps.Desktop/Infrastructure/CompositeFeatureFlags.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Infrastructure/CompositeFeatureFlagsTests.cs`

**Interfaces:**
- Consumes: `IFeatureFlags`, `ISettingsStore`, `AppSettings`, `FeatureFlagNames`.
- Produces: `CompositeFeatureFlags : IFeatureFlags` (ctor `(ISettingsStore store, IFeatureFlags env)`).

- [ ] **Step 1: Teste que falha** — `CompositeFeatureFlagsTests.cs`:
```csharp
using System.Collections.Generic;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class CompositeFeatureFlagsTests
{
    private sealed class FakeStore : ISettingsStore
    {
        private AppSettings _s;
        public FakeStore(AppSettings s) => _s = s;
        public AppSettings Load() => _s;
        public void Save(AppSettings settings) => _s = settings;
    }

    [Fact]
    public void EnvEnabled_OverridesSettingsFalse()
    {
        var env = new EnvironmentFeatureFlags(rawFlags: FeatureFlagNames.RdpEnabled);
        var store = new FakeStore(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.RdpEnabled] = false },
        });
        var flags = new CompositeFeatureFlags(store, env);

        Assert.True(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }

    [Fact]
    public void SettingsEnabled_WithEmptyEnv_ReturnsTrue()
    {
        var env = new EnvironmentFeatureFlags(rawFlags: "");
        var store = new FakeStore(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.NdeskEnabled] = true },
        });
        var flags = new CompositeFeatureFlags(store, env);

        Assert.True(flags.IsEnabled(FeatureFlagNames.NdeskEnabled));
    }

    [Fact]
    public void BothOff_ReturnsFalse()
    {
        var env = new EnvironmentFeatureFlags(rawFlags: "");
        var store = new FakeStore(new AppSettings());
        var flags = new CompositeFeatureFlags(store, env);

        Assert.False(flags.IsEnabled(FeatureFlagNames.RdpEnabled));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~CompositeFeatureFlagsTests" --nologo`
Expected: FALHA de compilação.

- [ ] **Step 3: Implementar `CompositeFeatureFlags.cs`:**
```csharp
namespace RemoteOps.Desktop.Infrastructure;

/// <summary>
/// Combina flags do usuário (settings persistidas) com a variável de ambiente
/// (<see cref="EnvironmentFeatureFlags"/>). O ambiente é override FORTE: o que o env
/// habilitou nunca é desabilitado por settings (garante paridade com CI/operação).
/// Recarrega o store a cada consulta para refletir mudanças salvas sem reiniciar.
/// </summary>
public sealed class CompositeFeatureFlags : IFeatureFlags
{
    private readonly ISettingsStore _store;
    private readonly IFeatureFlags _env;

    public CompositeFeatureFlags(ISettingsStore store, IFeatureFlags env)
    {
        _store = store;
        _env = env;
    }

    public bool IsEnabled(string flagName)
    {
        if (_env.IsEnabled(flagName))
        {
            return true;
        }

        AppSettings settings = _store.Load();
        return settings.Flags.TryGetValue(flagName, out bool enabled) && enabled;
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~CompositeFeatureFlagsTests" --nologo`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/Infrastructure/CompositeFeatureFlags.cs tests/RemoteOps.UnitTests/Desktop/Infrastructure/CompositeFeatureFlagsTests.cs
git commit -m "feat(gui): CompositeFeatureFlags (settings OU env, env como override)"
```

---

### Task 7: Registrar settings/flags no `AppCompositionRoot`

**Files:**
- Modify: `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs:76`

**Interfaces:**
- Consumes: `ISettingsStore`, `JsonSettingsStore`, `CompositeFeatureFlags`, `EnvironmentFeatureFlags`.
- Produces: DI resolve `ISettingsStore` (singleton) e `IFeatureFlags` como `CompositeFeatureFlags`.

- [ ] **Step 1: Trocar o registro de flags.** Substituir a linha:
```csharp
        // Feature flags (default OFF — REMOTEOPS_FEATURE_FLAGS env var)
        services.AddSingleton<IFeatureFlags, EnvironmentFeatureFlags>();
```
por:
```csharp
        // Settings persistidas + feature flags (settings OU env; env é override forte)
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IFeatureFlags>(sp => new CompositeFeatureFlags(
            sp.GetRequiredService<ISettingsStore>(),
            new EnvironmentFeatureFlags()));
```

- [ ] **Step 2: Build + suite completa (garante que os smoke tests do composition root seguem verdes)**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: build 0/0; todos os testes passam (428 + os novos das tasks 5–6).

- [ ] **Step 3: Commit**

```bash
git add src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs
git commit -m "feat(gui): registra ISettingsStore + CompositeFeatureFlags no composition root"
```

---

### Task 8: `SettingsViewModel`

**Files:**
- Create: `src/RemoteOps.Desktop/ViewModels/SettingsViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/SettingsViewModelTests.cs`

**Interfaces:**
- Consumes: `ISettingsStore`, `IUpdateService?`, `AppSettings`, `FeatureFlagNames`, `RelayCommand`, `BaseViewModel`, `Update.UpdateCheckResult`.
- Produces: `SettingsViewModel` com props `RdpEnabled`/`NdeskEnabled` (bool, TwoWay), `ThemeName`, `VersionText`, `UpdateStatus`, `CanCheckUpdates`; comandos `SaveCommand`, `CheckForUpdatesCommand`; evento `Saved`.

- [ ] **Step 1: Teste que falha** — `SettingsViewModelTests.cs`:
```csharp
using System.Collections.Generic;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelTests
{
    private sealed class FakeStore : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = new();
        private AppSettings _current;
        public FakeStore(AppSettings current) => _current = current;
        public AppSettings Load() => _current;
        public void Save(AppSettings settings) { _current = settings; Saved = settings; }
    }

    [Fact]
    public void Ctor_LoadsFlagsFromStore()
    {
        var store = new FakeStore(new AppSettings
        {
            Flags = new Dictionary<string, bool> { [FeatureFlagNames.RdpEnabled] = true },
        });

        var vm = new SettingsViewModel(store);

        Assert.True(vm.RdpEnabled);
        Assert.False(vm.NdeskEnabled);
    }

    [Fact]
    public void Save_PersistsToggledFlags_AndRaisesSaved()
    {
        var store = new FakeStore(new AppSettings());
        var vm = new SettingsViewModel(store);
        bool raised = false;
        vm.Saved += (_, _) => raised = true;

        vm.NdeskEnabled = true;
        vm.SaveCommand.Execute(null);

        Assert.True(store.Saved.Flags[FeatureFlagNames.NdeskEnabled]);
        Assert.True(raised);
    }

    [Fact]
    public void CheckForUpdates_Disabled_WhenNoUpdateService()
    {
        var vm = new SettingsViewModel(new FakeStore(new AppSettings()), updateService: null);
        Assert.False(vm.CheckForUpdatesCommand.CanExecute(null));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~SettingsViewModelTests" --nologo`
Expected: FALHA de compilação.

- [ ] **Step 3: Implementar `SettingsViewModel.cs`:**
```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Update;

namespace RemoteOps.Desktop.ViewModels;

public sealed class SettingsViewModel : BaseViewModel
{
    private readonly ISettingsStore _store;
    private readonly IUpdateService? _updateService;
    private AppSettings _settings;
    private bool _rdpEnabled;
    private bool _ndeskEnabled;
    private string _updateStatus = string.Empty;

    public SettingsViewModel(ISettingsStore store, IUpdateService? updateService = null)
    {
        _store = store;
        _updateService = updateService;
        _settings = store.Load();
        _rdpEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.RdpEnabled, out bool rdp) && rdp;
        _ndeskEnabled = _settings.Flags.TryGetValue(FeatureFlagNames.NdeskEnabled, out bool nd) && nd;

        SaveCommand = new RelayCommand(Save);
        CheckForUpdatesCommand = new RelayCommand(
            () => _ = CheckForUpdatesAsync(),
            () => _updateService != null);
    }

    public bool RdpEnabled { get => _rdpEnabled; set => Set(ref _rdpEnabled, value); }
    public bool NdeskEnabled { get => _ndeskEnabled; set => Set(ref _ndeskEnabled, value); }
    public string ThemeName => "Slate Signal (escuro)";
    public string VersionText =>
        $"Versão {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";
    public bool CanCheckUpdates => _updateService != null;

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CheckForUpdatesCommand { get; }

    /// <summary>Disparado após persistir; a janela fecha e avisa "requer reinício" se necessário.</summary>
    public event EventHandler? Saved;

    private void Save()
    {
        var flags = new Dictionary<string, bool>(_settings.Flags, StringComparer.OrdinalIgnoreCase)
        {
            [FeatureFlagNames.RdpEnabled] = RdpEnabled,
            [FeatureFlagNames.NdeskEnabled] = NdeskEnabled,
        };
        _settings = _settings with { Flags = flags };
        _store.Save(_settings);
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updateService is null)
        {
            return;
        }

        UpdateStatus = "Verificando…";
        try
        {
            UpdateCheckResult result = await _updateService.CheckForUpdatesAsync();
            UpdateStatus = result.UpdateAvailable
                ? $"Atualização disponível: {result.AvailableVersion}."
                : "Você está na versão mais recente.";
        }
        catch (Exception)
        {
            UpdateStatus = "Não foi possível verificar atualizações agora.";
        }
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~SettingsViewModelTests" --nologo`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/SettingsViewModel.cs tests/RemoteOps.UnitTests/Desktop/ViewModels/SettingsViewModelTests.cs
git commit -m "feat(gui): SettingsViewModel (flags persistidas + verificar atualizacoes)"
```

---

### Task 9: `SettingsWindow` (janela de Configurações)

**Files:**
- Create: `src/RemoteOps.Desktop/Views/SettingsWindow.xaml`
- Create: `src/RemoteOps.Desktop/Views/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: `SettingsViewModel`.
- Produces: `SettingsWindow` (ctor `(SettingsViewModel vm)`).

- [ ] **Step 1: `SettingsWindow.xaml`** (tematizada, com TabControl de 3 abas):
```xml
<Window x:Class="RemoteOps.Desktop.Views.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Configurações" Height="420" Width="520"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="{DynamicResource Brush.Bg.App}"
        Foreground="{DynamicResource Brush.Text.Primary}"
        FontFamily="{DynamicResource Font.Family.Base}"
        FontSize="{DynamicResource Font.Size.Body}">
    <DockPanel Margin="16">
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,12,0,0">
            <TextBlock Text="{Binding UpdateStatus}" VerticalAlignment="Center"
                       Foreground="{DynamicResource Brush.Text.Secondary}" Margin="0,0,12,0"/>
            <Button Content="Salvar" Command="{Binding SaveCommand}" MinWidth="88" Padding="10,4"/>
        </StackPanel>
        <TabControl>
            <TabItem Header="Aparência">
                <StackPanel Margin="12">
                    <TextBlock Text="Tema" FontWeight="SemiBold" Margin="0,0,0,4"/>
                    <TextBlock Text="{Binding ThemeName}" Foreground="{DynamicResource Brush.Text.Secondary}"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Recursos">
                <StackPanel Margin="12">
                    <CheckBox Content="Habilitar RDP (rdp.enabled)" IsChecked="{Binding RdpEnabled}" Margin="0,0,0,8"/>
                    <CheckBox Content="Habilitar assistência NDesk (ndesk.enabled)" IsChecked="{Binding NdeskEnabled}"/>
                    <TextBlock Text="Alterações em recursos podem exigir reiniciar o app."
                               Foreground="{DynamicResource Brush.Text.Tertiary}"
                               FontSize="11" Margin="0,12,0,0" TextWrapping="Wrap"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Atualização">
                <StackPanel Margin="12">
                    <TextBlock Text="{Binding VersionText}" Margin="0,0,0,8"/>
                    <Button Content="Verificar agora" Command="{Binding CheckForUpdatesCommand}"
                            HorizontalAlignment="Left" Padding="10,4"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="Sobre">
                <StackPanel Margin="12">
                    <TextBlock Text="RemoteOps Desktop" FontWeight="SemiBold"/>
                    <TextBlock Text="{Binding VersionText}" Foreground="{DynamicResource Brush.Text.Secondary}"/>
                    <TextBlock Text="Console de operação de rede." Margin="0,8,0,0"
                               Foreground="{DynamicResource Brush.Text.Secondary}"/>
                </StackPanel>
            </TabItem>
        </TabControl>
    </DockPanel>
</Window>
```

- [ ] **Step 2: `SettingsWindow.xaml.cs`:**
```csharp
using System.Windows;
using RemoteOps.Desktop.ViewModels;

namespace RemoteOps.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += (_, _) => Close();
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso 0/0.

- [ ] **Step 4: Commit**

```bash
git add src/RemoteOps.Desktop/Views/SettingsWindow.xaml src/RemoteOps.Desktop/Views/SettingsWindow.xaml.cs
git commit -m "feat(gui): janela de Configuracoes (Aparencia/Recursos/Atualizacao/Sobre)"
```

---

### Task 10: Comandos de sessão + evento no `HostListViewModel` (para menu de contexto)

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/HostListViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/HostListViewModelConnectTests.cs`

**Interfaces:**
- Produces: `HostListViewModel.ConnectCommand` (RelayCommand com param `string` protocolo), `HostListViewModel.OpenWinBoxCommand`, eventos `ConnectRequested` (`EventHandler<string>`) e `WinBoxRequested` (`EventHandler`).
- Consumed by: Task 11 (MainViewModel assina os eventos) e Task 14 (menu de contexto liga aos comandos).

- [ ] **Step 1: Teste que falha** — `HostListViewModelConnectTests.cs`:
```csharp
using RemoteOps.Desktop.Domain;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class HostListViewModelConnectTests
{
    [Fact]
    public void ConnectCommand_DisabledWithoutSelection()
    {
        var vm = new HostListViewModel(new InMemoryLocalStore(), "ws-local");
        Assert.False(vm.ConnectCommand.CanExecute("ssh"));
    }

    [Fact]
    public void ConnectCommand_RaisesConnectRequestedWithProtocol()
    {
        var store = new InMemoryLocalStore();
        var vm = new HostListViewModel(store, "ws-local");
        var asset = store.AddAssetAsync(new AddAssetRequest { WorkspaceId = "ws-local", Name = "r1" })
            .GetAwaiter().GetResult();
        vm.SelectedAsset = new AssetViewModel(asset);

        string? received = null;
        vm.ConnectRequested += (_, proto) => received = proto;
        vm.ConnectCommand.Execute("telnet");

        Assert.Equal("telnet", received);
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~HostListViewModelConnectTests" --nologo`
Expected: FALHA de compilação.

- [ ] **Step 3: Implementar.** Em `HostListViewModel.cs`, no ctor (após `LoadCommand = …`), adicionar:
```csharp
        ConnectCommand = new RelayCommand(
            obj => ConnectRequested?.Invoke(this, obj as string ?? "ssh"),
            _ => SelectedAsset != null);

        OpenWinBoxCommand = new RelayCommand(
            () => WinBoxRequested?.Invoke(this, EventArgs.Empty),
            () => SelectedAsset != null);
```
Adicionar as propriedades (junto das outras `RelayCommand`):
```csharp
    public RelayCommand ConnectCommand { get; }
    public RelayCommand OpenWinBoxCommand { get; }
```
Adicionar os eventos (junto de `AssetSelected`):
```csharp
    public event EventHandler<string>? ConnectRequested;
    public event EventHandler? WinBoxRequested;
```
No setter de `SelectedAsset`, após `DeleteHostCommand.RaiseCanExecuteChanged();`, adicionar:
```csharp
            ConnectCommand.RaiseCanExecuteChanged();
            OpenWinBoxCommand.RaiseCanExecuteChanged();
```
(`using System;` já entra pelos ImplicitUsings.)

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~HostListViewModelConnectTests" --nologo`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/HostListViewModel.cs tests/RemoteOps.UnitTests/Desktop/ViewModels/HostListViewModelConnectTests.cs
git commit -m "feat(gui): comandos Connect/WinBox + eventos no HostListViewModel"
```

---

### Task 11: Fechar aba atual + fiação de sessão no `MainViewModel`

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/TabsViewModel.cs`
- Modify: `src/RemoteOps.Desktop/ViewModels/MainViewModel.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/ViewModels/TabsViewModelCloseActiveTests.cs`

**Interfaces:**
- Produces: `TabsViewModel.CloseActiveTabCommand` (RelayCommand); `MainViewModel` assina `HostList.ConnectRequested`/`WinBoxRequested` roteando para `Inspector.OpenSessionCommand`/`OpenWinBoxCommand` (fonte única).

- [ ] **Step 1: Teste que falha** — `TabsViewModelCloseActiveTests.cs`:
```csharp
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class TabsViewModelCloseActiveTests
{
    [Fact]
    public void CloseActiveTab_RemovesActiveTab()
    {
        var tabs = new TabsViewModel();
        tabs.OpenTab("r1", "ssh");
        Assert.True(tabs.HasTabs);

        tabs.CloseActiveTabCommand.Execute(null);

        Assert.False(tabs.HasTabs);
    }

    [Fact]
    public void CloseActiveTab_DisabledWhenNoTab()
    {
        var tabs = new TabsViewModel();
        Assert.False(tabs.CloseActiveTabCommand.CanExecute(null));
    }
}
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~TabsViewModelCloseActiveTests" --nologo`
Expected: FALHA de compilação.

- [ ] **Step 3: `TabsViewModel.cs`** — no ctor (após `CloseTabCommand = …`):
```csharp
        CloseActiveTabCommand = new RelayCommand(
            () => CloseTab(ActiveTab),
            () => ActiveTab is { IsPinned: false });
```
Adicionar a propriedade:
```csharp
    public RelayCommand CloseActiveTabCommand { get; }
```
No setter de `ActiveTab`, transformar em:
```csharp
    public SessionTabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            Set(ref _activeTab, value);
            CloseActiveTabCommand.RaiseCanExecuteChanged();
        }
    }
```
Ao final de `CloseTab(...)` (após `RaisePropertyChanged(nameof(HasTabs));`) adicionar:
```csharp
        CloseActiveTabCommand.RaiseCanExecuteChanged();
```

- [ ] **Step 4: `MainViewModel.cs`** — no ctor, após `Inspector.SessionRequested += OnSessionRequested;`, adicionar a fiação de fonte única:
```csharp
        HostList.ConnectRequested += (_, protocol) =>
            Inspector.OpenSessionCommand.Execute(protocol);
        HostList.WinBoxRequested += (_, _) =>
            Inspector.OpenWinBoxCommand.Execute(null);
```
(Quando um host é selecionado na lista, `Inspector.Asset` já é esse host via `HostList.AssetSelected` → `Inspector.Asset`. Assim menu de contexto e botões do Inspector convergem no mesmo `RequestOpenSession`.)

- [ ] **Step 5: Rodar e ver passar**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" --filter "FullyQualifiedName~TabsViewModelCloseActiveTests" --nologo`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/TabsViewModel.cs src/RemoteOps.Desktop/ViewModels/MainViewModel.cs tests/RemoteOps.UnitTests/Desktop/ViewModels/TabsViewModelCloseActiveTests.cs
git commit -m "feat(gui): CloseActiveTab + fiacao de sessao (contexto -> Inspector) no MainViewModel"
```

---

### Task 12: Fábrica de `SettingsViewModel` no `MainViewModel` (abrir Configurações a partir do menu)

**Files:**
- Modify: `src/RemoteOps.Desktop/ViewModels/MainViewModel.cs`

**Interfaces:**
- Consumes: `ISettingsStore`, `IUpdateService?` (injetados via DI no ctor de produção).
- Produces: `MainViewModel.CreateSettingsViewModel()` → `SettingsViewModel`; expõe `IsUpdateAvailable` (bool) e `AppVersionText` (string) para o menu Ajuda.

- [ ] **Step 1: Estender o ctor do MainViewModel.** Adicionar dois parâmetros opcionais ao final da lista do ctor (após `INDeskBrokerClient? ndeskBrokerClient = null`):
```csharp
        RemoteOps.Desktop.Infrastructure.ISettingsStore? settingsStore = null,
        RemoteOps.Desktop.Update.IUpdateService? updateService = null)
```
Guardar em campos:
```csharp
    private readonly RemoteOps.Desktop.Infrastructure.ISettingsStore? _settingsStore;
    private readonly RemoteOps.Desktop.Update.IUpdateService? _updateService;
```
E no corpo do ctor (início): `_settingsStore = settingsStore; _updateService = updateService;`

- [ ] **Step 2: Adicionar a fábrica + infos.** Métodos/propriedades públicos no MainViewModel:
```csharp
    public SettingsViewModel CreateSettingsViewModel() =>
        new(_settingsStore ?? new RemoteOps.Desktop.Infrastructure.JsonSettingsStore(), _updateService);

    public string AppVersionText =>
        $"RemoteOps Desktop {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";
```

- [ ] **Step 3: Build + suite completa (o ctor mudou; garante que a resolução via DI e os testes de MainViewModel seguem verdes)**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: build 0/0; todos os testes passam (parâmetros novos são opcionais → DI e testes existentes não quebram).

- [ ] **Step 4: Commit**

```bash
git add src/RemoteOps.Desktop/ViewModels/MainViewModel.cs
git commit -m "feat(gui): MainViewModel expoe fabrica de SettingsViewModel + versao do app"
```

---

### Task 13: Barra de menus no MainWindow

**Files:**
- Modify: `src/RemoteOps.Desktop/MainWindow.xaml`
- Modify: `src/RemoteOps.Desktop/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `MainViewModel` (`Inspector.OpenSessionCommand`, `Inspector.OpenWinBoxCommand`, `Tabs.CloseActiveTabCommand`, `CreateSettingsViewModel()`, `AppVersionText`), `Views.SettingsWindow`.

- [ ] **Step 1: Envolver o layout num DockPanel com Menu.** Em `MainWindow.xaml`, logo após a abertura de `<Grid>` do conteúdo, envolver assim: substituir `<Grid>` (o de nível superior) por `<DockPanel>` contendo o `Menu` (dock top) e então o `Grid` atual:
```xml
    <DockPanel>
        <Menu DockPanel.Dock="Top">
            <MenuItem Header="_Arquivo">
                <MenuItem Header="Configurações…" Click="OpenSettings_Click" InputGestureText="Ctrl+,"/>
                <Separator/>
                <MenuItem Header="Sair" Click="Exit_Click" InputGestureText="Alt+F4"/>
            </MenuItem>
            <MenuItem Header="_Sessão">
                <MenuItem Header="Conectar via SSH"    Command="{Binding Inspector.OpenSessionCommand}" CommandParameter="ssh"/>
                <MenuItem Header="Conectar via Telnet" Command="{Binding Inspector.OpenSessionCommand}" CommandParameter="telnet"/>
                <MenuItem Header="Conectar via RDP"    Command="{Binding Inspector.OpenSessionCommand}" CommandParameter="rdp"/>
                <MenuItem Header="Abrir WinBox"        Command="{Binding Inspector.OpenWinBoxCommand}"/>
                <Separator/>
                <MenuItem Header="Fechar aba atual"    Command="{Binding Tabs.CloseActiveTabCommand}" InputGestureText="Ctrl+W"/>
            </MenuItem>
            <MenuItem Header="_Exibir">
                <MenuItem Header="Barra lateral"  IsCheckable="True" IsChecked="True" Checked="ToggleSidebar_Changed" Unchecked="ToggleSidebar_Changed"/>
                <MenuItem Header="Inspector"      IsCheckable="True" IsChecked="True" Checked="ToggleInspector_Changed" Unchecked="ToggleInspector_Changed"/>
                <Separator/>
                <MenuItem Header="Foco na busca" Click="FocusSearch_Click" InputGestureText="Ctrl+F"/>
            </MenuItem>
            <MenuItem Header="_Ferramentas">
                <MenuItem Header="Configurações…" Click="OpenSettings_Click"/>
                <MenuItem Header="Verificar atualizações…" Click="CheckUpdates_Click"/>
            </MenuItem>
            <MenuItem Header="Aj_uda">
                <MenuItem Header="Documentação" Click="Docs_Click"/>
                <MenuItem Header="Sobre…" Click="About_Click"/>
            </MenuItem>
        </Menu>
        <Grid>
            <!-- … o Grid de 3 painéis já existente (RowDefinitions/ColumnDefinitions + conteúdo) … -->
        </Grid>
    </DockPanel>
```
Dar `x:Name` aos elementos que o code-behind manipula: na coluna 0 (`ColumnDefinition Width="220"`) → `x:Name="SidebarColumn"`; na coluna 4 (`Width="260"`) → `x:Name="InspectorColumn"`; no `TextBox` de busca da barra superior → `x:Name="SearchBox"`.

- [ ] **Step 2: Handlers no `MainWindow.xaml.cs`.** Substituir o arquivo por:
```csharp
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RemoteOps.Desktop.ViewModels;
using RemoteOps.Desktop.Views;

namespace RemoteOps.Desktop;

public partial class MainWindow : Window
{
    private GridLength _sidebarWidth = new(220);
    private GridLength _inspectorWidth = new(260);

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleSidebar_Changed(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).IsChecked)
        {
            SidebarColumn.Width = _sidebarWidth;
        }
        else
        {
            _sidebarWidth = SidebarColumn.Width;
            SidebarColumn.Width = new GridLength(0);
        }
    }

    private void ToggleInspector_Changed(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).IsChecked)
        {
            InspectorColumn.Width = _inspectorWidth;
        }
        else
        {
            _inspectorWidth = InspectorColumn.Width;
            InspectorColumn.Width = new GridLength(0);
        }
    }

    private void FocusSearch_Click(object sender, RoutedEventArgs e) => SearchBox.Focus();

    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(Vm.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
    }

    private void Docs_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://github.com/") { UseShellExecute = true });

    private void About_Click(object sender, RoutedEventArgs e)
        => MessageBox.Show(this, Vm.AppVersionText, "Sobre o RemoteOps", MessageBoxButton.OK, MessageBoxImage.Information);
}
```

- [ ] **Step 3: Atalhos de teclado.** No `MainWindow.xaml`, dentro de `<Window>` adicionar `<Window.InputBindings>` (antes do `<DockPanel>`):
```xml
    <Window.InputBindings>
        <KeyBinding Modifiers="Ctrl" Key="W" Command="{Binding Tabs.CloseActiveTabCommand}"/>
    </Window.InputBindings>
```
(Ctrl+, e Ctrl+F ficam como dica visual `InputGestureText`; o binding real de Ctrl+W cobre o atalho mais usado. Ctrl+F pode ser adicionado depois se necessário.)

- [ ] **Step 4: Build + smoke**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso 0/0. Lançar o exe: menu aparece dark; "Configurações…" abre a janela; "Barra lateral"/"Inspector" escondem/mostram os painéis; "Sobre" mostra a versão.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/MainWindow.xaml src/RemoteOps.Desktop/MainWindow.xaml.cs
git commit -m "feat(gui): barra de menus (Arquivo/Sessao/Exibir/Ferramentas/Ajuda) + Configuracoes"
```

---

### Task 14: Menu de contexto no host + seleção por clique-direito + estado vazio da lista

**Files:**
- Modify: `src/RemoteOps.Desktop/Views/HostListView.xaml`
- Modify: `src/RemoteOps.Desktop/Views/HostListView.xaml.cs`

**Interfaces:**
- Consumes: `HostListViewModel.ConnectCommand`, `OpenWinBoxCommand`, `DeleteHostCommand`.

- [ ] **Step 1: Selecionar a linha no clique-direito.** No `DataGrid` de `HostListView.xaml`, adicionar um `RowStyle` com EventSetter e um `ContextMenu` (o `DataContext` do menu é o `HostListViewModel`, mesmo da DataGrid):
```xml
        <DataGrid.RowStyle>
            <Style TargetType="DataGridRow">
                <EventSetter Event="PreviewMouseRightButtonDown" Handler="Row_PreviewMouseRightButtonDown"/>
            </Style>
        </DataGrid.RowStyle>
        <DataGrid.ContextMenu>
            <ContextMenu>
                <MenuItem Header="Conectar via SSH"    Command="{Binding ConnectCommand}" CommandParameter="ssh"/>
                <MenuItem Header="Conectar via Telnet" Command="{Binding ConnectCommand}" CommandParameter="telnet"/>
                <MenuItem Header="Conectar via RDP"    Command="{Binding ConnectCommand}" CommandParameter="rdp"/>
                <MenuItem Header="Abrir WinBox"        Command="{Binding OpenWinBoxCommand}"/>
                <Separator/>
                <MenuItem Header="Excluir"             Command="{Binding DeleteHostCommand}"/>
            </ContextMenu>
        </DataGrid.ContextMenu>
```
> Nota: `DataGrid.ContextMenu` herda o `DataContext` da DataGrid (o `HostListViewModel`), então os bindings de comando resolvem direto — sem `BindingProxy`.

- [ ] **Step 2: Estado vazio.** Envolver o `DataGrid` num `Grid` com um `TextBlock` de placeholder que aparece quando `Assets.Count == 0`. Adicionar dentro do `DockPanel` (como conteúdo principal, no lugar do `DataGrid` solto):
```xml
        <Grid>
            <TextBlock Text="Nenhum host neste grupo. Digite um nome acima e clique em Adicionar."
                       Foreground="{DynamicResource Brush.Text.Tertiary}"
                       TextWrapping="Wrap" Margin="16"
                       HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Setter Property="Visibility" Value="Collapsed"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Assets.Count}" Value="0">
                                <Setter Property="Visibility" Value="Visible"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>
            <!-- o DataGrid existente entra aqui -->
        </Grid>
```

- [ ] **Step 3: Handler no `HostListView.xaml.cs`:**
```csharp
using System.Windows.Controls;
using System.Windows.Input;

namespace RemoteOps.Desktop.Views;

public partial class HostListView : UserControl
{
    public HostListView() => InitializeComponent();

    private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }
}
```
> Se `HostListView.xaml.cs` já tiver `InitializeComponent()` num ctor, só adicionar o método handler.

- [ ] **Step 4: Build + smoke**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso 0/0. Lançar: clique-direito num host seleciona e abre o menu; "Excluir" remove; com lista vazia aparece o placeholder.

- [ ] **Step 5: Commit**

```bash
git add src/RemoteOps.Desktop/Views/HostListView.xaml src/RemoteOps.Desktop/Views/HostListView.xaml.cs
git commit -m "feat(gui): menu de contexto no host + selecao por clique-direito + estado vazio"
```

---

### Task 15: Endurecer `App.OnStartup` (evitar crash silencioso)

**Files:**
- Modify: `src/RemoteOps.Desktop/App.xaml.cs:42-90`

**Interfaces:** nenhuma nova. Objetivo: `OnStartup` (async void) não pode derrubar o app sem feedback ao abrir vault/DB.

- [ ] **Step 1: Envolver o corpo de `OnStartup` em try/catch.** Manter `base.OnStartup(e);` fora do try. Envolver todo o restante do método (do `string dataDir = …` até o fim) num bloco:
```csharp
        try
        {
            // … corpo atual de OnStartup (dataDir, vault, store, composition root,
            //    forced update, MainWindow, sync) permanece idêntico aqui …
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Falha ao iniciar o RemoteOps Desktop:\n\n{ex.Message}",
                "Erro de inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
```

- [ ] **Step 2: Build + smoke**

Run: `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: sucesso 0/0. App abre normalmente (caminho feliz inalterado).

- [ ] **Step 3: Commit**

```bash
git add src/RemoteOps.Desktop/App.xaml.cs
git commit -m "fix(gui): OnStartup com try/catch + dialogo de erro (evita crash silencioso)"
```

---

### Task 16: Validação final — build, testes e novo instalador

**Files:** nenhuma modificação de código.

- [ ] **Step 1: Suite completa verde**

Run: `dotnet test "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug --nologo`
Expected: build 0 erro / 0 aviso; **todos** os testes passam (428 antigos + ~13 novos das tasks 5,6,8,10,11).

- [ ] **Step 2: Checklist de smoke manual.** Lançar o exe e confirmar:
  - App abre 100% dark coeso, com ícone na janela/barra de tarefas.
  - Barra de menus responde; "Configurações…" abre a janela tematizada.
  - Em Configurações → Recursos, marcar `ndesk.enabled`, Salvar, **reabrir o app**, reabrir Configurações → o toggle **persistiu**.
  - Adicionar um host; clique-direito → menu de contexto; "Excluir" funciona.
  - Estados vazios aparecem (host list vazia, "Nenhuma sessão", "Selecione um host").
  - "Exibir → Barra lateral/Inspector" escondem/mostram os painéis.

- [ ] **Step 3: Gerar o instalador redesenhado**

Run (PowerShell):
```powershell
dotnet publish "C:\dev\remoteops-gui-slate\src\RemoteOps.Desktop\RemoteOps.Desktop.csproj" `
  -c Release -p:PublishProfile=win-x64-velopack -o "C:\dev\remoteops-gui-slate\publish\win-x64"
vpk pack --packId RemoteOpsDesktop --packVersion 0.11.0 `
  --packDir "C:\dev\remoteops-gui-slate\publish\win-x64" --mainExe RemoteOps.Desktop.exe `
  --packTitle "RemoteOps Desktop" --outputDir "C:\dev\remoteops-gui-slate\Releases"
```
Expected: `RemoteOpsDesktop-win-Setup.exe` em `C:\dev\remoteops-gui-slate\Releases` (agora com ícone). O log do `vpk` deve mostrar "Verified VelopackApp.Run()".

- [ ] **Step 4: Commit final (docs/notas de release, se houver)**

```bash
git add -A
git commit -m "chore(gui): valida build/testes e gera instalador do redesign Slate Signal"
```

---

## Self-Review (executada na escrita do plano)

**Cobertura da spec:**
- §4.1 tema → Task 1 ✅ · §4.2 re-skin → Tasks 2,3 ✅ · §4.3 barra de menus → Task 13 ✅ · §4.4 contexto (fonte única) → Tasks 10,11,14 ✅ · §4.5 Configurações + flags persistidas → Tasks 5,6,7,8,9,12 ✅ · §4.6 ícone/empty states → Tasks 4,14 (+ empty states de Inspector/Tabs já existiam, re-skinados na Task 3) ✅ · §4.7 branch/instalador → worktree criada + Task 16 ✅ · §4.8 validação → Tasks com build/test + Task 16 ✅ · Risco "crash no startup" → Task 15 ✅.
- Microinterações (hover/foco): entregues pelos estilos de controle do tema (Task 1), sem task dedicada — correto.

**Placeholders:** nenhum "TBD/TODO"; todo passo de código traz o código. A conversão SVG→ICO (Task 4 Step 2) tem caminho principal (ImageMagick) + fallbacks — é o único artefato binário e está explicitado.

**Consistência de tipos:** `ConnectRequested`(EventHandler<string>)/`WinBoxRequested`(EventHandler) definidos na Task 10 e consumidos igual na Task 11; `ConnectCommand`/`OpenWinBoxCommand`/`CloseActiveTabCommand`/`CreateSettingsViewModel()`/`AppVersionText` batem entre definição e uso; `AppSettings.Flags`/`Theme`, `ISettingsStore.Load/Save`, `UpdateCheckResult.UpdateAvailable/AvailableVersion` conferidos contra o código lido.

## Execution Handoff

Ver a mensagem de handoff após salvar (Subagent-Driven recomendado vs Inline).
