# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [0.9.0-integration-cloud-sync] - 2026-06-30

### Adicionado

- **INT-5 — Cliente de sincronização remoto (Desktop ↔ Cloud), atrás da feature flag `cloud.sync.enabled` (default OFF):**
  - `RemoteOps.Contracts/Sync/`: `PullResponse`, `PushRequest`, `PushResult`, `ConflictDetail` movidos de `RemoteOps.Cloud` (forma JSON inalterada — compatibilidade total da API).
  - `RemoteOps.Sync/Remote/CloudSyncApiClient`: HTTP push/pull/login/refresh sobre `HttpClient` injetável; `Authorization: Bearer` + `X-Device-Id`; refresh automático em 401 + retry único; 409 = `PushResult` de conflito; erros viram `CloudSyncException` sem vazar token.
  - `SyncOrchestrator`: drena outbox → push → trata conflitos/cursor → pull → aplica → avança cursores; estado Offline/Syncing/Synced/Error + contagem de conflitos.
  - `LocalEntitiesChangeApplier`: aplica mudanças puxadas em `local_entities` (idempotente, monotônico via UPSERT `version >=`, sem re-emitir no outbox).
  - `SqliteSyncMetadataStore`: cursores (server + `outbox_cursor`) e `ConflictDetail` em `conflicts`, com migração aditiva/idempotente sobre o schema legado.
  - `VaultTokenStore`: tokens guardados como segredo no vault; apenas o envelopeId no `.tokenref`; rotação revoga o envelope anterior.
  - `SignalRSyncHintChannel` + `SyncSession`: hints `workspace.changed` → pull incremental; laço por intervalo resiliente (sincroniza mesmo sem WebSocket).
  - `adr/ADR-013-cliente-sync-remoto.md`; schemas em `contracts/` (`sync-push-request`, `sync-push-result`, `sync-pull-response`, `conflict-detail`).
  - Testes cross-platform em `tests/RemoteOps.UnitTests/Sync/`: round-trip push/pull, refresh em 401, conflito 409, applier created/updated/deleted, idempotência/monotonicidade, cursores, migração compatível, token store sem segredo, e prova de no-secret-in-log.

### Alterado

- `RemoteOps.Cloud/Sync/SyncModels.cs` removido; Cloud passa a usar os DTOs de `RemoteOps.Contracts.Sync` (sem mudança de forma JSON).
- `RemoteOps.Sync.csproj`: nova dependência `Microsoft.AspNetCore.SignalR.Client` (ADR-010/ADR-013).
- `src/RemoteOps.Desktop/App.xaml.cs`: monta a `SyncSession` atrás da flag e conecta `MainViewModel.SyncStatus` ao orquestrador (Dispatcher na UI thread); descarte no `OnExit`.
- `docs/04-modelo-dados-sync.md` e `docs/10-backend-cloud-sync.md` atualizados (migração + arquitetura do cliente + flag).

### Segurança

- Nenhum token/segredo/patch em log, exceção, fixture ou commit; `CloudSyncException` expõe só o status HTTP.
- Tokens via vault (DPAPI/envelope, ADR-003); `.tokenref` guarda só o envelopeId.
- `SecretEnvelope` nunca sofre auto-merge no cliente (espelha `secret-envelope.no-auto-merge`).
- TLS sempre validado; `X-Device-Id` em toda request; feature flag default OFF (revisão do `security-agent`).
- **Revisão de segurança (security-agent):** `SyncSessionFactory`/Desktop exigem **HTTPS** na URL do Cloud (M-1 — rejeita `http://`, fail-closed), evitando Bearer/refresh token em claro; `TokenSet.ToString()` redatado (L-1 — não expõe tokens); revogação de envelope no `VaultTokenStore` documentada como best-effort (L-3).
- **Revisão adversarial (orquestrador Opus) — hardening de concorrência/perda-de-dados:**
  - `SyncOrchestrator.SyncOnceAsync` agora é **serializado** (`SemaphoreSlim`): o laço por intervalo e o hint SignalR compartilham o mesmo outbox/cursores; sem exclusão mútua, dois ciclos concorrentes faziam read-modify-write não atômico do outbox/server cursor e podiam **pular mudanças locais** ou regredir o server cursor.
  - `SqliteSyncMetadataStore`: gravação de cursor **monotônica** (`MAX(cursor, excluded.cursor)`) — defesa em profundidade contra regressão.
  - `LocalEntitiesChangeApplier` agora **segrega `SecretEnvelope`** também no pull (ignora; nunca aplica no cache `local_entities`), coerente com a política de não auto-merge.
  - `SyncSession.DisposeAsync` descarta o canal de hints **antes** do `CancellationTokenSource` e `OnHintAsync` é blindado contra `ObjectDisposedException` numa corrida de shutdown.
  - +5 testes (serialização, monotonicidade ×2, segregação de SecretEnvelope ×2).

### Módulo

- `src/RemoteOps.Sync/Remote/*` — dono: `cloud-sync-agent`

## [0.9.0-integration-terminal-ui] - 2026-06-30

### Adicionado

