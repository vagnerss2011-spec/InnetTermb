# 23 — Governança, versionamento e changelog

## Objetivo

Evitar bagunça em um projeto grande com muitos agentes e muitas linhas de código. Este documento define como as partes trabalham em paralelo sem quebrar contratos, segurança ou qualidade.

## Princípios

- Agente trabalha em uma fronteira clara.
- PR pequena é melhor que PR gigante.
- Contrato compartilhado não muda sem ADR.
- Mudança visível exige changelog.
- Módulo sensível exige revisão de segurança.
- Release sem assinatura ou sem rastreabilidade não é release.
- Toda entrega tem teste mínimo e critério de aceite.

## Papéis

### Orquestrador humano

- Prioriza roadmap.
- Aprova mudanças de escopo.
- Decide conflitos entre agentes.
- Faz validação operacional.

### RemoteOps Architect Agent

- Mantém arquitetura.
- Revisa contratos.
- Cria ADRs.
- Bloqueia duplicação entre módulos.

### Release Manager Agent

- Mantém versões.
- Mantém changelog.
- Gera checklist de release.
- Coordena tags e artefatos.

### Security Agent

- Revisa credenciais, NDesk, WinBox Runner, logs e assinatura.
- Mantém threat model.
- Define políticas padrão seguras.

## Branching

```text
main
feature/<modulo>-<descricao>
spike/<tema>
fix/<bug>
docs/<tema>
release/<versao>
```

Evitar branch longa. Fazer integração frequente atrás de feature flags.

## PRs

Cada PR deve ter:

- descrição objetiva;
- módulo afetado;
- prints ou gravação curta quando for UI;
- testes executados;
- riscos;
- impacto em segurança;
- atualização de docs/ADR/changelog;
- link para issue/tarefa;
- checklist de rollback quando aplicável.

## CODEOWNERS

Áreas sensíveis:

- `src/RemoteOps.Security/`: security obrigatório.
- `src/RemoteOps.NDesk/`: security + ndesk obrigatório.
- `src/RemoteOps.MikroTik/`: mikrotik + security quando envolver senha.
- `src/RemoteOps.Rdp/`: desktop + architect.
- `contracts/`: architect + backend.
- `.github/`: devops.
- `installer/` e `signing/`: devops + security.

## Changelog

Toda mudança visível entra no `CHANGELOG.md`. Exemplos:

- novo botão `Abrir WinBox`;
- novo modo de permissão NDesk;
- alteração no contrato de sync;
- correção de vazamento de log;
- mudança no instalador;
- alteração no requisito mínimo de Windows.

Mudanças internas pequenas podem ficar fora, desde que não alterem comportamento, contrato, segurança ou operação.

## ADR

Criar ADR quando:

- mudar stack;
- mudar contrato público;
- introduzir nova dependência crítica;
- mudar modelo de criptografia;
- mudar arquitetura NDesk;
- alterar política de senha WinBox;
- alterar compatibilidade Windows.

## Versionamento por componente

O repositório pode ser monorepo, mas releases devem identificar componente:

- Desktop;
- Cloud;
- NDesk Agent;
- NDesk Relay;
- WinBox Tool Bundle;
- Contracts.

## Feature flags

Usar flags para recursos de risco:

- `mikrotik.winbox.passwordArguments.enabled`;
- `ndesk.adminMode.enabled`;
- `ndesk.fileTransfer.enabled`;
- `sync.realtime.enabled`;
- `rdp.activeX.enabled`;
- `terminal.telnet.enabled`.

Flags devem ser armazenadas por tenant/grupo quando aplicável.

## Definition of Done

Uma tarefa só está concluída quando:

- compila;
- tem teste mínimo;
- não quebra contrato;
- não loga segredo;
- docs foram atualizadas;
- changelog foi atualizado quando aplicável;
- passou revisão do CODEOWNER;
- tem caminho de rollback ou feature flag para recurso arriscado.

## Checklist de release interna

- CI verde.
- Testes manuais críticos executados.
- Changelog revisado.
- Versão incrementada.
- Binários assinados.
- Hashes publicados internamente.
- Migrações testadas.
- Backup/rollback documentado.
- Smoke test em Windows 10/11.
- Smoke test em Windows 7 para NDesk legado quando houver mudança no agente.

## Política de dependências

- Preferir dependências com licença compatível.
- Registrar dependência crítica em `docs/15-pesquisa-e-spikes.md`.
- Rodar security scan no CI.
- Não adicionar dependência grande em módulo sensível sem ADR.
- Evitar dependência que obrigue instalação manual em máquina atendida pelo NDesk.
