# Sync em tempo real resiliente — Design

**Data:** 2026-07-19
**Frente:** C (a primeira de quatro; ver "Contexto" abaixo)
**Versão base:** v1.3.2 (`152d6d6`)
**Status:** aprovado pelo operador (camadas 1 e 2 juntas)

---

## Problema

O operador cadastrou um dispositivo no PC A e, no PC B, **precisou fechar e abrir o app** para
enxergar a informação atualizada — mesmo clicando em "forçar sincronização". Ambos os PCs rodam
**v1.3.2**, então o bug antigo de applier (corrigido na v1.3.0, quando o applier gravava em
`local_entities`, tabela que ninguém lia) **não é a causa**.

A causa é outra: o canal de tempo real morre em silêncio e a rede de segurança que deveria cobri-lo
tem o mesmo defeito.

## Causa raiz (verificada linha a linha)

| # | Defeito | Evidência | Efeito |
|---|---|---|---|
| 1 | Ao reconectar, o cliente **nunca volta ao grupo** do workspace | `SyncHub.OnConnectedAsync` é stub (`_ = userId;`, `SyncHub.cs:40-45`); grupo é por ConnectionId (`SyncHub.cs:32`); `JoinWorkspace` só no connect (`SignalRSyncHintChannel.cs:37-41`) | Uma oscilação de rede → tempo real morto até reiniciar |
| 2 | `WithAutomaticReconnect()` **sem handler** `Reconnected`/`Closed` | `SignalRSyncHintChannel.cs:19-33` (grep: zero handlers no repo) | Após 4 tentativas (0/2/10/30s) o SignalR desiste calado |
| 3 | Connect inicial **engolido sem retry** | `SyncSession.cs:67-74`; engolido de novo em `App.xaml.cs:502-512` | Falhou ao abrir → nunca mais tenta |
| 4 | Polling de fallback **fixo em 2 minutos** | `SyncSessionFactory.cs:25` (`Interval = TimeSpan.FromMinutes(2)`); nenhum código sobrescreve | "Demora minutos" |
| 5 | `RunLoopAsync` chama `SyncOnceAsync` **fora de try/catch** | `SyncSession.cs:230`; `SetStatus` dispara `StatusChanged` (`SyncOrchestrator.cs:78,111,232`) e o assinante faz `Dispatcher.Invoke` (`App.xaml.cs:780`) | Exceção de assinante **mata o polling em silêncio** |
| 6 | Caminho do hint **sem debounce** | `OnHintAsync` chama `SyncOnceAsync` direto (`SyncSession.cs:214`); o debounce de 1,5s existe mas só está ligado a `LocalChangePushed` (`SyncSession.cs:46,49-54`); servidor emite **1 hint por change** (`SyncService.cs:130-142`) | Importar 200 hosts = ~200 ciclos completos enfileirados no gate do PC B, cada um re-enumerando o cofre |
| 7 | Ciclo que termina em `SyncState.Error` espera o intervalo **inteiro** | `SyncOrchestrator.cs:108-112` | Erro transitório custa 2 minutos |

**Observação:** o push do lado A já é rápido (~2s: mutação → outbox → `LocalChangePushed` → debounce
1,5s → ciclo incremental). O gargalo é **o lado B descobrir** que há novidade.

## Objetivo

O que é cadastrado num PC aparece no outro **em segundos**, sem fechar e abrir, e degrada com
elegância se o WebSocket cair (teto de staleness previsível em vez de indefinido).

## Arquitetura

Duas camadas com **modos de falha diferentes** — essa é a regra de projeto: redundância só conta se
os dois caminhos não morrerem pelo mesmo motivo.

### Camada 1 — Rede de segurança (só cliente)

1. **Laço blindado:** envolver o `SyncOnceAsync` de `RunLoopAsync` em try/catch. O laço de polling
   passa a ser incondicionalmente sobrevivente — nenhuma exceção de assinante o mata.
2. **Polling 45s:** default de `SyncSessionOptions.Interval` de 2min → 45s. Continua sendo opção de
   **código** (`SyncSessionOptions`), **não** vira ajuste na tela de Configurações — não há dor que
   justifique expor isso ao operador (YAGNI). Teto de staleness previsível mesmo com o canal morto.
3. **Retry curto com backoff em erro:** quando o ciclo termina em `SyncState.Error`, próximo ciclo em
   intervalo curto com backoff (5s → 10s → 20s, teto no intervalo normal) em vez do intervalo cheio.
4. **Debounce no caminho do hint:** rotear `OnHintAsync` pelo timer de debounce que já existe
   (`SyncSession.cs:52`), em vez de chamar `SyncOnceAsync` direto. Rajada de N hints = 1 ciclo.

### Camada 2 — Tempo real resiliente (cliente + servidor)

5. **Servidor — auto-join no hub (a peça central):** `SyncHub.OnConnectedAsync` passa a consultar as
   memberships do usuário autenticado e fazer `AddToGroupAsync` para cada workspace, com o nome de
   grupo canônico (`Guid.ToString("D")`).
   *Por que no servidor:* `OnConnectedAsync` roda **a cada reconexão** (ConnectionId novo), então o
   buraco do re-join fecha mesmo que o cliente esqueça. Elimina a classe inteira de bugs, não um
   exemplar — e normaliza o nome do grupo na origem.
6. **Cliente — re-join defensivo:** guardar o workspaceId no connect e registrar
   `_connection.Reconnected += ...` para re-chamar `JoinWorkspace`. Redundante com (5) por desenho.
