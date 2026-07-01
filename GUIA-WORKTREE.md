# Guia rápido — worktree (pro Vagner) 🧭

Guia simples pra criar as pastas de trabalho paralelas (worktrees) sem confusão.
Se bater dúvida, é só pedir pro Claude "me traz o comando da worktree certinho".

## A regra de ouro (decore só isto)

> **Sempre rode os comandos de worktree de DENTRO de uma pasta `remoteops-*` que já existe em `C:\dev`.**

Qualquer pasta `remoteops-*` serve (todas são "worktrees" do mesmo repositório).
**Nunca** rode direto em `C:\dev` — aquilo não é um repositório, dá erro.

## Modelo mental (o que é worktree)

- **Worktree = uma pasta + uma branch própria do MESMO repositório.**
- Cada frente de trabalho tem a sua pasta, isolada, pra os agentes não se atrapalharem.
- O `..\` nos comandos quer dizer "uma pasta acima" → as pastas novas nascem **ao lado**
  das outras, em `C:\dev\`.

```
C:\dev\
 ├─ remoteops-ndesk-gui\      ← você entra aqui pra rodar os comandos
 ├─ remoteops-packaging\      ← pasta nova criada pelo comando (..\remoteops-packaging)
 └─ remoteops-...\            ← cada frente, uma pasta irmã
```

## Passo a passo (sempre igual)

```cmd
:: 1. Entre numa pasta remoteops-* que já existe
cd C:\dev\remoteops-ndesk-gui

:: 2. Confirme que está no lugar certo — TEM que aparecer "origin" + uma URL do GitHub
git remote -v

:: 3. Atualize a base
git fetch origin main

:: 4. Crie a worktree da frente (troque os nomes conforme o Claude passar)
git worktree add ..\NOME-DA-PASTA -b NOME-DA-BRANCH origin/main
```

Depois é só abrir uma sessão do Claude **dentro da pasta nova** e colar o prompt da frente.

## Se der erro

| Erro | Significa | O que fazer |
|------|-----------|-------------|
| `fatal: not a git repository` | Você está em `C:\dev` (pasta errada) | `cd` pra uma pasta `remoteops-*` primeiro |
| `origin does not appear...` | A pasta atual não tem o remoto | `cd` pra outra `remoteops-*` e cheque `git remote -v` |
| `'..\pasta' already exists` | O nome de pasta já foi usado | Use outro nome (ex.: `-gui2`) ou peça o comando ajustado ao Claude |

## Quando a frente terminar

Dentro da pasta da frente:

```cmd
git push -u origin NOME-DA-BRANCH
```

E avise o Claude (ex.: "packaging terminou") — ele revisa, resolve conflitos e faz o merge.

---
*O Claude sempre te entrega o comando já preenchido com a pasta e a branch certas — você
não precisa montar na mão. Este guia é só um apoio.*
