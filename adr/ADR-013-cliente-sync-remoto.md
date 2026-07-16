# ADR-013 — Cliente de sincronização remoto (Desktop ↔ Cloud)

## Status

Aceita. Implementada na frente `feature/integration-cloud-sync` (INT-5).

> **Atualização (Fase 1 do cloud sync E2EE):** o gatilho previsto em "Critérios de revisão"
> foi acionado, e a decisão de landing-zone do pull foi **substituída pelo `ADR-021`**: o
> applier materializa nas **tabelas de domínio** e `local_entities` virou quarentena de tipo
> desconhecido. O restante deste ADR (orquestrador, cursores, conflitos, tokens no vault,
> flag) continua valendo como escrito.

## Contexto

O backend Cloud (ADR-010) já expõe `GET /sync/pull`, `POST /sync/push`, `POST /auth/login`,
`POST /auth/refresh` e o hub SignalR `SyncHub` (hint `workspace.changed`). O storage local
(ADR-008) já mantém o outbox (`local_outbox`), o cache de entidades (`local_entities`), o
cursor (`sync_cursor`) e a tabela `conflicts`, com toda mutação do `SqlCipherLocalStore`
(INT-3) gravando no outbox.

Falta a peça que liga os dois lados: drenar o outbox local e empurrar para o servidor,
puxar o changelog do servidor e aplicar localmente, reagir a hints em tempo real e refletir
o estado na UI. Os princípios obrigatórios continuam valendo: **offline-first** (ADR-002),
**segredos nunca descriptografados no servidor / sem auto-merge** (ADR-003), **nenhum
token/segredo em texto puro ou log** (CLAUDE.md). A lógica precisa ser **testável no CI sem
WPF/WebView2**, portanto mora em `RemoteOps.Sync` (cross-platform `net10.0`), não no Desktop.

## Decisão

1. **DTOs compartilhados em `RemoteOps.Contracts.Sync`.** `PullResponse`, `PushRequest`,
   `PushResult` e `ConflictDetail` saem de `RemoteOps.Cloud/Sync/SyncModels.cs` para
   `RemoteOps.Contracts/Sync/` (onde `SyncChange` já vive). A forma JSON é idêntica
   (records posicionais, mesmas propriedades) — compatibilidade total da API.

2. **`CloudSyncApiClient` em `RemoteOps.Sync`** sobre `HttpClient` com `HttpMessageHandler`
   **injetável** (testes usam handler fake, sem servidor real). Toda request carrega
   `Authorization: Bearer {JWT}` **e** `X-Device-Id: {Guid}`. Em `401`, faz refresh
   automático do token e **um único retry**. TLS é sempre validado — a verificação de
   certificado **nunca** é desabilitada.

3. **`SyncOrchestrator` em `RemoteOps.Sync`.** Ciclo: drena o outbox via
   `LocalSyncClient.PullAsync(outboxCursor)` → `POST /sync/push` → trata `PushResult`
   (avança `outbox_cursor`; grava `ConflictDetail` em `conflicts`) → `GET /sync/pull`
   a partir do `serverCursor` → aplica via `IRemoteChangeApplier` → avança `serverCursor`.
   Expõe um callback de estado (`Offline | Syncing | Synced | Error`) e a contagem de
   conflitos.

4. **`IRemoteChangeApplier` (interface em `RemoteOps.Sync`).** A implementação canônica
   `LocalEntitiesChangeApplier` aplica cada `SyncChange` (`created`/`updated`/`deleted`) na
   tabela `local_entities` do mesmo banco SQLCipher (via `WorkspaceContext`), de forma
   **idempotente** e **sem re-emitir no outbox** (evita loop de eco). Por morar em
   `RemoteOps.Sync` é testável no CI. O Desktop pode compor um applier que, além disso,
   reflita nas tabelas de domínio para atualização viva da UI (ver *Critérios de revisão*).

5. **Cursores.** `sync_cursor.cursor` guarda o `serverCursor` (último `changelog.id`
   aplicado). Para o outbox enviado, adiciona-se a coluna **`sync_cursor.outbox_cursor`**
   (último `local_outbox.id` confirmado pelo servidor). Migração **aditiva e idempotente**:
   `ALTER TABLE sync_cursor ADD COLUMN outbox_cursor INTEGER NOT NULL DEFAULT 0`, aplicada
   só quando `PRAGMA table_info` indica ausência da coluna.

6. **Conflitos.** A tabela `conflicts` é **estendida** (idem, via `ALTER ADD COLUMN`) com
   `client_change_id`, `base_version`, `current_version` e `reason` para registrar
   `ConflictDetail`. `SecretEnvelope` **nunca** sofre merge automático no cliente — o
   servidor devolve `reason = "secret-envelope.no-auto-merge"` e o cliente apenas registra,
   sem resolver. As colunas legadas `local_patch_json`/`server_patch_json` (NOT NULL) recebem
   `'{}'` quando a origem é um `ConflictDetail` do servidor (sem patch local).

