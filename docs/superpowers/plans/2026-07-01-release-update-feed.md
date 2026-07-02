# Release + feed de auto-update — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fazer "Verificar atualizações" funcionar no app instalado sem configuração (feed embutido + política opcional), publicar a v0.13.0 como release base, e automatizar releases futuros por tag.

**Architecture:** Relaxar `AppCompositionRoot.RegisterUpdateService` para usar uma URL de feed embutida por padrão (env sobrescreve) e tornar o feed de política opcional via um `NoPolicyFeedSource`. Publicar a baseline com `vpk upload github` e adicionar um `release.yml` acionado por tags `vX.Y.Z`.

**Tech Stack:** .NET 10, WPF, Velopack (`vpk` + `Velopack` NuGet), GitHub Actions, xUnit 2.9.3.

**Worktree:** `C:\dev\remoteops-release-update` (branch `feature/release-update-feed`, a partir de `origin/main` `15cb8e0`). Caminhos relativos a essa base.

**Spec:** `docs/superpowers/specs/2026-07-01-release-update-feed-design.md`

## Global Constraints

- `TreatWarningsAsErrors=true` — build 0/0. `Nullable=enable`, `ImplicitUsings=enable`.
- Testes xUnit (`[Fact]`/`Assert`), namespace espelha a pasta (`RemoteOps.UnitTests.Desktop.*`); suíte existente deve ficar verde.
- Repo: `https://github.com/vagnerss2011-spec/InnetTermb`. Feed embutido = essa URL.
- Nenhum segredo hardcoded; token via env/`GITHUB_TOKEN` (repo público → token opcional).
- Auto-update só funciona no app **instalado** (Velopack); em Debug/testes o `UpdateManager` lança `InvalidOperationException` → sem serviço (comportamento correto).
- Build: `dotnet build "C:\dev\remoteops-release-update\RemoteOps.sln" -c Debug --nologo`. Test: `dotnet test "C:\dev\remoteops-release-update\RemoteOps.sln" -c Debug --nologo`.

## Interfaces existentes reaproveitadas

- `IUpdatePolicyFeedSource` (Update): `Task<AppVersion?> GetMinimumRequiredVersionAsync(CancellationToken ct = default)`.
- `HttpUpdatePolicyFeedSource(HttpClient, Uri policyUrl)` : IUpdatePolicyFeedSource.
- `VelopackUpdateService(UpdateManager, IUpdatePolicyFeedSource)` : IUpdateService.
- `GithubSource(string repoUrl, string? token, bool prerelease)` + `UpdateManager(source)` (Velopack).
- `AppCompositionRoot.RegisterUpdateService(ServiceCollection)` — método a reescrever (atual: exige ambas env vars, retorna cedo senão).
- Publish profile `src/RemoteOps.Desktop/Properties/PublishProfiles/win-x64-velopack.pubxml`; ícone `src/RemoteOps.Desktop/assets/appicon.ico`.

---

### Task 1: `UpdateFeedConfig` (resolver de URL) + `NoPolicyFeedSource`

**Files:**
- Create: `src/RemoteOps.Desktop/Update/UpdateFeedConfig.cs`
- Create: `src/RemoteOps.Desktop/Update/NoPolicyFeedSource.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Update/UpdateFeedConfigTests.cs`
- Test: `tests/RemoteOps.UnitTests/Desktop/Update/NoPolicyFeedSourceTests.cs`

**Interfaces:**
- Produces: `UpdateFeedConfig.DefaultRepoUrl` (const), `UpdateFeedConfig.ResolveRepoUrl(string? envValue) -> string`; `NoPolicyFeedSource : IUpdatePolicyFeedSource`.

- [ ] **Step 1: Testes que falham** — `UpdateFeedConfigTests.cs`:
```csharp
using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class UpdateFeedConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRepoUrl_EmptyEnv_ReturnsDefault(string? env)
        => Assert.Equal(UpdateFeedConfig.DefaultRepoUrl, UpdateFeedConfig.ResolveRepoUrl(env));

    [Fact]
    public void ResolveRepoUrl_EnvSet_ReturnsEnvTrimmed()
        => Assert.Equal("https://github.com/acme/x", UpdateFeedConfig.ResolveRepoUrl("  https://github.com/acme/x  "));

    [Fact]
    public void DefaultRepoUrl_PointsAtThisRepo()
        => Assert.Equal("https://github.com/vagnerss2011-spec/InnetTermb", UpdateFeedConfig.DefaultRepoUrl);
}
```
`NoPolicyFeedSourceTests.cs`:
```csharp
using System.Threading.Tasks;
using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class NoPolicyFeedSourceTests
{
    [Fact]
    public async Task GetMinimumRequiredVersion_IsNull()
        => Assert.Null(await new NoPolicyFeedSource().GetMinimumRequiredVersionAsync());
}
```

