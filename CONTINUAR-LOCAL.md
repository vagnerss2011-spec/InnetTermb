# Continuar localmente — abrir uma sessão do Claude que edita sua pasta 🧭

Memória de retomada. Se você abrir o Claude Code **na sua máquina**, ele lê este arquivo (via
`CLAUDE.md`) e já tem todo o contexto do projeto e do jeito de trabalhar.

## 1. Por que a sessão da web não mexe na sua pasta

O Claude Code na **web/nuvem** roda num container Linux efêmero, isolado da sua máquina — ele
integra via GitHub (PRs), mas **não acessa `C:\dev`**. Para o Claude **editar seus arquivos
direto**, use o Claude Code **local** no Windows.

## 2. Como abrir a sessão local (Claude edita seus arquivos)

Na sua máquina Windows, com o repositório já clonado:

**Opção A — CLI:**
```cmd
:: instalar uma vez (requer Node.js)
npm install -g @anthropic-ai/claude-code

:: abrir o Claude DENTRO da pasta do clone
cd C:\dev\<pasta-do-clone-do-repo>
claude
```

**Opção B — App desktop ou extensão VS Code / JetBrains** apontando para a pasta do repo.

Detalhes/instalação oficial: https://code.claude.com/docs

Estando dentro da pasta do repo, o Claude local lê `CLAUDE.md` + `docs/` e assume o contexto.
Ele **cria/edita arquivos, roda `dotnet`, roda o app** — tudo na sua máquina.

## 3. Estado atual do projeto (branch `main`)

Suite Windows de acesso remoto (SSH/Telnet, MikroTik/WinBox, RDP, NDesk consentido) + backend.
Já na `main`, buildando e testando no CI (windows-latest):

- **Desktop WPF** roda (`dotnet run --project src\RemoteOps.Desktop`) — shell, abas, tema
  escuro, aba NDesk com fluxo de consentimento em **loopback** (fake).
- **Empacotamento Velopack** (ADR-019): `Setup.exe` + versão portátil + update sob demanda e
  forçado. Pipeline de release por tag `v*` (`.github/workflows/release.yml`).
- **Backend NDesk (broker de signaling)** roda de verdade (ASP.NET Core + Postgres) — validado
  ponta a ponta: tickets, consentimento, revogação, relay SDP/ICE. Link token só como hash
  SHA-256, nunca em claro.
- Base decidida e documentada: ADR-015 (construir), ADR-016 (Win10/11 + agente .NET),
  ADR-017 (libdatachannel + Vortice + coturn), ADR-018 (API de signaling), ADR-020 (sessionId).

**O que ainda NÃO existe:** o **agente nativo** (captura de tela + WebRTC via `libdatachannel`)
— a última peça para o compartilhamento de tela real entre duas máquinas. É o próximo passo.

## 4. Como está organizado (o mesmo padrão, pra reproduzir)

- **Uma frente = uma git worktree + uma branch + um agente.** Guia: `GUIA-WORKTREE.md`.
- **Convenções:** branch `feature/<modulo>-<descricao>`; ADR em `adr/` para mudança de
  contrato/schema/API/cripto; atualizar `CHANGELOG.md`; recurso sensível atrás de feature flag
  default-OFF; PRs em pasta sensível (`Security`, `NDesk`, `contracts`) exigem o label
  `security-reviewed` no CI antes do merge.
- **CI (`.github/workflows/ci.yml`):** build+test (windows), secret-scan, docs/contracts
  sanity, security-gate (label), merge-guard (`Depends-on`).
- **Orquestração:** o Claude da web revisa/rebaseia/integra as PRs; o Claude local implementa.

## 5. Como rodar/validar localmente

- **Só a GUI (mais simples):** `docs/26-runbook-teste-local.md`
  ```cmd
  set REMOTEOPS_FEATURE_FLAGS=ndesk.enabled,rdp.enabled
  dotnet run --project src\RemoteOps.Desktop
  ```
- **Backend NDesk (broker) + verificador:** `docs/27-executar-broker-local.md`
  (Docker: `docker compose -f deploy\docker-compose.dev.yml up -d`) → `tools\ndesk-signaling-check`.
- **Instalador/portátil:** `docs/26-empacotamento-atualizacao-velopack.md` (`dotnet publish` +
  `vpk pack`), ou empurrar uma tag `v*`.

## 6. Próximo passo recomendado

Construir o **agente nativo Win** (SPIKE-017/ADR-017): captura DXGI + `libdatachannel` +
injeção de input, ligado ao broker. Precisa de build/execução no Windows — por isso é a peça
ideal para a sessão **local**.
