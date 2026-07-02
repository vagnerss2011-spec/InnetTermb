# 11 — GitHub, CI e governança de código

## Estrutura de repositório sugerida

```text
remoteops-suite/
  src/
    RemoteOps.Desktop/
    RemoteOps.Terminal/
    RemoteOps.Rdp/
    RemoteOps.MikroTik/
    RemoteOps.Sync/
    RemoteOps.Cloud/
    RemoteOps.Security/
    RemoteOps.NDesk.Viewer/
    RemoteOps.NDesk.Agent/
    RemoteOps.NDesk.Relay/
  tests/
    RemoteOps.UnitTests/
    RemoteOps.IntegrationTests/
    RemoteOps.E2E.Tests/
    RemoteOps.NDesk.LegacyTests/
  contracts/
  docs/
  adr/
  installer/
  tools/
    winbox/
  .claude/
  .github/
```

## Branches

- `main`: sempre estável.
- `feature/<modulo>-<descricao>`.
- `spike/<tema>`.
- `fix/<bug>`.
- `docs/<tema>`.
- `release/<versao>`.

## Regras de proteção

Para `main`:

- PR obrigatório.
- Pelo menos 1 aprovação humana.
- CODEOWNERS obrigatório por pasta sensível.
- CI obrigatório.
- Proibir force push.
- Exigir branch atualizada.
- Exigir signed commits, se viável.
- Exigir changelog quando houver mudança visível.

## CODEOWNERS inicial

- Segurança/credenciais: security lead.
- NDesk: security + ndesk.
- MikroTik WinBox Runner: mikrotik + security quando envolver credencial.
- Sync contracts: architect + backend.
- RDP/ActiveX: desktop + architect.
- CI/release/signing: devops + security.

## Pull request template

Cada PR deve responder:

- O que mudou?
- Qual módulo?
- Como testar?
- Há impacto em segurança?
- Há segredo/log sensível?
- Atualizou docs/ADR?
- Atualizou changelog?
- Alterou contrato?
- Alterou compatibilidade Windows?
- Screenshots, se UI.

## CI inicial

Jobs:

1. Restore/build .NET no Windows.
2. Testes unitários.
3. `dotnet format`.
4. Testes JS do terminal, quando existir.
5. Build/test C++ do NDesk Agent, quando existir.
6. Testes Rust/Go, se existirem workers/relay.
7. Dependency/security scan.
8. Validação de JSON schemas em `contracts/`.
9. Checagem de changelog.
10. Empacotamento dry-run.

## Releases

- Tags semver por componente.
- Changelog obrigatório.
- Artefato Windows assinado.
- Installer MSIX/MSI/Velopack a decidir em ADR.
- Canal interno: alpha, beta, stable.
- Hash SHA-256 publicado internamente.
- Build manifest com commit, SDK e timestamp UTC.

### `release.yml` — pipeline de release do RemoteOps Desktop

Workflow separado do `ci.yml` (`.github/workflows/release.yml`), disparado por push de tag
`v*` (ex.: `v0.11.0`, `v0.11.0-beta.1`). Não roda em PR nem em push de branch.

Fluxo, em `windows-latest`:

1. Deriva a versão a partir do nome da tag (`v` removido) e valida contra o SemVer interno de
   `VERSIONING.md` (`X.Y.Z` ou `X.Y.Z-alpha|beta|rc.N`); tag fora do padrão falha o job antes de
   compilar.
2. `dotnet restore` + instala a Velopack CLI (`vpk`) + `dotnet publish` self-contained
   (`win-x64`) de `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj` usando o publish profile
   canônico `win-x64-velopack.pubxml` (**ADR-019**, `docs/26-empacotamento-atualizacao-velopack.md`)
   — mesmo profile usado localmente, para não divergir do publish documentado/validado
   (`PublishReadyToRun`, `IncludeNativeLibrariesForSelfExtract`, sem `PublishSingleFile`).
3. Empacota o publish com a Velopack CLI (`vpk pack`) — gera `Setup.exe`, `*-full.nupkg`/
   `*-delta.nupkg` e `releases.win.json` em `Releases/`.
4. Gera `SHA256SUMS.txt` e `build-manifest.json` (commit, branch, tag, versão do SDK,
   timestamp UTC) para os artefatos publicados, conforme a seção "Builds reproduzíveis" de
   `VERSIONING.md`, e publica tudo como artefato do workflow (`actions/upload-artifact`).
5. Publica o Release **e** o feed de auto-update via `vpk upload github` (ADR-019 §4) —
   sobe o instalador, os pacotes `.nupkg` (full/delta) e o índice `releases.win.json` como
   assets do GitHub Release da tag, usando apenas `GITHUB_TOKEN` (nenhum segredo adicional; repo
   de releases é público). Tag com sufixo `-alpha`/`-beta`/`-rc` passa `--pre` (marca o Release
   como prerelease). Esse feed é o que `GithubSource`/`UpdateManager`
   (`AppCompositionRoot.RegisterUpdateService`) consome em runtime — **não** usar
   `gh release create` manual aqui, pois ele não gera os artefatos de feed Velopack.

Fora de escopo deste workflow (ver `docs/11` §Secret management no CI e `ADR-019`):
assinatura de binário/instalador com certificado e publicação em canal interno alpha/beta/stable
(hoje só existe o canal padrão `win`).

## Secret management no CI

- Não armazenar certificado em texto puro.
- Preferir GitHub Environments com approvals.
- Segredos só em jobs de release.
- Nunca rodar release em PR de fork.
- Logs de CI nunca devem expor credenciais de teste reais.

## Estratégia para agentes

Cada agente deve receber:

- Documento do módulo.
- Contratos relevantes.
- Critério de aceite.
- Branch própria.
- Limite de escopo.
- Política de changelog.

O agente principal/arquitetura revisa:

- Interfaces.
- Quebra de contratos.
- Duplicação.
- Segurança.
- Testes.
- Impacto em release.

## Convenções

- IDs: ULID/GUID, consistente em todo sistema.
- Datas: UTC.
- Logs: estruturados.
- Erros: ProblemDetails no backend.
- Testes: nomes descritivos.
- Config: `appsettings.*.json` sem segredos.
- Feature flags para recursos arriscados.

## Integração com `docs/23`

As regras detalhadas de changelog, versionamento, Definition of Done, tags e release estão em `docs/23-governanca-versionamento-changelog.md` e `VERSIONING.md`.
