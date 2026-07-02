# Design — GUI estilo Termius: navegação por grupos/hosts (Fase 1)

- **Data:** 2026-07-01
- **Branch:** `feature/gui-termius-nav` (worktree `C:\dev\remoteops-termius`), a partir de `feature/packaging-velopack-update` (já com o tema Slate Signal, HEAD `f3c91ed`).
- **Status:** aprovado para spec → aguardando revisão antes do plano.
- **Referência visual:** Termius (dois prints do usuário: tela de grupos em cards + aba de terminal). Mockup dark aprovado.

## 1. Contexto e objetivo

Hoje o `RemoteOps.Desktop` tem um layout de **3 painéis** (árvore de grupos à esquerda, DataGrid de hosts, inspector à direita) + **barra de menus**. O usuário quer a IA no modelo **Termius**: um **rail lateral enxuto** (Hosts / Keychain / Logs), um **grid de grupos em cards**, **drill-down** para ver os hosts de um grupo, e **abrir um host** vira uma **aba de sessão** (terminal). Isso **substitui** o layout de 3 painéis e a barra de menus. O tema dark "Slate Signal", as abas de sessão (Terminal/RDP/NDesk) e a janela de Configurações **permanecem**.

## 2. Decisões (brainstorming)

| Tema | Decisão |
|---|---|
| Fatiamento | Navegação/IA agora (Fase 1); SFTP depois (Fase 2); Keychain-CRUD + Logs-persistidos são a Fase 1b |
| Tema | Manter dark "Slate Signal" com o layout Termius |
| Chrome | Substituir a barra de menus pelo modelo Termius (rail + toolbar + menu da conta); Configurações/Atualizações/Sobre vão para o menu da conta (avatar) |
| Abrir host | Duplo-clique conecta no protocolo principal; clique-direito para SSH/Telnet/RDP/WinBox |
| Keychain/Logs (Fase 1) | Telas **básicas** (Keychain lista refs de credencial read-only; Logs mostra eventos da sessão atual) — CRUD/persistência ficam na Fase 1b |
| Abordagem | **A** — shell com abas no topo (aba fixa "Hosts" + abas de sessão), reusando VMs de dados/sessão |

**Fora de escopo desta fase (Fase 1):** SFTP (Fase 2); CRUD completo de credenciais no vault e Logs persistidos/filtráveis (Fase 1b); tema claro; assinatura de código.

## 3. Abordagem escolhida (A) e por quê

O shell (`MainWindow`) passa a ser um **workspace com abas no topo**: uma **aba fixa "Hosts"** (o navegador: rail + conteúdo) e as **abas de sessão** abertas ao conectar. Conectar num host reusa o fluxo `OpenSessionRequest` que já existe (`MainViewModel.OnSessionRequested` → `TabsViewModel.OpenTerminalTab/OpenRdpTab`). O modelo de dados (`ILocalStore`) já expõe tudo que precisamos.

- **Por que não B** (sessões numa região fixa + navegador acima): não é o que os prints mostram; abas no topo é o modelo Termius.
- **Por que não C** (reescrever do zero): joga fora os ViewModels de dados/sessão que funcionam.

> **Nomenclatura:** a aba do navegador chama **"Hosts"** (não "Vaults" como no Termius) para não colidir com o *vault* de credenciais (RemoteOps.Security).

## 4. Design detalhado

### 4.1 Shell (abas no topo)

`MainWindow` vira um host de abas no topo:
- **Aba fixa "Hosts"** (não fechável) = o navegador (§4.2).
- **Abas de sessão** = uma por sessão aberta (Terminal/RDP/NDesk), reusando `TabsViewModel` + os tab-views existentes.
- Botão `+` (abre uma nova aba "Hosts" ou foca a existente — Fase 1 apenas foca a aba Hosts).
- (Fase 2: aba "SFTP".)

`WorkspaceViewModel` (novo) coordena: `Browser` (o navegador da aba Hosts) + `Tabs` (o `TabsViewModel` de sessões já existente). `MainWindow` liga um `TabControl` cujo primeiro item é o navegador e os demais são as abas de sessão.