- [ ] **Step 2: Rodar e ver falhar** — `dotnet test "C:\dev\remoteops-release-update\RemoteOps.sln" --filter "FullyQualifiedName~Desktop.Update" --nologo` → FALHA de compilação (tipos ausentes).

- [ ] **Step 3: Implementar `UpdateFeedConfig.cs`:**
```csharp
namespace RemoteOps.Desktop.Update;

/// <summary>
/// Resolve o feed de releases do Velopack. Embute o repositório oficial como padrão para
/// que o auto-update funcione no app instalado sem configuração; a env var
/// REMOTEOPS_UPDATE_FEED_REPO_URL sobrescreve (repo privado/staging).
/// </summary>
public static class UpdateFeedConfig
{
    public const string DefaultRepoUrl = "https://github.com/vagnerss2011-spec/InnetTermb";

    public static string ResolveRepoUrl(string? envValue)
        => string.IsNullOrWhiteSpace(envValue) ? DefaultRepoUrl : envValue.Trim();
}
```

- [ ] **Step 4: Implementar `NoPolicyFeedSource.cs`:**
```csharp
using System.Threading;
using System.Threading.Tasks;

namespace RemoteOps.Desktop.Update;

/// <summary>
/// Feed de política nulo: sem versão mínima exigida (nenhum update forçado). Usado quando
/// REMOTEOPS_UPDATE_POLICY_URL não está configurada, para que o update VOLUNTÁRIO funcione
/// sem depender de um feed de política.
/// </summary>
public sealed class NoPolicyFeedSource : IUpdatePolicyFeedSource
{
    public Task<AppVersion?> GetMinimumRequiredVersionAsync(CancellationToken ct = default)
        => Task.FromResult<AppVersion?>(null);
}
```

- [ ] **Step 5: Rodar e ver passar** — mesmo filtro → todos passam.

- [ ] **Step 6: Commit**
```bash
git add src/RemoteOps.Desktop/Update/UpdateFeedConfig.cs src/RemoteOps.Desktop/Update/NoPolicyFeedSource.cs tests/RemoteOps.UnitTests/Desktop/Update/UpdateFeedConfigTests.cs tests/RemoteOps.UnitTests/Desktop/Update/NoPolicyFeedSourceTests.cs
git commit -m "feat(update): UpdateFeedConfig (feed embutido) + NoPolicyFeedSource (politica opcional)"
```

---

### Task 2: Relaxar `RegisterUpdateService` (feed embutido + política opcional)

**Files:**
- Modify: `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs` (método `RegisterUpdateService`)

**Interfaces:**
- Consumes: `UpdateFeedConfig`, `NoPolicyFeedSource`, `HttpUpdatePolicyFeedSource`, `GithubSource`, `UpdateManager`, `VelopackUpdateService`.
- Produces: `IUpdateService` registrado no app instalado sem env vars.

