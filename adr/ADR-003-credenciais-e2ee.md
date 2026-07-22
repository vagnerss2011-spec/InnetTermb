# ADR-003 — Credenciais, envelope encryption e DPAPI

## Status

Aceita. Implementada na camada local em `feature/security-vault` (módulo `RemoteOps.Security`).

## Contexto

O sistema sincroniza senhas e chaves privadas. O servidor não deve ser ponto único de vazamento de plaintext.

## Decisão

Criptografar segredos com envelope encryption por workspace. Proteger chaves locais com DPAPI no Windows. Usar SQLCipher ou equivalente para o banco local.

## Consequências positivas

- Reduz impacto de vazamento do banco servidor.
- Protege cache local em notebook perdido.
- Permite rotação por workspace/credencial.

## Consequências negativas

- Recuperação de chave precisa processo formal.
- E2EE adiciona complexidade a multiusuário.
- Debug deve ser feito sem inspecionar plaintext.

## Regras

- Nunca salvar plaintext.
- Nunca logar segredo.
- Recuperação exige auditoria.
- Revelar senha, se existir, exige permissão especial.

## Implementação (camada local)

Esta seção registra o design realizado em `RemoteOps.Security`. Ver `docs/25-credential-vault.md` para detalhes e plano de testes.

### Hierarquia de chaves (envelope encryption)

```
DPAPI (CurrentUser) ── protege ──> Workspace Data Key (WDK, AES-256)
                                         │ embrulha (wrap)
                                         ▼
                                   Content Encryption Key (CEK, AES-256, uma por segredo)
                                         │ criptografa
                                         ▼
                                   Plaintext do segredo
```

- **WDK por workspace**: 32 bytes CSPRNG, nunca persistida em claro. Persiste apenas o blob protegido por DPAPI.
- **CEK por segredo**: gerada por `Seal`, vive em `stackalloc`, zerada no `finally`. Embrulhada pela WDK e guardada no envelope.
- **AES-256-GCM** (in-box `System.Security.Cryptography.AesGcm`): nonce de 12 bytes, tag de 16 bytes, AEAD autenticado.

### Associated Data (AAD)

O AAD liga cada ciphertext à sua identidade lógica e impede troca/replay:

- Conteúdo: `env|{envelopeId}|{workspaceId}|v{version}|{type}`.
- Wrap da CEK: `wdk|{workspaceId}`.

Adulterar campo (incluindo `type`), trocar envelope entre workspaces ou fazer downgrade de versão quebra a verificação do tag → `CryptographicException`.

Esse formato é **congelado**: vale para `DpapiRootedV1` e `AmkRootedV1`, e alterar um byte tornaria ilegível tudo o que já está selado em produção.

#### Raiz do time e AAD ampliado (`WkRootedV1`, Fatia 1)

A chave do workspace de **time** é **aleatória** (32 bytes CSPRNG), não derivada. Precisa ser: `AmkKeyDerivation.DeriveWorkspaceKey` = `HKDF(AMK, workspaceId)` e a **AMK é por conta**, então dois membros do mesmo workspace derivariam chaves **diferentes** e um não abriria o cofre do outro. Sendo sorteada, a WK pode ser **entregue cifrada** a cada membro; em disco fica **embrulhada sob a AMK** de quem a guarda (`AccountKeyService.WrapKey`, AAD `wk|{workspaceId}`), nunca em claro.

- Carimbo: `VaultAlgorithms.WkRootedV1` = `AES-256-GCM;CEK-wrap;WK-random-v1`; raiz `VaultKeyRooting.WkRandom`.
- Implementação: `WkWorkspaceKeyRing` — a **terceira** implementação de `IWorkspaceKeyRing`, ao lado do DPAPI e da AMK.
- **AAD do payload neste esquema:** `env|{envelopeId}|{workspaceId}|v{version}|{type}|{credentialId}|{algorithm}`.

