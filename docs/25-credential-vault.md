# 25 — Cofre de credenciais (Credential Vault)

Camada local de credenciais do `RemoteOps.Security`: envelope encryption por workspace, chave local protegida por DPAPI no Windows, rotação, revogação e auditoria sem segredo.

Referências: `docs/05-seguranca-credenciais-threat-model.md`, `docs/18-modelo-permissoes-rbac.md`, `docs/19-observabilidade-auditoria.md`, `adr/ADR-003-credenciais-e2ee.md`.

## Objetivo

Guardar senhas e chaves privadas de forma que:

- nenhum plaintext seja persistido ou logado;
- o cache local não abra em outro usuário/máquina (notebook roubado);
- seja possível rotacionar e revogar por credencial;
- toda operação sensível gere auditoria estruturada sem expor o segredo.

## Hierarquia de chaves

```
DPAPI (CurrentUser) ── protege ──> Workspace Data Key (WDK, AES-256, 1 por workspace)
                                         │ wrap (AES-256-GCM)
                                         ▼
                                   Content Encryption Key (CEK, AES-256, 1 por segredo)
                                         │ encrypt (AES-256-GCM)
                                         ▼
                                   Plaintext do segredo
```

Padrão de envelope encryption (mesma ideia de AWS KMS/GCP KMS): a WDK nunca aparece em claro no disco — só o blob protegido por DPAPI. Cada segredo tem sua própria CEK, embrulhada pela WDK.

## Componentes

| Tipo | Papel |
| --- | --- |
| `IVault` / `CredentialVault` | API rica do módulo: `StoreAsync`, `RetrieveAsync`, `RotateAsync`, `RevokeAsync` com `ReadOnlyMemory<char>` e contexto de auditoria. |
| `ICredentialVault` | Contrato fino cross-module (compatível com `RemoteOps.Contracts`). Implementado por `CredentialVault`. |
| `EnvelopeCipher` | Núcleo criptográfico: `Seal`/`Open` com CEK + wrap pela WDK, AAD ligando identidade. |
| `IWorkspaceKeyRing` / `WorkspaceKeyRing` | Gera/carrega a WDK; protege via `ILocalKeyProtector`. |
| `ILocalKeyProtector` / `DpapiKeyProtector` | DPAPI por P/Invoke a `crypt32.dll` (CurrentUser, sem NuGet externo). |
| `ICredentialStore` / `IWorkspaceKeyStore` | Persistência. `InMemory*` e `FileVaultStore` para camada/testes (SQLCipher fica para frente futura). |
| `SecretEnvelope` | Registro persistido: só material cifrado + metadados. `ToString()` redigido. |
| `VaultSecret` | Plaintext vivo em memória; `IDisposable` que zera o buffer; `ToString()` redigido. |
| `IVaultAuditSink` / `VaultAuditEvent` | Auditoria estruturada sem segredo. |

## Associated Data (AAD)

- Conteúdo: `env|{envelopeId}|{workspaceId}|v{version}|{type}`.
- Wrap da CEK: `wdk|{workspaceId}`.

Como GCM é AEAD, o AAD é autenticado mas não cifrado. Trocar envelope entre workspaces, fazer downgrade de versão ou adulterar qualquer campo (inclusive `type`) quebra o tag → `CryptographicException`.

## Proteção da chave local (DPAPI)

- `CryptProtectData`/`CryptUnprotectData` via P/Invoke, escopo **CurrentUser** (`CRYPTPROTECT_UI_FORBIDDEN`, sem `LOCAL_MACHINE`).
- Entropia adicional ligada ao workspace: `remoteops:wdk:{workspaceId}`.
- Erro DPAPI lança `CryptographicException` apenas com o código Win32 — nunca com material secreto.
- Em plataforma não-Windows, `DpapiKeyProtector` lança `PlatformNotSupportedException`; os testes cross-platform usam um `ILocalKeyProtector` falso que modela a ligação de identidade.

## Ciclo de vida

- **Store**: gera CEK, cifra plaintext, embrulha CEK na WDK, persiste `SecretEnvelope` (versão 1), audita `credential.create`.
- **Retrieve**: desembrulha CEK com a WDK, decifra, devolve `VaultSecret` (lifetime mínimo), audita `credential.use`.
- **Rotate**: novo envelope `Version + 1` com nova CEK; envelope antigo vira *tombstone* revogado; audita `credential.rotate`.
- **Revoke**: marca `RevokedAt`, zera material criptográfico; recuperação posterior lança `VaultException`; audita `credential.revoke`.