### 4.2 Aba "Hosts" = rail + conteúdo

`BrowserView` (novo) = rail lateral (§4.3) + região de conteúdo que troca conforme a seção do rail:
- **Hosts** → `HostsView` (§4.4)
- **Keychain** → `KeychainView` (§4.6)
- **Logs** → `LogsView` (§4.7)

`BrowserViewModel` mantém `ActiveSection` (enum `Hosts|Keychain|Logs`) e expõe os três sub-VMs.

### 4.3 Rail lateral

`RailView` (dentro de `BrowserView`): itens verticais **Hosts / Keychain / Logs** (ícone + rótulo, item ativo com accent + barra à esquerda) e, no rodapé, o **avatar da conta** que abre o **menu da conta** (§4.8). Sem Snippets/Port Forwarding/Known Hosts (removidos por decisão do usuário).

### 4.4 Conteúdo Hosts: grid de grupos → drill-down

`HostsView` + `HostsViewModel` — dois modos:
1. **Grid de grupos** (padrão): cards, um por grupo de topo, cada card mostra **nome + contagem de hosts**. Fonte: `ILocalStore.GetGroupsAsync(workspaceId)`; a contagem por grupo vem de `GetAssetsAsync(workspaceId, group.Id).Count`. Cada card é um `GroupCardViewModel { Id, Name, HostCount }`.
2. **Detalhe do grupo** (após clicar num card): **breadcrumb** ("Grupos / <nome>") + **botão Voltar** + **lista dos hosts** daquele grupo (linhas), via `GetAssetsAsync(workspaceId, groupId)`. Cada linha é um `AssetViewModel` (reusado), mostrando nome, protocolo principal, endereço, tags.

**Toolbar** (topo do conteúdo): campo de **busca** (filtra grupos no modo grid e hosts no modo detalhe) + botão **`+ Novo host ▾`** com dropdown **`Novo grupo`**.

**Abrir host:** **duplo-clique** numa linha de host → conecta no **protocolo principal** (o primeiro endpoint do host, ordem de preferência ssh→telnet→rdp→mikrotik). **Clique-direito** → menu de contexto com SSH/Telnet/RDP/WinBox (habilitados conforme os endpoints/flags do host) + Editar + Excluir. Conexão reusa o fluxo `OpenSessionRequest` → `MainViewModel.OnSessionRequested` (que já roteia terminal/RDP/placeholder e respeita a flag `rdp.enabled`).

**Estados vazios:** "Nenhum grupo ainda — crie um" (grid) e "Nenhum host neste grupo" (detalhe).

### 4.5 Novo host / Novo grupo + editor de host

- **Novo grupo:** um diálogo simples (campo nome + grupo pai opcional) que cria via `AddGroupAsync(workspaceId, name, parentId?)`.
- **Novo host / Editar host:** `HostEditorDialog` + `HostEditorViewModel` (modal, tematizado). Campos: nome, grupo, lista de **endpoints** (protocolo + endereço + porta — add/remove) e credencial. Reusa a lógica de endpoint do `InspectorViewModel` atual (`AddEndpointAsync`, resolução de porta por protocolo) e persiste com `AddAssetAsync`/`UpdateAssetAsync` + `AddEndpointAsync`/`DeleteEndpointAsync`. **Nunca exibe senha** (só refs de credencial).

### 4.6 Keychain (básico — Fase 1)

`KeychainView` + `KeychainViewModel`: lista **read-only** das referências de credencial via `ILocalStore.GetCredentialRefsAsync(workspaceId)` — mostra nome/tipo/uso, **nunca o segredo**. CRUD completo (criar/editar/excluir no vault cifrado) é **Fase 1b**. Um aviso discreto indica "gerenciamento completo em breve".

### 4.7 Logs (básico — Fase 1)

`LogsView` + `LogsViewModel`: mostra os **eventos da sessão atual** (um buffer em memória alimentado por um sink leve que também recebe os eventos de auditoria durante a execução). **Persistência consultável + filtros são Fase 1b** (hoje os audit sinks só fazem `Trace.WriteLine`, sem armazenamento). Aviso discreto: "histórico persistente em breve".

