# Design — Redesign da GUI RemoteOps Desktop ("Slate Signal") + menus e opções

- **Data:** 2026-07-01
- **Autor:** brainstorming assistido (Claude) + Vagner
- **Branch:** `feature/gui-slate-signal` (worktree `C:\dev\remoteops-gui-slate`), a partir de `feature/packaging-velopack-update`
- **Status:** aprovado para spec → aguardando revisão antes do plano de implementação

## 1. Contexto e objetivo

O `RemoteOps.Desktop` (WPF, .NET 10) hoje roda com visual **incoerente**: cores fixas misturando claro e escuro (barra de topo `#2D2D30`, sidebar `#F5F5F5`, inspector `#FAFAFA`, abas `#1E1E1E`) e controles WPF no estilo padrão (cinza "Aero"). Não há barra de menus, menus de contexto nem tela de Configurações — a barra de topo só tem título, busca e status de sync.

Paralelamente, existe um **design system dark completo e bem pensado ("Slate Signal")** na branch `feature/gui-design-theme` (worktree `remoteops-gui-design`), mas ele **não está** no app que está sendo testado (`packaging`), e as duas branches divergiram.

**Objetivo:** trazer o "Slate Signal" para o app de release (`packaging`), deixar o visual coeso e profissional, e criar a estrutura de **menus + menus de contexto + janela de Configurações**, entregando um `Setup.exe` testável.

## 2. Decisões tomadas no brainstorming

| # | Decisão | Escolha |
|---|---------|---------|
| Direção visual | Adotar/ refinar o "Slate Signal" (dark), portado para dentro do `packaging` | ✅ |
| Navegação | Barra de menus (Arquivo/Sessão/Exibir/Ferramentas/Ajuda) + menus de contexto nos hosts + janela de Configurações | ✅ |
| Profundidade | "Focado + polish": re-skin completo + menus + Configurações + ícone do app + ícones nos itens + estados vazios + hover/foco. Mantém o layout de 3 painéis | ✅ |
| Abordagem do port | **A** — portar o tema para dentro do `packaging` (aditivo), branch nova | ✅ |

**Não** faz parte deste trabalho (YAGNI): overhaul de UX (árvore de grupos/tags, dashboard, densidade configurável), tema claro, assinatura de código, paleta de comandos (Ctrl+K), importação/exportação de hosts.

## 3. Abordagem escolhida (A) e por quê

Portar o tema **para dentro do `packaging`** como camada aditiva, re-skinando as views atuais do `packaging` e usando as views já tematizadas do `gui-design` como referência.

- **Por que não B** (usar `gui-design` como base): traria as views tematizadas de graça, mas exigiria um merge grande e arriscado dos 13 commits de módulos do `packaging` (Update/Velopack, SqlCipher, resolvers RDP, NDesk.Broker) e reverteria a decisão de que `packaging` é a base de release.
- **Por que não C** (refazer do zero): joga fora trabalho bom e é mais lento.

## 4. Design detalhado

### 4.1 Sistema de tema (port)

Copiar do `gui-design` para `packaging` (worktree `remoteops-gui-slate`) a pasta inteira:

```
src/RemoteOps.Desktop/Themes/
  DarkTheme.xaml                (agregador de MergedDictionaries)
  Tokens/Colors.xaml            (paleta "Slate Signal" + brushes semânticos + hooks SystemColors)
  Tokens/Typography.xaml        (Font.Family.Base, Font.Size.*, estilos Text.TitleLg/Caption/…)
  Tokens/Spacing.xaml
  Tokens/Icons.xaml
  Controls/Buttons.xaml
  Controls/TextInputs.xaml
  Controls/TabControl.xaml
  Controls/DataGrid.xaml
  Controls/TreeView.xaml
  Controls/ScrollBar.xaml
  Controls/Misc.xaml
```

Wiring em `src/RemoteOps.Desktop/App.xaml`:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="Themes/DarkTheme.xaml"/>
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

Garantir que os `.xaml` do tema são compilados como `Page` (comportamento padrão do SDK WPF com `UseWPF=true`; validar no `.csproj` que não há `<Page Remove>`/globs conflitantes).

### 4.2 Re-skin das telas

Trocar cores fixas por `DynamicResource`. Mapa de substituição (referência: `Tokens/Colors.xaml`):

