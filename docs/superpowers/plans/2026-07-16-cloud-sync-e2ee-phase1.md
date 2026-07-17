# Cloud Sync E2EE — Fase 1 — Plano de Implementação

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Steps use `- [ ]`.

**Goal:** Habilitar login E2EE multi-device — logar em qualquer PC e ter todos os dados, com as senhas dos equipamentos decifráveis, sem o servidor ver nada em claro.

**Architecture:** Núcleo de cripto puro (`AccountKeyService`) roteia a AMK portável (Argon2id→KEK→wrap AMK; escrow por senha e recovery key). AMK vira a raiz da WDK (reusa EnvelopeCipher). Backend ganha endpoints de registro/kdf/escrow + transporte de SecretEnvelope + migrations + deploy. Cliente ganha UI de conta e wiring do sync de segredos.

**Tech Stack:** .NET 10, Konscious.Security.Cryptography.Argon2 (MIT), AES-256-GCM + HKDF-SHA256 (built-in), ASP.NET Core + EF/Npgsql (backend), WPF (UI), Docker (deploy).

## Global Constraints
- Cripto: AES-256-GCM p/ wrap; HKDF-SHA256 com domain-separation (`remoteops:e2ee:auth:v1` / `:kek:v1`); Argon2id 64MiB/3/1/32B (params guardados por conta). Chaves zeradas após uso.
- Servidor NUNCA recebe: senha, MasterKey, KEK, AMK, WDK, CEK, plaintext. Só ciphertext + auth-hash + escrows.
- Gates: build Release limpo (0/0), `dotnet format --verify-no-changes`, suíte verde. Deploy real no Debian é do operador (não manuseio credenciais de produção).
- Ver spec: `docs/superpowers/specs/2026-07-16-cloud-sync-e2ee-phase1-design.md`.

## File Structure
- Novo `src/RemoteOps.Security/Account/AccountKeyService.cs` (+ modelos) — cripto pura, o coração.
- Novo `src/RemoteOps.Security/Account/RecoveryKey.cs` — geração/parse base32.
- Alterado `RemoteOps.Security.csproj` (+Argon2), `WorkspaceKeyRing`/rooting da WDK pela AMK.
- Novo `src/RemoteOps.Desktop/Account/AccountSyncCoordinator.cs`, `Views/AccountWindow.xaml`.
- Backend (`remoteops-cloud`): `UserEntity` (+campos KDF/escrow), `SecretEnvelope` endpoints, `AuthEndpoints` (register/kdf), EF migrations, Dockerfile/compose, seed, runbook.
- Testes: `AccountKeyServiceTests` (prova multi-device), migração, backend (Testcontainers).

---

### Task 1: `AccountKeyService` — núcleo de cripto (a prova multi-device)

**Files:** Create `src/RemoteOps.Security/Account/AccountKeyService.cs`, `AccountKeyModels.cs`, `RecoveryKey.cs`; modify `RemoteOps.Security.csproj`; Test `tests/RemoteOps.UnitTests/Security/AccountKeyServiceTests.cs`.

**Interfaces — Produces:**
- `record AccountKeyMaterial(byte[] AuthHash, byte[] Kek)` (derivados da senha).
- `record AccountEnrollment(byte[] Amk, byte[] WrappedAmkPwd, byte[] WrappedAmkRec, string RecoveryKey, byte[] Argon2Salt, Argon2Params Params)`.
- `AccountKeyService.DeriveFromPassword(password, salt, params) → AccountKeyMaterial`
- `AccountKeyService.Enroll(password) → AccountEnrollment` (gera AMK+salt+recovery, embrulha).
- `AccountKeyService.UnwrapAmkWithPassword(password, salt, params, wrappedAmkPwd) → byte[] amk`
- `AccountKeyService.UnwrapAmkWithRecoveryKey(recoveryKey, wrappedAmkRec) → byte[] amk`
- `AccountKeyService.RewrapForNewPassword(amk, newPassword) → (salt, params, wrappedAmkPwd, authHash)`

