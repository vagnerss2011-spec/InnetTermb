# 24 — Orquestração multiagente paralela

Este documento define como o RemoteOps Suite é desenvolvido em **várias frentes em paralelo**, cada uma isolada em um git worktree próprio, com um **orquestrador** que abre PR e faz merge automático quando a frente passa no CI.

Leia junto com `AGENTS.md`, `docs/11-devops-github-ci.md`, `docs/12-roadmap-fases.md` e `docs/23-governanca-versionamento-changelog.md`.

## Modelo: uma frente = um worktree + uma branch + um agente + uma sessão

Cada frente de trabalho é totalmente isolada:

- **Worktree**: pasta irmã do repositório (mesmo `.git`, working dir separado). Ver `tools/dev/worktrees.sh`.
- **Branch**: `feature/<modulo>-<descricao>` conforme `docs/11`.
- **Agente dono**: subagente especializado em `.claude/agents/`.
- **Sessão/ferramenta**: pode ser Claude Code, Codex ou Antigravity. O modelo é agnóstico à ferramenta no nível do worktree.

Vantagem: N sessões trabalham ao mesmo tempo sem pisar uma na outra, porque cada uma tem seu próprio diretório e sua própria branch.

## Regra de ouro

**Estabilize contratos + skeleton + security vault antes de paralelizar.** Só comece a abrir frentes em paralelo depois que `contracts/`, o skeleton da solution e `RemoteOps.Security` estiverem em `main`. Isso evita conflito de interface entre módulos.

## Frentes, donos e dependências

| Frente / Pasta | Agente dono | Branch | Depende de | Onda |
|---|---|---|---|---|
| `contracts/` + skeleton + CI | `remoteops-architect` + `devops-agent` | `feature/contracts-skeleton` | — | 0 |
| `src/RemoteOps.Security/` | `security-agent` | `feature/security-vault` | contracts | 0 |
| `src/RemoteOps.Desktop/` | `desktop-shell-agent` | `feature/desktop-shell` | contracts, security | 1 |
| `src/RemoteOps.Sync/` (SQLite local) | `cloud-sync-agent` | `feature/sync-local` | contracts, security, desktop | 1 |
| `src/RemoteOps.Terminal/` | `ssh-telnet-agent` | `feature/terminal-ssh-telnet` | contracts, desktop, security | 2 |
| `src/RemoteOps.MikroTik/` + `tools/winbox/` | `mikrotik-agent` | `feature/mikrotik-winbox` | contracts, security, desktop | 2 |
| `src/RemoteOps.Cloud/` (backend) | `cloud-sync-agent` | `feature/cloud-backend` | contracts, security | 2 |
| `src/RemoteOps.Rdp/` | `rdp-agent` | `feature/rdp-activex` | contracts, desktop, security | 3 |
| `src/RemoteOps.NDesk.*` | `ndesk-agent` + `security-agent` | `feature/ndesk-*` | tudo acima + cloud | 4 |
| `tests/`, `docs/`, `adr/` | `qa-agent`, `docs-agent` | `feature/tests-*`, `docs/*` | módulo correspondente | contínuo |

## Ondas de execução

- **Onda 0 (serial):** `contracts` + skeleton + CI → `security` vault. Destrava todo o resto.
- **Onda 1 (paralelo):** Desktop shell ‖ Sync local. (após `security` em `main`)
- **Onda 2 (paralelo):** Terminal ‖ MikroTik ‖ Cloud backend ‖ RDP — 3–4 frentes simultâneas.
- **Onda 3:** RDP + MikroTik estruturado (RouterOS API/REST).
- **Onda 4:** NDesk (Viewer + Agent Win32 + Relay), somente após o spike comprar-vs-construir (ver `docs/15`, SPIKE-016).

### Atribuição de ferramenta sugerida

- **Claude Code** → `remoteops-architect`, `security-agent`, `desktop-shell-agent`, contratos/skeleton, revisões.
- **Codex** → módulos de protocolo bem-delimitados: Terminal, MikroTik, RDP (usar `AGENTS.md`).
- **Antigravity (Google)** → backend Cloud/Sync e tooling de DevOps/CI.

## Orquestrador e merge

O **orquestrador** é a sessão principal no papel `remoteops-architect`, com acesso ao GitHub (MCP/gh). Fluxo por frente:

1. O agente da frente implementa no seu worktree, roda testes locais, commita e faz push da branch.
2. O orquestrador abre o PR usando o template (`.github/PULL_REQUEST_TEMPLATE.md`) e dispara `code-review` + `security-review`.
3. O CI roda os **gates obrigatórios** (ver abaixo).
4. **Merge manual pelo orquestrador**: com todos os checks verdes E a revisão aprovada, o orquestrador faz o merge respeitando a ordem topológica (o `merge-guard` apenas sinaliza violações de ordem).
5. O orquestrador envia ao usuário um resumo: frente mergeada, bloqueada ou com falha.

> **Auto-merge está DESLIGADO de propósito.** Auto-merge em CI verde já causou `main` quebrada (o PR #8 foi mergeado antes dos commits de correção de segurança/build chegarem, exigindo o PR #11 de remediação). Por isso o merge é uma decisão humana/orquestrada, não automática.

### Por que os gates de CI importam

Mesmo com merge manual, o **CI é o portão**: nenhum PR é mergeado sem estes gates verdes:

- `build` + `test` + `dotnet format --verify-no-changes` no Windows;
- validação de `contracts/*.json`;
- **secret scanning** + checagem de "sem segredo em log/fixture";
- **security-gate**: pastas sensíveis (`Security`, `MikroTik`, `NDesk`, `contracts`) exigem o label `security-reviewed`;
- checagem de `CHANGELOG.md`;
- **merge-guard**: sinaliza se as dependências declaradas no PR ainda não estão em `main`.

## Convenção `Depends-on:`

Todo PR de frente que dependa de outro módulo deve declarar no corpo, uma linha por dependência:

```
Depends-on: feature/contracts-skeleton
Depends-on: feature/security-vault
```

O job `merge-guard` lê essas linhas e **sinaliza (check vermelho)** enquanto a branch dependente não tiver sido mergeada em `main` — reconhecendo merges via squash (consulta PR mergeado, não ancestralidade). O orquestrador respeita esse sinal no merge manual.

## Definition of Done por frente

- Build + testes verdes no CI (Windows).
- Sem segredo em log, commit, fixture ou screenshot.
- Contratos não quebrados (ou ADR + atualização de `contracts/` quando intencional).
- `CHANGELOG.md` atualizado.
- Documentação do módulo (`docs/`) atualizada quando aplicável.
- Critérios de aceite da fase (ver `docs/12`) satisfeitos.

## Como abrir uma nova frente

```bash
# a partir da raiz do repo, com main atualizada
bash tools/dev/worktrees.sh add terminal      # cria ../remoteops-terminal + feature/terminal-ssh-telnet
cd ../remoteops-terminal
# abrir a sessão (Claude/Codex/Antigravity) apontando para esta pasta e o agente dono
```

Encerrar uma frente após o merge:

```bash
bash tools/dev/worktrees.sh remove terminal
```