- **INT-2 — Aba de terminal real (WebView2 + xterm.js ↔ `ITerminalSessionProvider`):**
  - `Terminal/wwwroot/`: frontend local com xterm.js 5.3.0 + xterm-addon-fit 0.8.0, empacotados via esbuild (sem CDN). Assets em `js/terminal.bundle.js` + `css/xterm.css`.
  - `Terminal/TerminalTabViewModel.cs`: gerencia ciclo de vida de sessão SSH/Telnet (OpenAsync → pump de leitura → CloseAsync) independentemente da View.
  - `Terminal/TerminalTabView.xaml/.cs`: WebView2 + virtual host `https://terminal.local/`. Bridge C#↔JS via PostWebMessageAsString/WebMessageReceived (Base64). CSP `default-src 'none'`, DevTools desabilitados em Release, `AreHostObjectsAllowed=false`.
  - `adr/ADR-012-webview2-xterm-terminal-ui.md`.

### Alterado

- `RemoteOps.Desktop.csproj`: adiciona `Microsoft.Web.WebView2 1.0.2849.39` e os Content items do `wwwroot` (as refs de projeto já vêm do INT-1).
- `MainViewModel`: recebe os provedores SSH/Telnet como **keyed services** (`[FromKeyedServices(RemoteProtocol.Ssh/Telnet)]`, resolvidos pelo `AppCompositionRoot` do INT-1) além do `IWinBoxRunner` (INT-4); cria `TerminalTabViewModel` em `SessionRequested`.
- `TabsViewModel`: `OpenTerminalTab`, `CloseTab` chama `CloseAsync` em tabs terminais.
- `TabsView.xaml`: DataTemplates implícitos por tipo em vez de `ContentTemplate` fixo.
- Os adaptadores de seam (endpoint/credential/security-context/audit/host-key/telnet-consent) reutilizam as implementações do INT-1 já registradas no composition root; as variantes duplicadas trazidas originalmente pelo INT-2 foram removidas na integração.

### Segurança

- Output do terminal: bytes brutos Base64 via bridge — nunca `innerHTML`.
- `IHostKeyConfirmation`: TaskCompletionSource genuíno (FIX 1 / ADR-009).
- `ITelnetConsentProvider`: bloqueia TCP até ack explícito (FIX 2 / ADR-009).

## [0.9.0-integration-mikrotik-desktop] - 2026-06-30

### Adicionado

- `OpenWinBoxCommand` no `InspectorViewModel` (INT-4):
  - Visível apenas quando o asset tem pelo menos um endpoint com protocolo `mikrotik`.
  - Monta `ExternalToolLaunchRequest` a partir do `Asset`/`Endpoint` (IPv4/IPv6/FQDN, porta, credentialRefId).
  - Endereço IPv6 encaminhado com família `"ipv6"` para `WinBoxArgumentBuilder` adicionar colchetes.
  - `IncludePasswordArgument = false` por padrão (Modo A); senha nunca passada automaticamente sem política explícita.
  - `WinBoxValidationException` exibida na UI via propriedade `WinBoxError`/`HasWinBoxError`; nunca propagada silenciosamente.
  - Erro de validação limpo automaticamente ao selecionar outro host.
  - `RequestedBy = "local-user"` (placeholder até autenticação de usuário).
- Botão "Abrir WinBox" em `InspectorView.xaml` (protocolo `mikrotik` → visível; demais → collapsed), com feedback de erro em vermelho abaixo dos botões de ação.
- `tools/winbox/manifest.json` com `sha256: null` — fail-closed em dev; instrução de substituição documentada no campo `_note`.
- 8 novos casos de teste em `InspectorViewModelTests`: sem runner, sem endpoint mikrotik, endpoint mikrotik detectado, WinBoxValidationException → WinBoxError, sucesso limpa erro, request com endereço correto, IPv6 com família correta, troca de asset limpa erro anterior.

### Alterado

- `MainViewModel` aceita `IWinBoxRunner?` opcional e repassa ao `InspectorViewModel`. O `IWinBoxRunner` é resolvido pelo `AppCompositionRoot` (INT-1, `WinBoxRunner.Create` com manifesto por variável de ambiente) e injetado no `MainViewModel` via DI — sem fiação manual em `App.xaml.cs`. A referência a `RemoteOps.MikroTik` no `RemoteOps.Desktop.csproj` já vem do INT-1.

### Segurança

- Senha via argumento bloqueada por padrão (`IncludePasswordArgument = false`) — Modo A conforme ADR-006.
- Auditoria delegada ao `IWinBoxRunner` (nenhum segredo na camada de ViewModel).
- Manifesto sem sha256 válido bloqueia execução (fail-closed) antes de iniciar processo.

## [0.9.0-storage-encrypted] - 2026-06-30

### Adicionado

- `SqlCipherLocalStore : ILocalStore` em `src/RemoteOps.Desktop/Infrastructure/`:
  - Substituição persistente e criptografada do `InMemoryLocalStore` (ADR-008/ADR-003).
  - Tables SQLCipher por workspace no mesmo banco `sync-{workspaceId}.db`: `asset_groups`, `assets`, `endpoints`, `credential_refs`.
  - Toda mutação grava simultaneamente no outbox `local_outbox` via `ISyncClient.PushAsync` com `ClientChangeId` único — pronto para consumo pelo INT-5 (cloud sync).
  - **Metadados apenas**: `SecretEnvelopeId` é referência ao envelope; nenhum segredo persistido.
  - Implementa `GetEndpointAsync` e `GetCredentialRefAsync` (contrato `ILocalStore` estendido pelo INT-1).
- `WorkspaceContext` em `src/RemoteOps.Sync/`: classe pública que agrupa `ISyncClient` + `OpenConnectionAsync()`, expondo acesso ao banco sem vazar `IDbConnectionFactory` (internal) ao Desktop.
- `LocalSyncClientFactory.OpenWorkspaceAsync()`: novo método público que derive a chave uma única vez (via `VaultDbKeyProvider`) e devolve `WorkspaceContext` com sync + conexão reutilizando a mesma chave.