- [ ] **Step 1: Teste — a PROVA multi-device.** `AccountKeyServiceTests`: (a) `Enroll` gera material; (b) "device B" só com `password + salt + params + WrappedAmkPwd` recupera a MESMA AMK; (c) uma AMK→WDK→CEK selada no device A (via EnvelopeCipher) abre no device B; (d) `UnwrapAmkWithRecoveryKey` recupera a AMK; (e) senha errada lança; (f) domain-separation: `AuthHash != Kek`.
- [ ] **Step 2: Rodar** → FAIL (não compila).
- [ ] **Step 3:** `.csproj` +`<PackageReference Include="Konscious.Security.Cryptography.Argon2" Version="1.3.1" />` (verificar versão MIT publicada).
- [ ] **Step 4: Implementar** `AccountKeyService`: Argon2id(password,salt,params)→MasterKey(32B); HKDF-SHA256(MasterKey, info=auth)→AuthHash, (info=kek)→KEK; `Enroll`: AMK=RNG(32), salt=RNG(16), recovery=RecoveryKey.Generate(), WrappedAmkPwd=AesGcmWrap(AMK,KEK,aad="amk|pwd|v1"), WrappedAmkRec=AesGcmWrap(AMK, KDF(recovery), aad="amk|rec|v1"); unwrap inversos; zerar KEK/MasterKey. AES-GCM wrap helper interno.
- [ ] **Step 5: Rodar** → PASS.
- [ ] **Step 6: Commit.**

### Task 2: AMK como raiz da WDK (portabilidade real dos segredos)

**Files:** modify `WorkspaceKeyRing` / add `AmkWorkspaceKeyProvider`; Test.
**Consumes:** AMK (Task 1), `EnvelopeCipher`. **Produces:** WDK embrulhada pela AMK (não mais DPAPI-random) → um segredo selado num device abre em outro com a mesma AMK.
- [ ] Teste: selar SecretEnvelope com WDK-raiz-AMK no "device A"; abrir no "device B" só com a AMK. Rodar→fail. Implementar rooting. Rodar→pass. Commit.

### Task 3: Migração local (dados atuais DPAPI → AMK)

**Files:** `src/RemoteOps.Desktop/Account/LocalVaultMigrator.cs`; Test.
- [ ] Teste: vault com segredos DPAPI antigos → após migração, decifram sob a AMK nova. Idempotente. Backup antes. Rodar→fail→impl→pass→commit.

### Task 4: Backend — campos KDF/escrow + endpoints register/kdf/login

**Files (remoteops-cloud):** `UserEntity` (+argon2_salt/params, auth_hash_hash, wrapped_amk_pwd/rec, amk_key_version), `AuthEndpoints` (`/auth/register`, `/auth/kdf`, `/auth/login` c/ authHash, `/auth/password/change`), `TokenService`; Tests.
- [ ] TDD: register→kdf→login round-trip; servidor guarda PBKDF2(authHash), nunca plaintext; anti-enumeração; rate-limit. Commit.

### Task 5: Backend — transporte de SecretEnvelope

**Files:** `SecretEndpoints` (`POST /secrets` upsert opaco, `GET /secrets?since=`); remover a recusa de segredo no push; Tests.
- [ ] TDD: upsert/pull de blob opaco; RBAC; changelog referencia id. Commit.

### Task 6: Cliente — AccountSyncCoordinator + sync de segredos

**Files:** `src/RemoteOps.Desktop/Account/AccountSyncCoordinator.cs`; wire em `App.OnStartup`.
- [ ] login → VaultTokenStore → AMK (cache DPAPI) → SyncSessionFactory (segredos ON) → pull/push de SecretEnvelope. Teste de integração (fakes). Commit.

### Task 7: Cliente — UI Criar conta / Entrar

**Files:** `src/RemoteOps.Desktop/Views/AccountWindow.xaml(.cs)`, `ViewModels/AccountViewModel.cs`; startup.
- [ ] Tela login/registro; exibir RecoveryKey 1x (copiar+confirmar guardei); erros pt-BR. Render test STA. Commit.

### Task 8: Backend — migrations EF + deploy (Docker/HTTPS/seed) + runbook

**Files:** `Migrations/`, `Dockerfile`, `docker-compose.yml`, `scripts/seed.*`, `docs/runbook-deploy-debian.md`.
- [ ] `dotnet ef migrations add InitialCreate` + `Database.Migrate()` no startup; compose (API+Postgres+HTTPS via Caddy); seed 1º tenant/workspace; runbook passo-a-passo (o operador roda no Debian dele). Commit.

### Task 9: Validação + PR
- [ ] build Release 0/0 · suíte verde · `dotnet format` · self-review · PR `feature/cloud-sync-e2ee-phase1` → main (não merge até revisão/CI).
