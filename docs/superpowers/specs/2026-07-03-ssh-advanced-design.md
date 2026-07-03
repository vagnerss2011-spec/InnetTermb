# Front "SSH avançado" — chave privada + perfis de algoritmos — Design

**Data:** 2026-07-03
**Branch/worktree:** `feature/ssh-advanced` (`C:\dev\remoteops-ssh-advanced`), base `origin/main` `df34111` (v1.1.2).
**Release alvo:** **v1.2.0** (feature nova → bump minor, acordado com o usuário).

## Objetivo

Fechar o último ponto do feedback de campo: (1) **autenticação SSH por chave privada** (credencial nova no Keychain) e (2) **compatibilidade de key algorithms "tipo o PuTTY"** para equipamentos antigos e novos (MikroTik, OLTs, switches), via **perfil por endpoint**.

## Decisões (travadas no brainstorming)

| Decisão | Escolha |
|---|---|
| Formato de chave | **PEM/OpenSSH/ssh.com nativos** (RSA/DSA/ECDSA/Ed25519 — o que `Renci.SshNet 2024.2.0 PrivateKeyFile` lê). `.ppk` detectado explicitamente com orientação: *PuTTYgen → Conversions → Export OpenSSH key*. Sem parser PPK próprio. |
| Algoritmos | **Perfil por endpoint** com 3 presets: **Automático** (defaults da lib), **Compatível** (garante `diffie-hellman-group14-sha1`, `ssh-rsa`, `aes*-cbc`, `hmac-sha1`), **Legado** (Compatível + `diffie-hellman-group1-sha1`, `3des-cbc`). Sem lista ordenável estilo PuTTY nesta frente. |
| Passphrase | **Suportada**, guardada em **envelope separado** no vault (`CredentialMetadata.PassphraseEnvelopeId`) — rotaciona sem re-enviar a chave. |
| Entrada da chave | **Arquivo (Procurar…) E colar texto**, com validação comum (`-----BEGIN`) e detecção de PPK. |
| Auth no provider | **Dispatch estrito por `CredentialRef.Type`** (`privateKey` → chave; senão senha). Sem cadeia de fallback. |
| Valor canônico do tipo | `"privateKey"` (já previsto no comentário de `CredentialRef.cs:9`), fixado em constante compartilhada (`CredentialTypes`). O Type entra no AAD do AES-GCM — grafia divergente quebraria decrypt. |
| Rotação | **Replace key** (rotaciona envelope da chave) e **Change passphrase** (rotaciona/cria envelope da passphrase) — ações separadas; `ChangePassword` clássico fica só para credencial `password`. |

## Restrições globais (herdadas)

- `.NET 10`, WPF, `TreatWarningsAsErrors=true` → 0/0; Nullable; testes xUnit; suíte (**489**) verde; `dotnet format --verify-no-changes` (Release) antes de push.
- **Segredo nunca** em log/UI/auditoria/commit: chave e passphrase trafegam `char[]`/`VaultSecret` (auto-zero), padrão `GetPassword()` do code-behind.
- Rótulos do Keychain em **inglês**; resto pt-BR.
- SSH.NET 2024.2.0: `ConnectionInfo` expõe `KeyExchangeAlgorithms`/`HostKeyAlgorithms`/`Encryptions`/`HmacAlgorithms` (dicionários ordenados); `PrivateKeyFile(Stream, string? passphrase)`; `PrivateKeyAuthenticationMethod(username, keyFiles)`. `PasswordAuthenticationMethod` exige string (ADR-009 §FIX-3 documenta o mitigante).

## Arquitetura (3 unidades)

### U1 — Contratos + Keychain (credencial "SSH key")

**Contratos (`RemoteOps.Contracts.Assets`):**
- Novo `CredentialTypes` estático: `Password = "password"`, `PrivateKey = "privateKey"`, `PrivateKeyPassphrase = "privateKeyPassphrase"`.
- `CredentialMetadata` + `public string? PassphraseEnvelopeId { get; init; }` (aditivo; `metadata_json` serializa transparente).
- `EndpointProfile` + `public string? SshAlgorithmProfile { get; init; }` (`"auto" | "compat" | "legacy"`; null = auto). Persistência já existe (`profile_json` no SqlCipher).

