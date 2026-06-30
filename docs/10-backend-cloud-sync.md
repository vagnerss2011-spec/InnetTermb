# 10 — Backend cloud, sync e broker

> **Estado atual (feature/cloud-backend):** EF Core + Npgsql, JWT Bearer, SignalR e endpoints auth/sync implementados em `src/RemoteOps.Cloud`. Ver `adr/ADR-010-backend-ef-npgsql-signalr.md` e `CHANGELOG.md [0.7.0-cloud-backend]` para detalhes de implementação.

## Configuração obrigatória (variáveis de ambiente)

| Variável | Descrição |
|---|---|
| `ConnectionStrings__Default` | Connection string PostgreSQL. Nunca em appsettings. |
| `Jwt__SigningKey` | Chave HMAC-SHA256 (≥ 32 bytes). Nunca commitada. |
| `Jwt__Issuer` | Issuer do JWT (ex.: `remoteops-cloud`). Pode estar em appsettings. |
| `Jwt__Audience` | Audience do JWT (ex.: `remoteops-desktop`). Pode estar em appsettings. |



## Objetivo

Fornecer backend central para autenticação, multiusuário, sincronização, auditoria e broker de assistência remota.

## Stack recomendada

- ASP.NET Core.
- PostgreSQL.
- SignalR/WebSocket.
- Redis opcional para cache, pub/sub e sessões efêmeras.
- OpenTelemetry.
- Docker para dev e produção.
- Reverse proxy: Caddy, Nginx ou Traefik.
- TLS obrigatório.

## Serviços lógicos

```mermaid
flowchart TB
  API[REST API] --> Auth[Auth/RBAC]
  API --> Sync[Sync Service]
  API --> Inventory[Inventory Service]
  API --> Cred[Credential Metadata Service]
  API --> Audit[Audit Service]
  API --> NDesk[NDesk Broker]
  Sync --> DB[(PostgreSQL)]
  Audit --> DB
  NDesk --> Redis[(Redis opcional)]
  Sync --> SignalR[SignalR Hub]
```

## APIs principais

### Auth

- `POST /auth/login`
- `POST /auth/refresh`
- `POST /auth/logout`
- `POST /devices/register`
- `POST /devices/revoke`

### Sync

- `GET /sync/pull?workspaceId=&cursor=`
- `POST /sync/push`
- `GET /sync/conflicts`
- `POST /sync/conflicts/{id}/resolve`

### Inventory

- `GET /workspaces/{id}/assets`
- `POST /workspaces/{id}/assets`
- `PUT /assets/{id}`
- `DELETE /assets/{id}`

### Credentials

- `POST /credentials`
- `PUT /credentials/{id}/metadata`
- `POST /credentials/{id}/rotate`
- `POST /credentials/{id}/grant`
- `POST /credentials/{id}/revoke`

### NDesk

- `POST /ndesk/tickets`
- `GET /ndesk/tickets/{id}`
- `POST /ndesk/tickets/{id}/expire`
- `POST /ndesk/signal`
- `GET /ndesk/ws`

## Modelo de autorização

Toda request deve carregar:

- Usuário autenticado.
- Device ID.
- Workspace/Tenant.
- Correlation ID.

Verificações:

- Usuário ativo.
- Dispositivo autorizado.
- Role permite ação.
- Política do grupo/host permite protocolo.
- Ação sensível requer aprovação se configurado.

## Sync em tempo real

SignalR envia apenas hints. Exemplo:

```json
{
  "event": "workspace.changed",
  "workspaceId": "01HX...",
  "cursor": 98123,
  "entityType": "asset",
  "entityId": "01HY..."
}
```

## Cliente de sincronização (Desktop, INT-5)

> **Estado (feature/integration-cloud-sync):** cliente implementado em `RemoteOps.Sync/Remote`,
> ligado ao Desktop atrás da feature flag. Ver `adr/ADR-013-cliente-sync-remoto.md`.

O Desktop sincroniza com este backend atrás da flag **`cloud.sync.enabled` (default OFF)**. Com OFF
o app é offline-first puro (sem rede). Com ON, o `SyncOrchestrator`:

1. drena o outbox local (`LocalSyncClient.PullAsync(outboxCursor)`) → `POST /sync/push`;
2. trata o `PushResult` (avança `outbox_cursor`; grava `ConflictDetail`; SecretEnvelope sem auto-merge);
3. `GET /sync/pull(serverCursor)` → aplica via `IRemoteChangeApplier` → avança `serverCursor`;
4. reage aos hints `workspace.changed` (SignalR) com pull incremental, e roda também por intervalo.

Toda request leva `Authorization: Bearer {JWT}` + `X-Device-Id`. Refresh automático em 401 + retry
único. Tokens guardados via vault (DPAPI/envelope) — nunca em texto puro. TLS sempre validado.

### Configuração do cliente (variáveis de ambiente, Desktop)

| Variável | Descrição |
|---|---|
| `REMOTEOPS_CLOUD_SYNC_ENABLED` | `true` liga o sync (feature flag; default OFF). |
| `REMOTEOPS_CLOUD_URL` | Base URL https do Cloud. |
| `REMOTEOPS_CLOUD_WORKSPACE_ID` | GUID do workspace no servidor. |

Estado exposto na UI (`MainViewModel.SyncStatus`): Offline / Sincronizando / Sincronizado
(+ contagem de conflitos) / Erro.

## NDesk broker

Responsabilidades:

- Gerar token curto de convite.
- Validar operador e agente.
- Controlar expiração.
- Trocar mensagens de signaling.
- Encaminhar mensagens para relay quando necessário.
- Registrar auditoria.

## Produção self-host

### Servidor mínimo

- VM Linux com IPv4 público e IPv6 público.
- Docker/Compose.
- PostgreSQL com backup.
- TLS via ACME.
- Firewall liberando apenas portas necessárias.
- Monitoramento e logs.

### Portas sugeridas

- 443/tcp: API, SignalR e signaling.
- 3478/udp/tcp: TURN/relay se adotado.
- 5349/tcp: TURN TLS se adotado.

## Backup

- Backup diário PostgreSQL.
- Backup de configuração do servidor.
- Backup separado de chaves de recuperação, com procedimento manual e auditável.
- Teste periódico de restore.

## Observabilidade

- Correlation ID em todas as requests.
- Métricas: latency, error rate, sync lag, active sessions.
- Logs estruturados sem segredos.
- Alertas: erro de sync, falha de login repetida, alteração de credencial, falha NDesk.
