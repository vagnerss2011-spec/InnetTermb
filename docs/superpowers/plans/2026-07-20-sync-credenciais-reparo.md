# Reparo do sync de credenciais — Plano de Implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: superpowers:subagent-driven-development ou
> superpowers:executing-plans. Passos com checkbox (`- [ ]`).

**Goal:** credenciais (chaveiro e inline) sincronizam entre devices, incluindo o acervo de ~700
cadastrado ANTES da nuvem; editar senha deixa de quebrar a credencial; o canal de segredos para de
falhar em silêncio.

**Spec:** `docs/superpowers/specs/2026-07-20-sync-credenciais-reparo-design.md` — leia antes.

**Stack:** .NET 10, C#, WPF (MVVM), xUnit. Repo `C:\dev\remoteops-native`, branch
`fix/sync-credenciais` (já criada de origin/main).

## Restrições Globais

- **Gates:** `dotnet build -c Release`, `dotnet test`, `dotnet format --verify-no-changes`.
  `TreatWarningsAsErrors=true` — warning é erro. Campo de fake sem uso quebra o build.
- **E2EE:** nada de novo ponto de decifração; o cliente NUNCA decide segredo (ADR-003).
- **ADR-013:** log só com envelopeId + status/tipo do erro. NUNCA campo de envelope, senha ou token.
- **Comentários e UI em pt-BR**, explicando POR QUE (imite os arquivos vizinhos).
- **Chaves de tema:** confira que cada `DynamicResource` EXISTE antes de commitar (já mordeu 2x:
  `Brush.Accent` e `Brush.Status.Warning` não existem).
- **Render test STA** afirma VISIBILIDADE/TEXTO reais, nunca só "não lançou" (binding quebrado no WPF
  não lança — cai no valor padrão, e o padrão de `Visibility` é `Visible`).

---

## Task 1: Rotação para de órfãnar a credencial

**Arquivos:**
- Modificar: `src/RemoteOps.Desktop/ViewModels/KeychainViewModel.cs`
- Modificar/Criar: teste em `tests/RemoteOps.UnitTests/Desktop/ViewModels/`

**Contexto:** `CredentialVault.RotateAsync` (`CredentialVault.cs:88-102`) cria envelope com **id NOVO**
(`:94` + `:137` `Guid.NewGuid`) e tombstoneia o antigo (`:96`), devolvendo o novo (`:101`). Mas
`ReplaceKeyAsync` (`:101`), `ChangePassphraseAsync` (`:111`) e `ChangePasswordAsync` (`:152`)
**descartam o retorno**. O `CredentialRef.SecretEnvelopeId` fica apontando pro tombstone → conectar
falha no PRÓPRIO PC ("Envelope revogado", `CredentialVault.cs:179-182`) e nenhum patch entra no outbox,
então a troca nunca chega ao outro device.

- [ ] **Passo 1: Teste que falha**

Fake de vault que registra a rotação e devolve envelope com id novo; fake de store que registra
`UpdateCredentialRefAsync`. Asserção: após `ChangePasswordAsync`, o store recebeu um
`CredentialRef` cujo `SecretEnvelopeId` é o **id novo**.

```csharp
[Fact]
public async Task ChangePassword_Repoints_CredentialRef_To_The_New_Envelope()
{
    var (vm, vault, store) = Build();
    var cred = new CredentialRef { Id = "c1", Type = CredentialTypes.Password, SecretEnvelopeId = "env-antigo" };

    await vm.ChangePasswordAsync(cred, "novasenha".ToCharArray()); // pragma: allowlist secret

    Assert.Equal("env-antigo", vault.RotatedEnvelopeId);
    CredentialRef saved = Assert.Single(store.Updated);
    Assert.Equal(vault.NewEnvelopeId, saved.SecretEnvelopeId);
    Assert.NotEqual("env-antigo", saved.SecretEnvelopeId);
}
```
Repita para `ReplaceKeyAsync` (mesmo padrão) e para o ramo de rotação de `ChangePassphraseAsync`
(que repointa `Metadata.PassphraseEnvelopeId`).

- [ ] **Passo 2: Rodar e ver falhar** (`--filter "FullyQualifiedName~Keychain"`). Esperado: `store.Updated` vazio.

- [ ] **Passo 3: Implementar** — capturar o retorno e persistir:

