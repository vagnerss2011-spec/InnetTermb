# ADR-008 — SQLite/SQLCipher como storage local do sync (RemoteOps.Sync)

## Status

Aceita. Implementada na frente `feature/sync-local`.

## Contexto

O módulo `RemoteOps.Sync` precisa de um banco local para armazenar o outbox de mudanças
(`local_outbox`), cache de entidades (`local_entities`), cursor monotônico (`sync_cursor`)
e conflitos pendentes (`conflicts`) — conforme `docs/04-modelo-dados-sync.md`.

O banco deve ser:
- criptografado em repouso (proteção de notebook roubado, ADR-003);
- aberto sem dependência de servidor (offline-first, ADR-002);
- leve o suficiente para rodar no cliente Windows.

## Decisão

Usar **SQLite** como engine e **SQLCipher** como extensão de criptografia (AES-256-CBC),
integrados via NuGet:

```xml
<PackageReference Include="Microsoft.Data.Sqlite.Core" Version="9.0.6" />
<PackageReference Include="SQLitePCLRaw.bundle_sqlcipher" Version="2.1.10" />
```

`Microsoft.Data.Sqlite.Core` (sem o bundle padrão) combinado com
`SQLitePCLRaw.bundle_sqlcipher` substitui o engine SQLite pelo SQLCipher,
habilitando criptografia transparente em nível de arquivo.

### Derivação de chave

A chave do banco é passada via **PRAGMA raw hex** (não passphrase) como **primeiro
comando** após `Open()`:

```sql
PRAGMA key = "x'<64 hex chars / 32 bytes AES-256>'"
```

O formato `x'...'` usa bytes raw, evitando o round-trip PBKDF2 do modo passphrase
e garantindo que a chave derivada pelo vault seja usada diretamente.

### Proteção da chave (integração com ADR-003)

A chave AES-256 do banco local é gerada uma única vez por workspace (32 bytes CSPRNG),
armazenada no vault (`ICredentialVault`, DPAPI/envelope, ADR-003) e referenciada por
um arquivo `sync-{workspaceId}.keyref` que contém apenas o `envelopeId` — nunca o
material de chave em claro.

Fluxo de abertura:
1. Lê `envelopeId` de `sync-{workspaceId}.keyref`.
2. Chama `ICredentialVault.RetrieveSecretAsync(envelopeId)` → hexKey.
3. Executa `PRAGMA key = "x'{hexKey}'"` como primeiro comando.
4. Executa DDL (`CREATE TABLE IF NOT EXISTS ...`).

### Fallback documentado

Se `SQLitePCLRaw.bundle_sqlcipher` for substituído por `SQLitePCLRaw.bundle_e_sqlite3`
(SQLite padrão sem criptografia), o `PRAGMA key` é silenciosamente ignorado e o banco
fica não-cifrado. A abstração `IDbConnectionFactory` permite trocar o comportamento;
qualquer substituição exige novo ADR.

## Consequências positivas

- SQLite é battle-tested, disponível cross-platform, sem servidor.
- SQLCipher é amplamente usado, auditado, com licença BSD.
- Integração via NuGet oficial; sem P/Invoke adicional ao já usado em ADR-003.
- `IDbConnectionFactory` permite injetar implementações alternativas nos testes.

## Consequências negativas

- Dependência de biblioteca nativa (`.dll` do SQLCipher incluída no bundle).
- `PRAGMA key` deve ser o primeiro comando; ordem errada corrompe o banco.
- A chave hex chega como `string` imutável no cross-module thin contract — limitação
  conhecida documentada em ADR-003 § Limitações.

## Alternativas consideradas

- **EF Core + SQLite**: rejeitado por overhead de ORM desnecessário para outbox simples.
- **LiteDB**: rejeitado por falta de suporte nativo a SQLCipher e menor adoção.
- **Criptografia na camada de aplicação (campo a campo)**: rejeitado; aumentaria complexidade
  e deixaria metadados (entityType, entityId) legíveis em repouso.
- **Proteção só via DPAPI de arquivo (não SQLCipher)**: rejeitado; ACLs de SO são
  mais frágeis que criptografia de conteúdo verificada pelo engine.

## Implementação

- `src/RemoteOps.Sync/Storage/SqliteConnectionFactory.cs`: abre conexão + PRAGMA key.
- `src/RemoteOps.Sync/Storage/VaultDbKeyProvider.cs`: gera/recupera chave via vault.
- `src/RemoteOps.Sync/LocalSyncClient.cs`: DDL e operações de outbox.
- `tests/RemoteOps.UnitTests/Sync/SyncClientSecurityTests.cs`: verifica que o arquivo
  `.db` é ilegível sem a chave do vault.

## Regras derivadas

- A chave nunca aparece em log, exceção, fixture ou commit.
- O arquivo `.keyref` contém apenas o `envelopeId` (não o segredo).
- Qualquer mudança de engine (SQLite → outro) exige novo ADR.
- Mudança do schema local (tabelas/colunas) exige atualização de `docs/04-modelo-dados-sync.md`.
