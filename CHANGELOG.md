# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [0.4.0-sprint04] - 2026-06-30

### Adicionado

- **`src/RemoteOps.MikroTik/`** — módulo C#/.NET 10 do WinBox Runner:
  - `Models/MikroTikHostProfile.cs` — perfil de host MikroTik com IPv4, IPv6, porta, usuário, credencial, workspace e política de senha.
  - `Models/WinBoxLaunchRequest.cs` — request tipada (record) alinhada ao contrato `external-tool-launch.schema.json`, com suporte a RoMON.
  - `WinBoxToolManifest.cs` — lê `tools/winbox/manifest.json` e valida SHA-256 do executável antes do spawn.
  - `WinBoxArgumentBuilder.cs` — monta `ProcessStartInfo.ArgumentList` de forma segura: IPv6 com colchetes (RFC 3986), porta customizada, workspace, RoMON; senha nunca interpolada em string.
  - `WinBoxPolicy.cs` — `IWinBoxPolicyProvider` e implementação MVP `LocalWinBoxPolicyProvider`; senha por argumento requer flag global E flag da request simultaneamente.
  - `Audit/WinBoxAuditEvent.cs` — evento imutável com todos os campos de auditoria necessários; **nunca contém senha, token ou chave privada**.
  - `Audit/ConsoleWinBoxAuditSink.cs` — sink estruturado via `ILogger`; campos sensíveis nunca chegam ao log.
  - `WinBoxRunner.cs` — orquestrador: validação de hash → decisão de política → `Process.Start(UseShellExecute=false)` → eventos de auditoria.

- **`tests/RemoteOps.MikroTik.Tests/`** — testes xUnit cobrindo:
  - IPv4 padrão, IPv4 porta custom, IPv6 global, IPv6 link-local, IPv6 porta custom.
  - Senha omitida quando política nega (assert de ausência na lista de args).
  - Senha com espaço passada de forma segura via `ArgumentList`.
  - RoMON: `--romon <agent> <connect-to>` preposto corretamente.
  - Runner: executável não encontrado → falha auditada.
  - Runner: hash mismatch → falha auditada.
  - Runner: evento de auditoria nunca contém senha.
  - Runner: evento IPv6 emitido quando alvo é IPv6.

- **`tools/winbox/manifest.json`** — template de manifesto com campo `sha256` a ser substituído pelo hash oficial antes do uso em produção.

- **`RemoteOps.sln`** — solution unindo o módulo e seus testes.

### Segurança

- Senha jamais é passada por concatenação de string — usa-se `ProcessStartInfo.ArgumentList`.
- Hash SHA-256 do `winbox64.exe` é validado pelo manifesto antes de qualquer spawn.
- Todos os eventos de auditoria são verificados por ausência de segredos nos testes.
- Política de senha por argumento desativada por padrão; exige duas flags simultâneas.

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