### 4.8 Menu da conta (avatar)

Botão de avatar no rail abre um menu com **Configurações** (abre a `SettingsWindow` existente), **Verificar atualizações** e **Sobre** — reusa os handlers/serviços já criados no redesign Slate Signal (`CreateSettingsViewModel()`, `IUpdateService`, `AppVersionText`).

### 4.9 O que é aposentado / mantido

- **Aposenta:** `Views/SidebarView`, `Views/HostListView`, `Views/InspectorView` (como painéis fixos) e a **barra de menus** do `MainWindow`. A lógica útil (endpoints do Inspector, comandos de sessão do HostList, grupos do Sidebar) **migra** para os novos VMs (HostEditor, HostsViewModel).
- **Mantém:** tema Slate Signal (`Themes/`), abas de sessão (`TabsViewModel` + `Terminal/Rdp/NDesk` tab-views), `SettingsWindow`, `ISettingsStore`/`CompositeFeatureFlags`, `IWinBoxRunner`, o fluxo `OpenSessionRequest`.

### 4.10 ViewModels & fluxo de dados (resumo)

- `WorkspaceViewModel` → `BrowserViewModel` (rail + seções) + `TabsViewModel` (sessões, reusado).
- `BrowserViewModel` → `HostsViewModel` + `KeychainViewModel` + `LogsViewModel` + `AccountMenu`.
- `HostsViewModel` reusa `ILocalStore` (grupos/hosts/contagem) e dispara conexão pelo fluxo `OpenSessionRequest` existente.
- `HostEditorViewModel` reusa `ILocalStore` (asset/endpoint) + a lógica de endpoint do Inspector.
- Nenhum segredo trafega pela UI; só refs de credencial.

## 5. Testes

- `HostsViewModel`: carga de grupos + contagem correta; drill-down carrega hosts do grupo certo; busca filtra; "conectar" dispara `OpenSessionRequest` com o protocolo/host certos (protocolo principal no duplo-clique, específico no contexto).
- `HostEditorViewModel`: criar host com endpoints persiste via store; editar atualiza; validação de endereço/porta.
- `KeychainViewModel`: lista os refs do store (sem segredo).
- `BrowserViewModel`: alternar seção do rail troca o conteúdo.
- Smoke XAML: build 0/0 (TreatWarningsAsErrors), app abre na aba Hosts com o grid; duplo-clique abre aba de sessão; menu da conta abre Configurações; 441 testes anteriores seguem verdes.

## 6. Riscos e mitigações

| Risco | Mitigação |
|---|---|
| Reescrita grande da camada de UI do Desktop | VMs de dados/sessão reaproveitados; migração incremental; testes cobrindo os novos VMs |
| Aposentar Sidebar/HostList/Inspector pode quebrar o fluxo de sessão | Rotear conexão pelo MESMO `OnSessionRequested` já testado; não duplicar lógica |
| Contagem de hosts por grupo pode ficar cara (N+1 em muitos grupos) | Carregar grupos e agregar contagem numa passada; medir; otimizar só se necessário |
| `TreatWarningsAsErrors` | Build incremental por task |
| Confusão "Hosts tab" vs abas de sessão | Aba Hosts é fixa/não fechável e visualmente distinta |

## 7. Critérios de sucesso

- App abre na **aba Hosts** com o **grid de grupos** (cards com contagem), dark coeso.
- Clicar num grupo mostra seus **hosts**; **duplo-clique conecta** (abre aba de sessão) e clique-direito oferece SSH/Telnet/RDP/WinBox.
- **Novo host/Novo grupo** e **editor de host** funcionam (persistem no store).
- Rail com **Keychain** (lista read-only) e **Logs** (eventos da sessão) básicos; **menu da conta** abre Configurações/Atualizações/Sobre.
- Barra de menus e layout de 3 painéis **removidos**. Build 0/0, 441 testes anteriores verdes + novos testes de VM.
