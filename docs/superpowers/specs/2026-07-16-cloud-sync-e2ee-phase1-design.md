# Cloud Sync E2EE — Fase 1 (Núcleo multi-device) — Design

**Data:** 2026-07-16
**Status:** proposto (aguardando revisão do operador)
**Autor:** Vagner + Claude
**Escopo:** Fase 1 de 5 (ver §11). Refs: `docs/04-modelo-dados-sync.md`, `docs/10-backend-cloud-sync.md`, ADR-002 (offline-first), ADR-003 (E2EE de credenciais), ADR-013 (cliente sync).

## 1. Objetivo

Fazer o fluxo **"logo numa conta em qualquer PC e tenho todos os meus dados — inclusive as senhas dos equipamentos, decifráveis"** funcionar de ponta a ponta, com **E2EE** (o servidor nunca vê nada em claro), sobre o servidor de sync que já existe, deployado no Debian 13 do operador.

## 2. Contexto (o que já existe / o bloqueador)

Já existe (ver mapa de exploração): engine de sync push/pull com cursores + outbox durável (`RemoteOps.Sync/Remote/SyncOrchestrator`), login email/senha + JWT + refresh no backend (`remoteops-cloud`), SignalR hints, e a criptografia de envelope dos segredos (`RemoteOps.Security/Crypto/EnvelopeCipher`: CEK por segredo, embrulhado por WDK por workspace).

**Bloqueador que esta fase resolve:** hoje a raiz de chave é **DPAPI CurrentUser** — a WDK é aleatória **por máquina** e o backend **recusa** transportar segredos. Logo, um 2º device baixa metadados mas **não decifra as senhas**. A Fase 1 troca a *raiz* por uma **AMK portável** com escrow por senha (E2EE) e habilita o transporte dos segredos cifrados.

## 3. Decisões travadas (do brainstorming)

| Tema | Decisão |
|------|---------|
| Modelo de chave | **E2EE**: senha da conta (Argon2id) deriva a chave que abre o cofre; servidor só guarda blobs cifrados + escrow. |
| Recuperação | **Chave de recuperação** (segundo escrow da AMK), exibida 1x no registro. |
| KDF | **Argon2id** (memory-hard) — adiciona 1 lib de cripto vetada (MIT). |
| Arquitetura | **Servidor próprio** (completar ASP.NET + Postgres) no **Debian 13**; backup replicado é Fase 5. |
| Escopo | Fase 1 = núcleo E2EE multi-device (sem 2FA/recovery-email/replicação). |

## 4. Modelo de criptografia (o núcleo)

### 4.1 Hierarquia

```
senha  ──Argon2id(salt=perAccount, params públicos)──►  MasterKey (32B)   [SÓ no device]
MasterKey ──HKDF-SHA256──► AuthHash (32B)   → enviado ao servidor (prova de senha)
MasterKey ──HKDF-SHA256──► KEK (32B)        → embrulha/desembrulha a AMK   [nunca sai do device]

AMK (Account Master Key, 32B CSPRNG, criada 1x no registro) = raiz PORTÁVEL do cofre
  ├─ embrulha ─► WDK (por workspace) ─► embrulha ─► CEK (por segredo) ─► AES-256-GCM ─► senha do equipamento
  ├─ escrow "pwd":  Wrap(AMK, KEK)          ── guardado CIFRADO no servidor
  └─ escrow "rec":  Wrap(AMK, RecoveryKey)  ── guardado CIFRADO no servidor
```

- **Wrap/Unwrap** = AES-256-GCM (reusa `EnvelopeCipher` — a camada WDK→CEK já existe; muda só a *raiz*).
- **HKDF domain-separation:** `info="remoteops:e2ee:auth:v1"` para o AuthHash; `info="remoteops:e2ee:kek:v1"` para a KEK. Garante que o AuthHash (que o servidor recebe) é matematicamente incapaz de derivar a KEK.
- **Argon2id params (v1, ajustáveis por perfil da máquina, guardados no servidor por conta):** memória 64 MiB, iterações 3, paralelismo 1, saída 32B. Salt = 16B CSPRNG por conta (público).
- **RecoveryKey:** 160 bits CSPRNG, exibida formatada (grupos base32, ex.: `XXXX-XXXX-...`), nunca persistida em claro.

