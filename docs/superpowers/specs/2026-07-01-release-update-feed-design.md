# Design — Release + feed de auto-update (fazer "Verificar atualizações" funcionar)

- **Data:** 2026-07-01
- **Branch:** `feature/release-update-feed` (worktree `C:\dev\remoteops-release-update`), a partir do `origin/main` atual (`15cb8e0` — já com Slate Signal + navegação Termius).
- **Status:** aprovado para spec → aguardando revisão antes do plano.

## 1. Contexto e problema

O usuário tentou "Verificar atualizações" pela GUI e nada aconteceu. Diagnóstico (não é bug — é infra faltando):
1. **Nenhum GitHub Release publicado** (`gh release list` vazio) → o app não tem contra o que checar.
2. O app **só registra o serviço de update se DUAS env vars existirem** (`REMOTEOPS_UPDATE_FEED_REPO_URL` **e** `REMOTEOPS_UPDATE_POLICY_URL`) — como nenhuma está setada, o `IUpdateService` nem é criado e o botão fica inerte (`RegisterUpdateService` em `AppCompositionRoot.cs`).
3. O **`release.yml`** (pipeline que publicaria releases em tags) **não está no `main`**.
4. Detalhe do Velopack: o auto-update só funciona na **cópia instalada** (via `Setup.exe`), nunca no `.exe` de Debug nem no portátil.

**Objetivo:** fazer o "Verificar atualizações" funcionar no app instalado, com o mínimo de fricção pro usuário final, e automatizar a publicação de releases futuros.

## 2. Decisões (brainstorming)

| Tema | Decisão |
|---|---|
| Como o app acha o feed | **URL de feed embutida por padrão** (`github.com/vagnerss2011-spec/InnetTermb`), env vars **sobrescrevem**. Funciona out-of-the-box no app instalado. |
| Feed de política (update obrigatório) | **Opcional** — quando `REMOTEOPS_UPDATE_POLICY_URL` ausente, usar um `NoPolicyFeedSource` (sem versão mínima = sem update forçado). Desacopla "checar/aplicar update" de "update obrigatório". |
| Publicação | Publicar a **v0.13.0 como release base** agora (`vpk upload github`) **+** trazer um **`release.yml` fresco pro `main`** pra tags `vX.Y.Z` futuras publicarem automaticamente. |
| Branch | Nova a partir do `origin/main` atual (`15cb8e0`) — não reusar worktrees antigos. |

**Fora de escopo:** assinatura de código (SmartScreen continua avisando — frente separada); update forçado por política (fica opcional/desligado até haver um feed de política); mudar o fluxo de UI do botão (já existe).

## 3. Design detalhado

### 3.1 App — feed embutido + política opcional (código)

Em `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs`, reescrever `RegisterUpdateService`:

- Constante `private const string DefaultUpdateFeedRepoUrl = "https://github.com/vagnerss2011-spec/InnetTermb";`
- `string repoUrl = Environment.GetEnvironmentVariable("REMOTEOPS_UPDATE_FEED_REPO_URL");` → se vazio, usar `DefaultUpdateFeedRepoUrl`.
- Construir `UpdateManager(new GithubSource(repoUrl, token, prerelease:false))` dentro do `try/catch (InvalidOperationException)` **existente** — em app não-instalado (Debug/testes) o construtor lança e caímos fora sem registrar (comportamento correto: Debug não atualiza).
- **Política opcional:** se `REMOTEOPS_UPDATE_POLICY_URL` for uma URL absoluta válida → `HttpUpdatePolicyFeedSource(new HttpClient(), policyUrl)`; senão → `NoPolicyFeedSource` (novo).
- Registrar sempre o `IUpdateService` (no app instalado), com o policy feed resolvido acima.
- `REMOTEOPS_UPDATE_FEED_TOKEN` continua opcional (repo público → null).

Novo tipo `src/RemoteOps.Desktop/Update/NoPolicyFeedSource.cs`:
```
public sealed class NoPolicyFeedSource : IUpdatePolicyFeedSource
{
    public Task<AppVersion?> GetMinimumRequiredVersionAsync(CancellationToken ct = default)
        => Task.FromResult<AppVersion?>(null);
}
```
(Confirmar a assinatura exata contra `IUpdatePolicyFeedSource`.) Resultado: `CheckForUpdatesAsync` passa a reportar "há versão nova?" pelo feed do GitHub, e `MustUpdate` fica sempre false sem política — nenhum update forçado, mas o voluntário funciona.

