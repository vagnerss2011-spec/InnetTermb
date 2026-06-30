# ADR-002 — Sincronização offline-first

## Status

Proposta inicial.

## Contexto

Operadores precisam acessar hosts mesmo com conectividade instável. Alterações feitas por um usuário devem aparecer para os demais.

## Decisão

Usar modelo offline-first com banco local criptografado, outbox de mudanças, changelog no servidor e notificações SignalR/WebSocket.

## Consequências positivas

- App continua útil offline.
- Mudanças são rastreáveis.
- Sync é incremental.
- Conflitos podem ser resolvidos com versões.

## Consequências negativas

- Mais complexidade que CRUD online simples.
- Conflitos precisam de UX.
- Segredos exigem tratamento especial.

## Regras

- Servidor é autoridade para RBAC.
- Segredos não fazem merge automático.
- SignalR só notifica; cliente faz Pull.