```csharp
    public async Task ChangePasswordAsync(CredentialRef cred, char[] newPassword)
    {
        // Só credencial de senha rotaciona por aqui — chave usa ReplaceKey/ChangePassphrase.
        if (cred.Type == CredentialTypes.Password && cred.SecretEnvelopeId is { } envId)
        {
            // RotateAsync cria um envelope com ID NOVO e tombstoneia o antigo. Sem repontar o
            // CredentialRef, ele fica apontando pro tombstone: conectar falha NESTE PC ("Envelope
            // revogado") e, como nenhum metadado muda, nada entra no outbox — a troca nunca chega
            // ao outro device. O UpdateCredentialRefAsync conserta as duas coisas de uma vez.
            SecretEnvelope rotated = await _vault.RotateAsync(
                envId, newPassword, new VaultAccessContext { ActorUserId = Actor });
            await _store.UpdateCredentialRefAsync(cred with { SecretEnvelopeId = rotated.EnvelopeId });
        }

        Array.Clear(newPassword);
    }
```
Análogo em `ReplaceKeyAsync` e no ramo de rotação de `ChangePassphraseAsync` (neste, repontar
`Metadata.PassphraseEnvelopeId` — confira a forma real do `CredentialRef`/`Metadata`, pode não ser
`record` com `with`; se não for, construa a instância como o código vizinho já faz).

- [ ] **Passo 4: Ver passar. Passo 5: Commit** (`fix(cliente): rotacao de segredo repontava para o tombstone`).

---

## Task 2: Canal de segredos com voz e isolamento por item

**Arquivos:**
- Modificar: `src/RemoteOps.Sync/Remote/SecretSyncOrchestrator.cs`
- Modificar: `src/RemoteOps.Sync/Remote/SyncOrchestrator.cs`
- Modificar: `tests/RemoteOps.UnitTests/Sync/` (fakes + testes)

**Contexto:** `SyncOrchestrator.cs:120-124` engole QUALQUER exceção em `catch (Exception)` →
`SetStatus(Error)` sem log. `SecretSyncOrchestrator` push (`:102-132`) e pull não têm isolamento por
item: um envelope malformado trava push E pull, para sempre (o cursor só avança depois da página).

- [ ] **Passo 1: Testes que falham**

```csharp
// Um envelope venenoso não pode impedir os SADIOS de subir.
[Fact]
public async Task Poisoned_Envelope_Does_Not_Block_The_Others()
{
    // cofre com 3 envelopes; o do meio faz o codec lançar (ex.: EnvelopeId não-GUID)
    // asserção: os 2 sadios chegaram na api fake
}

// O pull tem de rodar mesmo se o push falhar — senão o device B para de receber.
[Fact]
public async Task Pull_Still_Runs_When_Push_Fails() { ... }

// A falha do canal de segredos NÃO pode virar o mesmo "Erro" genérico do changelog.
[Fact]
public async Task Secret_Channel_Failure_Is_Reported_Separately() { ... }
```

- [ ] **Passo 2: Rodar, ver falhar.**

- [ ] **Passo 3: Implementar**
- `PushLocalAsync`: try/catch **por envelope** — pula o problemático, segue os demais; registra
  envelopeId + tipo do erro (ADR-013: nada além disso).
- Apply do pull: try/catch **por dto**; item malformado é pulado e a página avança.
- `SyncOnceAsync` do `SecretSyncOrchestrator`: executar o pull mesmo se o push tiver falhado
  (capturar a falha do push, seguir pro pull, e ao final relançar/reportar o que houve).
- `SyncOrchestrator`: capturar a falha do canal de segredos em catch PRÓPRIO e expor estado distinto
  (metadados OK / segredos falhando), em vez de virar `Error` genérico.

- [ ] **Passo 4: Ver passar. Passo 5: Commit.**

---

## Task 3: "Reenviar tudo para a nuvem" — o reparo dos 700

**Arquivos:**
- Criar: `src/RemoteOps.Desktop/Infrastructure/CloudResyncService.cs` (ou similar)
- Modificar: `src/RemoteOps.Desktop/ViewModels/SettingsViewModel.cs` + `Views/SettingsWindow.xaml(.cs)`
- Criar: testes de VM + do serviço

**Contexto e POR QUE assim:** a fila congela o patch no momento da edição (`LocalSyncClient.cs:60`),
então patches de versões antigas são incompletos para sempre. O reparo re-emite pelo **caminho de
update existente** (`UpdateEndpointAsync` etc.), que já lê `baseVersion = versão local atual`
(`SqlCipherLocalStore.cs:373`) e monta o patch COMPLETO com o código de hoje. O servidor só rejeita se
`base_version < currentVersion` (`SyncService.cs:104`) — por isso o **pull ANTES** é obrigatório:
alinha a versão local com a do servidor, e o re-emit sobe com `base_version == servidor` → aceito →
versão+1 → propaga completo.