| Elemento | Antes (fixo) | Depois (token) |
|---|---|---|
| Janela (fundo/texto) | — | `Brush.Bg.App` / `Brush.Text.Primary` |
| Barra de topo | `#2D2D30` | `Brush.Bg.SurfaceRaised` + borda `Brush.Border.Subtle` |
| Sidebar | `#F5F5F5` (claro!) | `Brush.Bg.Surface` |
| Lista de hosts | branco padrão | `Brush.Bg.Surface` |
| Área de abas | `#1E1E1E` | `Brush.Bg.Canvas` |
| Inspector | `#FAFAFA` (claro!) | `Brush.Bg.Surface` + borda `Brush.Border.Subtle` |
| GridSplitters | `#DDD` | `Brush.Border.Default` |
| Título topo | inline | `Style="{DynamicResource Text.TitleLg}"` |
| Status de sync (texto/ponto) | `#ccc`/`#999` | `Text.Caption` + `Brush.Status.*` via DataTrigger (já modelado no `gui-design/MainWindow.xaml`) |

Arquivos a re-skinar (usar as versões tematizadas do `gui-design` como referência, **reconciliando** as diferenças funcionais do `packaging`):
`MainWindow.xaml`, `Views/SidebarView.xaml`, `Views/HostListView.xaml`, `Views/InspectorView.xaml`, `Views/TabsView.xaml`, e as abas de sessão `Terminal/TerminalTabView.xaml`, `Rdp/RdpTabView.xaml`, `NDesk/NDeskTabView.xaml`.

> Procedimento por view: `diff` packaging×gui-design; onde a diferença é só cor → adotar o token; onde o `packaging` tem estrutura/funcionalidade a mais → aplicar os tokens sobre a estrutura do `packaging`.

### 4.3 Barra de menus (novo)

Adicionar um `Menu` no topo do `MainWindow` (linha 0, acima ou integrada à barra atual). Estrutura e binding:

- **Arquivo**
  - Novo host… — foca o campo "novo host" na lista (ou abre inline) → `HostList.AddHostCommand` (após entrada de nome)
  - Configurações… `Ctrl+,` → `OpenSettingsCommand` (novo)
  - ──
  - Sair `Alt+F4` → `ExitCommand` (novo)
- **Sessão** (habilita quando há host selecionado)
  - Conectar via SSH → `ConnectCommand("ssh")` (novo, no MainViewModel)
  - Conectar via Telnet → `ConnectCommand("telnet")`
  - Conectar via RDP → `ConnectCommand("rdp")` (habilita se `CanOpenRdp`)
  - Abrir WinBox → `Inspector.OpenWinBoxCommand` (habilita se `IsMikroTikHost`)
  - ──
  - Fechar aba atual `Ctrl+W` → `Tabs.CloseActiveCommand` (novo/confirmar)
- **Exibir**
  - Sidebar (checkable) → `ToggleSidebarCommand` (novo)
  - Inspector (checkable) → `ToggleInspectorCommand` (novo)
  - Foco na busca `Ctrl+F` → `FocusSearchCommand` (novo)
- **Ferramentas**
  - Configurações… → `OpenSettingsCommand`
  - Verificar atualizações… → `CheckForUpdatesCommand` (novo; usa `IUpdateService`/`VelopackUpdateService`)
- **Ajuda**
  - Documentação → abre URL do repositório
  - Sobre… → `ShowAboutCommand` (novo; versão via `Update/AppVersion`)

Comandos novos ficam no `MainViewModel` (ou num `MenuViewModel` filho) e reutilizam serviços já injetados no `AppCompositionRoot`.

### 4.4 Menus de contexto no host (novo)

`ContextMenu` no item da lista de hosts (`HostListView` DataTemplate):

- Conectar via SSH / Telnet / RDP — `ConnectCommand(<proto>)`, habilitando por endpoint existente + flag
- Abrir WinBox — se `IsMikroTikHost`
- ──
- Editar… — seleciona o host (Inspector já edita endpoints)
- Excluir — `HostList.DeleteHostCommand` (com confirmação)

**Fonte única de sessão:** hoje `OpenSessionRequest` nasce em `InspectorViewModel.RequestOpenSession(protocol)` e é tratado em `MainViewModel.OnSessionRequested`. Para o contexto (que vive na lista, não no inspector), expor `ConnectCommand(protocol)` no `MainViewModel` que: garante o host selecionado como `Inspector.Asset` e dispara o **mesmo** fluxo `OpenSessionRequest`. Assim botões do Inspector, itens de menu e contexto convergem num caminho só.

### 4.5 Janela de Configurações (novo)

`SettingsWindow.xaml` (modal, dono = MainWindow), tematizada pelo Slate Signal, com abas:

