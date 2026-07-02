# Design — Config no GUI: Keychain CRUD + anexar credencial ao host + WinBox nas Configurações

- **Data:** 2026-07-02
- **Branch:** `feature/gui-credentials-winbox` (worktree `C:\dev\remoteops-cred-winbox`), a partir do `origin/main` `5384728`.
- **Status:** aprovado para spec → aguardando revisão antes do plano.
- **Origem:** feedback de campo — o operador (1) não achou onde apontar o WinBox e (2) não conseguiu criar login/senha no chaveiro.

## 1. Contexto e objetivo

Hoje o **Keychain é read-only** (lista metadados via `GetCredentialRefsAsync`) e o **WinBox só é configurável por env var** (`WINBOX_EXE_PATH`/`WINBOX_SHA256`, sem UI). Objetivo: permitir, **pela GUI**, criar/gerenciar credenciais de **login+senha** (guardadas cifradas no vault), **anexá-las a um host** (pra SSH/Telnet/RDP/WinBox usarem), e **apontar o executável do WinBox** (o app calcula e fixa o hash).

## 2. Decisões (brainstorming)

| Tema | Decisão |
|---|---|
| Escopo do Keychain | Credenciais **login+senha** (create/edit/delete/change-password); **chave privada fica pra frente do SSH**. Inclui **anexar credencial ao host** (senão a credencial é inútil). |
| Idioma | Rótulos do **Keychain em inglês** (Keychain, Credentials, Username, Password, Add credential, Rotate…); resto do app em pt-BR. |
| WinBox | Apontar o `.exe` (Procurar) → o app **calcula o SHA-256** e fixa **path+hash** no settings; botão **Re-pin** pra trocas intencionais. |
| Branch | Nova a partir do `origin/main` atual. |

**Fora de escopo:** chave privada SSH (frente "SSH avançado"); credenciais de outros tipos (só password); assinatura do WinBox por fonte externa (só auto-fix do hash do exe apontado).

## 3. Design detalhado

### 3.1 Keychain CRUD (aba Keychain, labels em inglês)

APIs reaproveitadas (já no DI como `ICredentialVault`): `StoreAsync(VaultStoreRequest, ReadOnlyMemory<char> secret) → SecretEnvelope`; `RotateAsync(envelopeId, newSecret, VaultAccessContext) → SecretEnvelope`; `RevokeAsync(envelopeId, VaultAccessContext)`. Metadados: `ILocalStore.AddCredentialRefAsync/GetCredentialRefsAsync/GetCredentialRefAsync/DeleteCredentialRefAsync` + (novo) `UpdateCredentialRefAsync`.

`KeychainViewModel` passa a receber **`ICredentialVault`** além do `ILocalStore`. Operações:

- **Create** (`name`, `username`, `password`):
  1. `credId = Guid.NewGuid().ToString("n")`.
  2. `env = vault.StoreAsync(new VaultStoreRequest { WorkspaceId, CredentialId = credId, Type = "password", ActorUserId = "local-user" }, password.AsMemory())`.
  3. `store.AddCredentialRefAsync(new CredentialRef { Id = credId, Name = name, Type = "password", Metadata = new CredentialMetadata { Username = username }, SecretEnvelopeId = env.Id })`.
  4. recarrega a lista.
- **Edit** (rename / change username, sem re-digitar senha): `store.UpdateCredentialRefAsync(ref with { Name, Metadata })` — mantém `Id` e `SecretEnvelopeId` (endpoints que referenciam continuam válidos).
- **Change password**: `vault.RotateAsync(ref.SecretEnvelopeId!, newPassword.AsMemory(), new VaultAccessContext { ActorUserId = "local-user" })`.
- **Delete**: `vault.RevokeAsync(ref.SecretEnvelopeId!, ctx)` + `store.DeleteCredentialRefAsync(ref.Id)`. Se algum endpoint usa essa credencial, exibir aviso não-bloqueante ("in use by N host(s)"); permitir mesmo assim (a conexão falharia graciosamente depois).
- **List**: já existe (só metadados; **nunca a senha**).

**UI**: a `KeychainView` ganha um botão **Add credential** e, por linha, **Edit / Change password / Delete**. O form de credencial (Name, Username, **Password** via `PasswordBox`) é um diálogo modal (`CredentialDialog`). A senha entra pelo `PasswordBox` (não é `Binding` — passada via code-behind ao comando) e **nunca é exibida depois**.

### 3.2 Anexar credencial ao host (HostEditor)

`HostEditorViewModel` ganha `ObservableCollection<CredentialRef> AvailableCredentials` (carregada de `GetCredentialRefsAsync`) e o endpoint em edição ganha um **`SelectedCredentialId`**. Ao adicionar/editar um endpoint, um **ComboBox de credencial** seta `Endpoint.CredentialRefId`. Botão **+ New credential** abre o `CredentialDialog` (§3.1), cria a credencial, atualiza a lista e a seleciona. Como os resolvers (`StoreCredentialRefResolver`/`RdpCredentialResolver`) já resolvem o segredo a partir do `CredentialRefId`, anexar é o que basta pra SSH/Telnet/RDP/WinBox usarem a credencial.