## Garantias de não-vazamento

- Plaintext só dentro de `VaultSecret`, zerado no `Dispose`.
- Buffers transitórios UTF-8 alugados de `ArrayPool` e zerados no `finally`.
- `SecretEnvelope`, `VaultAuditEvent` e todos os `ToString()` não contêm segredo.
- Nada de senha em log, exceção ou auditoria.

## Plano de testes

Mapeamento dos critérios de aceite da frente para os testes em `tests/RemoteOps.UnitTests/Security/`.

| Critério de aceite | Teste | Arquivo |
| --- | --- | --- |
| Criptografa e descriptografa (round-trip) | `Store_Then_Retrieve_Returns_Original_Secret`, `Empty_Secret_RoundTrips` | `VaultRoundTripTests.cs` |
| Nenhum plaintext persistido | `Stored_Envelope_Never_Contains_Plaintext` | `VaultRoundTripTests.cs` |
| Adulteração é detectada (AEAD) | `Tampered_Ciphertext_Fails_Authentication` | `VaultRoundTripTests.cs` |
| Restart preserva o segredo | `Secret_Survives_Process_Restart` | `VaultRestartTests.cs` |
| Outro usuário NÃO abre | `Different_User_Cannot_Open_Secret` | `VaultIsolationTests.cs` |
| Outra máquina NÃO abre | `Different_Machine_Cannot_Open_Secret` | `VaultIsolationTests.cs` |
| Rotação incrementa versão e revoga o anterior | `Rotate_Bumps_Version_New_Secret_Retrievable_Old_Revoked` | `VaultRotationRevocationTests.cs` |
| Revogação impede recuperação | `Revoke_Makes_Secret_Unretrievable`, `Revoking_Unknown_Envelope_Throws` | `VaultRotationRevocationTests.cs` |
| Contrato fino round-trip + revoga | `Thin_Contract_RoundTrips_And_Revokes` | `VaultRotationRevocationTests.cs` |
| Auditoria sem segredo | `Audit_Records_Lifecycle_Without_Any_Secret` | `VaultAuditTests.cs` |
| `VaultSecret` redigido / seguro pós-dispose | `VaultSecret_ToString_Is_Redacted`, `VaultSecret_Throws_After_Dispose` | `VaultAuditTests.cs` |
| Tombstone apaga material criptográfico | `Revoked_Envelope_Has_Crypto_Material_Erased` | `VaultTamperingTests.cs` |
| AAD autentica workspace/versão/tipo (anti troca/replay/downgrade) | `Tampering_WorkspaceId_Breaks_Open`, `Tampering_Version_Breaks_Open`, `Tampering_Type_Breaks_Open` | `VaultTamperingTests.cs` |
| DPAPI real round-trip / entropia errada falha (Windows) | `Protect_Then_Unprotect_RoundTrips_On_Windows`, `Unprotect_With_Wrong_Entropy_Fails_On_Windows` | `DpapiKeyProtectorTests.cs` |

Os testes de isolamento usuário/máquina rodam cross-platform usando um `ILocalKeyProtector` falso que modela a identidade DPAPI; os testes de DPAPI real só executam de fato no job `windows-latest` do CI (no-op fora do Windows).

## Uso seguro e limitações

- **Auditoria é obrigatória**: `CredentialVault` exige um `IVaultAuditSink` no construtor (ADR-003). Para descartar eventos conscientemente, passe `NullVaultAuditSink.Instance` — não há default silencioso.
- **Prefira `IVault` a `ICredentialVault`**: o contrato fino materializa o segredo como `string` (imutável, não zerável). É só para a fronteira cross-module; RBAC/UI devem usar `IVault` com `ReadOnlyMemory<char>` e `VaultSecret`.
- **Caminho do `FileVaultStore` deve ser privado do usuário** (`%APPDATA%\RemoteOps\`). O DPAPI já garante que o blob não abre para outro usuário, mas o arquivo herda os ACLs do diretório; ACL explícita é hardening rastreado.

## Pendências (issues a abrir)

- Persistência SQLCipher real substituindo `FileVaultStore` na integração com o DB local + outbox.
- "Revelar senha exige permissão especial" via RBAC (`docs/18`) na camada de aplicação.
- ACL restritiva no `FileVaultStore` (defesa em profundidade sobre o DPAPI).
- Teste verificando que o blob DPAPI não abre em escopo `LocalMachine`.