### 3.2 Publicar a v0.13.0 (baseline, uma vez)

**Re-gerar o pacote Velopack a partir DESTA branch** (código do `main` atual, com o app-side já corrigido) — não reusar o artefato do worktree `remoteops-termius` (que é pré-merge). Ou seja: `dotnet publish -c Release -p:PublishProfile=win-x64-velopack` → `vpk pack --packVersion 0.13.0 ...` → `vpk upload github --repoUrl https://github.com/vagnerss2011-spec/InnetTermb --releaseName "RemoteOps Desktop 0.13.0" --tag v0.13.0 --token <GH_TOKEN> --publish` (usando o token do `gh`). Cria o GitHub Release `v0.13.0` com `RELEASES` + `.nupkg` + `Setup.exe`. **Ação externa/pública — executada com OK explícito do usuário.**

### 3.3 `release.yml` no `main` (CI)

Novo `.github/workflows/release.yml` (fresco), gatilho `push` em tags `v*`:
1. checkout; setup-dotnet 10.0.x; instalar `vpk` (dotnet tool).
2. derivar versão da tag (`v1.2.3` → `1.2.3`), validar SemVer.
3. `dotnet publish src/RemoteOps.Desktop/RemoteOps.Desktop.csproj -c Release -p:PublishProfile=win-x64-velopack -o publish/win-x64`.
4. `vpk pack --packId RemoteOpsDesktop --packVersion <ver> --packDir publish/win-x64 --mainExe RemoteOps.Desktop.exe --icon src/RemoteOps.Desktop/assets/appicon.ico --outputDir Releases`.
5. `vpk upload github --repoUrl <este repo> --token ${{ secrets.GITHUB_TOKEN }} --tag v<ver> --publish --releaseName "RemoteOps Desktop <ver>"`.
- `runs-on: windows-latest`; permissões `contents: write`; sem segredos além do `GITHUB_TOKEN`. Assinatura fora de escopo (step separado quando houver certificado).

### 3.4 Notas de funcionamento (documentar)

- O update só liga no app **instalado** (Velopack) — não no Debug/portátil.
- Pra **ver** um update acontecendo: instalar a v0.13.0 e depois publicar uma v0.13.1 (via tag/pipeline) — com uma versão só, o feed existe mas não há "novo".

## 4. Testes

- `NoPolicyFeedSource.GetMinimumRequiredVersionAsync` retorna `null`.
- Helper de resolução do feed (env-ou-default): extrair a lógica "repoUrl = env ?? default" pra um método testável (ex.: `UpdateFeedConfig.ResolveRepoUrl(envValue)`), com testes (env presente → env; env vazio → default).
- Build 0/0 (TreatWarningsAsErrors); suíte existente verde.
- Validação manual: após publicar, `gh release list` mostra `v0.13.0` com asset `RELEASES`; um app instalado a partir do release não quebra ao "Verificar atualizações" (reporta "você está na versão mais recente").

## 5. Riscos e mitigações

| Risco | Mitigação |
|---|---|
| `UpdateManager` não testável em unit (precisa de contexto instalado) | Extrair a resolução de URL/política pra unidades testáveis; a construção do manager fica coberta pelo catch existente + validação manual |
| Publicar release é ação pública irreversível-ish | Fazer com OK explícito; tag `v0.13.0` é a baseline combinada |
| `vpk upload github` exige token com scope `repo` | O token do `gh` já tem `repo` + `workflow` (confirmado) |
| Feed embutido fixa o repo no código | Env var sobrescreve (staging/privado); aceitável |
| SmartScreen no `Setup.exe` (não assinado) | Fora de escopo; documentado |

## 6. Critérios de sucesso

- App **instalado** registra o `IUpdateService` sem nenhuma env var; "Verificar atualizações" responde (não fica inerte).
- Política é opcional — sem `REMOTEOPS_UPDATE_POLICY_URL`, nada de update forçado, mas o voluntário funciona.
- **GitHub Release `v0.13.0` publicado** (feed vivo).
- `release.yml` no `main` publica um release ao empurrar uma tag `vX.Y.Z`.
- Build 0/0, suíte verde + testes novos (NoPolicyFeedSource, resolução de feed).