### 4.2 O que o servidor guarda (tudo opaco)
`argon2_salt`, `argon2_params`, `auth_hash_hash` (PBKDF2 do AuthHash — o servidor nem guarda o AuthHash cru), `wrapped_amk_pwd` (`AMK⊗KEK`), `wrapped_amk_rec` (`AMK⊗RecoveryKey`), `amk_key_version`. E os **SecretEnvelope** (ciphertext/nonce/tag/wrappedCEK — já opacos) + changelog. **Nunca:** senha, MasterKey, KEK, AMK, WDK, CEK, ou qualquer plaintext.

### 4.3 Em repouso, em cada device
Depois do login/unlock, a AMK desembrulhada é cacheada **protegida por DPAPI CurrentUser** (defesa em profundidade — mantém a propriedade "laptop roubado sem a senha não abre"), e a KEK/MasterKey são zeradas da memória. A portabilidade vem do escrow; o DPAPI protege só o cache local.

## 5. Contratos / endpoints (backend)

Novos/estendidos (todos HTTPS, corpo JSON, blobs em base64):

- `POST /auth/register` — `{email, argon2Salt, argon2Params, authHash, wrappedAmkPwd, wrappedAmkRec, amkKeyVersion, firstWorkspace{...}}` → cria conta+workspace; servidor guarda `PBKDF2(authHash)`. **Não** recebe senha nem AMK em claro.
- `GET /auth/kdf?email=` — devolve `{argon2Salt, argon2Params}` (pré-login, pra o device derivar a MasterKey). Rate-limited; resposta uniforme pra e-mails inexistentes (anti-enumeração).
- `POST /auth/login` — passa a receber `authHash` (não a senha) + `deviceId/deviceName`; retorna `{access, refresh, wrappedAmkPwd, amkKeyVersion, workspaces[]}`.
- `POST /auth/password/change` — `{oldAuthHash, newAuthHash, newArgon2Salt/Params, newWrappedAmkPwd}` (a AMK não muda; re-embrulha só).
- `POST /account/recover/rewrap` — (pós reset de acesso, Fase 4 usa o email; aqui o contrato já existe) `{recoveryProof, newWrappedAmkPwd, newArgon2Salt/Params, newAuthHash}`.
- **Segredos:** `POST /secrets` (upsert `SecretEnvelope` opaco) + `GET /secrets?since=cursor` — o sync passa a transportar `SecretEnvelope` (hoje é recusado). O `changelog` referencia `secret_envelope_id`; o blob viaja por estes endpoints (fora do changelog de metadados).

## 6. Fluxos

- **Criar conta (device 1):** gera AMK+WDK, deriva MasterKey→AuthHash+KEK, monta os 2 escrows, gera RecoveryKey → `POST /auth/register`. **Exibe a RecoveryKey 1x** (com "copie e guarde"). Migra os dados locais atuais (§7).
- **Logar (device 2):** `GET /auth/kdf` → deriva MasterKey da senha → `POST /auth/login` (AuthHash) → recebe `wrappedAmkPwd` → desembrulha AMK com a KEK → sync baixa metadados **e** `SecretEnvelope` → decifra com WDK/CEK. **Tudo aparece.**
- **Unlock (reabrir o app):** usa o cache DPAPI da AMK; se ausente/expirado, pede a senha e re-deriva.
- **Trocar senha:** `password/change` re-embrulha a AMK (segredos intactos).
- **Recuperação (com a RecoveryKey):** desembrulha `wrappedAmkRec` → re-embrulha a AMK sob a nova senha → sobe.
- **Perdeu senha E recovery key:** cofre irrecuperável por design (E2EE) — deixar explícito na UI.

## 7. Migração dos dados locais existentes

