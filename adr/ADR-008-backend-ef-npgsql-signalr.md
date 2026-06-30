# ADR-008 — EF Core + Npgsql + JWT Bearer + SignalR no backend RemoteOps.Cloud

## Status

Aceita

## Contexto

O módulo `RemoteOps.Cloud` (ASP.NET Core, `net10.0`) precisa evoluir de um esqueleto com `GET /health`
para um servidor completo que oferece:

- Persistência relacional em PostgreSQL (tenants, workspaces, usuários, sync changelog, auditoria).
- Autenticação JWT stateless com refresh token.
- Avaliação de RBAC server-side por workspace.
- Sync pull/push sobre `SyncChange` com change cursors.
- Hints em tempo real via WebSocket sem payload completo.

A política de projeto (`CLAUDE.md`) exige que toda lib externa seja justificada em ADR antes de ser
adicionada ao `.csproj`. As quatro libs abaixo são pré-requisito umas das outras e são documentadas
neste único ADR para evitar fragmentação.

O ADR-003 (aceito) já estabeleceu que o servidor **nunca descriptografa segredos** — armazena apenas
`SecretEnvelope` com ciphertext/nonce/algoritmo/key_version. Esta decisão mantém e reforça esse
princípio.

## Decisão

Adotar as seguintes dependências no `src/RemoteOps.Cloud`:

| Pacote | Versão âncora | Finalidade |
|---|---|---|
| `Microsoft.EntityFrameworkCore` | 10.x | ORM — migrations, DbContext, queries tipadas |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.x | Provider PostgreSQL nativo para EF Core |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.x | Middleware de validação de JWT |
| `Microsoft.AspNetCore.SignalR` | built-in ASP.NET Core 10 | Hub WebSocket para hints de sync |

E no `tests/RemoteOps.UnitTests`:

| Pacote | Finalidade |
|---|---|
| `Microsoft.EntityFrameworkCore.InMemory` | Provider em memória para testes unitários sem banco real |
| `Microsoft.AspNetCore.Mvc.Testing` | `WebApplicationFactory` para testes de integração de endpoints |

**SignalR** já está incluído no SDK do ASP.NET Core 10 e não exige NuGet separado; é listado aqui
apenas para documentação da decisão de uso.

## Consequências positivas

- **EF Core + Npgsql**: migrations geradas com `dotnet ef`, queries com LINQ tipado, sem SQL
  hand-rolled propenso a injeção. O suporte nativo do Npgsql a UUID (`Guid`), JSONB e arrays
  PostgreSQL simplifica o mapeamento das colunas `*_json`.
- **JWT Bearer**: integra nativamente com `IHttpContextAccessor` e `ClaimsPrincipal`; o middleware
  valida assinatura, expiração e audience sem código customizado. Chaves de assinatura injetadas via
  `IConfiguration` nunca ficam em `appsettings*.json`.
- **SignalR**: protocolo agnóstico (WebSocket, SSE, long-poll), grupos por workspace, broadcast
  tipado. O cliente Desktop (.NET) usa `Microsoft.AspNetCore.SignalR.Client` — mesma camada.
- **EF InMemory (testes)**: permite rodar testes RBAC/sync sem PostgreSQL local ou container,
  mantendo CI simples no Windows.

## Consequências negativas

- **EF Core** adiciona ~3 MB de dependências transitivas ao binário publicado.
- **Migrations** precisam ser aplicadas antes de cada deploy; adicionam etapa ao pipeline.
- **EF InMemory** não suporta transações reais, constraints FK nem funções PostgreSQL — testes de
  conflito de versão devem usar SQLite in-process se precisarem de comportamento mais fiel.
- **JWT stateless** significa que tokens não podem ser revogados antes da expiração sem uma lista
  de revogação (não implementada neste PR; logout revoga apenas o refresh token no banco).
- **SignalR** via WebSocket pode não funcionar em redes corporativas com proxy HTTP/1.1 — clientes
  fazem fallback automático para SSE/long-poll.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Dapper (micro-ORM) | SQL manual aumenta risco de injeção; sem migrations integradas |
| ADO.NET puro | Idem; exige mais código de infraestrutura sem ganho no MVP |
| Marten (PostgreSQL document store) | Bom para event-sourcing, mas muda o modelo de dados do docs/04 |
| Cookie-based auth | Não adequado para cliente Desktop nativo e sincronização multi-device |
| gRPC streaming em vez de SignalR | Mais complexo no cliente Desktop WPF; SignalR já inclui fallback |
| Testcontainers.PostgreSql | Requer Docker em CI Windows; EF InMemory é suficiente para testes unitários |

## Critérios de revisão

- Se o EF Core InMemory mostrar divergência de comportamento crítica vs. PostgreSQL (ex.: conflito
  de versão não detectado), substituir por SQLite in-process nos testes afetados.
- Se o volume de changelog exigir particionamento ou queries `COPY` nativas, avaliar Dapper pontual
  para as queries de hot path.
- Se tokens JWT precisarem de revogação imediata, introduzir Redis como deny-list (ver docs/10,
  Redis opcional) via novo ADR.
- Rever em 6 meses se Npgsql/EF Core 10 houver breaking changes após .NET 10 GA.
