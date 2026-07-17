# ADR-021 — O applier do changelog materializa nas tabelas de domínio

## Status

Aceita. Implementada na frente `feature/cloud-sync-e2ee-phase1` (Fase 1, spec §12 "Sync e2e").

Substitui a decisão de landing-zone do `ADR-013` (seção "Critérios de revisão": *"o
`local_entities` permanece como landing-zone canônica do pull"*). O resto do `ADR-013`
(orquestrador, cursores, conflitos, tokens no vault, flag) continua valendo.

## Contexto

O `ADR-013` registrou, em "Consequências negativas", uma limitação conhecida:

> O `LocalEntitiesChangeApplier` aplica no cache `local_entities`, que as tabelas de domínio do
> `SqlCipherLocalStore` não leem — a reflexão viva na UI fica como evolução.

E previu o gatilho de revisão: *"Se a UI precisar refletir mudanças remotas em tempo real,
compor no Desktop um `IRemoteChangeApplier` que mapeie `SyncChange` → tabelas de domínio"*.

A Fase 1 do cloud sync E2EE **é** esse gatilho, e mais que isso: a razão de existir da fase é
"logo numa conta em qualquer PC e tenho todos os meus dados — inclusive as senhas". Com o
changelog caindo em `local_entities`, o device B baixava tudo, avançava o cursor e **mostrava a
lista de hosts vazia**. Não era degradação de UX: era a fase inteira não fechando. Um cofre cheio
de senhas que não estão amarradas a nenhum host visível é, para o operador, um app vazio.

## Decisão

**1. O applier materializa nas tabelas reais.** `LocalEntitiesChangeApplier` mapeia os 4 tipos do
changelog nas tabelas que a UI lê: `asset_group`→`asset_groups`, `asset`→`assets`,
`endpoint`→`endpoints`, `credential_ref`→`credential_refs`. Upsert monotônico por versão (só
aplica se a recebida ≥ a local), delete respeitando o tombstone do changelog (com cascata
asset → endpoints, igual à do store), e sem re-emitir no outbox.

**2. `local_entities` deixa de ser landing-zone e vira QUARENTENA.** Nenhum dos 4 tipos conhecidos
escreve nela. Só tipo **desconhecido** cai lá — e por um motivo concreto, não por simetria: o
cursor avança de qualquer jeito, então descartar perderia a mudança **para sempre** se uma versão
futura do app passar a entender o tipo. Mantê-la como cache dos tipos conhecidos seria uma segunda
verdade sobre os mesmos dados — foi exatamente essa duplicação que produziu o bug.

**3. O applier fica em `RemoteOps.Sync`, não composto no Desktop** (o `ADR-013` sugeria o Desktop).
Motivo: o mesmo do próprio `ADR-013` para rejeitar "cliente HTTP no Desktop" — *não testável no CI
sem WPF*. Para isso o schema das tabelas de domínio saiu do `SqlCipherLocalStore` para
`RemoteOps.Sync/Storage/LocalSchema.cs`: store e applier escrevem nas **mesmas** tabelas, e duas
definições do mesmo schema já divergiram uma vez. Desktop referencia Sync (não o contrário), então
Sync é o assembly que os dois alcançam.

**4. Patches são parciais por contrato.** Um rename emite só `{name}`. O upsert toca apenas as
colunas presentes no patch; os defaults de NOT NULL entram só no INSERT. Upsert de linha inteira
faria o rename zerar o `workspace_id` do grupo e ele sumiria da lista.

**5. As chaves do patch são os nomes das colunas**, e saem de uma **allowlist por tipo** no applier
— nome de coluna nunca vem do patch, então um servidor comprometido não escolhe onde escrever.
Colunas `*_json` (`tags_json`, `profile_json`, `metadata_json`) viajam já serializadas: o mesmo
texto que a coluna guarda, gravado verbatim, sem chance de o round-trip reinterpretar.

**6. Ids são canonizados para o formato "n"** (32 hex, sem hífens) na chegada. O backend guarda o
`EntityId` num `Guid` e o devolve com `ToString()` — formato "D", **com** hífens —, enquanto os
campos do patch são ecoados **verbatim**, no "n" que o device de origem escreveu. Sem canonizar,
`assets.id` viraria "D", `endpoint.asset_id` continuaria "n", e o host chegaria no outro device sem
endereço nenhum. É a mesma armadilha que o `SecretEnvelopeWireCodec` já teve que resolver no canal
de segredos (ver `ADR-003`/spec §5).

**7. `SecretEnvelope` continua RECUSADO no changelog** (`ADR-003`, inalterado). O segredo viaja pelo
canal `/secrets` (`SecretSyncOrchestrator`). O `secret_envelope_id` que aparece em `credential_ref`
é apenas uma **referência** — e é justamente ela que liga o host à senha no outro device.

## Consequências

### Positivas

- O device B enxerga grupo, host (com vendor/model/device_role), endpoint (com endereço, porta e
  perfil), o link para a credencial e o username — e decifra a senha. A fase fecha.
- Uma verdade só sobre onde os dados do operador moram; o schema num lugar só.
- O applier segue testável no CI sem WPF, e provado ponta a ponta com fakes de rede que recusam o
  que o backend real recusa (`DeviceToDeviceWorkspaceSyncTests`).

### Negativas / limitações conhecidas

- **A UI não recarrega sozinha após um ciclo de sync.** `HostsViewModel.LoadAsync()` roda uma vez
  no `MainWindow.Loaded`; o `StatusChanged` do orquestrador só atualiza a string de status. No
  primeiro launch do device B os dados chegam ao banco **depois** de a lista ter sido carregada —
  o operador vê a lista vazia e só encontra os hosts no **próximo** launch. Está no escopo da
  **Fase 2** ("sync automático robusto + força-sync UI", spec §11) e é o que falta para o fluxo ser
  fluido, não correto.
- **Revogação de segredo não propaga** (tombstone não sobe — `SecretEnvelopeWireCodec`, pré-existente).
- `local_entities` continua no schema com um papel novo e menor; bancos antigos podem ter linhas
  dos 4 tipos conhecidos lá, agora inertes. Não são lidas nem migradas: os dados verdadeiros vêm do
  changelog no próximo pull (o cursor do servidor é independente dessa tabela).
- O applier passa a conhecer o schema de domínio — acoplamento assumido, e a razão de o schema ter
  virado um módulo compartilhado em vez de ser duplicado.

## Alternativas consideradas

| Alternativa | Motivo da rejeição |
|---|---|
| Manter `local_entities` como cache **e** materializar | Duas verdades sobre o mesmo dado — exatamente a duplicação que causou o bug. |
| Compor o applier no Desktop (como o `ADR-013` sugeria) | Não testável no CI sem WPF — o mesmo motivo que o `ADR-013` usou para manter o cliente HTTP fora do Desktop. |
| Ler `local_entities` no `SqlCipherLocalStore` (UNION com as tabelas) | Toda leitura pagaria o merge; o modelo de dados viraria dois formatos por entidade, para sempre. |
| Descartar tipo desconhecido | O cursor avança do mesmo jeito → mudança perdida para sempre num upgrade futuro. |
| Upsert de linha inteira (sem patch parcial) | O rename (`{name}`) zeraria o resto da linha e o grupo sumiria da lista. |
| Deixar o id em "D" e canonizar na leitura | Espalharia a conversão por todo consumidor; o AAD/FK exigem um formato só, e o cofre já canoniza em "n". |

## Critérios de revisão

- Quando a Fase 2 trouxer o refresh vivo da UI, o gancho natural é o `StatusChanged` do
  orquestrador (já ligado em `App.OnSyncStatusChanged`) — preservando seleção/expansão da árvore,
  que hoje um `LoadAsync()` cru perderia.
- Se o backend ganhar colunas próprias de `credentialId`/`type` no `SecretEnvelope`, revisar junto
  a gambiarra do `keyVersion` (`SecretEnvelopeWireCodec`).
- Se um 5º tipo de entidade entrar no changelog, adicioná-lo ao mapa do applier — até lá ele cai na
  quarentena e não se perde.
- Se o changelog passar a carregar entidade de outro workspace, revisar o filtro de
  `credential_refs` (hoje sem coluna `workspace_id`, escopado por `scope`).
