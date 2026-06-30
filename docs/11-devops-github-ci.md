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
