# Cloud Sync E2EE — Fase 4 (Recuperação de senha por email) — Design

**Data:** 2026-07-18
**Status:** aprovado (design travado na Fase 1 §5/§6/§11; operador autorizou executar)
**Autor:** Vagner + Claude
**Escopo:** Fase 4 de 5. Refs: `2026-07-16-cloud-sync-e2ee-phase1-design.md` (§4 cripto, §5 contratos, §6 fluxos).

## 1. Objetivo

Permitir que o operador que **esqueceu a senha** volte a usar a conta, sem furar o E2EE. O
mecanismo é **plugável de envio de email** (funciona ponta a ponta com um enviador fake/log em
dev e testes; SMTP real fica a cargo do operador via configuração — o Claude nunca manuseia a
credencial de SMTP).

## 2. A verdade incômoda do E2EE: email restaura ACESSO, não o COFRE

Num app comum, "esqueci a senha" → link no email → define nova senha → está tudo de volta. **Aqui
isso é impossível por construção** e é a decisão central da fase:

```
senha nova ──Argon2id──► MasterKey nova ──HKDF──► KEK nova
AMK (raiz do cofre) está selada sob a KEK ANTIGA em wrapped_amk_pwd
⇒ o servidor NÃO consegue re-selar a AMK sob a KEK nova: ele nunca teve a AMK.
```

Logo a recuperação é **de dois fatores**, e cada fator resolve uma metade:

| Fator | O que prova | O que destrava |
|-------|-------------|----------------|
| **Token por email** | controle do email | **ACESSO**: autoriza trocar a prova de senha (AuthHash) no servidor **sem** o AuthHash antigo |
| **Chave de recuperação** | posse do 2º escrow | **COFRE**: desembrulha `wrapped_amk_rec` → recupera a AMK → re-embrulha sob a senha nova |

O cliente combina os dois: com o token ele busca `wrapped_amk_rec`; com a chave de recuperação
ele abre a AMK, re-embrulha sob a senha nova (`AccountKeyService.RewrapForNewPassword`) e sobe o
novo escrow. **A AMK não muda** → os `SecretEnvelope` continuam decifráveis; a versão da AMK é a
mesma; a chave de recuperação antiga continua válida (não a rotacionamos nesta fase — YAGNI).

**Perdeu senha E chave de recuperação:** o cofre é **irrecuperável por design** (§6 da Fase 1). O
email sozinho **não** reabre o cofre. A UI diz isso explicitamente — não fingimos uma recuperação
que o E2EE proíbe. (Re-chavear a conta com um cofre novo — abandonando os segredos antigos — é uma
ação destrutiva fora do escopo desta fase.)

## 3. Contratos (backend) — todos anônimos, no grupo `/auth` (já rate-limited por IP)

| Método | Corpo | Resposta | Notas |
|--------|-------|----------|-------|
| `POST /auth/password/forgot` | `{email}` | **sempre** `202 Accepted` | Não-enumerante: resposta idêntica exista ou não a conta. Se existir e for E2EE, gera token e dispara email. |
| `POST /auth/password/reset-context` | `{token}` | `200 {wrappedAmkRec}` \| `400` | Autorizado pelo token. Devolve o escrow opaco (inútil sem a chave de recuperação). **Não** consome o token. |
| `POST /auth/password/reset` | `{token, newAuthHash, newArgon2Salt, newArgon2Params, newWrappedAmkPwd}` | `204` \| `400` | Valida o token, re-embrulha (igual ao `password/change`, mas sem `oldAuthHash`), marca o token usado, **revoga todos os refresh tokens** da conta. |

Token: por que `POST` com o token no corpo (e não `GET ?token=`) — token de uso único no
query-string vaza em logs de acesso/proxy e no `Referer`. No corpo, não.

## 4. Modelo de dados

Nova tabela `password_reset_tokens` (espelha `refresh_tokens`):

- `Id` (Guid, PK)
- `UserId` (Guid, FK → users, indexado)
- `TokenHash` (SHA-256 hex do token cru, **índice único**) — o token cru nunca toca o disco
- `ExpiresAt` (TTL de **30 min**)
- `UsedAt` (nulo até o reset; **uso único**)
- `CreatedAt`

Token cru = 32 bytes CSPRNG em Base64Url (256 bits). Hash igual ao dos refresh tokens
(`SHA-256 hex`) — é alta entropia, não precisa de PBKDF2 (o PBKDF2 é para segredos de baixa
entropia como AuthHash/senha).

## 5. Serviço `PasswordResetService`

- `RequestAsync(email, ct)` — normaliza; acha conta E2EE ativa; **cooldown de reenvio** de 1 min
  (não dispara novo email/token se já houve um ativo recente → anti email-bomb); invalida tokens
  ativos anteriores; cria token; dispara `IEmailSender`. **Sempre** volta sem sinalizar existência.
- `GetResetContextAsync(token, ct)` — valida token ativo (existe, não usado, não expirado, user
  ativo/E2EE) → devolve `wrapped_amk_rec` base64; senão `null`. Não consome.
