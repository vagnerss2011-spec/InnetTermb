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

### Proteção da chave local (DPAPI)

- Via P/Invoke a `crypt32.dll` (`CryptProtectData`/`CryptUnprotectData`) — **sem pacote NuGet externo**, honrando a restrição "sem libs externas sem ADR".
- Escopo **CurrentUser** (sem flag `LOCAL_MACHINE`) + entropia ligada ao workspace (`remoteops:wdk:{workspaceId}`).
- Consequência direta do threat model: blob copiado para outro usuário/máquina **não abre** (notebook roubado / conta diferente).

### Rotação e revogação

- `RotateAsync`: cria novo envelope com `Version + 1` e nova CEK; o envelope anterior vira *tombstone* (revogado, material criptográfico zerado para `[]`).
- `RevokeAsync`: marca `RevokedAt` e zera o material; recuperação posterior lança `VaultException`.

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
