# Fatia 1 — Times compartilhados — PONTO DE RETOMADA

> **Parado em 2026-07-22 a pedido do operador.** Branch `feat/time-fatia1`, 24 commits.
> Leia este arquivo primeiro; o plano completo está em `2026-07-21-time-fatia1.md`.

## Estado

| | |
|---|---|
| Branch | `feat/time-fatia1` (pushada; **não** tem PR aberto) |
| Base | `origin/main` = v1.4.7 (`d798ed0`) |
| Tamanho | 172 arquivos, **+27.089 / −258** |
| Gates | build Release **0 avisos** · **1627 testes, 0 falhas** · `dotnet format` limpo |
| Árvore | limpa |
| Versão | **ainda não bumpada** — o release seria **v1.5.0** |

O número que importa: **258 remoções em 27 mil linhas**. É quase tudo aditivo, ao lado do que já
existe. O cofre pessoal e os ~700 devices do operador são o ativo inegociável, e há teste que simula
o app atualizando sobre o banco e o cofre de hoje.

## O que a fatia entrega

Criar time (nasce **vazio**) · convite por e-mail + **código fora-de-banda** · gestão compartilhada
com a senha abrindo dos dois lados · escolher cofre ao abrir · botão "Trocar de cofre…" que drena a
fila antes de sair · tela de Equipe · indicador de cofre na barra e no título.

## Como a criptografia ficou (o essencial)

- **WK = chave do time**, 32 bytes **aleatórios**, presa a ninguém — é a única que pode ser entregue
  a outro membro. Raiz `WkRootedV1`, ao lado das duas que já existiam (DPAPI e AMK).
- Cada membro guarda a WK **embrulhada sob a própria AMK** (`Membership.WrappedWk`), publicada por
  `PUT /workspaces/{id}/key` (idempotente; blob divergente → 409, nunca troca em silêncio).
- Convite: o dono sorteia 160 bits, deriva `K_invite = HKDF(código)`, embrulha a WK sob ela e sobe
  **blob + SHA-256 do código**. O servidor nunca vê o código nem a WK. O e-mail leva o link, **nunca
  o código**.
- AAD do esquema novo: `env|{id}|{ws}|v{n}|{type}|{credentialId}|{algorithm}` — amarra credencial e
  carimbo, então re-associação e downgrade quebram o GCM. **O AAD do `AmkRootedV1` não foi tocado.**

## ⚠️ Ordem de deploy (não negociável)

**Backend PRIMEIRO, cliente depois.** Migrações a aplicar: `AddTeamInvites`, `AddWorkspaceKind`,
`AddSecretEnvelopeConcurrencyToken`. Servidor: `innetstorage` (45.5.16.109),
`/opt/innet/remoteops-sync/InnetTermb`. **Não atrapalhar o Nextcloud que roda na mesma máquina.**

## O que falta (nesta ordem)

1. **[Eu recomendo consertar antes de lançar] A porta trancada por dentro.**
   Quando `SessionVaultScope` **recusa** um cofre no boot (regras 4/6), o `App` mostra a recusa e faz
   `Shutdown()` — e o botão "Trocar de cofre…" vive **dentro das Configurações**, ou seja, do outro
   lado de um app que não abre. O operador fica trancado com os ~700 intactos e inalcançáveis.
   Ficou bem mais difícil de alcançar depois da REGRA 0 (`kind` decide antes do marcador), mas é a
   **terceira** vez que esse formato aparece nesta fatia (as outras duas: cache da AMK ilegível, e a
   recusa de escopo).
   **Direção:** oferecer a saída **na própria recusa**, reusando `VaultSwitch` + o reinício que já
   existem. Cuidado: a saída leva a **escolher outro cofre**, nunca a "ignorar a recusa" — a recusa
   existe para não misturar dois acervos.
   Arquivos: `src/RemoteOps.Desktop/App.xaml.cs:190-203`, `Account/SessionVaultScope.cs`,
   `Account/VaultSwitch.cs`.

2. **[Não rodou] Verificação adversarial do último conserto** (`03deade`, o `kind` autoritativo).
   Foi o único estágio que entrou sem revisão independente. As lentes que valem: *tentar envenenar o
   banco pessoal* (com foco na **janela do deploy**: backend novo × cliente velho e o inverso) e *o
   acervo continua intacto*.