7. **Tokens via vault.** `ITokenStore` abstrai persistência; `VaultTokenStore` guarda o
   conjunto `{access, refresh, expiresAt}` como **um segredo no vault** (`ICredentialVault`,
   DPAPI/envelope, ADR-003) e persiste apenas o `envelopeId` num arquivo `*.tokenref` — nunca
   o token em claro, em arquivo ou log. Rotação revoga o envelope anterior.

8. **Cliente SignalR atrás de `ISyncHintChannel`.** A implementação concreta usa
   `Microsoft.AspNetCore.SignalR.Client` (a "mesma camada" prevista na ADR-010): conecta,
   chama `JoinWorkspace(workspaceId)` e, ao receber `workspace.changed`, dispara um pull
   incremental. A interface mantém o orquestrador testável sem hub real. **Nova dependência
   NuGet** em `RemoteOps.Sync`, justificada aqui conforme CLAUDE.md.

9. **Feature flag `cloud.sync.enabled` (default OFF).** Com OFF o app é **idêntico** ao
   atual (offline-first puro, zero rede). Com ON o orquestrador roda em background (intervalo
   + on-demand) e reage aos hints. Recurso sensível → flag + revisão do `security-agent`.

## Consequências positivas

- Toda a lógica de rede/sync isolada em `RemoteOps.Sync` (`net10.0` cross-platform) →
  testável no CI sem WPF/WebView2.
- `HttpMessageHandler` injetável e `ISyncHintChannel`/`IRemoteChangeApplier` por interface →
  testes determinísticos, sem servidor nem hub reais.
- DTOs em `Contracts` → cliente e servidor compartilham a forma exata; menos drift de schema.
- Migração aditiva (`ALTER ADD COLUMN` guardado por `PRAGMA table_info`) → bancos já criados
  pelo INT-3 abrem sem recriação nem perda de dados.
- Flag OFF por padrão → risco zero para o app atual; rollout controlado.
- Tokens só no vault → nenhuma credencial em claro em disco ou log.

## Consequências negativas

- `RemoteOps.Sync` ganha dependência transitiva de `Microsoft.AspNetCore.SignalR.Client`.
- A tabela `conflicts` passa a ter dois formatos de linha (patch legado vs. `ConflictDetail`);
  documentado em `docs/04`.
- JWT stateless: refresh em `401` implica um retry extra; se o refresh falhar, o estado vira
  `Error` e o ciclo entra em backoff.
- ~~O `LocalEntitiesChangeApplier` aplica no cache `local_entities`, que as tabelas de domínio
  do `SqlCipherLocalStore` não leem — a reflexão viva na UI fica como evolução (abaixo).~~
  **Resolvido pelo `ADR-021`** (Fase 1): o applier materializa nas tabelas de domínio. O que
  resta é o refresh vivo da UI, agendado para a Fase 2.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Cliente HTTP no `RemoteOps.Desktop` | Não testável no CI sem WPF; acopla rede à UI. |
| `HttpClient` com handler fixo | Impede teste do fluxo push/pull/401 sem servidor real. |
| Tokens no banco SQLCipher (sem vault) | Spec exige vault/DPAPI; banco já é cifrado, mas o envelope do vault é a fronteira correta para credenciais. |
| Linha dedicada em `sync_cursor` para o outbox | Coluna `outbox_cursor` é mais simples e idempotente que uma 2ª linha sentinela. |
| Nova tabela `sync_conflicts` | Spec pede gravar em `conflicts`; estender evita divergência de nome. |
| Polling puro (sem SignalR) | Latência maior; a ADR-010 já prevê o hub. Mantemos polling como fallback do flag. |

## Critérios de revisão

- ~~Se a UI precisar refletir mudanças remotas em tempo real, compor no Desktop um
  `IRemoteChangeApplier` que mapeie `SyncChange` → tabelas de domínio (`assets`, `endpoints`,
  `credential_refs`, `asset_groups`) do `SqlCipherLocalStore`, mantendo idempotência e sem
  re-emitir outbox. O `local_entities` permanece como landing-zone canônica do pull.~~
  **Acionado e resolvido pelo `ADR-021`**, com dois desvios documentados lá: o applier ficou em
  `RemoteOps.Sync` (não no Desktop — testabilidade no CI sem WPF, o mesmo critério que este ADR
  usou para manter o cliente HTTP fora do Desktop), e `local_entities` **deixou** de ser a
  landing-zone canônica (virou quarentena de tipo desconhecido).
- Se o changelog crescer muito, avaliar paginação/backoff adaptativo no orquestrador.
- Se a revogação imediata de token for necessária, alinhar com a deny-list prevista na ADR-010.
- Rever a granularidade da flag (`cloud.sync.enabled`) caso seja preciso ligar pull sem push
  (modo somente-leitura) por workspace.
