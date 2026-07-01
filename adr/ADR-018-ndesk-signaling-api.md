# ADR-018 — NDesk Broker: API de tickets e protocolo de signaling

## Status

Aceita. Implementada na frente `feature/ndesk-broker-signaling`.

## Contexto

`docs/09-acesso-remoto-ndesk.md` e `docs/22-ndesk-performance-legacy-windows.md` já descrevem
o papel do Broker/Signaling (emitir convites, parear operador/agente, trocar SDP/ICE, nunca
transportar mídia) e `ADR-015`/`ADR-016` confirmam a decisão de construir o NDesk in-house em
.NET. Faltava a API concreta: como um ticket é emitido e resgatado, como o consentimento
(`contracts/ndesk-permission-grant.schema.json`) é validado antes de liberar uma sessão, e qual
o protocolo de signaling entre operador e agente. Este ADR define esse contrato de API/protocolo
novo, conforme exige a regra 4 do `CLAUDE.md` ("toda mudança de contrato, schema, API ou
criptografia exige ADR").

Os três contracts já existentes (`ndesk-ticket`, `ndesk-permission-grant`,
`ndesk-session-telemetry`) não mudam de schema — o broker mapeia diretamente para os POCOs já
existentes em `src/RemoteOps.Contracts/NDesk/*.cs`. `ADR-016` (PR #30, ainda não mesclada em
`main` no momento desta implementação) não afeta esses contratos nem o desenho do broker —
apenas a tecnologia do agente/captura, fora do escopo desta ADR.

## Decisão

### 1. Ciclo de vida do ticket (`src/RemoteOps.NDesk.Broker/Tickets`)

- `POST /ndesk/tickets` (operador autenticado via JWT, mesmo emissor do `RemoteOps.Cloud`):
  emite um ticket `waiting` com TTL curto (padrão 10 min, máximo 30 min — acima disso o pedido
  é silenciosamente clampado ao padrão). O **link token** é gerado com
  `RandomNumberGenerator` (32 bytes, base64url), devolvido **uma única vez** no corpo desta
  resposta, e **nunca** persistido em claro — só o hash SHA-256 (`LinkTokenHash`), no mesmo
  padrão já usado por `RefreshTokenEntity.TokenHash` em `RemoteOps.Cloud`. Nenhum log do broker
  imprime o valor cru do token (`NoSecretInLogTests` no projeto de testes é o guarda de
  regressão).
- `GET /ndesk/tickets/{id}` (operador autenticado): status atual do ticket, **escopado ao
  operador que o criou** (`CreatedByUserId`) — consultar o ticket de outro operador devolve 404,
  não 403, para não expor um oráculo de enumeração. Nunca devolve o link token.
- `POST /ndesk/tickets/redeem` (anônimo — o usuário assistido não tem conta): recebe o link
  token cru, valida hash + TTL + **uso único** (status precisa ser `waiting`) e transiciona para
  `connected`, atribuindo um `sessionId` novo. Resgate de um token já usado retorna 409;
  expirado retorna 410; desconhecido retorna 404 — em nenhum desses casos o token aparece em
  log ou resposta.
- Estados: `waiting → connected → {closed | denied}`, ou `waiting → expired` por TTL. Todas as
  transições são auditadas (`NDeskAuditService`, ação `ndesk.ticket.*`).

### 2. Consentimento como gate obrigatório (`src/RemoteOps.NDesk.Broker/Consent`)

- `POST /ndesk/sessions/{sessionId}/consent` (anônimo, chamado pelo agente após a tela de
  consentimento do usuário assistido): grava `NDeskPermissionGrant`. **Nunca concede mais do
  que o ticket solicitou** — `mode`/`permissions` são validados como subconjunto do que foi
  pedido em `POST /ndesk/tickets`; excesso é recusado com 422.
- `POST /ndesk/sessions/{sessionId}/consent/deny`: fecha o ticket como `denied`, audita
  `ndesk.consent.denied`.
- `POST /ndesk/sessions/{sessionId}/revoke`: revoga o grant e fecha a sessão imediatamente —
  chamável por qualquer lado (usuário assistido ou operador), conforme "revogação imediata"
  exigida pelo `CLAUDE.md` princípio 3.
- `NDeskPermissionGrantService.IsSessionAuthorizedAsync(sessionId)` é o **gate único**:
  retorna `true` somente se existir um grant não-revogado e não-expirado para a sessão. Todo
  outro componente que precisa decidir "esta sessão pode prosseguir?" consulta este método —
  não há segunda implementação da regra em outro lugar.

### 3. Signaling (`src/RemoteOps.NDesk.Broker/Signaling/NDeskSignalingHub`, SignalR em `/hubs/ndesk`)

- `JoinSession(sessionId, role)`: operador (`role="operator"`, exige JWT do criador do ticket)
  ou agente (`role="agent"`, anônimo) entram no grupo SignalR da sessão. Exige ticket com
  status `connected` — sessões encerradas não podem ser reabertas.
- `SendSignal(sessionId, type, payload)`: repassa um envelope **opaco** (`type` ∈
  `sdp-offer | sdp-answer | ice-candidate`, `payload` = string opaca de SDP/ICE) para o outro
  lado do grupo. **Chama `IsSessionAuthorizedAsync` antes de cada envio** e lança
  `HubException` se não houver grant válido — o broker recusa a sessão em vez de repassar.
  O broker nunca inspeciona, transcodifica ou armazena o conteúdo do `payload` — é só
  rendezvous, mídia real trafega direto entre operador/agente (P2P) ou por um relay dedicado
  fora do escopo deste ADR (`docs/09` §Relay).
- `EndSession(sessionId, reason)`: revoga o grant, fecha o ticket e notifica o grupo.
- `POST /ndesk/sessions/{sessionId}/revoke` (REST, equivalente HTTP do `EndSession`):
  intencionalmente anônimo — o agente não tem conta, só o `sessionId` (mesmo nível de confiança
  já aceito no Hub). Precisamente por não poder autenticar o chamador, o `revokedBy` gravado na
  auditoria **nunca** vem do corpo da requisição — é sempre derivado do `ClaimsPrincipal` quando
  autenticado, ou do literal `"assisted-user"` quando anônimo, para não permitir forjar o autor
  no log de auditoria.

### 4. Telemetria (`src/RemoteOps.NDesk.Broker/Telemetry`)

- `POST /ndesk/sessions/{sessionId}/telemetry`: grava uma amostra
  (`contracts/ndesk-session-telemetry.schema.json`) por chamada. Campos são estritamente os do
  schema (rota, RTT, perda, bitrate, FPS, codec, CPU/memória do agente) — nenhum campo aceita
  conteúdo de tela, input ou payload de aplicação.

### 5. Persistência e auditoria

- `NDeskDbContext` (EF Core + Npgsql em produção, InMemory nos testes — mesmo padrão de
  `RemoteOps.Cloud`/`ADR-010`) com quatro tabelas: `ndesk_tickets`, `ndesk_permission_grants`,
  `ndesk_session_telemetry`, `ndesk_audit_events`.
- `NDeskAuditService` sanitiza metadata bloqueando chaves com `password`/`secret`/`token`/
  `key`/`credential`/`plaintext`/`hash`/`linktoken` (mesmo padrão de
  `RemoteOps.Cloud.Audit.AuditService`), registrando `[REDACTED]` no lugar do valor.

## Consequências positivas

- Nenhuma sessão de signaling é alcançável sem passar pelos três portões em sequência: ticket
  válido (TTL + single-use) → consentimento explícito (subconjunto do solicitado) → gate
  checado a cada `SendSignal`. Os três são testados isoladamente e em conjunto.
- Superfície pequena e sem estado compartilhado além do banco — o broker não guarda estado de
  mídia, só metadados de sessão/consentimento/telemetria.
- Reaproveita integralmente os contracts e o padrão de projeto/teste já validados em
  `RemoteOps.Cloud`/`RemoteOps.Sync` — sem drift de convenção entre módulos backend.

## Consequências negativas

- **Redeem/single-use não é atômico sob concorrência real (múltiplas instâncias do broker).**
  A implementação atual faz leitura-verificação-escrita no `NDeskTicketService`, correta para
  uma instância única (MVP), mas uma corrida entre duas requisições de redeem simultâneas em
  instâncias diferentes do broker poderia, em teoria, resgatar o mesmo ticket duas vezes.
  Mitigação futura: `ExecuteUpdateAsync` com `WHERE status = 'waiting'` (compare-and-swap no
  banco) antes de qualquer deploy horizontal — registrado aqui como débito conhecido, não
  bloqueante para o MVP de instância única.
- **[RISCO ABERTO, RASTREADO — não é aceitação silenciosa]** O broker confia no claim `sub` do
  JWT do operador para autorizar `POST /ndesk/tickets` e `JoinSession(role: "operator")`, mas
  **não** revalida a associação `workspaceId ↔ operador` contra uma tabela de `Memberships`
  (que vive só em `RemoteOps.Cloud`, propositalmente não referenciado aqui para não misturar
  módulos/acoplar deployments). Um operador autenticado pode hoje declarar qualquer
  `workspaceId` no corpo do pedido de emissão — isso foi confirmado como achado HIGH (IDOR /
  multi-tenant scoping) em revisão de segurança automática desta mesma PR.
  - **Blast radius mitigado, não eliminado, nesta PR:** `GET /ndesk/tickets/{id}` agora escopa
    a consulta ao `CreatedByUserId` do ticket (ver seção 1) — um operador de outro workspace não
    consegue *ler* tickets de terceiros só adivinhando o GUID. O que permanece possível é
    *criar* um ticket rotulado com um `workspaceId` que não é seu, poluindo a atribuição de
    auditoria daquele workspace. Isso **não** concede acesso a credenciais/dados do workspace
    alvo — a capacidade real de controle remoto continua exigindo que um humano do lado
    assistido resgate o ticket e conceda consentimento explícito, fora de banda.
  - **Bloqueante antes de:** (a) qualquer UI/relatório que confie em "listar tickets por
    workspace" para decisões de segurança; (b) exposição deste endpoint fora da rede interna
    confiável de operadores autenticados. Fechar exige uma de duas rotas: referenciar
    `Memberships` do `RemoteOps.Cloud` (acopla os módulos) ou o `RemoteOps.Cloud` passar a
    emitir um claim de workspace verificável no JWT (mudança no emissor, fora do escopo deste
    ADR) — decisão a tomar antes do próximo passo do NDesk que dependa de isolamento de tenant.
- `SendSignal` não faz nenhuma validação de forma sobre `payload` além de ser uma string — é
  deliberado (o broker não deve interpretar SDP/ICE), mas significa que um payload malformado
  só é detectado pelo lado receptor (operador/agente), não pelo broker.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Persistir o link token em claro para simplificar o redeem | Viola CLAUDE.md princípio 1; o hash SHA-256 com comparação exata já é O(1) via índice único, sem custo real. |
| Gate de consentimento verificado só no `JoinSession`, não em cada `SendSignal` | Uma revogação a meio da sessão (`RevokeConsentAsync`) só teria efeito na próxima reconexão — contraria "revogação imediata" do CLAUDE.md. Verificar a cada `SendSignal` é mais caro (uma leitura extra por mensagem) mas é o único jeito de garantir revogação instantânea. |
| Broker relay de mídia (fallback quando P2P falha) nesta mesma API | Fora de escopo — `docs/09` trata Relay como componente separado; misturar mídia no broker de signaling contradiria a decisão central "broker nunca passa mídia". |
| Endpoints de consentimento/telemetria autenticados por JWT de operador | O usuário assistido não tem conta RemoteOps — só o link token da sessão; exigir JWT inviabilizaria o fluxo ad-hoc descrito em `docs/09`. |

## Critérios de revisão futura

- Se o broker for implantado em múltiplas instâncias (horizontal scaling), implementar o
  compare-and-swap de `RedeemTicketAsync` antes do rollout (ver "Consequências negativas").
- Se o NDesk precisar de RBAC por workspace equivalente ao `RemoteOps.Cloud`, decidir entre
  referenciar `Memberships` do Cloud (acopla os módulos) ou replicar um subconjunto mínimo no
  broker.
- Revisar quando `SPIKE-017`/`ADR-017` (stack de transporte de mídia) for concluído — pode
  introduzir mensagens de signaling adicionais (ex.: negociação de relay TURN) que estendam
  `SendSignal`.

## Referências

- `docs/09-acesso-remoto-ndesk.md` §Broker/Signaling, §Consentimento, §Auditoria.
- `docs/22-ndesk-performance-legacy-windows.md` §Broker/Signaling, §Telemetria obrigatória.
- `contracts/ndesk-ticket.schema.json`, `contracts/ndesk-permission-grant.schema.json`,
  `contracts/ndesk-session-telemetry.schema.json`.
- `adr/ADR-005-acesso-remoto-webrtc.md`, `adr/ADR-015-ndesk-buy-vs-build.md`,
  `adr/ADR-016-ndesk-pivo-win10-net.md` (PR #30, referenciada — sem dependência de código).
- `adr/ADR-010-backend-ef-npgsql-signalr.md` — padrão de backend (EF+Npgsql+SignalR) reaproveitado.
