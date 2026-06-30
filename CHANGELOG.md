# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [0.7.0-sync-local] - 2026-06-30

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