**Validação pura (`RemoteOps.Desktop/Infrastructure/PrivateKeyInput.cs`):**
- `enum PrivateKeyKind { Valid, PuttyPpk, Invalid }`; `static PrivateKeyKind Classify(string text)`: `-----BEGIN` (qualquer variante PEM/OpenSSH) → Valid; começa com `PuTTY-User-Key-File` → PuttyPpk; senão Invalid. Puro, testável.

**KeychainViewModel:**
- `CreateAsync` (senha) inalterado.
- Novo `CreateKeyAsync(string name, string username, char[] privateKey, char[]? passphrase)`: `vault.StoreAsync(Type=PrivateKey)` → envelope da chave; se passphrase não-vazia → segundo `StoreAsync(Type=PrivateKeyPassphrase)`; `CredentialRef { Type=PrivateKey, Metadata { Username, HasPrivateKey=true, PassphraseEnvelopeId } }`; `Array.Clear` em ambos.
- Novo `ReplaceKeyAsync(cred, char[] newKey)`: `RotateAsync` no envelope da chave.
- `ChangePassphraseAsync(cred, char[] newPassphrase)`: se `PassphraseEnvelopeId` existe → `RotateAsync`; senão → `StoreAsync` + `UpdateCredentialRefAsync` com o novo id.
- `ChangePasswordAsync` ganha guarda: se `Type != Password` → não faz nada (a View nem oferece).
- `DeleteAsync`: revoga também o envelope de passphrase quando existir.

**CredentialDialog (UI, inglês):**
- Add mode ganha `Type` (ComboBox **Password** | **SSH key**).
- Painel SSH key: **Browse…** (`OpenFileDialog`, filtro `*.pem;*.key;*.openssh;*.*`) e TextBox multiline para colar; **Passphrase (optional)** PasswordBox.
- Code-behind: `char[] GetPrivateKey()` (do arquivo lido ou do TextBox — nunca prop bindável) e `char[] GetPassphrase()` (mesmo padrão Marshal do `GetPassword`).
- Validação no Save: `PrivateKeyInput.Classify` → PuttyPpk mostra a mensagem do PuTTYgen; Invalid mostra "Cole/importe uma chave privada OpenSSH/PEM (-----BEGIN …)".
- KeychainView: toolbar ganha **Replace key** e **Change passphrase** (habilitados só para credencial `privateKey`); **Change password** habilitado só para `password`.

### U2 — Perfis de algoritmos (`RemoteOps.Terminal.Ssh.SshAlgorithmPolicy`)

- `static class SshAlgorithmPolicy` com `public const string Auto/Compat/Legacy` e `static void Apply(ConnectionInfo info, string? profile)`:
  - `null`/`"auto"` → não toca nada (defaults da lib).
  - `"compat"` → garante presentes (adicionando ao FIM da ordem, preferência continua moderna): KEX `diffie-hellman-group14-sha1`, `diffie-hellman-group-exchange-sha1`; HostKey `ssh-rsa`; Ciphers `aes128-cbc`, `aes192-cbc`, `aes256-cbc`; HMAC `hmac-sha1`. Entradas movidas/copiadas a partir do catálogo da própria lib (nunca implementação própria de crypto).
  - `"legacy"` → compat + KEX `diffie-hellman-group1-sha1`; Cipher `3des-cbc`.
- Como os dicionários da `ConnectionInfo` já vêm populados com tudo que a lib suporta, `Apply` **reordena/garante** entradas usando os `Func` originais do próprio dicionário (remove e re-insere no fim) — zero criptografia nova; testável inspecionando presença/ordem das chaves.

### U3 — Provider SSH (chave + perfil na conexão)

- `SshConnectionOptions` (interno, `RemoteOps.Terminal.Ssh`): `Host`, `Port`, `Username`, `Password` (string?), `PrivateKeyUtf8` (byte[]?), `PrivateKeyPassphrase` (string?), `AlgorithmProfile` (string?).
- `ISshConnectionFactory.Create(SshConnectionOptions)` substitui a assinatura atual (interface interna, 1 impl + fakes de teste — mudança contida):
  - chave: `new PrivateKeyFile(new MemoryStream(PrivateKeyUtf8), passphrase)` → `PrivateKeyAuthenticationMethod(username, keyFile)`; parse inválido/passphrase errada → `InvalidOperationException("Chave privada inválida ou passphrase incorreta.")` (aparece na aba — mecanismo v1.1.1).
  - senha: `PasswordAuthenticationMethod` como hoje.
  - sempre: `SshAlgorithmPolicy.Apply(info, options.AlgorithmProfile)` antes de criar o client.
