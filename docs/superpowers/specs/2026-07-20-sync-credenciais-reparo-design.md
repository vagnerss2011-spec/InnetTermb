# Sincronização de credenciais — reparo do acervo + resiliência — Design

**Data:** 2026-07-20
**Versão base:** v1.4.4
**Status:** aprovado pelo operador (botão manual; os 4 itens)

---

## Problema (relatado em produção)

Operador logou na conta E2EE em dois PCs. **Hosts sincronizaram; credenciais não.** No PC B, ao
conectar, o app diz **"o endpoint não tem credencial"**. Ele tem ~700 devices, credenciais criadas
**antes** de ligar a nuvem, e precisa dos dois tipos sincronizados (chaveiro e inline).

Observações que fecharam o diagnóstico:
- No PC B o **Chaveiro LISTA os nomes** das credenciais → o metadado da credencial sincronizou.
- A barra de status diz **"Sincronizado"** (sem erro) → o canal não está caindo.
- A mensagem é `ep.CredentialRefId is null` (`SessionLauncher.cs:263`) → o **vínculo** endpoint→credencial
  chegou nulo.

## Causa raiz (confirmada no código)

A fila de envio **congela o patch no momento da edição**: `LocalSyncClient.cs:60` grava
`JsonSerializer.Serialize(change.Patch)` em `patch_json`, e o envio relê esse blob (`:105`) — **não
reconstrói do registro atual**.

Cadeia do incidente:
1. Os ~700 devices foram cadastrados numa versão com o bug "endpoint sobe sem `credential_ref_id`"
   (corrigido na Fase 1). Cada edição gravou um patch **incompleto** na fila — congelado.
2. Ligar a nuvem drenou os patches **velhos e incompletos** para o servidor.
3. O `credential_ref` tinha patch completo → subiu certo → Chaveiro do PC B lista os nomes.
4. O `endpoint` subiu **sem** o vínculo → PC B recebe `credential_ref_id` nulo.

**Consequência:** corrigir o código do patch não repara isto sozinho — o estrago está gravado na fila e
no servidor. É preciso **re-emitir** o dado completo.

Backend verificado ao vivo: `GET /secrets` → **401** (existe), rota falsa → 404. O canal de segredos
está deployado; **não** é a causa deste sintoma.

## Bugs adicionais confirmados (investigação Fable, 4 lentes)

- **Rotação de segredo órfã (perda de dado, `KeychainViewModel.cs:101/111/152`):** `RotateAsync` cria um
  envelope com **id novo** e tombstoneia o antigo, mas os três métodos **descartam o retorno** e não
  chamam `UpdateCredentialRefAsync`. O `CredentialRef.SecretEnvelopeId` fica apontando pro tombstone:
  conectar falha **no próprio PC** ("Envelope revogado") e a troca **nunca chega** ao outro device.
- **Canal de segredos tudo-ou-nada e silencioso (`SyncOrchestrator.cs:120-124`, `SecretSyncOrchestrator`
  push/pull sem isolamento por item):** um envelope malformado trava push **e** pull, pra sempre, e o
  único sinal é "Erro" genérico. Falha silenciosa — a classe de defeito recorrente deste app.

## Objetivo

Credenciais (chaveiro **e** inline) sincronizam entre devices, **incluindo o acervo de ~700 criado antes
da nuvem**; editar senha não quebra a credencial; e o canal deixa de falhar em silêncio.

## Arquitetura da solução (4 itens)

### Item 1 — Reparo do acervo: "Reenviar tudo para a nuvem" (resolve os 700)

Botão **manual** em Configurações → Conta, com confirmação + progresso. Ao clicar, no PC A:

1. **Pull primeiro** (um `SyncOnceAsync`) para alinhar as versões locais com o servidor — evita
   re-emitir com `base_version` atrasada, que o servidor rejeitaria como `version.conflict`
   (`SyncService.cs:104`).
2. **Re-emite via o caminho de update que já existe** — para cada `AssetGroup`, `Asset`, `Endpoint` e
   `CredentialRef` do workspace, chama o `Update*Async` correspondente
   (`ILocalStore.cs:22/30/38/…`). Cada update lê `baseVersion = versão local atual` e grava o **patch
   completo de hoje** no outbox (`SqlCipherLocalStore.UpdateEndpointAsync:373`). Como após o pull a
   versão local == versão do servidor, o push sobe com `base_version == servidor` → **aceito**
   (não é `<`) → aplicado como versão+1 → propaga o dado completo ao PC B.
