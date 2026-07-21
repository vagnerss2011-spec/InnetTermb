# Times compartilhados (workspace de equipe com E2EE) — Design

**Data:** 2026-07-20
**Base:** v1.4.6
**Status:** desenho aprovado pelo operador; **Fatia 0 em execução**

---

## O que o operador pediu

Criar um **time**, convidar pessoas **por e-mail**, e ter **dois cofres**: o pessoal e o do time. No do
time ficam os **clientes**, e dentro de cada cliente os equipamentos **por tipo** (Huawei, MikroTik,
Linux, outros). Gestão compartilhada: qualquer membro cadastra/edita e **sincroniza entre todos**.

## A restrição que define tudo

`AmkKeyDerivation.DeriveWorkspaceKey` = `HKDF(AMK, workspaceId)`, e a **AMK é por conta**. Dois usuários
no mesmo workspace derivam WDKs **diferentes** — o colega não decifra nada. Não é bug: é o E2EE
funcionando. O servidor não pode "dar acesso" porque **não tem a chave**.

Compartilhar exige uma **WK (Workspace Key) aleatória do time**, entregue **cifrada a cada membro**.

**Verdade operacional que vai na UI, não escondida:** remover um membro corta o acesso **futuro**. Não
apaga o que ele já viu. Senha que um ex-membro conhecia **precisa ser trocada no equipamento**.

## Decisões do operador (que definiram o escopo)

| Pergunta | Resposta | Efeito |
|---|---|---|
| Pessoal e time simultâneos na 1ª entrega? | **Escolher ao abrir o app** | Fatia 1 entrega semanas antes |
| Tamanho do time em 12 meses | **2 a 5 pessoas** | **Sem criptografia assimétrica** — convite por código basta |
| Os ~700 devices migram? | **Time começa vazio**, move cliente a cliente | **Elimina a ferramenta de migração em massa** (a fatia mais cara e arriscada) |
| Quem vê as senhas? | **Todos do time** | Papéis controlam cadastrar/editar, não enxergar |

Isso reduziu a estimativa de **~3 meses para ~3-4 semanas** até o time funcionando.

## O que já existe (verificado)

- `MembershipEntity` (WorkspaceId/UserId/Role/PermissionsJson) e `WorkspaceEntity` — **prontos**
- **10 papéis** + permissões finas, **já aplicados** no `/sync` e `/secrets` (`PermissionEvaluator`)
- Login já devolve **todos** os workspaces (`TokenService` → `WorkspaceSummary`); o cliente é que usa
  só o primeiro (`E2eeAccountAuthenticator`)
- SignalR já faz auto-join de **todos** os workspaces do usuário (v1.4.0)
- SMTP funcionando (Fase 4) e o padrão de token seguro (hash + TTL + uso único) do reset de senha
- **Banco local por workspace já existe** (`LocalSyncClientFactory` gera `sync-{ws}.db`)
- `AccountKeyService.WrapKey/UnwrapKey` **públicos**, com comentário antecipando este uso
- `LocalVaultMigrator` como modelo de re-selagem (2 fases, backup, retomada, idempotência)
- **Cliente = grupo + tipo = chips** (Huawei/MikroTik/Linux): **já lançado** na v1.2.25 — custo zero

## Fatiamento

### Fatia 0 — pré-requisitos (ESTA ENTREGA, v1.4.7)

Dois defeitos que quebram em campo e valem **mesmo sem o time**:

1. **Limite de 900 assets** (`SqlCipherLocalStore.cs:182-184`): `GetAssetsAsync` lança acima de 900 por
   causa do teto de variáveis do SQLite (999). O operador tem **~700** — está a 200 de distância. O
   estouro acontece no **"Reenviar tudo"** (`CloudResyncService`) e em qualquer varredura de workspace
   inteiro; a lista diária consulta por grupo e não encosta. Correção: buscar endpoints em **lotes**
   (chunk de ~500 ids) em vez de um `IN` único.
2. **Revogação não propaga** (`SecretsService.Decode` recusa material vazio; o tombstone zera tudo).
   Hoje, ao trocar uma senha, o envelope antigo **fica vivo e decifrável no disco do outro device, para
   sempre**. Num time isso é resíduo de senha velha em N máquinas a cada troca — quebra o
   *"alteram e sincroniza corretamente"* no caso mais sensível. Correção: contrato de tombstone
   (flag `revokedAt` + aceitar material vazio **apenas** para tombstone) no servidor, no codec e no
   `IsSyncable`.