- `SshSessionProvider.OpenAsync`:
  - branch por `credRef.Type == CredentialTypes.PrivateKey`: recupera envelope da chave (`RevealUtf8()` → byte[]) e, se `Metadata.PassphraseEnvelopeId` presente, o da passphrase (`RevealString()`); ambos via `using VaultSecret` no mesmo escopo do connect (padrão atual da senha).
  - lê `endpoint.Profile?.SshAlgorithmProfile` → `options.AlgorithmProfile`.
  - `ConnectWithTofuAsync` passa a receber `SshConnectionOptions` (TOFU/host-key inalterados).

### HostEditor (UI do perfil)

- Linha "adicionar endpoint" ganha coluna **"Compat. SSH"** (ComboBox: Automático/Compatível/Legado, default Automático; tooltip explica quando usar e avisa que Legado usa algoritmos fracos — só para o host que precisa). Janela vai a 800px.
- Ao adicionar endpoint `ssh` com perfil ≠ Automático: `Endpoint.Profile = new EndpointProfile { SshAlgorithmProfile = valor }` (preserva `VendorProfile`/`TerminalEncoding` se editando endpoint existente — nesta frente só criação nova usa o combo). Para outros protocolos o combo é ignorado.

## Fluxo de dados

Keychain → vault (2 envelopes) → `CredentialRef(Type=privateKey, Metadata.PassphraseEnvelopeId)` → host editor anexa credencial ao endpoint (fluxo existente) + perfil SSH no `Endpoint.Profile` → `SessionRequest` → `SshSessionProvider` resolve endpoint+credencial → `SshConnectionOptions` → factory aplica auth por chave + `SshAlgorithmPolicy` → `Connect()` (TOFU inalterado).

## Erros

- Diálogo: PPK → mensagem PuTTYgen; formato inválido → mensagem clara; chave vazia → Save desabilitado.
- Conexão: parse/passphrase errada → `InvalidOperationException` legível → aba mostra "Falha ao conectar: …" (v1.1.1). Algoritmo incompatível → mensagem da lib aparece na aba; orientação: trocar o perfil para Compatível/Legado no editor do host.
- Credencial `privateKey` usada como senha: **impossível** — dispatch estrito por Type (antes do branch, o PEM seria enviado como senha ao servidor).

## Testes (TDD)

- `PrivateKeyInput.Classify` (PEM ok / OpenSSH ok / PPK detectado / lixo).
- `SshAlgorithmPolicy.Apply` (auto intocado; compat contém group14-sha1+ssh-rsa+aes-cbc+hmac-sha1; legacy contém group1-sha1+3des; modernos continuam à frente).
- `KeychainViewModel.CreateKeyAsync` (2 envelopes com/sem passphrase; metadata correto; buffers zerados — FakeVault registra tipos), `ReplaceKeyAsync`/`ChangePassphraseAsync` (rotação/criação), `DeleteAsync` revoga os dois, `ChangePasswordAsync` guarda por tipo.
- `SshSessionProvider` com fake factory: credencial `privateKey` → options com `PrivateKeyUtf8` e sem `Password`; credencial `password` → inverso; perfil do endpoint chega em `AlgorithmProfile`.
- `HostEditorViewModel`: endpoint ssh com perfil Compatível → `Profile.SshAlgorithmProfile == "compat"`; Automático → Profile null (ou campo null).
- Guarda de segredo: nenhum log/auditoria contém material de chave.

## Fora de escopo (registrado)

- **keyboard-interactive** (alguns MikroTik/Cisco só autenticam assim) — limitação conhecida; front próprio.
- **Parser .ppk** embutido; **lista ordenável estilo PuTTY** (o preset cobre; combo é o caminho de evolução).
- **Persistência do HostKeyStore (TOFU)** — hoje in-memory (re-pergunta por restart); follow-up recomendado.
- Edição de perfil SSH em endpoint já existente (nesta frente: remover e re-adicionar o endpoint).
