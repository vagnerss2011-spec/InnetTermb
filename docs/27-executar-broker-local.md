# 27 — Executar o NDesk Broker localmente

Runbook para subir o `RemoteOps.NDesk.Broker` numa máquina de desenvolvimento e validar o
fluxo de signaling por HTTP. Complementa `docs/09-acesso-remoto-ndesk.md` e `adr/ADR-018`.

> O broker é um serviço ASP.NET Core cross-platform (roda em Windows, Linux e macOS). Só o
> **agente** e o **Desktop** são específicos de Windows.

## 1. Banco de dados

O broker usa PostgreSQL. Para dev, suba o Postgres via Docker:

```bash
docker compose -f deploy/docker-compose.dev.yml up -d
```

Isso expõe o Postgres em `localhost:5433` (base `ndesk`, usuário `postgres`, senha `dev`).

O schema é criado automaticamente pelo broker no startup (`EnsureCreated`, ver `Program.cs`).
Nenhuma migration manual é necessária para dev.

> **Nota (débito, ADR-018):** `EnsureCreated` é suficiente para MVP/dev, mas **não versiona**
> alterações de schema. Antes de produção ou de escalar horizontalmente, migrar para EF
> migrations versionadas (`dotnet ef migrations add`) e desligar o init automático com
> `NDESK_DB_SKIP_INIT=true`, deixando um migrador dedicado aplicar o schema.

## 2. Configuração (variáveis de ambiente)

Nenhum segredo fica em código — tudo vem do ambiente:

| Variável | Exemplo | Descrição |
|----------|---------|-----------|
| `ConnectionStrings__Default` | `Host=127.0.0.1;Port=5433;Database=ndesk;Username=postgres;Password=dev` | Conexão Postgres |
| `Jwt__SigningKey` | (≥32 bytes) | Chave HMAC do JWT do operador — **mesma** do RemoteOps.Cloud |
| `Jwt__Issuer` | `remoteops` | Emissor esperado |
| `Jwt__Audience` | `remoteops-ndesk` | Audiência esperada |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5080` | Bind local |
| `NDESK_DB_SKIP_INIT` | `true` | (opcional) pula o `EnsureCreated` quando um migrador externo cuida do schema |

## 3. Rodar

```bash
dotnet run -c Release --project src/RemoteOps.NDesk.Broker
curl http://127.0.0.1:5080/health      # -> {"status":"healthy"}
```

## 4. Smoke test do fluxo (validado)

Sequência exercitada de ponta a ponta contra um Postgres real. O operador precisa de um JWT
(HS256, assinado com `Jwt__SigningKey`, claim `sub` = GUID do usuário). Os endpoints do lado
atendido (`redeem`/`consent`/`revoke`) são anônimos por design — o agente ad-hoc não tem login.

| # | Chamada | Resultado esperado |
|---|---------|--------------------|
| 1 | `POST /ndesk/tickets` (Bearer) | `200` + `linkToken` (só aqui, uma vez) |
| 2 | `POST /ndesk/tickets` sem token | `401` |
| 3 | `GET /ndesk/tickets/{id}` de outro operador | `404` (anti-IDOR) |
| 4 | `POST /ndesk/tickets/redeem` (linkToken) | `200` + `sessionId`, ticket `connected` |
| 5 | redeem repetido | `409` (uso único) |
| 6 | `POST /ndesk/sessions/{id}/consent` excedendo o pedido | `422` |
| 7 | consent válido (subconjunto) | `200` |
| 8 | `POST /ndesk/sessions/{id}/revoke` | `204`, ticket `closed` |

### Invariantes de segurança confirmados no banco

- `LinkTokenHash` guardado como SHA-256 (64 hex) — o token cru **nunca** é persistido.
- Nenhum evento de `ndesk_audit_events` contém token/segredo em `MetadataJson`.
- O token cru não aparece em nenhum log do broker.

## 5. Parar

```bash
docker compose -f deploy/docker-compose.dev.yml down
```
