# 26 — Runbook de teste local (Desktop)

## Objetivo

Guia prático para rodar o `RemoteOps.Desktop` localmente, ligar cada feature flag, saber o que
esperar de cada aba e ter um checklist de smoke test — sem precisar de hosts reais, backend Cloud
ou broker NDesk real. Complementa `docs/06-desktop-ui-ux.md` (especificação de UX) e
`docs/13-plano-testes-qa.md` (plano de testes geral).

## Pré-requisitos

- Windows 10/11.
- .NET SDK 10.0.x (`dotnet --version`). Se não estiver no PATH, instale com
  `dotnet-install.ps1 -Channel 10.0` (veja `AGENTS.md`).
- WebView2 Runtime (normalmente já vem com Windows/Edge atualizados). Sem ele, a aba de
  Terminal degrada com uma mensagem de erro em vez de derrubar o app — ver
  [Limitações conhecidas](#limitações-conhecidas).
- Opcional, só para testar a ação "Abrir WinBox" de ponta a ponta: `winbox64.exe` real e um
  manifesto com `WINBOX_SHA256` correspondente (ver `docs/21-mikrotik-winbox-runner.md`).

## Como rodar

```powershell
dotnet restore
dotnet build RemoteOps.sln -c Debug
dotnet run --project src\RemoteOps.Desktop
```

Ou, depois de compilar uma vez, execute o binário diretamente (mais rápido para iterar e mais
fácil de automatizar smoke test, já que `dotnet run` bloqueia o terminal até a janela fechar):

```powershell
src\RemoteOps.Desktop\bin\Debug\net10.0-windows\RemoteOps.Desktop.exe
```

Primeiro run cria `%APPDATA%\RemoteOps\` (vault DPAPI + banco SQLCipher por workspace, vazio).
Isso é esperado — sidebar e lista de hosts começam vazias; cadastre um grupo/host pela própria UI
para exercitar Inspector e abas de sessão.

## Feature flags

**Atenção:** existem **dois mecanismos diferentes** de flag no Desktop hoje — não são a mesma
variável. Isso não é elegante, mas é o comportamento real do `App.xaml.cs`/`AppCompositionRoot`
atual; ver [Limitações conhecidas](#limitações-conhecidas) para a nota de harmonização futura.

### `REMOTEOPS_FEATURE_FLAGS` (lista separada por vírgula)

Lida por `EnvironmentFeatureFlags` (`Infrastructure/IFeatureFlags.cs`). Sem a variável (ou vazia),
nenhuma flag está ligada.

```powershell
$env:REMOTEOPS_FEATURE_FLAGS = "ndesk.enabled,rdp.enabled"
src\RemoteOps.Desktop\bin\Debug\net10.0-windows\RemoteOps.Desktop.exe
```

Flags conhecidas (`Infrastructure/IFeatureFlags.cs` → `FeatureFlagNames`):

| Flag | Efeito quando ligada | Default |
|---|---|---|
| `rdp.enabled` | Mostra o botão "RDP" no Inspector (quando o host tem endpoint RDP) e habilita `MainViewModel.OnSessionRequested` a abrir uma aba RDP real (MSTSCAX/ActiveX). Desligada: clique em RDP abre só uma aba placeholder. | OFF |
| `ndesk.enabled` | Abre, já na inicialização, uma aba **NDesk** fixa (pinada) com o painel do operador e o painel mock do lado atendido — broker fake in-memory (`LoopbackNDeskBrokerClient`), sem rede. | OFF |

MikroTik/WinBox **não** tem feature flag — o botão "Abrir WinBox" aparece sempre que o host
selecionado tem um endpoint `mikrotik` (ver `docs/21-mikrotik-winbox-runner.md`, `ADR-006`).

### `REMOTEOPS_CLOUD_SYNC_ENABLED` (booleano `"true"`) + config própria

Lido diretamente em `App.xaml.cs::TryBuildSyncOptions`, **não** passa por `REMOTEOPS_FEATURE_FLAGS`
nem por `IFeatureFlags`. Precisa das três variáveis para realmente ligar o sync:

```powershell
$env:REMOTEOPS_CLOUD_SYNC_ENABLED = "true"
$env:REMOTEOPS_CLOUD_URL = "https://localhost:5001"   # exige HTTPS — fail-closed (ADR-013)
$env:REMOTEOPS_CLOUD_WORKSPACE_ID = "ws-local"
```

Sem `REMOTEOPS_CLOUD_SYNC_ENABLED=true`, ou com URL ausente/inválida/não-HTTPS, o app sobe
idêntico ao modo offline — nunca derruba por falta/erro de config de nuvem.

## O que cada aba faz

- **Terminal (SSH/Telnet):** WebView2 + xterm.js falando com `ITerminalSessionProvider` real
  (SSH.NET / TcpClient+Telnet). Abrir requer host com endpoint `ssh`/`telnet` e credencial.
- **RDP:** atrás de `rdp.enabled`. `WindowsFormsHost` hospedando o ActiveX MSTSCAX
  (`AxMsRdpClient9NotSafeForScripting`). Senha resolvida do vault só no momento do connect,
  nunca retida em campo da ViewModel (ADR-009 §FIX-3).
- **MikroTik/WinBox:** não é uma aba de sessão — é o botão "Abrir WinBox" no Inspector, que lança
  o `winbox64.exe` **externo** oficial via `WinBoxRunner` (processo separado, não embutido na
  janela). Ver `docs/21-mikrotik-winbox-runner.md`.
- **NDesk:** atrás de `ndesk.enabled`. Aba fixa com painel do operador (gerar ticket, conectar) e
  painel mock do "lado atendido" (aceitar/recusar consentimento) na mesma janela, para demonstrar
  o fluxo consentido ponta a ponta sem precisar de um segundo processo/máquina. Broker é
  `LoopbackNDeskBrokerClient` (fake in-memory) — o broker real (`src/RemoteOps.NDesk.Broker`,
  ASP.NET Core/SignalR) já existe mas ainda não está plugado no Desktop via DI.

## Limitações conhecidas

- **Dois mecanismos de flag diferentes** (`REMOTEOPS_FEATURE_FLAGS` vs `REMOTEOPS_CLOUD_SYNC_ENABLED`)
  — ver acima. Harmonizar isso é uma mudança de contrato de configuração do módulo de sync, fora do
  escopo desta frente (robustez do shell); registrar como possível débito técnico para o
  `cloud-sync-agent`.
- **WinBox** precisa do executável real + `WINBOX_SHA256` válido (`WINBOX_EXE_PATH`,
  `WINBOX_SHA256`). Sem isso, `WinBoxToolManifest.Validate()` recusa fail-closed e o Inspector
  mostra o erro em texto vermelho — não crasha, mas também não abre nada (comportamento esperado
  em dev).
- **RDP** depende do controle ActiveX MSTSCAX (`mstscax.dll`) estar registrado no Windows —
  normalmente já está em Windows 10/11 com Terminal Services habilitado. Falha de COM
  (`new AxMsRdpClient9NotSafeForScripting()`) é capturada e mostrada como texto de erro na aba,
  não derruba o app.
- **NDesk** é loopback fake — nenhuma rede real, nenhum agente temporário de verdade. Serve para
  exercitar UX/consentimento, não performance/NAT/relay (isso é `docs/22`).
- **Terminal** depende dos assets `Terminal/wwwroot/js/terminal.bundle.js` e `css/xterm.css` já
  compilados e commitados (ver `CONTRIBUTING.md` §Frontend do Terminal). Isso é um invariante de
  build, não algo que o app valida em runtime — se alguém apagar esses arquivos do checkout, a
  aba fica presa em "Conectando..." sem mensagem de erro (WebView2 reporta falha de navegação
  silenciosamente hoje). Não reproduzido nesta frente; registrado aqui para uma futura melhoria de
  UX de erro, não para bloquear esta PR.
- Sem hosts reais cadastrados, os únicos smoke tests possíveis são de **UI/robustez** (abre,
  não crasha, mostra placeholder correto) — não validam os protocolos de verdade contra
  equipamento real.
- **Risco latente da mesma classe do bug corrigido nesta frente, hoje neutralizado:** as colunas
  de `HostListView.xaml` fazem bind de propriedades somente-leitura de `AssetViewModel` (`Name`,
  `PrimaryProtocol`, `PrimaryAddress`, `Vendor`, `Tags`). Isso é inofensivo hoje porque
  `DataGrid.IsReadOnly="True"` impede o `TextBox` de edição de célula (TwoWay por padrão) de ser
  instanciado — mas se essa trava for removida no futuro (ex.: para permitir renomear host inline)
  sem proteção equivalente, um duplo-clique numa célula reproduziria o mesmo crash. Travado como
  invariante verificável em `tests/RemoteOps.UnitTests/Desktop/HostListViewRenderTests.cs`
  (achado do `qa-agent` nesta frente).

## Solução de problemas (startup)

Desde esta frente, `App.xaml.cs` tem uma rede de segurança contra crash silencioso:

- Qualquer falha durante a inicialização (vault/DPAPI, banco SQLCipher, resolução de DI, ou até
  um erro de binding na primeira janela) aparece como uma caixa de mensagem
  **"RemoteOps — Não foi possível iniciar"** com o tipo/mensagem da exceção, em vez do processo
  simplesmente desaparecer.
- Exceções não tratadas na UI thread depois do startup (ex.: um `async void` de evento) aparecem
  como **"RemoteOps — Erro inesperado"**; o app tenta continuar rodando.
- Se mesmo assim o processo sumir sem diálogo nenhum (cenário mais raro — exceção fora da UI
  thread, ou falha nativa tipo access violation), a fonte mais confiável de diagnóstico no Windows
  é o Visualizador de Eventos:

  ```powershell
  Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-5)} |
      Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') } |
      Select-Object TimeCreated, ProviderName, Id, Message |
      Format-List
  ```

  O provider `.NET Runtime` (evento 1026) traz o stack trace completo da exceção não tratada —
  foi assim que se encontrou a causa raiz do bug corrigido nesta frente (ver CHANGELOG.md).

## Checklist de smoke por aba

Passos manuais (ou de uma futura sessão) para considerar o shell "abre limpo" antes de qualquer
mudança de UI. Sem feature flags, repita com `ndesk.enabled`, `rdp.enabled` e as duas juntas.

### Startup

- [ ] `dotnet run --project src\RemoteOps.Desktop` (ou o `.exe` direto) abre a janela principal
      sem diálogo de erro e sem o processo desaparecer.
- [ ] Sidebar, lista de hosts, Inspector e área de abas aparecem vazios/placeholder (sem host
      cadastrado ainda) — nenhuma exceção no Visualizador de Eventos.
- [ ] Status de sync no topo mostra "Offline" (sem `REMOTEOPS_CLOUD_SYNC_ENABLED=true`).
- [ ] Repetir com `REMOTEOPS_FEATURE_FLAGS=ndesk.enabled`, depois `rdp.enabled`, depois
      `ndesk.enabled,rdp.enabled` — mesmo resultado (janela abre limpa) nos quatro casos.
- [ ] Teste negativo do handler de erro: force uma falha de startup de propósito (ex.: revogar
      temporariamente a permissão de escrita em `%APPDATA%\RemoteOps`) e confirme que aparece a
      caixa "RemoteOps — Não foi possível iniciar" em vez do processo sumir; confirme visualmente
      que o texto da mensagem não contém senha/token/connection string.

### Cadastro básico (pré-requisito para os smokes de aba abaixo)

- [ ] Sidebar: "Adicionar grupo" cria um grupo e aparece na árvore.
- [ ] Lista de hosts: "Adicionar host" com o grupo selecionado cria um host.
- [ ] Inspector: selecionar o host mostra "Ações rápidas" (SSH/Telnet sempre visíveis; RDP e
      WinBox condicionais — ver abaixo) e o formulário "Adicionar endpoint".

### Terminal (SSH/Telnet)

- [ ] Adicionar endpoint `ssh` (porta 22) **e**, separadamente, `telnet` (porta 23) ao host — são
      `ITerminalSessionProvider` keyed diferentes, testar como dois casos, não "SSH ou Telnet".
- [ ] Clicar no botão SSH/Telnet do Inspector abre uma aba nova com WebView2 (texto
      "Conectando..." e depois xterm, ou erro de conexão — qualquer um dos dois é OK; o que não
      pode acontecer é o app travar/fechar).
- [ ] Teste negativo: endpoint com porta claramente errada ou host que não resolve DNS — a aba
      mostra erro de conexão sem travar a UI inteira (outras abas continuam clicáveis).
- [ ] Fechar a aba (✕) encerra a sessão sem exceção — testar tanto **depois** de conectada quanto
      **durante** a tentativa de conexão (simetria com o guard que o RDP já tem via `IsLoaded`).
- [ ] (Só se quiser exercitar a degradação de verdade) Em uma máquina sem WebView2 Runtime
      instalado, a aba mostra "Erro ao inicializar terminal: …" em vez de crashar — comportamento
      já coberto por código (`TerminalTabView.InitWebViewAsync`), não precisa desinstalar o
      runtime desta máquina de dev para confirmar.

### MikroTik / WinBox

- [ ] Adicionar endpoint `mikrotik` ao host → botão "Abrir WinBox" aparece no Inspector.
- [ ] Sem `WINBOX_EXE_PATH`/`WINBOX_SHA256` configurados (default local): clicar no botão mostra
      erro em texto vermelho abaixo dos botões de ação ("Manifesto WinBox sem sha256 válido —
      execução bloqueada"), o app continua respondendo normalmente.
- [ ] Trocar de host limpa o erro anterior.
- [ ] Teste negativo específico: com `WINBOX_EXE_PATH` apontando para um executável qualquer
      **existente** mas sem `WINBOX_SHA256` — continua fail-closed idêntico (não tenta abrir
      nada); confirma que o gate é o hash, não só a existência do arquivo.
- [ ] (Observação, não bloqueador) O campo "Adicionar endpoint" não valida formato de
      endereço — algo como `%%%` ou só espaços vira `Fqdn` sem validação (`InspectorViewModel`
      só decide IPv4/IPv6 via `IPAddress.TryParse`; qualquer outra coisa cai no ramo FQDN).
      Confirmar que isso não quebra a UI, só produz um endpoint com endereço estranho.
- [ ] (Só com WinBox real instalado e hash aprovado) Clicar abre o `winbox64.exe` como processo
      separado, com o endereço do host pré-preenchido.

### RDP

- [ ] Sem `rdp.enabled`: adicionar endpoint `rdp` ao host, botão "RDP" **não aparece** no
      Inspector (`CanOpenRdp=false`); nenhum outro efeito colateral.
- [ ] Com `rdp.enabled`: botão "RDP" aparece; clicar abre uma aba nova hospedando o ActiveX
      MSTSCAX. Sem servidor RDP real, espera-se erro de conexão exibido na aba (texto vermelho),
      não crash.
- [ ] Teste negativo explícito: conectar a um endereço inacessível (ex.: `192.0.2.1`, faixa
      TEST-NET-1 não roteável) com `rdp.enabled` ligado. Confirmar **visualmente** que a mensagem
      "Erro ao conectar RDP: …" exibida na aba não contém a senha resolvida do vault nem
      credencial — validação de ausência de segredo aplicada à UI, não só a log.
- [ ] Com `rdp.enabled` OFF, confirmar que SSH/Telnet continuam funcionando normalmente no mesmo
      host (sem efeito colateral cruzado da flag).
- [ ] Fechar a aba durante a tentativa de conexão não deixa sessão órfã (guard de `IsLoaded` já
      coberto por teste/ADR-014).
- [ ] MSTSCAX/COM ausente não é testável numa única máquina de dev (normalmente já vem registrado
      em Windows 10/11) — pendente de uma segunda VM sem Terminal Services habilitado para validar
      de verdade que a falha de COM vira texto de erro em vez de exceção não tratada.

### NDesk

- [ ] Com `ndesk.enabled`: aba "NDesk" aparece **fixa** (pinada, sem X de fechar) já na
      inicialização, mesmo sem nenhuma sessão iniciada — este é exatamente o caminho que
      crashava antes da correção desta frente.
- [ ] Painel "Operador": "Gerar ticket" preenche o campo de ticket; "Conectar" com esse ticket
      simula a chegada de uma sessão.
- [ ] Painel "Lado atendido (mock)": ao conectar, mostra operador/empresa/ticket/modo/permissões
      solicitadas e os botões Aceitar/Recusar.
- [ ] "Aceitar" leva o estado a `Connected` nos dois painéis; banner vermelho "Sessão NDesk em
      andamento" e botão "Encerrar" ficam visíveis no painel do operador.
- [ ] "Encerrar" (de qualquer lado) volta ambos os painéis a `Idle`/sem solicitação pendente, e dá
      para iniciar um novo ciclo (gerar ticket → conectar → aceitar) sem reabrir o app.
- [ ] "Recusar" também retorna a `Idle` sem nunca passar por `Connected` — confirmar que o painel
      do **operador** (não só o lado atendido) reflete a recusa: banner vermelho some, estado
      volta a `Idle` dos dois lados.
- [ ] Teste negativo mais importante desta aba: no painel Operador, digitar um ticket que nunca
      foi gerado (ex.: `"000000"`) e clicar "Conectar" — deve aparecer mensagem de erro
      ("Ticket '000000' não encontrado.") em vez de travar, e nenhuma sessão abre em nenhum dos
      dois painéis.
- [ ] Campo de ticket vazio → botão "Conectar" aparece desabilitado.
- [ ] Conferir que os valores exibidos no painel "lado atendido" batem **exatamente** com o que
      foi usado ao gerar o ticket — é a exigência literal de "consentimento visível" do
      `CLAUDE.md`. Já automatizado em `NDeskTabViewConsentContentTests`; vale conferência visual
      humana pelo menos uma vez.
- [ ] Duplo-clique rápido em "Conectar" não abre duas sessões (o `CanExecute` já deveria impedir;
      é só confirmação visual).

## Matriz Windows/servidores/equipamentos

O que está coberto por esta frente (verificação nesta única máquina de dev, sem lab de rede) vs.
o que fica pendente para uma matriz completa:

| Eixo | Valores a cobrir | Cobertura hoje |
|---|---|---|
| SO Desktop | Windows 11 23H2/24H2, Windows 10 22H2 | Só a máquina de dev atual verificada nesta frente |
| SO "servidor" (RDP alvo) | Windows Server 2019/2022/2025, Windows 10/11 com RDP habilitado | Não exercitado — precisa de host real |
| WebView2 Runtime | presente / ausente | Só "presente" verificado manualmente; "ausente" coberto só por leitura de código |
| MSTSCAX (`mstscax.dll`) | registrado / não registrado | Não exercitado — precisa de VM sem Terminal Services |
| WinBox | instalado com hash aprovado / ausente / hash divergente | Só "ausente" (fail-closed) verificado |
| Equipamento MikroTik | RouterOS v6/v7, portas WinBox custom, IPv6 link-local | Fora de escopo local — precisa de lab de rede |
| SSH/Telnet alvo | Linux OpenSSH, appliance Cisco/MikroTik via Telnet | Fora de escopo local — precisa de host real |

## Critérios de aceite desta frente

- [ ] `dotnet build` da solution: 0 avisos, 0 erros.
- [ ] `dotnet test` da solution: 100% passando, incluindo os testes de render STA.
- [ ] As 4 combinações de `REMOTEOPS_FEATURE_FLAGS` (nenhuma, `ndesk.enabled`, `rdp.enabled`,
      ambas) abrem limpo, sem diálogo de erro e sem o processo desaparecer.
- [ ] Checklist manual por aba (seção acima) executado ao menos uma vez, sem hosts reais.
- [ ] Nenhum segredo em log, commit, fixture, screenshot ou mensagem de erro exibida na UI
      (Terminal/RDP/WinBox/NDesk).
- [ ] `docs/26-runbook-teste-local.md` e `CHANGELOG.md` atualizados no mesmo PR.

## Referências

- `docs/06-desktop-ui-ux.md` — especificação de UX.
- `docs/09-acesso-remoto-ndesk.md` — especificação NDesk (broker, agente, viewer).
- `docs/13-plano-testes-qa.md` — plano de testes geral do produto.
- `docs/21-mikrotik-winbox-runner.md` — WinBox Runner.
- `adr/ADR-011-dependency-injection-desktop.md` — composition root/DI.
- `adr/ADR-014-rdp-hospedagem-activex-e-politicas.md` — RDP.
- `tests/RemoteOps.UnitTests/Desktop/NDesk/NDeskTabViewRenderTests.cs`,
  `NDeskTabViewConsentContentTests.cs`, `tests/RemoteOps.UnitTests/Desktop/HostListViewRenderTests.cs` —
  testes de renderização WPF (thread STA + `Window` real) que fecham os gaps descritos acima.
