# Aviso de atualização não-intrusivo — Design

**Data:** 2026-07-20
**Versão base:** v1.4.1 (`05079cc`)
**Status:** aprovado pelo operador

---

## Problema

O operador pediu "um pop up avisando que tem atualização nova". A investigação mostrou que **o popup
já existe e está corretamente ligado** — o pedido revela um problema diferente do que parece.

Estado verificado:

- `MainWindow.PromptUpdateIfAvailableAsync` (`MainWindow.xaml.cs:77`) mostra "Nova versão X disponível
  … Baixar e instalar agora?" com Sim/Não.
- Está ligado no `Loaded` da janela principal (`MainWindow.xaml.cs:53`).
- O feed está saudável: consultado como o Velopack consulta, devolve `v1.4.1`, não-rascunho,
  `releases.win.json` correto.

**A causa real:** a verificação acontece **uma única vez, na abertura do app**. O operador é de ISP e
deixa o RemoteOps aberto o dia inteiro; uma versão publicada durante o expediente nunca é anunciada,
porque a janela já foi carregada. Ele só saberia se fechasse e reabrisse depois do lançamento.

**Dois agravantes encontrados no caminho:**

1. `WorkspaceViewModel.CheckForUpdatesQuietAsync` faz `catch (Exception) → return null`
   (`WorkspaceViewModel.cs:89-92`). Uma falha de rede na abertura deixa o app **indistinguível de
   "está tudo atualizado"** — nada aparece, nem o aviso, nem o erro.
2. O aviso atual é **modal e rouba foco**. Num console de operação de rede isso é perigoso: se
   pipocar enquanto o operador digita num equipamento em produção, as teclas vão para o diálogo — e um
   `Enter` destinado ao roteador vira "Sim, atualizar agora", reiniciando o app no meio de uma
   manutenção. Hoje esse risco é real, ainda que a janela de exposição seja curta (só no startup).

## Objetivo

O operador fica sabendo de uma versão nova **mesmo com o app aberto há horas**, sem nunca ser
interrompido no meio de uma sessão em equipamento.

## Decisão (escolhida pelo operador)

Indicador **discreto e persistente**; diálogo **só quando ele clicar**.

## Arquitetura

### 1. Verificação periódica

Além da checagem de abertura (que permanece), um timer re-verifica a cada **3 horas** enquanto o app
está aberto. Intervalo é opção de **código**, não vai à tela de Configurações (não há dor que
justifique — YAGNI).

O timer é `DispatcherTimer` (afinidade com a UI thread, que é onde o resultado será consumido) e é
**parado no fechamento**. Disciplina obrigatória aprendida na v1.4.1: recurso que não solta pendura o
processo, e processo pendurado segura o mutex de instância única — o app não reabre sem reiniciar o
Windows. Ver [[feedback_remoteops_shutdown_instancia_unica]].

### 2. Indicador na barra de status

Na barra que já existe (`BrowserView.xaml:78-120`, hoje "Sincronizado · Tempo real"), entra um item
**clicável** que só é visível quando há atualização:

```
Sincronizado · Tempo real · ⬆ Atualização 1.4.2 disponível
```

Persistente: fica até o operador agir. Não pisca, não rouba foco, não fecha sozinho.

### 3. Clique abre o fluxo que já existe

O clique dispara o mesmo diálogo de confirmação ("Baixar e instalar agora? O RemoteOps reinicia
sozinho ao concluir") e, no sim, o `TryApplyUpdateAsync` que já existe. Nenhum caminho novo de
download/aplicação é criado.

### 4. O modal de abertura sai

`PromptUpdateIfAvailableAsync` deixa de exibir diálogo por conta própria e passa a **alimentar o
indicador**. Elimina de vez o risco de roubo de foco descrito acima.

### 5. Falha de checagem deixa de ser ambígua

O indicador carrega no `ToolTip` o horário da **última verificação bem-sucedida**. Assim "não aparece
nada" deixa de significar as duas coisas ao mesmo tempo ("atualizado" e "não consegui verificar").
Falha continua não gerando alarme visual — o objetivo é remover ambiguidade, não criar barulho.

## Componentes

- **`UpdateNotificationViewModel`** (novo) — responsabilidade única: sabe se há atualização, qual a
  versão, quando foi a última checagem boa, e expõe o comando de aplicar. Fica ao lado de
  `SyncStatusViewModel` no `BrowserViewModel`; não se mistura com sync (são domínios distintos que só
  dividem uma barra na tela).
- **`IUpdateService`** (existente) — reaproveitado sem alteração de contrato.
- **`MainWindow`** (existente) — deixa de exibir o modal no `Loaded`; passa a hospedar o timer e a
  encaminhar o pedido de aplicar para o diálogo de confirmação.

## Testes

| Alvo | Como |
|---|---|
| VM: sem atualização → indicador invisível | fake de serviço devolvendo `UpdateAvailable=false` |
| VM: com atualização → texto e visibilidade corretos | fake devolvendo versão nova |
| VM: falha na checagem → não vira "atualizado"; preserva último sucesso | fake que lança |
| VM: checagem repetida não duplica estado | duas checagens seguidas |
| Timer para no fechamento | asserção de que o dispose para o timer |
| Render STA da barra nos dois estados | padrão existente em `SyncStatusBarRenderTests` |

## O que não pode quebrar

- **Nunca roubar foco** — é o ponto da mudança.
- **Tema:** nenhum `FontSize` literal; tudo por `DynamicResource` (lição v1.2.24). Controle novo entra
  nos render tests STA.
- **Fechamento:** timer parado no dispose; nada de recurso pendurado (lição v1.4.1).
- **Nenhum log novo** com URL de feed.
- **Gates de CI:** `dotnet format`, build Release, `TreatWarningsAsErrors`.

## Fora de escopo

- Expor o intervalo na tela de Configurações (YAGNI).
- Notificação nativa do Windows (toast) — o indicador na barra resolve, e toast some sozinho.
- Atualização automática sem consentimento — decisão do operador continua obrigatória.