7. **Cliente — retry do connect inicial:** backoff em `SyncSession.StartAsync` em vez de engolir.
8. **Cliente — normalização de workspaceId:** `Guid.Parse(...).ToString("D")` no caminho de env var
   (`App.xaml.cs:813-826`, fail-closed se inválido) e filtro do hint em `OrdinalIgnoreCase`
   (`SyncSession.cs:184`). *O caminho da conta já é imune* (workspaceId vem da sessão do servidor,
   `AccountSyncCoordinator.cs:89,194`) — isto cobre o caminho legado por env var.
9. **Status do canal visível:** o `SyncStatusViewModel` ganha um estado derivado ("Tempo real" vs
   "Periódico") exibido na **barra de status de sync que já existe** — sem tela nova, sem ícone novo,
   só o texto do estado atual. Serve para o operador diagnosticar em campo se o WebSocket está
   passando, sem depender de log.

## Fluxo de dados (depois)

```
PC A: mutação → outbox → push (~2s)
                            │
                            ▼
Servidor: grava change → emite workspace.changed p/ Group(wsId "D")
                            │
              ┌─────────────┴─────────────┐
              ▼                           ▼
   PC B: hint → DEBOUNCE 1,5s      (canal morto?)
              │                           │
              ▼                           ▼
        1 ciclo incremental      polling 45s (laço blindado)
              │                           │
              └─────────────┬─────────────┘
                            ▼
            applier grava nas tabelas reais
                            ▼
       ChangesApplied → debounce 300ms → Dispatcher
                            ▼
              HostsViewModel.ReconcileFromStoreAsync
```

## Tratamento de erro

- **Laço de polling:** nunca propaga. Exceção → log (sem segredo) → `SyncState.Error` → retry curto.
- **Canal de hints:** falha é esperada e não fatal; o polling cobre. Reconexão é automática e o
  re-join é garantido pelos dois lados.
- **Token expirado no reconnect:** `AccessTokenProvider` lê o `VaultTokenStore` sem refresh
  (`SyncSessionFactory.cs:84`); refresh só acontece em 401 HTTP (`CloudAuthChannel.cs:83-119`). Custo
  máximo: um ciclo de polling até o refresh ocorrer pelo caminho HTTP. **Aceito nesta rodada**;
  refresh proativo no canal fica registrado como follow-up.

## Testes

Restrição real: `HubConnection` é construído dentro do construtor do `SignalRSyncHintChannel` e
**não é mockável** sem refactor. Portanto o teste se concentra no que é testável de verdade:

| Alvo | Como |
|---|---|
| Laço blindado | Fake de orquestrador que lança; asserção de que o laço segue vivo e faz o próximo ciclo |
| Retry/backoff em erro | Sequência de estados com relógio injetado; asserção dos intervalos |
| Debounce do hint | N hints em rajada → 1 ciclo |
| Normalização de workspaceId | GUID maiúsculo/minúsculo/inválido → forma canônica ou fail-closed |
| Filtro do hint | Case-insensitive |
| Auto-join no hub | Teste do `SyncHub` com memberships fake; asserção de `AddToGroupAsync` por workspace, nome canônico |
| Status do canal na UI | Render test STA do `SyncStatusViewModel`/barra |

O canal SignalR ponta a ponta fica para **validação manual em campo** (o operador derruba a rede e
confirma que volta sozinho) — falha silenciosa é por desenho (ADR-013).

## O que NÃO pode quebrar

- **E2EE:** nada aqui toca cripto, cofre ou envelopes. Sigilo intacto por construção.
- **Ordem metadados → segredos** (`SyncOrchestrator.cs:94-99`): intocável.
- **Nenhum log novo pode imprimir a URL do WebSocket** — o JWT vai na query (ADR-013).
- **Toda reconciliação marshalada pro Dispatcher** (`App.xaml.cs:536-538`).
- **Compatibilidade:** o auto-join é aditivo; cliente antigo continua funcionando (ele já faz
  `JoinWorkspace` explícito). Nenhuma mudança de protocolo de sync.
- **Gates de CI:** `dotnet format --verify-no-changes` + build Release + `TreatWarningsAsErrors`.

## Fora de escopo (deferidos, com motivo)

- **Evento `SecretsApplied`** — o ganho real é pequeno: o Keychain recarrega a cada abertura
  (`BrowserViewModel.cs:31`) e o picker do editor recarrega no `Loaded`
  (`HostEditorDialog.xaml.cs:15`); só a lista já aberta fica stale. E o guard de versão
  (`SecretSyncOrchestrator.cs:181`) não é estrito, então dispararia evento espúrio em re-download.
- **Batching de hints no servidor** (hoje 1 por change) — o debounce do cliente já resolve o custo.
- **Ativação da nuvem a quente, sem restart** — exige key-ring trocável no grafo de DI
  (`App.xaml.cs:158`); caro e o restart de 2s é aceitável.
- **Compactação do changelog no servidor** — `PatchJson.Contains(cid)` sem índice
  (`SyncService.cs:88-94`) fica mais lento com o changelog crescendo. Real, mas é outra obra.

## Contexto — as outras três frentes

Este spec cobre **só a frente C**. As demais seguem em sequência, cada uma com spec próprio:

- **B — cadastro de host:** hoje o Salvar aceita dois estados inválidos em silêncio (host sem
  endpoint com o formulário descartado; endpoint SSH/RDP sem credencial) e a edição hidrata a porta
  do default do protocolo, não da salva (host em SSH:2222 abre mostrando 22). Vira formulário único
  com update in-place, preservando merge de `EndpointProfile`.
- **A — onboarding da nuvem:** URL default do produto + tela de conta direta (criar/entrar).
- **D — layout adaptativo:** aproveitar telas grandes (mais colunas, detalhe lado a lado, mais hosts
  visíveis) e corrigir as 4 janelas de tamanho fixo que podem cortar o botão de login em 1366x768.