O acréscimo de `credentialId` e `algorithm` responde a um achado da revisão de segurança da v1.4.7: hoje o `Algorithm` e o cabeçalho `type|credentialId` viajam **fora de qualquer AAD** (o cabeçalho vai no `keyVersion`, que o servidor guarda como string opaca), o que permitiria a um servidor malicioso **re-associar** um envelope a outra credencial. Num cofre compartilhado o efeito é concreto: o colega abriria a senha do equipamento X sob o equipamento Y e a usaria. O `algorithm` entra junto para fechar o **downgrade** — sem ele, rebaixar o carimbo para `AmkRootedV1` pediria o AAD antigo, que não tem `credentialId`.

**Limitação assumida:** os envelopes já selados sob `AmkRootedV1`/`DpapiRootedV1` continuam sem `credentialId` no AAD. Corrigir o esquema antigo exigiria reescrever todos eles; a correção entra onde é de graça — nos envelopes que nascem agora. O teste `Amk_CredentialIdTrocado_ContinuaAbrindo_LimitacaoAssumidaDoEsquemaAntigo` documenta a fronteira em código.

O wrap da CEK segue `wdk|{workspaceId}` nas três raízes: ele já prende o embrulho ao workspace, e o esquema é autenticado no AAD do payload.

**O blob do CONVITE também é preso ao workspace.** A WK viaja ao convidado embrulhada sob `K_invite = HKDF(código)`, com AAD `wk|invite|{workspaceId}|v1` (GUID canonizado — "D" minúsculo — porque os dois lados recebem o id do servidor e a caixa da string não é contrato). Sem o id no AAD, quem informa o workspace do convite é o **servidor** (`/invites/{id}/context`): um servidor malicioso trocaria o workspace na resposta e o blob abriria do mesmo jeito — o convidado importaria a WK do time A como chave do time B e selaria segredos de B sob uma chave que o convidador de A conhece, com a lista de membros de B mentindo sobre quem consegue ler. Com o id no AAD, a mentira quebra o tag GCM e o aceite falha **alto, antes de a chave aterrissar**.

#### Custódia do embrulho da WK no servidor (`PUT /workspaces/{id}/key`)

A AMK é **portável entre devices**; o embrulho da WK gravado em disco **não é**. Sem uma cópia do embrulho no servidor, o segundo computador da mesma conta não tem o que restaurar — e o `WkWorkspaceKeyRing` com criação permitida **sortearia outra WK**, bifurcando o cofre do time em silêncio. Por isso cada membership guarda o `WrappedWk` **daquele membro** (blob sob a AMK **dele**, AAD `wk|{workspaceId}`), e cada conta só lê e só escreve o **próprio**.

Até a Fatia 1e esse campo só era escrito no **aceite do convite** — logo, quem **criava** o time nunca tinha embrulho guardado. O `PUT` fecha isso sem acoplar a custódia da chave ao ato de convidar.

Regra de gravação, e por que ela é o máximo que o servidor pode fazer **sem quebrar o E2EE**:

- **Ausente → grava.** Primeira publicação vence.
- **Byte a byte igual → no-op** (200, `stored:false`). É o caminho normal: o app republica a cada abertura, e o blob publicado é o **guardado em disco** (não um re-embrulho), então a igualdade se sustenta. Pelo mesmo motivo, restaurar do servidor guarda o blob **verbatim**.
- **Diferente → 409, e o blob guardado NÃO muda.** O servidor não tem AMK nenhuma: dois blobs diferentes tanto podem ser a mesma WK com nonce novo quanto WKs diferentes. Distinguir os dois casos exigiria o servidor **conhecer a chave** — exatamente o que este ADR proíbe. Então a detecção possível é por **presença e igualdade de bytes**, nunca por comparação de chave.
- **Quem compara chave é o cliente.** No 409 ele baixa o embrulho guardado e o abre com a própria AMK: mesma chave → no-op silencioso; chave diferente → erro alto em pt-BR. Recusar (em vez de ignorar em silêncio) é o que devolve a ambiguidade ao único lado capaz de resolvê-la.

### Proteção da chave local (DPAPI)

