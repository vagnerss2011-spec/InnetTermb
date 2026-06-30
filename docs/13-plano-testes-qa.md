# 13 — Plano de testes e QA

## Camadas de teste

### Unitários

- Serviços de domínio.
- Resolução IPv6/IPv4.
- Merge/conflict resolution.
- RBAC.
- Sanitização de logs.
- Serialização de contratos.

### Integração

- Backend + PostgreSQL.
- Sync push/pull.
- SignalR notifications.
- Vault local + DB.
- SSH contra container/lab.
- RouterOS CHR/lab quando disponível.

### E2E Desktop

- Criar grupo/host/credencial.
- Abrir SSH.
- Abrir RDP.
- Sincronizar dois clientes.
- Resolver conflito.

### Segurança

- Verificar ausência de plaintext no DB local.
- Verificar ausência de segredos em logs.
- Testar revogação de usuário/dispositivo.
- Testar host key/cert alterado.
- Testar permissões negadas.

#### Cofre de credenciais (vault)

Plano registrado em `docs/25-credential-vault.md` (tabela de rastreabilidade critério → teste). Cobertura em `tests/RemoteOps.UnitTests/Security/`:

- Round-trip criptografa/descriptografa, inclusive segredo vazio.
- Envelope persistido nunca contém o plaintext (varredura de JSON e de bytes).
- Adulteração de ciphertext é detectada pelo AEAD (GCM).
- Restart de processo preserva o segredo (mesma identidade DPAPI).
- Outro usuário/máquina NÃO abre o segredo (modelado cross-platform; DPAPI real no `windows-latest`).
- Rotação incrementa versão e revoga o envelope anterior; revogação impede recuperação.
- Auditoria registra o ciclo de vida sem nenhum segredo; `VaultSecret` redigido e seguro após `Dispose`.

#### Cloud backend (auth, RBAC, sync, auditoria)

Cobertura em `tests/RemoteOps.UnitTests/Cloud/` (EF InMemory, sem credentials reais):

**RbacTests (11 cenários)**

| Cenário | Tipo | Critério |
|---|---|---|
| Owner tem `asset.read` | allow | `result.Granted == true` |
| Usuário inativo é bloqueado | deny | `reason == "user.inactive"` |
| ReadOnly não tem `asset.create` | deny | `reason == "role.not-granted"` |
| Operator tem `sync.pull` | allow | `result.Granted == true` |
| ReadOnly não tem `sync.push` | deny | `reason == "role.not-granted"` |
| Deny explícito em membership vence Owner | deny | `reason == "member.explicit-deny"` |
| Grant explícito em membership expande ReadOnly | allow | `result.Granted == true` |
| Deny de grupo bloqueia Operator com `session.ssh.open` | deny | `reason == "group.explicit-deny"` |
| Device revogado bloqueia acesso | deny | `reason == "device.revoked"` |
| Workspace suspenso bloqueia | deny | `reason == "workspace.inactive"` |
| Tenant mismatch bloqueia cross-workspace | deny | `reason == "workspace.cross-tenant"` |
| Usuário sem membership bloqueia | deny | `reason == "membership.missing"` |

**SyncTests (6 cenários)**

| Cenário | Tipo | Critério |
|---|---|---|
| Pull retorna mudanças desde cursor com paginação | positivo | `count == pageSize, hasMore == true` |
| Pull retorna vazio quando não há mudanças após cursor | positivo | `empty, hasMore == false` |
| Pull negado para usuário sem membership | negativo | `RbacDeniedException` |
| Push sem conflito aplica e retorna `ok` | positivo | `status == "ok", version == 1` |
| Push com BaseVersion defasado retorna conflito | negativo | `status == "conflict", reason == "version.conflict"` |
| Push idempotente: mesmo ClientChangeId não duplica | positivo | `count(changelog) == 1` |
| Push de SecretEnvelope sempre conflito | negativo | `reason == "secret-envelope.no-auto-merge"` |

**AuditTests (4 cenários)**