3. **Estágio 1f — release.** Bump **1.5.0**, `CHANGELOG.md` (já escrito em `[Unreleased]`) +
   `operator-changelog.json` em linguagem de operador, PR, CI verde, **label `security-reviewed`**
   (a branch toca `RemoteOps.Security/`; o gate reavalia ao rotular desde o fix no `ci.yml`),
   merge, tag, `bash tools/mirror-release.sh v1.5.0`.

## Dívida declarada (não bloqueia)

- Badge `TeamPending` não se atualiza quando a chave chega na sessão viva (cura ao reabrir).
- `WkVersion` é gravado e **nunca lido** — é para a rotação da WK (Fatia 3).
- Indicador de cofre **dentro** do `HostEditorDialog` (a barra e o título ficam visíveis atrás dele).
- Sem rate-limit nos endpoints de convite (código de 160 bits torna força bruta inviável).
- Sem listar/revogar convite pendente.
- Sem endpoint de **promover papel** — "último dono não sai" pode travar se ninguém for promovido.
- Corridas provadas só no provider InMemory (sem Postgres real em CI).

## O padrão que esta fatia revelou (leia antes de revisar de novo)

Cinco rodadas de revisão adversarial, **cinco bloqueantes**, todos da MESMA classe — em duas formas:

**(a) "não sei" virando afirmação:** arquivo ausente → "o banco é meu"; 404 → "não é um time";
chave nula → "cofre pessoal"; cache ilegível → "conta inválida". Nenhum é erro de lógica: todos são
uma **ausência lida como fato positivo**, que então **vira escrita em disco**.

**(b) código pronto que nenhum caminho de produção alcança:** `CreateTeamAsync` sem botão;
`LogoutAsync` sem botão. A suíte fica verde porque o teste chama a função direto — provando que algo
que ninguém usa funciona.

**Ao revisar esta base, procure essas duas coisas por nome.** "Procure bugs" produz revisão genérica.

## Teste manual do operador (DUAS contas, DOIS PCs) — só depois do item 1

Nesta máquina o screenshot não captura a janela do RemoteOps: **o olho dele é a única prova visual.**

0. **Backend v1.5.0 no ar antes de qualquer cliente.**
1. **PC-1, cofre pessoal:** anote o **número exato de equipamentos** e conecte num cliente conhecido.
   É a linha de base.
2. Configurações → Conta → Equipe → **"Criar time…"**. O aviso de time **VAZIO** tem que aparecer
   ANTES de criar; depois de criar, a tela diz que esta janela **continua no cofre pessoal**.
3. **"Trocar de cofre…"** → confirma (mostra a fila pendente) → reinicia → login → **tela de escolha
   com 2 cofres** → escolhe o TIME.
4. ⭐ **PROVA DO TIME VAZIO:** abre com **ZERO equipamentos**. Se aparecer UM que seja: **PARE e
   reporte.**
5. No time: cadastre **1 equipamento de bancada** (nunca de cliente) com senha. Gere convite para a
   conta B — o código aparece **na tela**; confira no e-mail que **o código NÃO está lá**.
6. **PC-2, conta B:** aceita o convite com o código → troca de cofre → escolhe o time → vê **só** o
   equipamento de teste **e a senha dele ABRE**. É a prova do E2EE compartilhado ponta a ponta.
7. **PC-1 volta ao pessoal.** ⭐ **PROVA DO ACERVO:** número **exatamente** igual ao do passo 1, o
   equipamento de teste do time **não** aparece, e o cliente do passo 1 **conecta com a senha
   guardada**.
8. **PC-2, conta A (segundo PC do dono):** escolhe o TIME → a chave desce sozinha e a senha abre.
   Volta ao pessoal e repete a conferência do passo 7 nesta máquina.
9. **Dia seguinte, os dois PCs:** abre normalmente (sem login) e cai direto no pessoal, tudo no lugar.

⛔ **PARE e reporte** se em qualquer passo aparecer *"cofre pessoal de outra instalação"* ou *"não foi
possível identificar o cofre"* falando do cofre DELE.
