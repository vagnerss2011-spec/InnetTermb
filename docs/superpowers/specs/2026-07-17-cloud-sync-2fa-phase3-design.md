# Cloud Sync E2EE — Fase 3 (2FA / TOTP) — Design

**Data:** 2026-07-17
**Status:** implementado (PR pendente de merge)
**Autor:** Vagner + Claude
**Escopo:** Fase 3 de 5 (ver spec Fase 1 §11). Refs: `2026-07-16-cloud-sync-e2ee-phase1-design.md`.

## 1. Objetivo

Adicionar **autenticação em dois fatores por app (TOTP)** — Google Authenticator, Authy, etc. — ao
login da conta na nuvem. O gancho `UserEntity.MfaRequired` já existia (nunca checado); esta fase o
ativa e adiciona enroll/confirm/disable.

## 2. Decisão-chave (ADR): 2FA é AUTENTICAÇÃO, não cripto do cofre

> **O TOTP prova identidade ao SERVIDOR. Ele NÃO participa da criptografia do cofre.**

A chave do cofre continua vindo **só da senha** (Argon2id → KEK → AMK, Fase 1 §4). O TOTP protege o
**acesso ao servidor** (login), não a decifragem dos segredos. Consequências que precisam estar
explícitas para ninguém confundir:

- **"Reset de 2FA"** (admin zera `MfaRequired`, ou a Fase 4 usa o e-mail de recuperação de acesso)
  devolve o **acesso à conta** — **nunca** decifra o cofre.
- Quem **esquece a senha E perde a chave de recuperação** continua com o cofre **irrecuperável por
  design**, com ou sem 2FA. O 2FA não muda nada nesse eixo.
- Um servidor comprometido que force `MfaRequired=false` **não ganha** acesso aos segredos: ele
  segue só com ciphertext + escrows inúteis sem a senha (threat model Fase 1 §10).

Essa fronteira está repetida em comentário no topo de `TotpService`, `UserEntity` e `MfaService`.

## 3. TOTP (RFC 6238)

- **HMAC-SHA1**, passo **30s**, **6 dígitos**, janela **±1** (aceita passo anterior/próximo p/ clock
  skew). Segredo = **20 bytes** CSPRNG.
- **Cripto built-in** (`HMACSHA1`) — **nenhuma lib** nova (é ~30 linhas; evita vendorar dependência
  no app de credenciais). Validado pelos **vetores oficiais do Apêndice B da RFC** nos testes.
- Segredo exibido em **Base32** (RFC 4648) + **`otpauth://`** URI (issuer `RemoteOps`, account =
  e-mail) para o QR.
- `TotpService` é **puro** (recebe o `now` de fora) → teste determinístico.

### Anti-replay: NÃO implementado (decisão documentada)

A RFC 6238 permite reapresentar o mesmo código dentro da mesma janela. Não guardamos "último passo
usado" por conta. Justificativa: um device único não sofre; o rate-limit do `/auth` e a exigência de
**senha antes do TOTP** contêm o abuso. Anti-replay fica como **follow-up** (exigiria uma coluna
`MfaLastUsedStep` e cuidado com concorrência).

## 4. Segredo TOTP em repouso

Diferente do material E2EE (opaco porque o cliente cifra), o segredo TOTP é do **servidor** (ele
valida os códigos). Para que um dump do banco sozinho não o revele, `MfaSecretProtector` o embrulha
com **AES-256-GCM** sob uma chave **derivada (HKDF) do segredo de assinatura do deploy** (mesmo padrão
do `KdfDecoyKey`). **Trade-off:** protege contra "vazou só o banco"; não contra "vazou banco + chave
de assinatura" (aí o atacante já forja JWT). Rotacionar `Jwt__SecretKeyBase64` torna os segredos TOTP
indecifráveis → re-inscrição (mesmo custo de rotação que já invalida os JWTs; o disable por admin
segue disponível).

## 5. Backend — endpoints

| Endpoint | Auth | Efeito |
|----------|------|--------|
| `POST /auth/mfa/enroll` | sim | Gera segredo (pendente, **não ativa**). Devolve Base32 + otpauth URI. 409 se já ativo. |
| `POST /auth/mfa/confirm` | sim | Valida um código → `MfaRequired=true` + `MfaEnrolledAt`. |
| `POST /auth/mfa/disable` | sim | Exige código válido → desliga e limpa o segredo. |
| `POST /auth/login` | não | Se `MfaRequired`, exige `totpCode` **depois** do AuthHash validar. |

### Login e anti-enumeração

O `LoginResult` distingue três desfechos: `Success`, `InvalidCredentials`, `MfaRequired`. O TOTP só é
checado **depois** do AuthHash validar — quem recebe `mfa_required` **já provou a senha**, então o
sinal **não vira oráculo de enumeração** (senha errada devolve sempre `InvalidCredentials`, nunca
`mfa_required`). O custo/timing do caminho de falha antes desse ponto segue idêntico ao da correção de
enumeração já existente (o PBKDF2 roda uma vez, com ou sem 2FA na conta). O endpoint devolve **401 com
corpo estruturado** `{ "error": "mfa_required" }` para a UI distinguir de credencial inválida.

## 6. Cliente

- `E2eeLoginRequest.TotpCode`; `AccountApiClient` lê o corpo do 401 e lança `MfaRequiredException`
  (só quando `error=mfa_required`) — a senha **nunca** vai em claro; o fluxo E2EE de authHash é o
  mesmo, só acrescenta o campo `totpCode`.
- `AccountViewModel`: modo de **desafio 2FA** — o backend responde `mfa_required`, a UI mostra o campo
  do código, e o próximo submit **reenvia a senha** (do PasswordBox, que não é limpo no desafio) + o
  código. A KEK é zerada a cada intento.
- **Ativar 2FA** (Configurações → Conta): `MfaEnrollmentWindow`/`MfaEnrollmentViewModel` chamam
  `enroll` → mostram o **segredo Base32 formatado + a otpauth URI copiável** (com "escaneie ou
  digite") → `confirm`. Também **desativa**. O cliente autenticado (`MfaApiClient`) reusa o
  `CloudAuthChannel` (Bearer + refresh) do sync.
- **Sem QR bitmap:** exibimos o segredo + a URI (opção explicitamente aceita no escopo). Renderizar o
  QR fica como follow-up (exigiria gerar bitmap — evitado para não vendorar dependência).

## 7. Testes

- `TotpService`: vetores RFC 6238, janela ±1, código malformado/errado, Base32, otpauth URI.
- `MfaSecretProtector`: round-trip, não-plaintext, nonce fresco, falha com chave diferente.
- `MfaService`: enroll→confirm→login com/sem código, disable, e a **guarda de enumeração** (senha
  errada + 2FA → `InvalidCredentials`, não `mfa_required`).
- Cliente: detecção `mfa_required`, passthrough do código no orquestrador, desafio na VM, wire dos
  contratos, `MfaApiClient` autenticado, render STA das telas novas.

## 8. Pendências (follow-up)

- **Códigos de recuperação de backup** (one-time, hasheados) — não implementados nesta fase.
- **Anti-replay** do TOTP (coluna `MfaLastUsedStep`).
- **QR bitmap** na tela de ativação (hoje: segredo + URI).
- **Testcontainers/Postgres** para os testes de MFA (hoje InMemory, como o resto do backend).
