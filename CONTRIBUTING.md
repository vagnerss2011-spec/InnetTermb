# Contribuindo — RemoteOps Suite (modelo multiagente paralelo)

Este projeto é desenvolvido em **várias frentes em paralelo**. Cada frente é isolada em um
git worktree próprio, com sua branch, seu agente dono e sua sessão. Um **orquestrador** abre PR
e o merge é automático quando o CI fica verde. Detalhes em
[`docs/24-orquestracao-multiagente-paralela.md`](docs/24-orquestracao-multiagente-paralela.md).

## Abrir uma frente

```bash
git switch main && git pull
bash tools/dev/worktrees.sh list          # ver frentes disponíveis
bash tools/dev/worktrees.sh add terminal  # cria ../remoteops-terminal + feature/terminal-ssh-telnet
cd ../remoteops-terminal
# abra a sessão (Claude Code / Codex / Antigravity) nesta pasta, com o agente dono da frente
```

No Windows: `pwsh tools/dev/worktrees.ps1 add terminal`.

## Branches

- `feature/<modulo>-<descricao>` — trabalho de módulo.
- `spike/<tema>`, `fix/<bug>`, `docs/<tema>`, `release/<versao>`.

## Ordem de dependência (`Depends-on:`)

Se a frente depende de outra, declare no **corpo do PR**, uma linha por dependência:

```
Depends-on: feature/contracts-skeleton
Depends-on: feature/security-vault
```

O job `merge-guard` (`.github/workflows/automerge.yml`) **bloqueia o merge** enquanto a branch
dependente não estiver em `main`. Isso mantém a ordem das ondas mesmo com auto-merge ligado.

Ondas: **0** contracts+skeleton+security → **1** desktop ‖ sync → **2** terminal ‖ mikrotik ‖ cloud ‖ rdp → **3** rdp/mikrotik estruturado → **4** ndesk.

## Definition of Done

- Build + testes verdes no CI (Windows).
- Sem segredo em log, commit, fixture ou screenshot.
- Contratos não quebrados (ou ADR + atualização de `contracts/` quando intencional).
- `CHANGELOG.md` atualizado.
- Documentação do módulo atualizada quando aplicável.
- Critérios de aceite da fase satisfeitos (`docs/12-roadmap-fases.md`).

## CI e merge

Gates obrigatórios (em `.github/workflows/ci.yml`): build/test/format no Windows, validação de
contratos JSON, `secret-scan`, `security-gate` (PRs sensíveis exigem o label `security-reviewed`)
e checagem de changelog. O `merge-guard` (`.github/workflows/automerge.yml`) sinaliza violações da
ordem `Depends-on:`.

**Auto-merge está DESLIGADO de propósito** (auto-merge em CI verde já quebrou a `main`: #8 → #11).
O merge é **manual, feito pelo orquestrador** com todos os checks verdes + revisão aprovada,
respeitando a ordem das ondas.

### Configuração recomendada no GitHub (Settings)

1. **Branch protection** em `main` com os checks obrigatórios: `dotnet`, `docs-contracts`,
   `secret-scan`, `security-gate`, `merge guard (Depends-on order)`.
2. **Allow auto-merge** deve ficar **desabilitado**.
3. Para PRs tocando `src/RemoteOps.Security/**`, `src/RemoteOps.NDesk.*/**` ou `contracts/`: rode
   `/security-review`, depois adicione o label `security-reviewed`.

## Hooks e permissões de sessão (recomendado)

Os scripts de hook já estão em `.claude/hooks/`. Para ativar nas suas sessões, **você** cria
`.claude/settings.json` (não é versionado por padrão; gere a partir do exemplo). Conteúdo sugerido:

```json
{
  "permissions": {
    "allow": [
      "Read", "Glob", "Grep",
      "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)",
      "Bash(git fetch:*)", "Bash(git branch:*)", "Bash(git worktree list:*)",
      "Bash(bash tools/dev/worktrees.sh list:*)",
      "Bash(dotnet restore:*)", "Bash(dotnet build:*)",
      "Bash(dotnet test:*)", "Bash(dotnet format:*)",
      "Bash(npm ci:*)", "Bash(npm run lint:*)", "Bash(npm test:*)"
    ]
  },
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "bash .claude/hooks/session-start.sh" } ] }
    ],
    "PreToolUse": [
      { "matcher": "Bash", "hooks": [ { "type": "command", "command": "bash .claude/hooks/block-destructive.sh" } ] }
    ],
    "PostToolUse": [
      { "matcher": "Write|Edit|MultiEdit", "hooks": [ { "type": "command", "command": "echo 'RemoteOps: arquivo alterado — revise testes, docs e ausência de segredos.'" } ] }
    ]
  }
}
```

- `SessionStart` orienta a sessão e restaura dependências (no-op se as ferramentas não existirem).
- `PreToolUse` bloqueia comandos claramente destrutivos (`rm -rf /`, force-push em `main`, etc.).
- A allowlist reduz prompts repetidos nas N sessões paralelas. Ajuste à vontade.

## Frontend do Terminal (WebView2 / xterm.js)

O módulo `src/RemoteOps.Desktop/Terminal/wwwroot/` contém assets front-end gerados via npm + esbuild.
Os arquivos compilados (`js/terminal.bundle.js`, `css/xterm.css`) **são versionados no repositório**,
portanto o build npm só precisa ser re-executado ao alterar dependências ou o `build.js`.

### Primeiro setup (ou ao atualizar xterm.js)

```powershell
cd src\RemoteOps.Desktop\Terminal\wwwroot
npm ci                # instala exatamente as versões de package-lock.json
node build.js         # gera js/terminal.bundle.js e css/xterm.css
```

### Verificar a integridade dos assets gerados

Após o build, confirme que os arquivos existem e não estão vazios:

```powershell
Get-Item js\terminal.bundle.js, css\xterm.css | Select-Object Name, Length
```

Saída esperada:

```
Name                 Length
----                 ------
terminal.bundle.js   ~420000
xterm.css            ~5000
```

### Atualizar versão do xterm.js

1. Edite `package.json` com a nova versão desejada.
2. Execute `npm install` para regenerar `package-lock.json`.
3. Execute `node build.js` para rearranjar o bundle.
4. Adicione uma entrada no `CHANGELOG.md` e abra um ADR se a API mudar.

> **Nota:** `node_modules/` está no `.gitignore` — nunca commite esta pasta.

## Subagentes

Os agentes em `.claude/agents/` têm escrita habilitada (`Edit, Write`) e são donos de frentes
específicas — ver tabela em `docs/24`. Use o agente certo para cada pasta e respeite as fronteiras
de `AGENTS.md`.