- **Aparência** — tema (por ora "Slate Signal (escuro)"; slot para tema claro futuro).
- **Recursos** — toggles `rdp.enabled` e `ndesk.enabled` (feature flags), **persistidos**.
- **Atualização** — versão atual (`Update/AppVersion`) + botão "Verificar agora" (`IUpdateService`). (Canal/pré-release só se o `VelopackUpdateService` já expuser; senão, fora de escopo.)
- **Sobre** — versão, licença, links.

**Persistência de settings (mudança de arquitetura pequena e necessária):**
Hoje `IFeatureFlags` só tem `EnvironmentFeatureFlags` (lê `REMOTEOPS_FEATURE_FLAGS`, **só leitura**). Para os toggles funcionarem e persistirem:

1. `ISettingsStore` + `JsonSettingsStore` — grava/lê `%LocalAppData%\RemoteOps\settings.json` (flags do usuário, tema).
2. `CompositeFeatureFlags : IFeatureFlags` — `IsEnabled(x)` = `settings.Flags[x]` **OU** `env[x]` (env continua valendo como override, preservando o comportamento atual dos testes/CI).
3. `AppCompositionRoot` passa a registrar `CompositeFeatureFlags` no lugar de `EnvironmentFeatureFlags`.
4. Ao salvar em Configurações, gravar no `JsonSettingsStore`; flags que exigem restart avisam o usuário.

> Ponto de decisão de implementação (será detalhado no plano): merge semântico env×settings — proposta: `enabled = env.Contains(flag) || settings.Get(flag) == true`. Nunca **desabilitar** por settings o que o env habilitou (env é o override forte de operação/CI).

### 4.6 Ícone, estados vazios e microinterações

- **Ícone:** criar `assets/appicon.ico` (marca RemoteOps — nó/sinal em ciano sobre slate) e setar `<ApplicationIcon>` no `.csproj`. **Resolve também o gap de branding do instalador (ADR-019)** e a janela/atalhos passam a ter ícone.
- **Estados vazios:** "Nenhum host neste grupo — adicione um" (HostList), "Nenhuma sessão aberta" (Tabs), "Selecione um host" (Inspector).
- **Microinterações:** hover/foco/selected já vêm dos estilos de controle do tema; garantir que DataGrid/lista de hosts e TreeView da sidebar consomem os estilos.

### 4.7 Estratégia de branch/worktree e entrega

- Trabalho em `C:\dev\remoteops-gui-slate` (branch `feature/gui-slate-signal`, base `packaging`).
- Ao final: `dotnet build` + `dotnet publish -p:PublishProfile=win-x64-velopack` + `vpk pack` → **novo `Setup.exe`** do app redesenhado, sem tocar na branch de release.

### 4.8 Testes e validação

1. `dotnet build "C:\dev\remoteops-gui-slate\RemoteOps.sln" -c Debug` — **verde** (lembrar `TreatWarningsAsErrors=true`).
2. `dotnet test … --no-build` — os **428 testes** existentes seguem passando (nenhuma regressão de lógica).
3. Testes novos onde couber lógica não-trivial: `CompositeFeatureFlags` (precedência env×settings), `JsonSettingsStore` (round-trip), comandos de menu (CanExecute).
4. Smoke manual (checklist): app abre no dark coeso; barra de menus responde; contexto no host conecta/edita/exclui; Configurações abre, alterna flag e persiste após reabrir; estados vazios aparecem; ícone na janela e na barra de tarefas.

## 5. Riscos e mitigações

| Risco | Mitigação |
|---|---|
| Views divergiram (packaging×gui-design) | Reconciliar view-a-view por `diff`; tokens sobre a estrutura do `packaging` |
| Flags graváveis podem quebrar comportamento por env (testes/CI) | `CompositeFeatureFlags` mantém env como override; testes cobrindo precedência |
| `TreatWarningsAsErrors` quebra por XAML/estilo | Build incremental; tratar warnings na hora |
| `vpk pack` sem ícone antes / com ícone depois | Adicionar `.ico` cedo; validar publish |
| Escopo inflar (overhaul) | Manter YAGNI da seção 2 |

## 6. Critérios de sucesso

- App abre com visual dark **coeso** (sem áreas claras remanescentes), controles estilizados.
- Barra de menus + menus de contexto funcionais, ligados a comandos reais.
- Janela de Configurações abre, alterna `rdp.enabled`/`ndesk.enabled` e **persiste** entre execuções.
- Ícone do app presente; estados vazios presentes.
- Build verde, 428 testes verdes, novo `Setup.exe` gerado.