### Alterado

- `src/RemoteOps.Desktop/App.xaml.cs`: `async void OnStartup` inicializa vault (DPAPI + `FileVaultStore`), `LocalSyncClientFactory` e `WorkspaceContext`, cria o `SqlCipherLocalStore` e o injeta — junto com o `CredentialVault` de produção — no `AppCompositionRoot.Build(vault, store)` (integração com ADR-011). O composition root resolve o restante do grafo (adapters de terminal/WinBox, providers keyed, `MainViewModel`).
- `AppCompositionRoot`: novo overload `Build(CredentialVault vault, ILocalStore store)` que registra o vault e o store de produção como instâncias, mantendo `Build()` (in-memory) para os smoke tests. Sub-grafo de vault in-memory só no caminho de teste.
- `RemoteOps.Desktop.csproj`: adicionada referência a `RemoteOps.Sync`.
- `InMemoryLocalStore`: mantida para testes de ViewModel; não é mais usada na produção (`App.xaml.cs`).
- `docs/04-modelo-dados-sync.md`: seção de schema local atualizada para incluir tabelas `asset_groups`, `assets`, `endpoints`, `credential_refs`.
- `DeleteAssetAsync` agora executa os dois DELETEs (`endpoints` e `assets`) dentro de uma única transação SQLite — elimina janela de corrupção em caso de falha entre as duas instruções.
- Exceção lançada pelo `PRAGMA key` em `SqliteConnectionFactory` agora é sanitizada antes de propagar: nova `InvalidOperationException` com código de erro SQLite mas sem `hexKey` na mensagem nem no inner exception — impede vazamento da chave via logs de exceção.
- `LocalSyncClientFactory.OpenWorkspaceAsync` valida `workspaceId` contra `Path.GetInvalidFileNameChars()` antes de qualquer operação de arquivo — previne path traversal.
- `GetAssetsAsync` agora lança `InvalidOperationException` descritiva ao exceder 900 ativos por workspace, evitando erro genérico do SQLite ao superar `SQLITE_LIMIT_VARIABLE_NUMBER`.

### Segurança

- Chave AES-256 do banco derivada uma única vez por `VaultDbKeyProvider` (DPAPI/envelope, ADR-003); `WorkspaceContext` mantém a fábrica em memória sem re-acessar o vault por operação.
- `hexKey` nunca aparece em log, exceção, string de conexão ou commit (ADR-008 regras derivadas); `SqliteConnectionFactory` sanitiza exceção do PRAGMA key para garantir isso mesmo em falhas de abertura.
- Banco ilegível sem a chave do vault — verificado por teste `Db_Is_Unreadable_Without_Key`.
- `Pooling=False` preserva isolamento de chave por workspace (sem reuso de conexão já decifrada entre workspaces).
- Queries todas parametrizadas (`$param`) — sem SQL injection.
- `CredentialRef.SecretEnvelopeId` incluído no outbox patch como referência (não o segredo); `CredentialMetadata` serializada como JSON sem campos de segredo.
- `workspaceId` validado contra path traversal (`../evil`) antes de montar caminhos de arquivo.

### Módulo

- `src/RemoteOps.Desktop/Infrastructure/SqlCipherLocalStore.cs` — dono: `cloud-sync-agent`
- Depends-on: `feature/integration-composition`

## [0.9.0-integration-composition] - 2026-06-30

### Adicionado

- **Composition root com DI** em `App.xaml.cs` via `AppCompositionRoot` (ADR-011): substitui `new InMemoryLocalStore()` manual por `ServiceCollection`/`ServiceProvider`. Shutdown faz `Dispose` do provider.
- **`Microsoft.Extensions.DependencyInjection`** via `PackageReference` 10.0.0 (DI não faz parte do framework WPF, só do ASP.NET Core); project references novas para `RemoteOps.Security`, `RemoteOps.Terminal` e `RemoteOps.MikroTik` adicionadas ao Desktop.
- **Adaptadores em `src/RemoteOps.Desktop/Integration/`:**
  - `LocalStoreEndpointResolver` — resolve `EndpointId` via `ILocalStore.GetEndpointAsync`.
  - `StoreCredentialRefResolver` — resolve `CredentialRefId` via `ILocalStore.GetCredentialRefAsync`.
  - `AppTerminalSecurityContext` — contexto de segurança MVP (`local-user` / hostname); substituível em INT-3.
  - `StructuredTerminalAuditSink` — auditoria de sessões SSH/Telnet em `Trace` sem segredos.
  - `ModalHostKeyConfirmation` — diálogo WPF TOFU assíncrono via `TaskCompletionSource`; destaca `isChanged=true` com ícone de aviso (ADR-009 §FIX-1).
  - `ModalTelnetConsentProvider` — consentimento WPF bloqueante antes de qualquer conexão TCP Telnet (ADR-009 §FIX-2).
  - `StoreWinBoxCredentialResolver` — resolve senha WinBox via vault; `VaultSecret` descartado imediatamente (ADR-009 §FIX-3).
  - `StructuredWinBoxAuditSink` — auditoria WinBox em `Trace` sem senhas.