Uma vez, na criação/primeiro-login E2EE: para cada `SecretEnvelope` local, `Open` com a WDK antiga (DPAPI) → `Seal` sob a WDK nova (raiz AMK). Idempotente e reversível-por-backup (copiar `vault.json`/DB antes). Sem isso o operador perderia os hosts/credenciais atuais.

## 8. Cliente (RemoteOps.Desktop / RemoteOps.Sync)

- **`AccountKeyService`** (novo, em `RemoteOps.Security`): Argon2id (nova lib) + HKDF + wrap/unwrap da AMK + geração/validação da RecoveryKey. **Puro/testável** (sem IO/rede) — o alvo dos primeiros testes.
- **UI de conta:** janela **Criar conta / Entrar** (email, senha, confirmar; tela pós-registro com a RecoveryKey). Integra no fluxo de startup (hoje não há login).
- **`AccountSyncCoordinator`:** liga login → tokens (VaultTokenStore) → AMK (cache DPAPI) → `SyncSessionFactory`. Sync de segredos habilitado.
- Reusa `EnvelopeCipher`/`WorkspaceKeyRing` trocando a raiz da WDK (DPAPI → AMK-wrapped).

## 9. Backend (remoteops-cloud) — deployável

- Entidades/campos novos: colunas de KDF/escrow em `UserEntity`; endpoint/persistência de `SecretEnvelope`; `/auth/register`, `/auth/kdf`, `password/change`.
- **Migrations EF** (`dotnet ef migrations add InitialCreate` + `Database.Migrate()` no startup) — hoje não existem.
- **Deploy Debian 13:** Dockerfile + docker-compose (API + Postgres), HTTPS (reverse proxy/Caddy ou cert), env vars (`Jwt__SecretKeyBase64`, `REMOTEOPS_DB_CONNECTION`), seed do 1º tenant/workspace. Runbook de instalação.

## 10. Segurança / threat model (Fase 1)

- **Servidor comprometido / DB vazado:** atacante tem só ciphertext + `AMK⊗KEK` (inútil sem a senha) + `AMK⊗RecoveryKey` (inútil sem a recovery key) + `PBKDF2(AuthHash)`. **Não decifra segredos.** ✔
- **MITM:** TLS obrigatório (já enforçado; `REMOTEOPS_CLOUD_URL` https).
- **Device roubado (sem senha):** cache DPAPI CurrentUser não abre → sem segredos. ✔
- **Anti-enumeração de email**, rate-limit em `/auth/kdf` e `/auth/login`.
- **Não-objetivo Fase 1:** 2FA (Fase 3), força-bruta distribuída além de rate-limit, HSM.

## 11. Fora de escopo (fases seguintes)

Fase 2 (sync automático robusto + crash-safety + força-sync UI + conflito) · Fase 3 (2FA TOTP) · Fase 4 (recuperação por email/SMTP) · Fase 5 (replicação de backup cifrado pra Dropbox/Nextcloud).

## 12. Testes (Fase 1)

- **`AccountKeyService`** (puro): round-trip Argon2id→KEK→wrap/unwrap AMK; **prova multi-device** (device B com só a senha + `wrappedAmkPwd` recupera a AMK e decifra um SecretEnvelope selado pelo device A); recovery-key reabre; senha errada falha; domain-separation (AuthHash ≠ KEK).
- **Migração local:** dados DPAPI antigos → AMK novos → decifram igual.
- **Backend (integração, Testcontainers Postgres):** register/kdf/login round-trip; servidor nunca persiste plaintext; secret upsert/pull; RBAC.
- **Sync e2e:** device A cria host+senha → device B loga → vê e decifra.
- Gates de sempre: build Release limpo, `dotnet format`, suíte verde.

## 13. Riscos

- **Vendorar cripto** (Argon2 lib) no app de credenciais — mitigar: lib MIT vetada, fixar versão, revisar. **Requer OK explícito do operador** (já dado no brainstorming).
- **Perda irreversível** se perder senha + recovery key — mitigar por UX (avisos fortes, forçar guardar a recovery key).
- **Complexidade do deploy** no Debian — mitigar por Docker + runbook.