### 3.3 WinBox nas Configurações

- `AppSettings` ganha `string? WinBoxExePath` e `string? WinBoxSha256`.
- `SettingsWindow` ganha uma **nova aba "Ferramentas externas"** (em pt-BR — só o Keychain é em inglês) com a seção WinBox: campo do caminho (read-only) + **Procurar…** (`OpenFileDialog`, filtro `*.exe`) → ao escolher, o app **calcula o SHA-256** do arquivo e guarda **path+hash** no `AppSettings` (via `ISettingsStore.Save`). Botão **Re-fixar hash** recomputa o hash do caminho atual. Mostra o hash fixado (curto) e um aviso: "o app valida este hash antes de abrir o WinBox".
- Helper `Sha256File(path) → string` (hex minúsculo).
- `AppCompositionRoot.BuildWinBoxManifest(sp)` passa a resolver `ISettingsStore` e ler `WinBoxExePath`/`WinBoxSha256` **primeiro**; fallback `WINBOX_EXE_PATH`/`WINBOX_SHA256` (env); fallback default `C:\Tools\WinBox\winbox64.exe`. Assim o `WinBoxRunner` valida contra o hash fixado pela GUI (fail-closed) — "apontou o exe → abre".

### 3.4 Segurança

- Senha entra por `PasswordBox` mascarado, é passada como `char[]`/`ReadOnlyMemory<char>` ao vault e **nunca exibida** depois (a lista mostra só metadados).
- **Auto-fix do hash confia no exe apontado** no momento da configuração — não verifica supply-chain da origem. Ganho real: detecta **troca/adulteração posterior** do exe. Documentar esse limite (o modo "colar hash manual" foi deixado de fora por decisão).

## 4. Componentes (arquivos)

Novos: `Views/CredentialDialog.xaml(.cs)` + `ViewModels/CredentialDialogViewModel.cs`; `Infrastructure/HashUtil.cs` (Sha256File).
Modificados: `ViewModels/KeychainViewModel.cs` (+vault, +CRUD), `Views/KeychainView.xaml(.cs)` (+botões), `ViewModels/HostEditorViewModel.cs` (+credenciais), `Views/HostEditorDialog.xaml` (+combo), `ViewModels/SettingsViewModel.cs` (+WinBox), `Views/SettingsWindow.xaml` (+seção), `Infrastructure/AppSettings.cs` (+WinBox path/hash), `Integration/AppCompositionRoot.cs` (BuildWinBoxManifest + KeychainViewModel ctor), `Infrastructure/ILocalStore.cs` + `InMemoryLocalStore.cs` + `SqlCipherLocalStore.cs` (+UpdateCredentialRefAsync).

## 5. Testes

- `KeychainViewModel` (fake `ICredentialVault` + `InMemoryLocalStore`): Create guarda no vault e cria a `CredentialRef` com o `SecretEnvelopeId` certo; ChangePassword chama `RotateAsync`; Delete chama `RevokeAsync` + remove a ref; Edit atualiza nome/username mantendo Id+envelope.
- `HashUtil.Sha256File`: hash conhecido de um arquivo temporário.
- `BuildWinBoxManifest`/resolução: usa settings quando setado, senão env, senão default (testar via uma função pura de resolução `ResolveWinBoxManifest(settings, env)` extraída pra ser testável).
- `HostEditorViewModel`: selecionar uma credencial seta o `Endpoint.CredentialRefId`; "New credential" adiciona à lista.
- `UpdateCredentialRefAsync` em `InMemoryLocalStore` (round-trip).
- Build 0/0 (TreatWarningsAsErrors), suíte existente (425) verde.

## 6. Riscos e mitigações

| Risco | Mitigação |
|---|---|
| `PasswordBox` não é bindável (WPF) | Ponte via code-behind passando `Password` ao comando; não guardar a senha em prop bindável |
| `UpdateCredentialRefAsync` toca SqlCipher + InMemory | Implementar nos dois + teste no InMemory; seguir o padrão de `UpdateAssetAsync` |
| Excluir credencial em uso deixa endpoint órfão | Aviso "in use by N"; conexão falha graciosamente (não crash) |
| Hash auto-fixado não valida origem | Documentado; detecta troca posterior; modo manual fora de escopo |
| `BuildWinBoxManifest` é estático | Resolver `ISettingsStore` do `IServiceProvider` recebido |

## 7. Critérios de sucesso

- Pela GUI: **criar** uma credencial login+senha, **anexá-la a um host**, e **conectar** SSH/Telnet usando-a.
- Editar (nome/username), **trocar senha** (rotate) e **excluir** (revoke) uma credencial.
- Keychain com **rótulos em inglês**; senha nunca exibida.
- **Configurar o WinBox** apontando o `.exe` (hash calculado/fixado) → abrir o WinBox pelo menu de contexto sem env var.
- Build 0/0, 425 testes anteriores verdes + testes novos.