- **`ILocalStore` estendido** com `GetEndpointAsync(string endpointId)` e `GetCredentialRefAsync(string credentialRefId)`; `InMemoryLocalStore` implementa os novos métodos.
- **Provedores SSH e Telnet registrados** como `ITerminalSessionProvider` com chave de protocolo (keyed services, `AddKeyedSingleton`).
- **`IWinBoxRunner` registrado** via `WinBoxRunner.Create()` com manifesto configurável por variável de ambiente (`WINBOX_EXE_PATH`, `WINBOX_SHA256`).
- **`adr/ADR-011-dependency-injection-desktop.md`** — documenta adoção de DI, regras de uso e alternativas consideradas.
- **`CompositionRootSmokeTests`** em `tests/RemoteOps.UnitTests/Desktop/`: 16 testes verificando resolução completa do grafo sem abrir sessão real.
- **`IntegrationAdapterTests`** — testes unitários para os dois novos métodos do `ILocalStore` e caminhos de erro dos adaptadores.
- `InternalsVisibleTo("RemoteOps.UnitTests")` no Desktop para acesso a `AppCompositionRoot` nos testes.

### Segurança

- Segredos nunca registrados como instâncias no container; credenciais só via `IVault`.
- `StructuredTerminalAuditSink` e `StructuredWinBoxAuditSink` auditam sem segredo: `TerminalAuditEvent` e `AuditEvent` excluem campos de senha por construção.
- `ModalHostKeyConfirmation` usa `TaskCompletionSource` assíncrono para evitar deadlock no thread de conexão SSH (ADR-009 §FIX-1).
- `ModalTelnetConsentProvider` bloqueia conexão TCP até ack explícito do usuário (ADR-009 §FIX-2).
- `StoreWinBoxCredentialResolver` usa `using var secret` (lifetime mínimo) ao revelar o vault secret (ADR-009 §FIX-3).
- Nenhuma sessão remota aberta nesta frente (INT-2 pendente).

## [0.8.0-mikrotik-winbox-v2] - 2026-06-30

### Adicionado

- Re-integração do WinBox Runner na estrutura canônica do repositório (branch `feature/mikrotik-winbox-v2`):
  - `WinBoxRunner : IWinBoxRunner` — implementa `LaunchAsync(ExternalToolLaunchRequest, CancellationToken)` com `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`.
  - `WinBoxToolManifest` — valida SHA-256 do executável; fail-closed quando sha256 ausente, inválido ou placeholder (nunca `NullReferenceException`).
  - `WinBoxArgumentBuilder` — monta argumentos posicionais IPv4/IPv6 sem `ArgumentList.Add(string.Empty)`.
  - `WinBoxPolicy` / `LocalWinBoxPolicyProvider` — política com deny real por workspace/host; `PasswordArgumentAllowed=false` por padrão (Modo A).
  - `IWinBoxAuditSink` / `IWinBoxCredentialResolver` / `IWinBoxProcessLauncher` — interfaces injetáveis para produção e testes.
  - Eventos de auditoria: `winbox_tool_validated`, `winbox_open_requested`, `winbox_open_started`, `winbox_open_failed`, `winbox_password_argument_used`, `winbox_ipv6_target_used`; nenhum com segredo.
- Testes em `tests/RemoteOps.UnitTests/MikroTik/`:
  - `WinBoxArgumentBuilderTests` — IPv4/IPv6 global/link-local, porta, sem `argv` vazio, senha vazia/espaços/policy-deny.
  - `WinBoxRunnerTests` — manifesto sem sha256, manifesto placeholder, policy deny (host/workspace/senha), RoMON recusado, IPv6 audit event, no-password-in-audit-events.

### Alterado

- `adr/ADR-006-mikrotik-winbox-externo.md` — documentadas decisões de deferimento (workspace posicional e RoMON não confirmados contra CLI oficial), risco de exposição de senha em tabela de processos e controles implementados. Sign-off do `security-agent` pendente para merge.

### Segurança

- **FIX 1 — sem argv vazio**: senha só é adicionada quando `!string.IsNullOrEmpty(password)` AND login presente AND política permite; nunca há placeholder `""` nos argumentos.
- **FIX 2 — RoMON deferido**: `Romon.Enabled=true` é recusado com `WinBoxValidationException` auditada (`reason=romon_not_confirmed_official_cli`) até validação da sintaxe oficial.
- **FIX 3 — manifesto fail-closed**: sha256 nulo, vazio ou com menos de 64 hex chars → exceção de validação + evento auditado; nunca `NullReferenceException`.
- **FIX 4 — policy deny real**: `LocalWinBoxPolicyProvider` nega por workspace/host; `IncludePasswordArgument=true` sem `PasswordArgumentAllowed` na política lança exceção explícita e auditada.
- Senha via argumento de processo documentada como risco na ADR-006 (visível na tabela de processos local); desativada por padrão; Modo B requer habilitação explícita por política de workspace.

## [0.7.3-terminal-ssh-telnet] - 2026-06-30

### Adicionado

- Adaptadores SSH e Telnet re-integrados na estrutura canônica (`src/RemoteOps.Terminal`, Opção A):
  - `SshSessionProvider` (SSH.NET 2024.x, `Renci.SshNet`): `Protocol`, `OpenAsync`, `CloseAsync`,
    `WriteAsync`, `ReadAsync`, `ResizeAsync` conforme `ITerminalSessionProvider`.
  - `TelnetSessionProvider` (TcpClient + IAC state-machine própria): mesma interface.
- Interfaces públicas novas: `IEndpointResolver`, `ICredentialRefResolver`, `IHostKeyConfirmation`,
  `ITelnetConsentProvider`, `ITerminalAuditSink`, `ITerminalSecurityContext`.
