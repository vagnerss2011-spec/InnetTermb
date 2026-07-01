# ADR-020 — NDesk: operador descobre o `sessionId` pelo status do ticket

## Status

Aceita. Complementa `ADR-018` (API de signaling do broker).

## Contexto

No fluxo do NDesk (`docs/09`, `ADR-018`), o operador emite um ticket e o compartilha
(link token) com o usuário atendido fora de banda. O agente resgata o ticket
(`POST /ndesk/tickets/redeem`) e recebe um `sessionId` (GUID aleatório) — e só a partir daí a
sessão de signaling existe. Para entrar no hub (`JoinSession`), **os dois lados precisam do
`sessionId`**.

O problema, descoberto ao exercitar o broker de verdade com `tools/ndesk-signaling-check`: o
`sessionId` só era devolvido na resposta do resgate, ao **agente**. O **operador** — que criou
o ticket — não tinha como obtê-lo: `GET /ndesk/tickets/{id}` (o único canal do operador)
retornava o `NDeskTicket` **sem** o `sessionId`. Resultado: o operador não conseguia entrar no
signaling, travando o fluxo operador↔agente entre máquinas distintas.

## Decisão

Adicionar o campo **`sessionId`** ao contrato `NDeskTicket`
(`contracts/ndesk-ticket.schema.json` + `src/RemoteOps.Contracts/NDesk/NDeskTicket.cs`),
opcional (nulo enquanto o ticket está `waiting`, populado após o resgate quando vira
`connected`).

`GET /ndesk/tickets/{id}` passa a devolvê-lo, e como esse endpoint já é **escopado ao criador**
do ticket (`GetStatusAsync` retorna `null`/404 para qualquer outro usuário — anti-IDOR, ver
`ADR-018`), o `sessionId` só chega a quem tem direito: o operador criador (via status
autenticado) e o agente (via resposta do resgate).

## Consequências

### Positivas
- Destrava o fluxo real: o operador faz *polling* do status até `connected` e obtém o
  `sessionId` para entrar no `JoinSession`.
- Mudança **aditiva e retrocompatível**: campo opcional; clientes existentes ignoram.
- Nenhum segredo novo é exposto — o `sessionId` é um GUID de sessão (não um token de acesso a
  credencial), e o gate de consentimento continua sendo o controle real antes de qualquer
  `SendSignal` (`ADR-018`). O `link token` continua saindo **uma única vez** na emissão e
  nunca é persistido em claro.

### Negativas / a rastrear
- O `sessionId` passa a funcionar como *bearer* dos endpoints anônimos do lado atendido
  (`consent`/`revoke`/`telemetry`), como já era o caso (`ADR-018` §débitos). Expô-lo ao
  operador não amplia a superfície além do que o próprio resgate já fazia com o agente.
- *Polling* de status é a forma mais simples agora; um push pelo próprio hub (notificar o
  operador quando o agente resgata) é uma evolução possível, fora do escopo deste ADR.

## Validação

`tools/ndesk-signaling-check` passou a **descobrir o `sessionId` pelo status** (sem atalho
interno) antes de entrar no hub, e o loop operador↔agente completa de ponta a ponta contra um
broker real (relay de SDP/ICE, recusa sem consentimento e após revogação).

## Referências

- `adr/ADR-018-ndesk-signaling-api.md`
- `contracts/ndesk-ticket.schema.json`, `src/RemoteOps.Contracts/NDesk/NDeskTicket.cs`
- `tools/ndesk-signaling-check/`