- [ ] **Step 1: Substituir o corpo de `RegisterUpdateService`** por:
```csharp
    private static void RegisterUpdateService(ServiceCollection services)
    {
        // Feed embutido por padrão (env sobrescreve) — auto-update funciona no app
        // instalado sem configuração (ADR-019). Ver Update/UpdateFeedConfig.cs.
        string repoUrl = Update.UpdateFeedConfig.ResolveRepoUrl(
            Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_REPO_URL"));

        UpdateManager manager;
        try
        {
            // Token opcional (repo público → null); nunca hardcoded (ADR-019 §4).
            string? accessToken = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_TOKEN");
            var source = new GithubSource(repoUrl, accessToken, prerelease: false);
            manager = new UpdateManager(source);
        }
        catch (InvalidOperationException)
        {
            // App não instalado pelo Velopack (Debug/testes) — sem locator; segue sem update.
            return;
        }

        // Política de update forçado é OPCIONAL: sem REMOTEOPS_UPDATE_POLICY_URL, usa um
        // feed nulo (sem versão mínima) — o update voluntário funciona mesmo assim.
        string? policyUrlRaw = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_POLICY_URL");
        IUpdatePolicyFeedSource policyFeed =
            !string.IsNullOrWhiteSpace(policyUrlRaw)
            && Uri.TryCreate(policyUrlRaw, UriKind.Absolute, out Uri? policyUrl)
                ? new HttpUpdatePolicyFeedSource(new HttpClient(), policyUrl)
                : new Update.NoPolicyFeedSource();

        services.AddSingleton(policyFeed);
        services.AddSingleton<IUpdateService>(sp => new VelopackUpdateService(
            manager, sp.GetRequiredService<IUpdatePolicyFeedSource>()));
    }
```
> Garantir os `using` necessários no topo do arquivo (`using RemoteOps.Desktop.Update;` já existe para `IUpdateService`/`HttpUpdatePolicyFeedSource`; `UpdateFeedConfig`/`NoPolicyFeedSource` estão no mesmo namespace `RemoteOps.Desktop.Update`, referenciados como `Update.X` acima para não depender de using novo — ou adicionar o using e usar sem prefixo).

- [ ] **Step 2: Build + suíte completa** — `dotnet test "C:\dev\remoteops-release-update\RemoteOps.sln" -c Debug --nologo`. Expected: 0/0, todos verdes. (Em testes, `new UpdateManager(...)` lança `InvalidOperationException` → `return` → nenhum `IUpdateService` registrado, igual antes; os `CompositionRootSmokeTests` seguem passando.)

- [ ] **Step 3: Commit**
```bash
git add src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs
git commit -m "feat(update): feed embutido por padrao + politica opcional em RegisterUpdateService"
```

---

### Task 3: `release.yml` — publicar releases por tag `vX.Y.Z`

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Criar `.github/workflows/release.yml`:**
```yaml
name: release

on:
  push:
    tags:
      - "v*"

permissions:
  contents: write

jobs:
  build-and-release:
    name: Build, empacotar (Velopack) e publicar
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - name: Derivar e validar versão da tag (SemVer)
        id: ver
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          $version = $tag -replace '^v', ''
          if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(-(alpha|beta|rc)\.[0-9]+)?$') {
            Write-Error "Tag '$tag' fora do SemVer esperado (vX.Y.Z[-alpha|beta|rc.N])."
            exit 1
          }
          "version=$version" | Out-File -FilePath $env:GITHUB_OUTPUT -Append

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - name: Instalar Velopack CLI (vpk)
        run: dotnet tool install -g vpk

      - name: Publish self-contained (win-x64)
        run: dotnet publish src/RemoteOps.Desktop/RemoteOps.Desktop.csproj -c Release -p:PublishProfile=win-x64-velopack -o publish/win-x64

      - name: vpk pack
        run: >
          vpk pack --packId RemoteOpsDesktop --packVersion ${{ steps.ver.outputs.version }}
          --packDir publish/win-x64 --mainExe RemoteOps.Desktop.exe
          --packTitle "RemoteOps Desktop" --packAuthors "InnetTermb"
          --icon src/RemoteOps.Desktop/assets/appicon.ico --outputDir Releases

      - name: vpk upload github (publica o release + feed)
        run: >
          vpk upload github --repoUrl https://github.com/vagnerss2011-spec/InnetTermb
          --token ${{ secrets.GITHUB_TOKEN }} --tag ${{ github.ref_name }}
          --releaseName "RemoteOps Desktop ${{ steps.ver.outputs.version }}" --publish --outputDir Releases
```
> Assinatura de binário (SignTool/cert) fora de escopo — step separado quando houver certificado. `GITHUB_TOKEN` padrão basta (repo próprio).

- [ ] **Step 2: Validar sintaxe** — se `actionlint` estiver disponível: `actionlint .github/workflows/release.yml`. Senão, revisar manualmente que o YAML é válido (indentação, chaves `on/jobs/steps`). Não há como rodar o workflow sem push de tag.

- [ ] **Step 3: Commit**
```bash
git add .github/workflows/release.yml
git commit -m "ci(release): publica release Velopack por tag vX.Y.Z (vpk upload github)"
```