- `TerminalAuditEvent` + `TerminalActions`: auditoria de sessão sem conteúdo de terminal.
- `TelnetNegotiator`: parser RFC 854/855 (IAC/WILL/WONT/DO/DONT/SB, ECHO, SGA, NAWS).
- `HostKeyStore`: cache em memória de host keys TOFU por sessão de provider.
- Testes unitários em `tests/RemoteOps.UnitTests/Terminal/`: 12 casos cobrindo protocol,
  OpenAsync, TOFU bloqueante, consentimento Telnet, resize, round-trip e auditoria sem segredo.
- `adr/ADR-009-ssh-telnet-libs-e-credenciais.md`.

### Segurança

- **FIX 1 — TOFU assíncrono:** callback `HostKeyReceived` é síncrono (captura fingerprint e
  rejeita); `ConfirmAsync` genuinamente assíncrono acontece **fora** do callback, sem
  `.GetAwaiter().GetResult()`. Evita deadlock com UI thread do WebView2.
- **FIX 2 — Consentimento Telnet bloqueante:** `ITelnetConsentProvider.RequestConsentAsync`
  deve resolver via `TaskCompletionSource` da UI; a conexão TCP não é aberta até ack explícito.
  Telnet desabilitado por padrão.
- **FIX 3 — Higiene de senha:** `VaultSecret` descartado imediatamente após autenticação.
  Limitação de Renci.SshNet (`PasswordAuthenticationMethod` exige `string`) documentada no
  ADR-008 §FIX-3 com mitigantes adotados. Nenhum log/fixture/evento de auditoria contém senha.
- **FIX 5 — Auditoria de host key alterada:** `terminal.hostkey.changed` emitido com fingerprint
  **antes** de perguntar ao usuário. `terminal.hostkey.accepted/rejected` auditados.
- **FIX 6 — `"default-group"` removido:** grupo vem do `ITerminalSecurityContext`; pendente issue
  para conectar ao RBAC real (referenciada no ADR-008 §FIX-6).

### Restrições respeitadas

- Estrutura canônica preservada: root `RemoteOps.sln`, `src/RemoteOps.Contracts` inalterado.
- Sem redefinição de `IRemoteSessionProvider`, `SessionRequest`, `SessionHandle`, `RemoteProtocol`.
- Sem segredo em log, fixture ou commit.
- `RemoteOps.Terminal` (`net10.0` cross-platform) não referencia nada Windows-specific.

## [0.7.2-cloud-backend] - 2026-06-30

### Adicionado

- `src/RemoteOps.Cloud`: backend evoluído de `GET /health` para servidor completo com auth, RBAC, sync e auditoria.
  - **EF Core + Npgsql**: `AppDbContext` com 13 entidades (tenants, workspaces, users, memberships, asset_groups, assets, endpoints, credential_refs, secret_envelopes, changelog, audit_events, devices, refresh_tokens) e migrations pendentes de aplicação.
  - **Auth JWT**: `POST /auth/login`, `POST /auth/refresh`, `POST /auth/logout`. Tokens emitidos com PBKDF2-SHA256 (310k iterações). Refresh token armazenado como hash SHA-256. Chave de assinatura JWT via variável de ambiente `Jwt__SigningKey`.
  - **RBAC server-side**: `PermissionEvaluator` avalia 8 etapas (usuário ativo → device → workspace → role → membro → grupo → aprovação). Negação explícita vence herança. 10 papéis padrão com permissões granulares de `docs/18`.
  - **Sync pull/push**: `GET /sync/pull?workspaceId=&cursor=` (paginado, cursor por `changelog.id`); `POST /sync/push` (conflito por `BaseVersion`, idempotência por `ClientChangeId`, SecretEnvelope nunca merge automático).
  - **SignalR**: `SyncHub` em `/hubs/sync` emite hint `workspace.changed` com `workspaceId`, `cursor`, `entityType`, `entityId`. Broadcast escopado ao grupo do workspace. Sem payload completo (ADR-002).
  - **Auditoria**: `AuditService` persiste `AuditEvent` (tipo canônico de `RemoteOps.Contracts.Audit`) em toda ação sensível. `Metadata` sanitizado — chaves com "password", "secret", "token", "key", "hash" são `[REDACTED]`.
  - **ProblemDetails**: `CloudExceptionHandler` + `CorrelationIdMiddleware`. Todos os erros retornam `application/problem+json` com `correlationId`. Sem stack trace em produção.
- `adr/ADR-010-backend-ef-npgsql-signalr.md`: ADR justificando EF Core, Npgsql, JWT Bearer e SignalR (pré-requisito obrigatório de CLAUDE.md).
- Testes em `tests/RemoteOps.UnitTests/Cloud/`: `RbacTests` (11 cenários — allow/deny, negação explícita, device/workspace/membership/cross-tenant), `SyncTests` (pull paginado, push ok/conflito/idempotente, SecretEnvelope bloqueado), `AuditTests` (gravação, sanitização de segredos, mapeamento para contrato canônico).

### Segurança

