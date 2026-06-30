# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [Unreleased] — feature/terminal-ssh-telnet

### Adicionado

- `adr/ADR-008-sshnet-adapter.md` — decisão de usar Renci.SshNet (SSH.NET, MIT) para o adaptador SSH-2.
- `src/RemoteOps.Contracts` — projeto de contratos: `IRemoteSessionProvider`, `ICredentialVault`, `IHostKeyStore`, `ITelnetPolicy`, `PlaintextCredential`, `SessionRequest`, `HostKeyInfo`.
- `src/RemoteOps.Terminal.SSH` — `SshSessionProvider`: PTY shell, senha/chave privada, validação TOFU de host key, keepalive 30s, preferência IPv6.
- `src/RemoteOps.Terminal.Telnet` — `TelnetSessionProvider`: negociação RFC 854 (NAWS, TTYPE), gate de política (`ITelnetPolicy`), aviso visual de protocolo sem criptografia.
- `src/RemoteOps.Terminal.Core` — `TerminalSessionManager` (até 10 sessões), `TerminalSession`, `BridgeMessage` (schema JSON do bridge WebView2↔C#).
- `src/RemoteOps.Terminal.Core/wwwroot` — frontend xterm.js 5.3.0 local (sem CDN), FitAddon, bridge `window.chrome.webview.postMessage`, diálogo de host key, banner de aviso Telnet, copy/paste Ctrl+Shift+C/V.
- `src/RemoteOps.Desktop` — `TerminalTabView` (WebView2 + virtual host `terminal.local`) e `TerminalTabViewModel` (CommunityToolkit.Mvvm).
- `tests/RemoteOps.Terminal.Tests` — testes xUnit: `PlaintextCredentialTests` (zeroing de bytes), `TelnetNegotiatorTests` (IAC parsing), `TelnetPolicyTests` (gate de política e warning).
- `src/RemoteOps.sln` — solution file cobrindo todos os projetos do módulo.

### Segurança

- `PlaintextCredential.Dispose()` zera arrays de senha/chave privada com `CryptographicOperations.ZeroMemory`.
- Scripts JS usam `textContent`/DOM seguro — nenhum `innerHTML` com dados externos.
- Assets xterm.js servidos localmente via virtual host WebView2; nenhuma dependência de CDN em runtime.
- DevTools, context menu e status bar do WebView2 desabilitados em produção.
- Credencial descartada dentro da task de sessão imediatamente após o handshake SSH.
- Nenhum conteúdo de terminal é logado; apenas evento de início/fim de sessão.

### Pendente / Issues a criar

- [ ] Implementar `ICredentialVault` concreto (bloqueado por `contracts-skeleton` / `security-vault`).
- [ ] Implementar `IHostKeyStore` concreto com SQLCipher (bloqueado por `security-vault`).
- [ ] Resolver `groupId` real a partir do registro de endpoint (linha TODO em `TelnetSessionProvider`).
- [ ] Integrar `TerminalTabViewModel` no shell de abas principal (bloqueado por `desktop-shell`).
- [ ] Adicionar autenticação por chave privada Ed25519/ECDSA ao fluxo de UI.
- [ ] Teste de integração com host SSH de laboratório (Sprint 02, entrega 6).

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
