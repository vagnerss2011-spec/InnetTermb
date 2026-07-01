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
2. `dotnet restore` + `dotnet publish` self-contained (`win-x64`) de
   `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`.
3. Empacota o publish com a Velopack CLI (`vpk pack`) — instalador `Setup.exe`. A configuração
   do projeto para Velopack (referência de pacote, `VelopackApp.Build().Run()` no `Main`, ícone)
   é entregue pela frente paralela `feature/packaging-velopack-update` (**ADR-019**); este
   workflow assume o publish self-contained padrão e os nomes de `APP_ID`/`APP_EXE` definidos no
   topo do arquivo — ajustar ali se a ADR-019 definir valores diferentes.
4. Gera também um ZIP portátil (todo o publish self-contained, sem instalação) e expõe o
   executável principal avulso como terceiro artefato.
5. Gera `SHA256SUMS.txt` e `build-manifest.json` (commit, branch, tag, versão do SDK,
   timestamp UTC) para os artefatos publicados, conforme a seção "Builds reproduzíveis" de
   `VERSIONING.md`.
6. Publica os 5 arquivos (instalador, ZIP portátil, executável avulso, hashes, manifest) como
   artefato do workflow (`actions/upload-artifact`) **e** como assets do GitHub Release da tag
   (`gh release create`, usando apenas `GITHUB_TOKEN` — nenhum segredo adicional). Tag com sufixo
   `-alpha`/`-beta`/`-rc` marca o Release como prerelease.

Fora de escopo deste workflow (ver `docs/11` §Secret management no CI e `ADR-019`):
assinatura de binário/instalador com certificado, publicação em canal interno alpha/beta/stable
e feed de auto-update do Velopack.

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