- Servidor **nunca descriptografa segredos**: `SecretEnvelopeEntity` armazena apenas `ciphertext`, `nonce`, `tag`, `algorithm`, `keyVersion` — sem WDK, CEK ou plaintext. Conforme ADR-003.
- Senha/chave JWT nunca em `appsettings*.json` — obrigatoriamente via variável de ambiente ou secret store.
- Refresh token armazenado como `SHA-256(valor)` — vazamento do banco não permite uso do token.
- Auditoria registra toda ação sensível (login, push, grant/revoke); `Metadata` com sanitização defensiva de palavras-chave sensíveis.
- `AuditService.SanitizeMetadata` bloqueia chaves contendo "password", "secret", "token", "key", "credential", "plaintext", "hash" mesmo se o chamador cometer o erro de incluí-las.
- `SyncService` rejeita push de `SecretEnvelope` com `secret-envelope.no-auto-merge`.

### Correções de segurança (pós security-review)

- **[HIGH] `TokenService.RefreshAsync`**: adicionada verificação de status do device antes de emitir novo JWT. Device revogado bloqueia o refresh imediatamente e revoga o refresh token em cascade. Antes, a revogação do device não interrompia refresh tokens existentes (até 30 dias de validade).
- **[MEDIUM] `SyncHub.JoinWorkspace`**: adicionada verificação de membership antes de adicionar o cliente ao grupo SignalR. Antes, qualquer usuário autenticado podia assinar hints de workspaces aos quais não pertencia.
- **[MEDIUM] `SyncEndpoints`**: `X-Device-Id` header passou a ser **obrigatório** em `GET /sync/pull` e `POST /sync/push` (retorna 400 se ausente). Garante que a verificação de device revocation no `PermissionEvaluator` seja sempre executada, sem possibilidade de bypass por omissão do header.

## [0.7.1-sync-local] - 2026-06-30

### Adicionado

- `LocalSyncClient` em `src/RemoteOps.Sync`: implementação completa de `ISyncClient` sobre SQLite/SQLCipher local.
  - `PushAsync`: grava lote no outbox local (`local_outbox`), idempotente por `ClientChangeId` via `INSERT OR IGNORE`.
  - `PullAsync(fromCursor, limit)`: lê o outbox paginado a partir do cursor, ordenado por `id ASC`; atualiza `CurrentCursor`.
  - Schema local: `local_outbox`, `local_entities`, `sync_cursor`, `conflicts` (conforme `docs/04`).
  - Índice em `(entity_id, entity_type)` para lookup de entidades.
- `LocalSyncClientFactory`: cria instâncias de `LocalSyncClient` com chave do banco protegida pelo vault.
- `VaultDbKeyProvider` (`Storage/`): obtém/cria chave AES-256 do banco via `ICredentialVault` (DPAPI/envelope, ADR-003); persiste apenas o `envelopeId` no arquivo `.keyref`, nunca o material de chave em claro.
- `SqliteConnectionFactory` (`Storage/`): abre conexão SQLCipher via `PRAGMA key = "x'hexbytes'"` como primeiro comando (bytes raw, sem PBKDF2).
- `IDbConnectionFactory` e `IDbKeyProvider`: abstrações internas que permitem substituição em testes.
- Suíte de testes `tests/RemoteOps.UnitTests/Sync/`: round-trip Push→Pull, idempotência por `ClientChangeId`, cursor/paginação monotônica, criptografia do banco (DB ilegível sem chave do vault) e ausência de segredo em logs.
- `ADR-008-sqlite-local-sync-storage.md`: documenta a escolha de SQLite/SQLCipher via NuGet, derivação de chave, fallback e regras.
- `docs/04-modelo-dados-sync.md`: seção de schema local adicionada com DDL completo e descrição do cursor monotônico.

### Segurança

- A chave AES-256 do banco local nunca é persistida em claro: fica protegida pelo vault (DPAPI/envelope) e referenciada por `envelopeId` no arquivo `.keyref`.
- `PRAGMA key` usa bytes raw (`x'...'`), evitando PBKDF2 desnecessário.
- Nenhum material de chave, plaintext ou patch sensível aparece em log, exceção, fixture ou commit.
- Teste `Encrypted_Db_Is_Unreadable_Without_Key` verifica fisicamente que o arquivo `.db` é ilegível sem a chave do vault (requer SQLCipher presente; skip automático caso contrário).
- Teste `KeyRef_File_Contains_Only_EnvelopeId_Not_Secret` garante que o `.keyref` nunca contém os 64 hex chars da chave do banco.

### Módulo

- `src/RemoteOps.Sync` — dono: `cloud-sync-agent`
- Depends-on: `feature/contracts-skeleton`, `feature/security-vault`
## [0.7.0-desktop-shell] - 2026-06-30

### Corrigido

- Endpoint com endereço **IPv6** agora é gravado no campo `Ipv6` (antes ia para `Ipv4`, pois `IPAddress.TryParse` aceita literais IPv6); detecção por `AddressFamily`.
- Endpoint recém-adicionado **reflete imediatamente** no `AssetViewModel`/DataGrid via novo `ILocalStore.GetAssetAsync` + `AssetViewModel.Refresh` (antes só aparecia após reload).
- Teste `AddEndpoint_StoresEndpoint` fortalecido (verifica persistência e campos, não só limpeza do input) e novos casos para IPv6 e FQDN.

### Adicionado

