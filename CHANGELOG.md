# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [0.4.0-skeleton] - 2026-06-29

### Adicionado

- `RemoteOps.sln` na raiz — solution .NET 10 SDK-style com 9 projetos.
- `Directory.Build.props` com `Nullable=enable`, `LangVersion=latest`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`.
- `.editorconfig` com estilo de código C#, JSON, YAML e shell.
- `src/RemoteOps.Contracts` (classlib net10.0): POCOs imutáveis gerados a partir de `contracts/*.schema.json` e `docs/17` — `SessionRequest`, `SessionHandle`, `SyncChange`, `Asset`, `Endpoint`, `CredentialRef`, `AuditEvent`, `NDeskTicket`, `NDeskPermissionGrant`, `NDeskSessionTelemetry`, `ExternalToolLaunchRequest`. Interface `IRemoteSessionProvider` conforme `docs/02`.
- `src/RemoteOps.Security` (classlib net10.0): stub `ICredentialVault` com TODO.
- `src/RemoteOps.Terminal` (classlib net10.0): stub `ITerminalSessionProvider` com TODO.
- `src/RemoteOps.MikroTik` (classlib net10.0): stubs `IMikroTikSessionProvider` e `IWinBoxRunner` com TODO.
- `src/RemoteOps.Sync` (classlib net10.0): stub `ISyncClient` com TODO.
- `src/RemoteOps.Desktop` (WPF net10.0-windows): janela vazia compilável.
- `src/RemoteOps.Rdp` (classlib net10.0-windows): stub `IRdpSessionProvider` com TODO.
- `src/RemoteOps.Cloud` (ASP.NET Core net10.0): app mínimo com endpoint `GET /health`.
- `src/deferred/RemoteOps.NDesk.Viewer` e `RemoteOps.NDesk.Relay`: stubs marcados como deferred, fora da solution, à espera das frentes feature/ndesk-*.
- `tests/RemoteOps.UnitTests` (xUnit net10.0): 13 smoke tests cobrindo todos os projetos cross-platform.

### Alterado

- `.github/workflows/ci.yml`: removidos guards `if (Test-Path *.sln)` do job `dotnet` — build, test e format passam a rodar de verdade.

### Segurança

- Nenhum segredo, senha ou chave privada adicionado.
- `ICredentialVault` deixa explícito que nunca expõe segredo em logs; `CredentialRef.SecretEnvelopeId` documenta que só a referência ao envelope é armazenada nos POCOs.

## [0.3.0-planning] - 2026-06-29

### Adicionado

- Modelo de orquestração multiagente paralela: `docs/24-orquestracao-multiagente-paralela.md` com frentes (worktree por módulo), agentes donos, ondas de execução e ordem de merge.
- Scripts `tools/dev/worktrees.sh` e `tools/dev/worktrees.ps1` para criar/remover worktrees por frente.
- `CONTRIBUTING.md` com fluxo de frentes, convenção `Depends-on:`, Definition of Done e settings/hook recomendados.
- Hooks de sessão em `.claude/hooks/` (`session-start.sh` e `block-destructive.sh`).
- Workflow `.github/workflows/automerge.yml` com `merge-guard` (valida `Depends-on:`) e auto-merge em CI verde.

### Alterado

- Subagentes em `.claude/agents/` passam a ter escrita habilitada (`Edit, Write`) para atuarem como donos de frentes.
- `.github/workflows/ci.yml` reforçado com jobs `secret-scan` e `security-gate` (label `security-reviewed` para pastas sensíveis) e checagem de changelog mais flexível.

### Segurança

- Auto-merge total só é habilitado com o CI como portão real: secret scan, gate de revisão de segurança em pastas sensíveis e guarda de ordem de dependência.
- Hook bloqueia comandos destrutivos (`rm -rf` em raiz/home/wildcard, force-push em `main`, remoção recursiva forçada).

## [0.2.0-planning] - 2026-06-29

### Adicionado

- Decisão de tratar MikroTik via WinBox oficial externo no MVP.
- Documento `docs/21-mikrotik-winbox-runner.md` com runner, argumentos, riscos e critérios de aceite.
- Decisão de criar agente temporário NDesk Win32 nativo para Windows 7/10 sem Java, WebView2 ou .NET moderno.
- Documento `docs/22-ndesk-performance-legacy-windows.md` com NAT, relay, conexão lenta, codec adaptativo e modos de permissão.
- Documento `docs/23-governanca-versionamento-changelog.md` para changelog, versionamento, branches, PRs e releases.
- ADRs 006 e 007.
- Contratos de lançamento de ferramenta externa, concessão de permissão NDesk e telemetria de sessão.
- Prompts de sprint para WinBox Runner, NDesk legado/performance e governança de release.
- Agente `release-manager-agent`.

### Alterado

- Stack principal continua C#/.NET/WPF para o desktop da empresa, mas o agente temporário NDesk legado passa a ser tratado como componente nativo separado.
- Módulo MikroTik deixa de depender de API-SSL/REST no MVP e passa a priorizar WinBox oficial externo.
- Pipeline de PR passa a exigir avaliação de changelog e versionamento.

### Segurança

- Adicionado alerta sobre risco de senha em argumento de processo ao abrir WinBox.
- Adicionado modelo de permissões NDesk: básico, controle, transferência e administrador, sempre com consentimento explícito.

## [0.1.0-planning] - 2026-06-29

### Adicionado

- Planejamento inicial do RemoteOps Suite.
- Módulos SSH/Telnet, RDP, MikroTik, sync, segurança, NDesk, DevOps, QA e agentes.