---

### Task 4: Publicar a v0.13.0 como baseline (ops — ação externa)

**Files:** nenhuma alteração de código. Produz um GitHub Release.

> ⚠️ Ação **pública/externa** (cria um GitHub Release + tag no repositório). No auto-mode do harness isso pode ser **bloqueado** (como o merge do PR foi) — se for, o **usuário executa** os comandos abaixo no cmd (o `gh` já está autenticado com scope `repo`). Não force o bloqueio.

- [ ] **Step 1: Re-gerar o pacote Velopack desta branch** (código do main atual + Task 1/2):
```powershell
cd C:\dev\remoteops-release-update
dotnet publish src\RemoteOps.Desktop\RemoteOps.Desktop.csproj -c Release -p:PublishProfile=win-x64-velopack -o publish\win-x64
vpk pack --packId RemoteOpsDesktop --packVersion 0.13.0 --packDir publish\win-x64 --mainExe RemoteOps.Desktop.exe --packTitle "RemoteOps Desktop" --packAuthors "InnetTermb" --icon src\RemoteOps.Desktop\assets\appicon.ico --outputDir Releases
```

- [ ] **Step 2: Publicar no GitHub Releases** (obter o token do gh e usar no vpk):
```powershell
$env:GH_TOKEN = (gh auth token)
vpk upload github --repoUrl https://github.com/vagnerss2011-spec/InnetTermb --token $env:GH_TOKEN --tag v0.13.0 --releaseName "RemoteOps Desktop 0.13.0" --publish --outputDir Releases
```
Expected: cria o release `v0.13.0` com assets `RELEASES`, `RemoteOpsDesktop-0.13.0-full.nupkg`, `RemoteOpsDesktop-win-Setup.exe`.

- [ ] **Step 3: Verificar** — `gh release list --repo vagnerss2011-spec/InnetTermb` mostra `v0.13.0`; `gh release view v0.13.0 --repo vagnerss2011-spec/InnetTermb --json assets` lista o asset `RELEASES` (o índice do feed Velopack).

---

### Task 5: Validação final + nota de docs

**Files:**
- Modify: `CHANGELOG.md` (entrada `[Unreleased]`)

- [ ] **Step 1: Suíte completa** — `dotnet test "C:\dev\remoteops-release-update\RemoteOps.sln" -c Debug --nologo` → 0/0, verde.
- [ ] **Step 2: Nota no CHANGELOG** — em `[Unreleased]`, adicionar: "Auto-update: feed de releases embutido por padrão (env sobrescreve) e feed de política opcional (`NoPolicyFeedSource`); pipeline `release.yml` publica por tag `vX.Y.Z`; baseline v0.13.0 publicada." Commit: `docs(update): changelog do feed de auto-update`.
- [ ] **Step 3: Smoke** — build Debug + lançar o exe: `Verificar atualizações` no app **instalado** (não Debug) deve responder "você está na versão mais recente" após a baseline existir. (No Debug segue inerte — esperado.)

---

## Self-Review (executada na escrita)

**Cobertura da spec:** §3.1 app (feed embutido + política opcional) → Tasks 1,2 · §3.2 baseline publish → Task 4 · §3.3 release.yml → Task 3 · §3.4 notas → Task 5 + build constraints · §4 testes → Task 1 (helper+NoPolicy) + Task 2/5 (build/suíte) · §6 critérios → Tasks 2,3,4,5.

**Placeholders:** nenhum "TBD". A nota "adicionar o using e usar sem prefixo" (Task 2) é uma alternativa explícita, não um placeholder. Task 4 é ops (comandos concretos) — o único passo não-código, marcado como ação externa que pode exigir execução pelo usuário.

**Consistência de tipos:** `UpdateFeedConfig.DefaultRepoUrl`/`ResolveRepoUrl` (Task 1) consumidos igual na Task 2; `NoPolicyFeedSource` (Task 1) casa com `IUpdatePolicyFeedSource` (assinatura confirmada) e é usado na Task 2; `VelopackUpdateService(UpdateManager, IUpdatePolicyFeedSource)` e `GithubSource`/`UpdateManager` conferidos contra o código atual; `release.yml` usa o publish profile + ícone existentes.

## Execution Handoff
Ver a mensagem de handoff após salvar.