- Shell WPF/MVVM inicial em `src/RemoteOps.Desktop/`:
  - **Janela principal** com 4 regiões redimensionáveis (GridSplitter): sidebar de grupos, lista de hosts, área de abas de sessão, inspector.
  - **Domain:** `AssetGroup` (grupo local), `AddAssetRequest`.
  - **Infrastructure:** `ILocalStore` + `InMemoryLocalStore` — CRUD de grupo/host/endpoint/credentialRef em memória; sem segredo.
  - **ViewModels:** `BaseViewModel` (INotifyPropertyChanged), `RelayCommand` (ICommand), `SidebarViewModel`, `AssetGroupViewModel`, `HostListViewModel`, `AssetViewModel`, `InspectorViewModel`, `TabsViewModel`, `SessionTabViewModel`, `MainViewModel` (mediador).
  - **Views (UserControls):** `SidebarView` (árvore de grupos), `HostListView` (DataGrid de hosts com filtro e CRUD), `InspectorView` (detalhes + adicionar endpoint + ações rápidas SSH/Telnet/RDP), `TabsView` (TabControl de sessões placeholder).
  - `App.xaml.cs` instancia `InMemoryLocalStore` + `MainViewModel` sem DI externo.
- Testes de ViewModel em `tests/RemoteOps.UnitTests/Desktop/`:
  - `SidebarViewModelTests`, `HostListViewModelTests`, `InspectorViewModelTests`, `TabsViewModelTests`, `MainViewModelTests`.
  - Projeto de testes migrado para `net10.0-windows` (necessário para referenciar projeto WPF; DPAPI tests já têm guard `OperatingSystem.IsWindows()`).

### Restrições respeitadas

- Nenhuma dependência de protocolo real; usa `IRemoteSessionProvider` apenas como interface de contratos.
- Nenhum segredo em log ou UI; credencial exibe apenas nome e metadata (nunca senha).
- Sem nova dependência NuGet externa (sem ADR necessária).

## [0.6.0-orchestration-fix] - 2026-06-30

### Alterado

- `merge-guard` (`.github/workflows/automerge.yml`) reconhece dependências mergeadas via **squash** (consulta PR mergeado em vez de ancestralidade) — corrige falso-negativo que bloqueava PRs com `Depends-on:` mesmo com a dependência já em `main`.
- **Auto-merge desligado**: o merge passa a ser **manual, feito pelo orquestrador**, com CI verde + revisão. O workflow foi renomeado de `automerge` para `merge-guard`.
- `docs/24-orquestracao-multiagente-paralela.md` e `CONTRIBUTING.md` atualizados para o fluxo de merge manual.

### Segurança