- `ResetAsync(req, ct)` — valida token ativo; **valida o material novo ANTES de mutar** (decode +
  `ValidateParams`, para não deixar a conta meio-trocada e trancada); grava salt/params/AuthHashHash/
  `wrapped_amk_pwd`; marca `UsedAt`; **revoga todos os refresh tokens** e os demais tokens de reset
  ativos; `SaveChanges`. Semente de relógio (`UtcNow`) injetável para testar expiração.

`ValidateParams`/`DecodeExact`/`DecodeNonEmpty` são **extraídos** de `AccountService` para um helper
compartilhado (`E2eeMaterialCodec`) e reusados aqui — mesma validação, sem duplicar (piso Argon2id
OWASP, salt 16B, AuthHash 32B, saída 32B).

## 6. Envio de email (plugável)

`RemoteOps.Cloud/Email/`:

- `EmailMessage(ToEmail, Subject, TextBody)` — record.
- `IEmailSender { Task SendAsync(EmailMessage, ct) }`.
- `LoggingEmailSender` — **default**; loga destinatário/assunto/corpo (com o token). Faz o fluxo
  rodar ponta a ponta em dev/CI **sem SMTP**. Nos testes, um `FakeEmailSender` captura as mensagens.
- `SmtpEmailSender` — `System.Net.Mail.SmtpClient` (sem dependência nova), lê `Smtp:Host/Port/
  Username/Password/From/UseSsl` da config (env do operador).
- `EmailServiceSetup.Configure(services, config)` — registra `SmtpEmailSender` **se** `Smtp:Host`
  estiver setado; senão `LoggingEmailSender`. Loga qual escolheu no startup.

O `.env.example` ganha o bloco `Smtp__*` **sem valores** (o operador preenche). O Claude não
manuseia a credencial real de SMTP.

## 7. Cliente (RemoteOps.Desktop / RemoteOps.Sync)

- `CloudAuthChannel`: `ForgotPasswordAsync(email)`, `GetResetContextAsync(token)`,
  `ResetPasswordAsync(...)`.
- VM de recuperação: (a) "Esqueci a senha" → pede email → `/forgot` → "confira seu email"
  (mensagem constante); (b) tela de reset: token (do email) + chave de recuperação + nova senha
  (+ confirmar). Fluxo cripto **reusa o que já existe** em `RemoteOps.Security` (sem tocar nela →
  sem acionar o security-gate): `UnwrapAmkWithRecoveryKey(recoveryKey, wrappedAmkRec)` →
  `RewrapForNewPassword(amk, novaSenha)` → `POST /reset`.
- UI: link "Esqueci a senha" na tela de login + diálogo de reset. Estilos implícitos por
  `TargetType` (coerência de tema — ver [[project_remoteops_theme_consistency]]). Teste de render STA.

## 8. Segurança / threat model (Fase 4)

- **Servidor/DB vazado:** ganha `password_reset_tokens` = só hashes SHA-256 de tokens de uso único
  com TTL curto; sem o token cru (que só existe no email do dono) não reseta nada. **Não decifra
  segredos** — o reset exige `newWrappedAmkPwd`, que só o cliente com a chave de recuperação produz. ✔
- **Atacante com acesso ao email da vítima:** consegue **ACESSO** (reset da prova de senha) mas
  **não o cofre** — sem a chave de recuperação não produz `newWrappedAmkPwd` da AMK existente. Isto é
  o E2EE funcionando: email comprometido ≠ cofre comprometido. ✔
- **Enumeração:** `/forgot` sempre `202`, resposta constante. `reset-context`/`reset` com token
  inválido dão `400` genérico. Grupo `/auth` já é rate-limited por IP; `RequestAsync` tem cooldown
  por conta (anti email-bomb). Risco residual aceito: `/forgot` pode ter diferença de timing
  (envio de email só no caminho "existe") — baixo, documentado, mitigável movendo o envio para fora
  do caminho da resposta se virar problema.
- **Reuso de token:** uso único (`UsedAt`) + expiração (30 min) + invalidação dos demais ao usar.
- **Sessões após reset:** todos os refresh tokens são revogados → re-login em todos os devices.

## 9. Fora de escopo

Rotação da chave de recuperação no reset (a antiga segue válida); re-chaveamento destrutivo do cofre
para quem perdeu senha+recovery; envio de email transacional sofisticado (retries/fila/DKIM — o
`SmtpEmailSender` é o mínimo; o operador pluga o que quiser via `IEmailSender`).

## 10. Testes

- **`PasswordResetService`** (InMemory + serviços reais, padrão `CloudTestContext`): token hashed
  persistido; email capturado; email desconhecido não gera token nem lança; cooldown; context
  devolve `wrapped_amk_rec`; token expirado/usado/inválido rejeitado; reset re-embrulha e revoga
  refresh tokens; **round-trip cripto real** com `AccountKeyService` (chave de recuperação abre a
  AMK, nova senha loga e decifra um segredo selado antes do reset); validação de material inválido
  não muta a conta.
- **Endpoints** (`CloudApiFactory`): rotas anônimas, binding camelCase, `/forgot` sempre 202.
- **Migração:** `password_reset_tokens` criada.
- **Cliente:** VM de reset (unwrap→rewrap→reset) com canal fake; render STA do diálogo.
- Gates de sempre: build Release limpo, `dotnet format --verify-no-changes`, suíte verde.