3. **Sync final** (`SyncOnceAsync`) para drenar o outbox.

**Por que o caminho de update, e não um push cru:** ele já acerta o versionamento e monta o patch
completo com o código atual. Nada de mexer em `base_version` na mão.

*Escopo:* re-emite **metadados** (grupos/assets/endpoints/credential_refs). As **senhas** (envelopes) o
canal de segredos já enumera o cofre inteiro por ciclo (`SecretSyncOrchestrator.cs:98`) — não precisam
de re-emit; sobem sozinhas quando o canal roda (item 3 garante que rode e apareça).

*Idempotente:* clicar duas vezes é inofensivo — o segundo re-emit sobe patches idênticos, versão+1, sem
efeito colateral.

### Item 2 — Editar senha para de quebrar a credencial

Nos três métodos do `KeychainViewModel` (`ChangePasswordAsync`, `ReplaceKeyAsync`, ramo de rotação de
`ChangePassphraseAsync`): capturar o `SecretEnvelope` retornado por `RotateAsync` e persistir o novo
`EnvelopeId` via `_store.UpdateCredentialRefAsync`. Isso religa o `CredentialRef` ao envelope vivo E
emite o patch no outbox → a troca propaga ao outro device.

### Item 3 — Canal de segredos com voz e resiliência

- **Isolamento por item:** `PushLocalAsync` e o apply do pull passam a ter try/catch **por envelope/dto**
  — um item venenoso é pulado e registrado (envelopeId + tipo do erro), sem travar os demais nem o pull.
- **Estado próprio:** `SyncOrchestrator` captura a falha do canal de segredos separada do changelog e
  expõe um estado distinto ("metadados OK; senhas não sincronizam"), em vez de virar "Erro" genérico.
- **Pull mesmo se o push falhar:** o PC B continua recebendo o que já está no servidor.

*ADR-013:* log só com envelopeId + status/tipo do erro — **nunca** campos do envelope nem token.

### Item 4 — Teste real de dois devices (a camada que quebrou não tinha teste)

Integração sobre `WebApplicationFactory` do `RemoteOps.Cloud` (pipeline ASP.NET completo, servidor
in-memory) + dois clientes com `SecretsApiClient`/`CloudSyncApiClient` reais + dois `FileVaultStore` em
temp com a **mesma AMK**:
- Device A cadastra host + credencial de chaveiro e host + senha inline → ciclo A → ciclo B →
  **o cofre do B ABRE o segredo** (decifração real valida AAD/codec fim-a-fim).
- **Reparo:** simula um endpoint com `credential_ref_id` faltando no servidor; "Reenviar tudo" no A; o B
  passa a ver o vínculo.
- **Rotação:** A troca a senha → B abre a NOVA e o A ainda conecta.
- **Veneno:** envelope malformado pré-inserido → o B ainda entrega os demais e o canal expõe erro próprio.

## O que NÃO pode quebrar

- **E2EE:** tudo move blobs opacos; nenhum ponto novo de decifração. O cliente **nunca decide segredo**
  (ADR-003) — re-emit e quarentena adiam/repassam, não escolhem conteúdo.
- **Nenhum segredo em log** (ADR-013).
- **Ordem metadados → segredos** preservada.
- **Idempotência do reparo:** re-emitir não pode duplicar entidade nem gerar conflito espúrio (por isso o
  pull-first + o caminho de update com `base_version` correto).
- Gates de CI: `dotnet format`, build Release, `TreatWarningsAsErrors`.

## Fora de escopo (deferidos, com motivo)

- **Revogação que propaga** (tombstone não sobe — limitação assumida do `SecretEnvelopeWireCodec:36-38`).
- **Reconciliação do ledger `secrets_pushed`** contra o servidor (útil se o servidor perder envelopes;
  não é a causa aqui).
- **Backend ganhar colunas `credentialId`/`type`** (hoje viajam no `keyVersion` como gambiarra assumida).