### Fatia 1 — o time nasce (~2-3 semanas)

> **PRÉ-REQUISITO DE SEGURANÇA herdado da v1.4.7** (achado da revisão, severidade BAIXA **hoje**, real
> quando a chave passa a ser compartilhada): o upsert de segredo **não tem token de concorrência**. Um
> upsert "vivo" concorrente com a chegada de um tombstone pode limpar o `RevokedAt`
> (last-write-wins do EF) e **ressuscitar a senha revogada** — hoje o material plantado é
> indecifrável (o atacante não tem a WDK da vítima), mas com a **WK compartilhada** ele passa a ser
> decifrável. Correção: `UseXminAsConcurrencyToken` na `SecretEnvelopeEntity` + tratar
> `DbUpdateConcurrencyException` como 409, ou write condicional
> (`UPDATE ... WHERE "RevokedAt" IS NULL` para upsert vivo). **Não mergear a Fatia 1 sem isso.**
>
> O achado irmão (lápide obrigatoriamente vazia) **já foi corrigido na v1.4.7**, nos dois lados.

- **WK aleatória** por workspace de time + `WkWorkspaceKeyRing` (3ª implementação de
  `IWorkspaceKeyRing`, ao lado de DPAPI e AMK) + carimbo `VaultAlgorithms.WkRootedV1`.
- **Convite por código fora-de-banda:** o e-mail leva o link; o **código de 160 bits** (mesmo formato da
  chave de recuperação) vai por WhatsApp/telefone. `K_invite = HKDF(código)`; a WK viaja cifrada sob
  ela. **E-mail vazado sozinho não entrega o cofre.** O servidor guarda só o blob e um hash do código.
- Backend: `InviteEntity` + `WrappedWk`/`WkVersion` na membership + endpoints criar/aceitar/remover.
  Reusa o padrão do reset de senha (hash, TTL, uso único, anti-enumeração).
- Cliente: **escolher workspace ao abrir**; tela de Equipe (membros, papéis, remover).
- **Ao introduzir o `WkRootedV1`, incluir `credentialId` no AAD** — hoje `Algorithm` e o header
  `type|credentialId` ficam **fora** de qualquer AAD, o que permitiria a um servidor malicioso
  re-associar um envelope a outra credencial. Correção barata agora, cara depois.

### Fatia 2 — conforto (depois, sob demanda)

Pessoal + time **ao mesmo tempo** (seletor no shell, duas sessões, dois bancos). Só quando o operador
sentir falta — ele aceitou escolher ao abrir.

### Fatia 3 — encolhida pela decisão do operador

Sem migração em massa (time começa vazio). Resta apenas a **rotação da WK** ao remover alguém.
**Correção de estimativa da verificação adversarial:** rotacionar **re-cifra o payload inteiro** de cada
envelope, não só o embrulho da chave — a versão está **dentro do AAD** (`EnvelopeCipher.cs:104-105`) e o
servidor só propaga com `Version+1`. São minutos, não milissegundos. Exige `WkVersion` **por envelope
desde o dia 1**, senão o estado misto v1/v2 é indetectável e vira erro mudo no PC do colega.

## Riscos assumidos

- **Ex-membro retém o que viu.** Rotação protege o futuro; a resposta completa é operacional (trocar
  senhas nos equipamentos). Vai escrito na tela de remoção.
- **Token de refresh vive 30 dias**: remover membership corta a autorização, mas o corte efetivo depende
  do próximo ciclo. Aceitável para time de 2-5 pessoas de confiança.
- **"Só usar, sem ver" não existe de verdade**: quem tem a WK decifra na própria máquina. O operador
  escolheu "todos veem", o que é honesto com a tecnologia.
- **Bancos separados por workspace são obrigatórios** — o outbox não é escopado; com banco único a fila
  empurraria host do time pro cofre pessoal.

## O que não pode quebrar

- **O cofre pessoal e os ~700 devices**: intocados. O time começa vazio.
- **E2EE**: o servidor nunca vê WK nem código de convite (só blob e hash).
- **Boot do app**: workspace novo precisa entrar em `AppRuntime.VaultWorkspaces`, senão o app **trava na
  abertura**.
- **`IsSyncable`**: o formato novo entra **adicionando**, nunca trocando — senão segredos param de subir
  em silêncio (a classe de falha mais traiçoeira desta base).