- Via P/Invoke a `crypt32.dll` (`CryptProtectData`/`CryptUnprotectData`) — **sem pacote NuGet externo**, honrando a restrição "sem libs externas sem ADR".
- Escopo **CurrentUser** (sem flag `LOCAL_MACHINE`) + entropia ligada ao workspace (`remoteops:wdk:{workspaceId}`).
- Consequência direta do threat model: blob copiado para outro usuário/máquina **não abre** (notebook roubado / conta diferente).

### Rotação e revogação

- `RotateAsync`: cria novo envelope com `Version + 1` e nova CEK; o envelope anterior vira *tombstone* (revogado, material criptográfico zerado para `[]`).
- `RevokeAsync`: marca `RevokedAt` e zera o material; recuperação posterior lança `VaultException`.
- O *tombstone* nasce com **`Version + 1`**: a revogação é uma versão nova do registro, não uma edição da atual. Sem o incremento ela seria indistinguível da cópia viva para o ledger de push e para a monotonicidade do servidor, e **morreria no device onde foi feita**.

#### Propagação da revogação (v1.4.7)

Até a v1.4.6 o *tombstone* **não subia**: `IsSyncable` exigia `RevokedAt is null` e o backend recusava material vazio. O efeito não era de transporte, era de **segurança** — ao trocar/revogar uma senha, o envelope antigo continuava **vivo e decifrável no disco do outro device, para sempre**.

O contrato agora é ponta a ponta:

- `SecretEnvelopeDto` ganha **`revokedAt` opcional** (entra *adicionando*: servidor e cliente antigos ignoram o campo e continuam funcionando).
- O servidor aceita **base64 vazio exclusivamente** quando `revokedAt` vem preenchido; para envelope vivo, corpo vazio continua sendo 400.
- **Revogação é caminho só de ida.** No servidor, upsert vivo por cima de um envelope revogado é recusado (`envelope.revoked`); no device que recebe, um *tombstone* local nunca é sobrescrito por cópia viva — nem na **mesma versão**, caso que o guarda de downgrade (`existing.Version > incoming.Version`) não cobre e que ressuscitaria a senha revogada no disco.
- O device que recebe grava o material **vazio**: é ele que apaga o segredo lá, e não apenas a marca.

### Não exposição de segredo

- Plaintext só existe dentro de `VaultSecret` (IDisposable, zera buffer no `Dispose`); `ToString()` redigido (`VaultSecret(***)`).
- `SecretEnvelope.ToString()` e `VaultAuditEvent` não contêm material secreto. Auditoria estruturada registra ação, workspace, ator, envelope, versão e timestamp — nunca o segredo.
- Erros DPAPI expõem apenas o código Win32, nunca o conteúdo.

### Alternativas consideradas

- `System.Security.Cryptography.ProtectedData` (NuGet): rejeitado para evitar dependência externa não coberta por ADR. O P/Invoke direto entrega o mesmo DPAPI sem nova dependência.
- AES-CBC + HMAC: rejeitado em favor de GCM (AEAD in-box, AAD nativo, menos margem para erro de composição).

### Limitações conhecidas (revisão de segurança da PR)

- **Contrato fino `ICredentialVault` usa `string`**: `string` é imutável e não pode ser zerada — o segredo vive na heap até o GC. É aceitável só para a fronteira cross-module; RBAC/UI devem usar `IVault` com `ReadOnlyMemory<char>`/`VaultSecret`.
- **`FileVaultStore` herda os ACLs do diretório**: é store de referência (a produção usa SQLCipher). O caminho deve ser privado do usuário (`%APPDATA%\RemoteOps\`); ACL explícita fica como hardening rastreado.
- **Auditoria obrigatória**: o `CredentialVault` exige um `IVaultAuditSink` explícito; descartar eventos requer passar `NullVaultAuditSink.Instance` conscientemente.

### Pendências fora deste escopo

- Persistência SQLCipher real (hoje há `FileVaultStore`/in-memory para a camada e testes) — issue a abrir.
- Gatilho de "revelar senha exige permissão especial" via RBAC (`docs/18`) na camada de aplicação — issue a abrir.
- ACL restritiva no `FileVaultStore` (defesa em profundidade sobre o DPAPI) — issue a abrir.
- Teste validando que o blob DPAPI não abre em escopo `LocalMachine` — issue a abrir.