| Cenário | Tipo | Critério |
|---|---|---|
| Evento é persistido com action/workspace/actor corretos | positivo | DB tem 1 evento |
| Metadata com chave "password" é sanitizada para `[REDACTED]` | segurança | JSON não contém valor original |
| Metadata null não lança exceção | borda | `metadataJson == "{}"` |
| ToContractEvent mapeia para tipo canônico | positivo | `action`, `targetType`, `targetId` corretos |

**AuthTests (2 cenários — remediação security-review)**

| Cenário | Tipo | Critério |
|---|---|---|
| `RefreshAsync` bloqueado quando device revogado | segurança | `result == null`, `stored.RevokedAt != null` |
| `RefreshAsync` bloqueado quando device não existe no DB | segurança | `result == null` |

**Checklist de segurança (cloud-backend)**

- [x] Logs não contêm segredo (user/password nunca logados; apenas hash do email)
- [x] Permissões negadas são testadas (11 cenários RbacTests)
- [x] Erros retornam `application/problem+json` com `correlationId` — sem stack trace
- [x] Ações sensíveis geram `AuditEvent` (`sync.push`, `auth.login`, `credential.rotate`)
- [x] `SecretEnvelopeEntity` armazena apenas ciphertext/nonce/tag/algorithm/keyVersion (sem WDK/CEK)
- [x] Fixtures de teste sem credentials reais (InMemory DB, sem connection string)
- [x] Refresh token armazenado como SHA-256 hash — vazamento do banco não permite uso
- [x] `AuditService.SanitizeMetadata` remove "password", "secret", "token", "key", "credential", "plaintext", "hash"
- [x] `RefreshAsync` verifica status do device — device revogado invalida refresh token (AuthTests)
- [x] `SyncHub.JoinWorkspace` verifica membership — usuário sem acesso não entra no grupo SignalR
- [x] Sync endpoints exigem `X-Device-Id` header — ausência retorna 400 (device revocation enforced)

**Testes de integração pendentes (requerem PostgreSQL/CI)**

- [ ] `dotnet ef migrations add InitialCreate` + `dotnet ef database update` aplicam schema sem erro
- [ ] `GET /health` responde 200 com PostgreSQL real
- [ ] `POST /auth/login` emite JWT válido; `/auth/refresh` rotaciona refresh token
- [ ] SignalR hub `/hubs/sync` aceita conexão autenticada; `JoinWorkspace` agrupa o cliente
- [ ] `POST /sync/push` emite hint `workspace.changed` via SignalR ao grupo correto
- [ ] Grep no diff: nenhum segredo em `appsettings*.json`, fixtures ou logs de teste

### NDesk

- Consentimento obrigatório.
- Encerrar sessão pelo cliente.
- Token expirado não conecta.
- Controle não funciona sem permissão.
- Auditoria completa.
- Teste em redes diferentes.

## Ambientes de teste

- Windows 10.
- Windows 11.
- Windows Server com RDP/NLA.
- Linux OpenSSH.
- MikroTik RouterOS/CHR.
- Simulador Telnet legado.
- Duas redes/NATs para NDesk.

## Testes de performance

- 50 hosts na lista.
- 5.000 hosts na lista.
- 10 abas SSH simultâneas.
- 20 abas SSH simultâneas.
- Sync de 10.000 mudanças.
- NDesk com diferentes latências.

## Testes de UX

- Busca em menos de 200 ms para inventário local.
- Abrir sessão com até dois cliques.
- Indicação clara de credencial herdada.
- Feedback claro quando offline.
- Aviso claro para Telnet.

## Definition of Done por módulo

- Testes unitários relevantes.
- Testes de integração quando há IO/rede/banco.
- Logs sanitizados.
- Auditoria em ação sensível.
- Documentação atualizada.
- Critérios de aceite do documento do módulo atendidos.

## Dados de teste

Nunca usar credenciais reais. Usar fixtures sintéticas e secrets gerados para teste.

## Automação inicial

- GitHub Actions para unit/integration.
- Testes RDP/NDesk podem começar como manuais documentados e evoluir para laboratório automatizado.