- Reduz risco de `main` quebrada por merge automático prematuro — auto-merge em CI verde já havia mergeado o vault (#8) antes das correções de segurança/build, exigindo o PR de remediação #11.

## [0.5.0-security-vault] - 2026-06-29

### Adicionado

- Camada de cofre de credenciais em `src/RemoteOps.Security` com envelope encryption por workspace e proteção da chave local por DPAPI no Windows (ver `docs/25-credential-vault.md`).
  - `Vault/`: `IVault`/`CredentialVault` (API rica: store/retrieve/rotate/revoke com `ReadOnlyMemory<char>` e contexto de auditoria), `SecretEnvelope`, `VaultSecret` (IDisposable que zera o buffer, `ToString()` redigido), `VaultModels`, `VaultException`.
  - `Crypto/`: `EnvelopeCipher` (CEK por segredo em AES-256-GCM, embrulhada pela Workspace Data Key; AAD ligando envelope/workspace/versão), `IWorkspaceKeyRing`/`WorkspaceKeyRing`, `WorkspaceKey`, `ILocalKeyProtector`, `DpapiKeyProtector` (P/Invoke a `crypt32.dll`, escopo CurrentUser, sem NuGet externo).
  - `Storage/`: `ICredentialStore`, `IWorkspaceKeyStore`, `InMemoryStores`, `FileVaultStore`.
  - `Audit/`: `IVaultAuditSink`, `VaultAuditEvent`, `InMemoryVaultAuditSink` — auditoria estruturada sem segredo.
- Suíte de testes `tests/RemoteOps.UnitTests/Security/`: round-trip, ausência de plaintext, detecção de adulteração (AEAD), persistência após restart, isolamento usuário/máquina, rotação/revogação, auditoria sem segredo e DPAPI real (Windows).

### Alterado

- `src/RemoteOps.Security/ICredentialVault.cs`: removido TODO; documentado que o contrato fino é implementado por `CredentialVault` (assinaturas inalteradas — sem mudança de contrato público).
- `adr/ADR-003-credenciais-e2ee.md`: status `Proposta inicial` → `Aceita`; adicionada seção de implementação (hierarquia de chaves, AAD, DPAPI, rotação/revogação, alternativas).
- `docs/13-plano-testes-qa.md`: registrado o plano de testes do cofre na seção de Segurança.

### Segurança

- Nenhum segredo, senha ou chave privada em texto puro: plaintext só vive dentro de `VaultSecret` (zerado no `Dispose`); buffers transitórios alugados de `ArrayPool` e zerados no `finally`.
- WDK nunca persistida em claro — apenas blob protegido por DPAPI (CurrentUser + entropia por workspace) → cache local não abre em outro usuário/máquina.
- AAD impede troca/replay de envelope entre workspaces, downgrade de versão e troca de `type` (campo `type` autenticado no AAD).
- Auditoria, exceções e `ToString()` não contêm segredo (inclui `WorkspaceKey`); erros DPAPI expõem apenas o código Win32.
- Hardening da revisão de segurança (security-agent): `plaintext`/WDK zerados também nos caminhos de exceção; `IVaultAuditSink` obrigatório (sem default silencioso); rotação emite `credential.revoke` do envelope antigo; testes de adulteração de AAD e de apagamento no tombstone.

## [0.4.0-skeleton] - 2026-06-29

### Adicionado

- `RemoteOps.sln` na raiz — solution .NET 10 SDK-style com 9 projetos.
- `Directory.Build.props` com `Nullable=enable`, `LangVersion=latest`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`.
- `.editorconfig` com estilo de código C#, JSON, YAML e shell.
- `src/RemoteOps.Contracts` (classlib net10.0): POCOs imutáveis gerados a partir de `contracts/*.schema.json` e `docs/17` — `SessionRequest`, `SessionHandle`, `SyncChange`, `Asset`, `Endpoint`, `CredentialRef`, `AuditEvent`, `NDeskTicket`, `NDeskPermissionGrant`, `NDeskSessionTelemetry`, `ExternalToolLaunchRequest`. Interface `IRemoteSessionProvider` conforme `docs/02`.
- `src/RemoteOps.Security` (classlib net10.0): stub `ICredentialVault` com TODO.
- `src/RemoteOps.Terminal` (classlib net10.0): stub `ITerminalSessionProvider` com TODO.
- `src/RemoteOps.MikroTik` (classlib net10.0): stubs `IMikroTikSessionProvider` e `IWinBoxRunner` com TODO.
- `src/RemoteOps.Sync` (classlib net10.0): stub `ISyncClient` com TODO.
- `src/RemoteOps.Desktop` (WPF net10.0-windows): janela vazia compilável.
- `src/RemoteOps.Rdp` (classlib net10.0-windows): stub `IRdpSessionProvider` com TODO.
- `src/RemoteOps.Cloud` (ASP.NET Core net10.0): app mínimo com endpoint `GET /health`.
- `src/deferred/RemoteOps.NDesk.Viewer` e `RemoteOps.NDesk.Relay`: stubs marcados como deferred, fora da solution, à espera das frentes feature/ndesk-*.
- `tests/RemoteOps.UnitTests` (xUnit net10.0): 13 smoke tests cobrindo todos os projetos cross-platform.

### Alterado

- `.github/workflows/ci.yml`: removidos guards `if (Test-Path *.sln)` do job `dotnet` — build, test e format passam a rodar de verdade.

### Segurança

- Nenhum segredo, senha ou chave privada adicionado.
- `ICredentialVault` deixa explícito que nunca expõe segredo em logs; `CredentialRef.SecretEnvelopeId` documenta que só a referência ao envelope é armazenada nos POCOs.

## [0.3.0-planning] - 2026-06-29

### Adicionado

- Modelo de orquestração multiagente paralela: `docs/24-orquestracao-multiagente-paralela.md` com frentes (worktree por módulo), agentes donos, ondas de execução e ordem de merge.
- Scripts `tools/dev/worktrees.sh` e `tools/dev/worktrees.ps1` para criar/remover worktrees por frente.
- `CONTRIBUTING.md` com fluxo de frentes, convenção `Depends-on:`, Definition of Done e settings/hook recomendados.
- Hooks de sessão em `.claude/hooks/` (`session-start.sh` e `block-destructive.sh`).
- Workflow `.github/workflows/automerge.yml` com `merge-guard` (valida `Depends-on:`) e auto-merge em CI verde.

### Alterado

- Subagentes em `.claude/agents/` passam a ter escrita habilitada (`Edit, Write`) para atuarem como donos de frentes.
- `.github/workflows/ci.yml` reforçado com jobs `secret-scan` e `security-gate` (label `security-reviewed` para pastas sensíveis) e checagem de changelog mais flexível.

### Segurança

- Auto-merge total só é habilitado com o CI como portão real: secret scan, gate de revisão de segurança em pastas sensíveis e guarda de ordem de dependência.
- Hook bloqueia comandos destrutivos (`rm -rf` em raiz/home/wildcard, force-push em `main`, remoção recursiva forçada).

## [0.2.0-planning] - 2026-06-29

### Adicionado

- Decisão de tratar MikroTik via WinBox oficial externo no MVP.
- Documento `docs/21-mikrotik-winbox-runner.md` com runner, argumentos, riscos e critérios de aceite.
- Decisão de criar agente temporário NDesk Win32 nativo para Windows 7/10 sem Java, WebView2 ou .NET moderno.
- Documento `docs/22-ndesk-performance-legacy-windows.md` com NAT, relay, conexão lenta, codec adaptativo e modos de permissão.
- Documento `docs/23-governanca-versionamento-changelog.md` para changelog, versionamento, branches, PRs e releases.
- ADRs 006 e 007.
- Contratos de lançamento de ferramenta externa, concessão de permissão NDesk e telemetria de sessão.
- Prompts de sprint para WinBox Runner, NDesk legado/performance e governança de release.
- Agente `release-manager-agent`.

### Alterado

- Stack principal continua C#/.NET/WPF para o desktop da empresa, mas o agente temporário NDesk legado passa a ser tratado como componente nativo separado.
- Módulo MikroTik deixa de depender de API-SSL/REST no MVP e passa a priorizar WinBox oficial externo.
- Pipeline de PR passa a exigir avaliação de changelog e versionamento.

### Segurança

- Adicionado alerta sobre risco de senha em argumento de processo ao abrir WinBox.
- Adicionado modelo de permissões NDesk: básico, controle, transferência e administrador, sempre com consentimento explícito.

## [0.1.0-planning] - 2026-06-29

### Adicionado

- Planejamento inicial do RemoteOps Suite.
- Módulos SSH/Telnet, RDP, MikroTik, sync, segurança, NDesk, DevOps, QA e agentes.