- [ ] **Passo 1: Testes que falham**

```csharp
[Fact]
public async Task Resync_ReEmits_Every_Entity_Through_The_Update_Path()
{
    // store fake com 2 grupos, 3 assets, 4 endpoints, 2 credential_refs
    // asserção: Update*Async chamado para CADA um (contagens exatas)
}

[Fact]
public async Task Resync_Pulls_Before_ReEmitting()
{
    // asserção de ORDEM: o SyncOnceAsync inicial acontece ANTES do primeiro Update
    // (é o que evita conflito de versão — sem isso, 700 conflitos)
}

[Fact]
public async Task Resync_Is_Idempotent()
{
    // rodar duas vezes não duplica entidade
}

[Fact]
public async Task Resync_Reports_Progress()
{
    // progresso monotônico de 0 a total
}

[Fact]
public async Task Resync_Without_Cloud_Does_Nothing() { ... }
```

- [ ] **Passo 2: Rodar, ver falhar.**

- [ ] **Passo 3: Implementar o serviço**

Assinatura sugerida (ajuste ao que existir):
```csharp
public sealed class CloudResyncService
{
    /// <summary>
    /// Re-emite TODO o acervo local pelo caminho de update normal, para reparar patches incompletos
    /// que ficaram congelados na fila por versões antigas do app.
    ///
    /// <para><b>Por que o pull vem primeiro:</b> o re-emit sobe com a versão LOCAL como base, e o
    /// servidor rejeita <c>base_version &lt; versão dele</c> como conflito. Sem alinhar antes, um
    /// acervo de centenas de devices viraria centenas de conflitos em vez de reparo.</para>
    /// </summary>
    public async Task ResyncAllAsync(IProgress<ResyncProgress>? progress, CancellationToken ct = default)
}
```
Ordem: `SyncOnceAsync` (pull) → grupos → assets → endpoints → credential_refs (cada um pelo
`Update*Async`) → `SyncOnceAsync` (drena). Progresso por item. **Não** toca em envelopes: o canal de
segredos já enumera o cofre inteiro por ciclo (`SecretSyncOrchestrator.cs:98`).

- [ ] **Passo 4: UI** — botão em Configurações → Conta com **confirmação** (explicando o que faz e que
não altera dado nenhum, só reenvia) + progresso + resultado. Sem `FontSize` literal; chaves de tema
conferidas; render test STA nos dois estados (ocioso / em progresso).

- [ ] **Passo 5: Ver passar. Passo 6: Commit.**

---

## Task 4: Teste real de dois devices (a camada que quebrou não tinha teste)

**Arquivos:** criar `tests/RemoteOps.UnitTests/Integration/` (ou projeto de integração, se preferir).

**Contexto:** os testes atuais (`DeviceToDeviceSecretSyncTests`) usam `FakeSecretsApi`; `SecretsTransportTests`
chama o service direto. **A camada HTTP real + rotas + auth nunca é exercitada** — e é exatamente a que
divergiu em produção.

- [ ] **Passo 1: Montar o harness** — `WebApplicationFactory` do `RemoteOps.Cloud` (pipeline completo,
banco in-memory/SQLite), dois clientes com `SecretsApiClient`/`CloudSyncApiClient` REAIS apontados no
handler do TestServer, dois `FileVaultStore` em temp com a **mesma AMK**.

- [ ] **Passo 2: Os quatro cenários**
```
a) round-trip: A cadastra host + credencial de chaveiro E host + senha inline → ciclo A → ciclo B
   → asserção FORTE: o cofre do B ABRE o segredo (decifração real valida AAD/codec fim-a-fim)
b) reparo: servidor com endpoint SEM credential_ref_id → "Reenviar tudo" no A → B passa a ver o vínculo
c) rotação: A troca a senha → B abre a NOVA e o A ainda conecta (pega a Task 1)
d) veneno: envelope malformado no servidor → B ainda entrega os demais + canal reporta erro próprio
```

- [ ] **Passo 3: Ver passar. Commit.**

---

## Task 5: Validação, changelog e release

- [ ] `dotnet build -c Release` (0 avisos), `dotnet test` (tudo verde), `dotnet format --verify-no-changes`.
- [ ] Bump `<Version>` para **1.4.5**; entradas em `CHANGELOG.md` e `operator-changelog.json`
  (linguagem de operador: explique que existe um botão para reenviar o acervo antigo e por que era preciso).
- [ ] PR, CI verde, **revisão adversarial Fable**, tratar achados, merge, tag `v1.4.5`,
  `bash tools/mirror-release.sh v1.4.5`.
