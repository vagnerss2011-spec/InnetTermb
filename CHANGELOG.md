# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [Unreleased]

### Alterado

- **Merge de `origin/main` (NDesk #37-#41) em `feature/gui-termius-nav`:** reconcilia a
  navegação Termius (shell `TabControl`, `WorkspaceViewModel`, `SessionLauncher`,
  `BrowserView`/`HostsView`/`KeychainView`/`LogsView`) com o trabalho de NDesk do main. Views
  antigas do shell (`SidebarView`, `HostListView`, `InspectorView`, `TabsView` **não** foi
  removida — continua embutida em `MainWindow.xaml` como a área de abas de sessão) e
  `MainViewModel`/`SidebarViewModel`/`HostListViewModel`/`InspectorViewModel` permanecem
  removidos, conforme decidido nesta frente. `NDeskTabView.xaml` mantém o crash-fix de
  `Mode=OneWay` (#37) e ganha a temática Slate Signal. `AppCompositionRoot` mantém o registro
  de `INDeskBrokerClient`/`LoopbackNDeskBrokerClient` e as demais dependências de NDesk.
  **Débito aceito:** o auto-open de aba NDesk (`ndesk.enabled` → abre `NDeskTabViewModel` no
  startup), antes em `MainViewModel`, não foi re-conectado a `WorkspaceViewModel` nesta
  passagem — `TabsViewModel.OpenNdeskTab` e `FeatureFlagNames.NdeskEnabled` continuam
  disponíveis, só falta o ponto de chamada no novo shell.

### Adicionado

- **NDesk — operador descobre o `sessionId` pelo status do ticket (`ADR-020`):** o
  `GET /ndesk/tickets/{id}` passa a devolver o `sessionId` ao criador do ticket (campo novo,
  opcional, em `contracts/ndesk-ticket.schema.json` e `NDeskTicket`), destravando o fluxo real
  operador↔agente — antes só o agente recebia o `sessionId` no resgate e o operador não tinha
  como entrar no signaling. Endpoint já escopado ao criador (anti-IDOR, `ADR-018`), então o
  campo só chega a quem tem direito. Validado ao vivo: `tools/ndesk-signaling-check` agora
  descobre o `sessionId` por esse endpoint (10/10 checks contra Postgres real).

### Corrigido

- **NDesk Broker — `JoinSession`/`EndSession` do hub liam só o claim `sub`**, mas o middleware
  JWT mapeia `sub` para `ClaimTypes.NameIdentifier` (`MapInboundClaims=true`) — com um JWT real
  o operador era **sempre recusado** no signaling. Passa a ler `NameIdentifier` (com `sub` de
  reserva), igual aos endpoints REST. Bug encontrado ao rodar o broker de verdade (não pego
  pelos testes unitários com fakes que setavam `sub` literal); blindado por 2 testes de
  regressão em `NDeskSignalingHubTests` (com claim mapeado).

### Adicionado

- **NDesk Broker executável + runbook local:** o broker não subia contra um banco novo (sem
  migrations nem `EnsureCreated`, a primeira escrita falhava). `Program.cs` passa a criar o
  schema no startup via `EnsureCreated` (desligável por `NDESK_DB_SKIP_INIT=true`; débito de
  migrations versionadas registrado em `docs/27` e ADR-018). `deploy/docker-compose.dev.yml`
  (Postgres de dev) e `docs/27-executar-broker-local.md` (config, execução e smoke test do
  fluxo ticket→redeem→consent→revoke). Validado de ponta a ponta contra um Postgres real:
  emissão/uso-único/expiração de ticket, anti-IDOR no status, gate de consentimento, revogação;
  e confirmado no banco que o link token só existe como hash SHA-256 (nunca em claro) e que a
  auditoria não contém segredo.
- **`tools/ndesk-signaling-check`** (cross-platform, fora da solution): verificador de
  integração que opera operador+agente contra um broker real e valida o hub SignalR de ponta a
  ponta (relay de SDP/ICE, recusa sem consentimento e após revogação). 9/9 checks executados de
  fato contra um broker com Postgres real.
- **DevOps — pipeline de release (`release.yml`):** novo workflow GitHub Actions, separado do
  `ci.yml`, disparado por push de tag `v*`. Em `windows-latest`: deriva e valida a versão SemVer
  a partir da tag (`VERSIONING.md`), publica o `RemoteOps.Desktop` self-contained (`win-x64`),
  empacota com a Velopack CLI (`vpk pack`) gerando o instalador `Setup.exe`, gera um ZIP
  portátil e expõe o executável avulso, calcula `SHA256SUMS.txt`/`build-manifest.json`, e publica
  os 5 artefatos como assets do GitHub Release e como artefato do workflow. Usa somente
  `GITHUB_TOKEN`; assinatura de binário fica fora de escopo. Consome a config Velopack do projeto
  (ADR-019). `docs/11-devops-github-ci.md` documenta o fluxo completo.
- Auto-update: feed de releases embutido por padrão (env sobrescreve) e feed de política
  opcional (`NoPolicyFeedSource`); pipeline `release.yml` publica por tag `vX.Y.Z`. **Pendente:**
  a baseline `v0.13.0` ainda **não** foi publicada (sem tag local/remota, sem GitHub Release em
  `vagnerss2011-spec/InnetTermb` — Task 4 é ação de usuário, não executada neste workflow); até
  lá, o smoke test do fluxo "Verificar atualizações" no app instalado não pode ser validado.

## [1.2.6] - 2026-07-06

### Corrigido

- **Loop de auto-update (baixava, "instalava", voltava pra versão antiga, sem parar):** o WebView2
  do terminal gravava seus dados na pasta padrão (ao lado do exe, **dentro de `current\`**) e os
  processos `msedgewebview2.exe` mantinham esses arquivos **travados**. O Velopack não conseguia
  trocar `current\` ao aplicar a atualização → o apply falhava, o app reiniciava na versão antiga,
  rechecava o feed e baixava de novo — loop infinito (confirmado ao vivo: `RemoteOpsDesktop-1.2.5-full.nupkg`
  baixado em `packages\`, 12 processos `msedgewebview2.exe`, pasta `current\RemoteOps.Desktop.exe.WebView2`
  de 31 MB). Correção: `TerminalTabView` define `CoreWebView2CreationProperties.UserDataFolder` para
  `%LocalAppData%\RemoteOps\WebView2` (**fora** de `current\`) antes de `EnsureCoreWebView2Async` — o
  `current\` fica livre e a atualização aplica. Guarda de regressão em `WebViewUserDataFolderTests`.
  **Nota de recuperação:** máquinas presas no loop precisam instalar a v1.2.6 uma vez pelo Setup.exe
  (com o app fechado) para quebrar o ciclo; a partir dela o auto-update aplica normalmente.

## [1.2.5] - 2026-07-06

### Corrigido

- **Terminal SSH abria "escuro e travado" (não dava pra digitar):** ao abrir a aba de sessão, o
  xterm.js conectava de verdade (PTY, auth e I/O OK — visto ao vivo num MikroTik CCR1009), mas
  (a) **nunca recebia o foco do teclado** → o operador tinha que clicar dentro pra digitar, e
  (b) o `fitAddon.fit()` inicial corria contra o layout da aba → o terminal ficava sem pintar até
  uma interação forçar o redesenho. Agora o terminal **foca e reajusta automaticamente** quando a
  aba fica ativa: `terminal-init.js` foca após o layout (duplo `requestAnimationFrame`), expõe
  `window.__roActivate` (fit + focus) e refoca no clique/foco da janela; `TerminalTabView` chama
  `__roActivate` no `IsVisibleChanged` (troca de aba) e logo após a conexão iniciar, focando também
  o `WebView2` (WPF). Conectar por duplo-clique no host já funcionava — agora o terminal abre pronto
  pra digitar, sem o clique extra que parecia "reconectar".

## [1.2.4] - 2026-07-06

### Adicionado

- **Instância única (`SingleInstanceGuard`):** abrir o RemoteOps uma 2ª vez agora traz a janela já
  aberta para frente em vez de subir outra cópia. A 2ª instância sinaliza a 1ª (named
  `Mutex` + `EventWaitHandle` em escopo `Local\`) e encerra **antes** de abrir o vault/SqlCipher —
  eliminando a disputa do banco local (`sync-local.db`, `Pooling=False`) que gerava erros confusos
  quando o ícone era clicado mais de uma vez. Lógica sem UI e testável (nomes de mutex/evento
  injetáveis); a ativação da janela roda via `Dispatcher` na 1ª instância.

## [1.2.3] - 2026-07-06

### Corrigido

- **Auto-update nunca funcionava (feed apontava para repo privado):** o app lia o feed do Velopack
  em `github.com/vagnerss2011-spec/InnetTermb`, que é **privado** — sem `REMOTEOPS_UPDATE_FEED_TOKEN`,
  o GitHub devolvia **404** e `CheckForUpdatesAsync` falhava ("Não foi possível verificar
  atualizações agora"), então o check de startup e o botão "Verificar agora" nunca achavam nada.
  Cada versão precisava ser instalada na mão. Corrigido apontando `UpdateFeedConfig.DefaultRepoUrl`
  para o repo **público** `InnetTermb-releases` (só instaladores + feed; o código-fonte continua
  privado), que o `GithubSource` lê anonimamente.

### Alterado

- **`release.yml` publica no feed público quando `secrets.RELEASES_REPO_TOKEN` existe** (PAT
  fine-grained com escrita em `InnetTermb-releases`); sem o secret, publica no repo privado como
  antes (não quebra o release) e o feed público é preenchido por `tools/mirror-release.sh`.
- **Novo `tools/mirror-release.sh`:** espelha os artefatos de feed de uma release do repo privado
  para o público (usado até o `RELEASES_REPO_TOKEN` ser configurado no CI).

## [1.2.2] - 2026-07-06

### Corrigido

- **Editor de host não abria ("Erro inesperado") — impossível criar/editar host:** o `HostEditorDialog`
  declarava `ResizeMode="CanResizeWithGrips"`, valor **inexistente** do enum `ResizeMode` (o correto é
  `CanResizeWithGrip`, singular). Como valor de enum em XAML é string convertida em runtime, o build
  (mesmo com `TreatWarningsAsErrors`) passava e o `EnumConverter` lançava `FormatException` →
  `XamlParseException` dentro de `InitializeComponent()` só na hora de abrir o diálogo.
  `App.OnDispatcherUnhandledException` capturava como "Erro inesperado" e o formulário nunca aparecia,
  então **nenhum host podia ser cadastrado nem editado** — regressão desde a v1.1.2 (quando o editor
  virou redimensionável), presente em v1.1.2, v1.2.0 e v1.2.1. Corrigido para `CanResizeWithGrip`.
- **Guarda de regressão:** novo `HostEditorDialogRenderTests` renderiza o `HostEditorDialog` de verdade
  (thread STA + tema real, mesmo padrão do `NDeskTabViewRenderTests`) e falha se o diálogo voltar a
  lançar no parse/layout — cobrindo a classe de bug (enum/recurso/binding só validado em runtime) que o
  build não pega.

## [1.2.1] - 2026-07-03

### Corrigido

- **Terminal em branco sem erro ("parece só frontend"):** se o bundle do xterm não carregava
  (CSP, cópia truncada no publish, erro de script), o `terminal-init.js` quebrava e a aba ficava
  preta enquanto o SSH conectava por trás — sem eco e sem mensagem. Agora o init roda em IIFE com
  guarda (`typeof Terminal/FitAddon`), mostra o erro no container e avisa o C# (`init_error`); e
  `OnNavigationCompleted` confirma via `ExecuteScriptAsync` que o xterm executou antes de conectar.
- **Erro de conexão SSH agora em pt-BR acionável:** `SshConnectionError` mapeia
  `SshAuthenticationException`/`SshOperationTimeoutException`/`SshConnectionException` para
  mensagens claras (senha incorreta, timeout, negociação/algoritmo). `ConnectWithTofuAsync` passa a
  envolver os **dois** connects (o 2º, onde a autenticação acontece em host novo, não tinha
  tratamento). O segredo nunca aparece na mensagem.
- **WebView2 Runtime ausente na máquina do operador:** vira mensagem acionável (orienta instalar o
  Runtime da Microsoft) em vez de erro genérico — o terminal não abre sem ele.
- **Host key SSH lembrada entre reinícios:** `HostKeyStore` persiste em
  `%AppData%\RemoteOps\known_hosts.json`; o TOFU só pergunta na primeira vez de verdade e a
  detecção de host key alterada funciona entre sessões.

## [1.2.0] - 2026-07-03

### Adicionado

- **Autenticação SSH por chave privada:** nova credencial "SSH key" no Keychain — importar
  arquivo `.pem`/OpenSSH (RSA/ECDSA/Ed25519) **ou** colar o texto, com **passphrase opcional**
  guardada em envelope separado no cofre (`CredentialMetadata.PassphraseEnvelopeId`). `.ppk` do
  PuTTY é detectado com instrução de conversão (não há parser PPK nativo na SSH.NET). O
  `SshSessionProvider` faz **dispatch por `CredentialRef.Type`** — a chave NUNCA vai como senha
  ao servidor; o buffer da chave (byte[]) é zerado após o connect. Toolbar do Keychain ganha
  **Replace key** e **Change passphrase** (só para credenciais de chave). Constantes canônicas
  em `CredentialTypes` (o Type entra no AAD do AES-GCM).
- **Perfil de segurança SSH por endpoint (`EndpointProfile.SshAlgorithmProfile`):** coluna
  **Segurança SSH** no editor de host — **Automático** (defaults permissivos da SSH.NET, que já
  habilitam algoritmos legados; conecta a equipamento antigo) e **Estrito** (`SshAlgorithmPolicy`
  remove KEX/host-key/cifra/HMAC fracos — `group1/14-sha1`, `ssh-rsa`, `*-cbc`, `3des`,
  `hmac-sha1` — para hardening em hosts modernos). Aplicado no `ConnectionInfo` antes do
  `Connect()`.

### Fora de escopo (registrado)

- Keyboard-interactive (alguns MikroTik/Cisco), parser `.ppk` embutido, painel de algoritmos
  ordenável estilo PuTTY, persistência do host-key store (TOFU) — follow-ups.

## [1.1.2] - 2026-07-03

### Adicionado

- **Atualização aplicável pela GUI:** o check em Configurações → Atualização passa a guardar o
  `UpdateCheckResult` e habilitar **"Baixar e instalar"** (`IUpdateService.ApplyUpdateAsync` —
  download + reinício automático via Velopack). Antes NENHUM caminho da GUI chamava o apply: o
  operador via "atualização disponível" e tinha que baixar o `Setup.exe` na mão.
- **Check de atualização no startup:** ao abrir o app (após carregar os hosts, sem bloquear),
  verifica o feed em silêncio e, havendo versão nova, pergunta "Baixar e instalar agora?" —
  "Depois" mantém o caminho manual. `WorkspaceViewModel.CheckForUpdatesQuietAsync` nunca lança
  (ZIP portátil/offline → null).
- **Auditoria de acessos na aba Logs:** `StructuredTerminalAuditSink`/`StructuredWinBoxAuditSink`
  emitem linha legível no `IUiLogSink` (hora, protocolo://host, ação, ator) além do `Trace` —
  a aba Logs deixou de ficar vazia. Sem segredo por construção.

### Corrigido

- **Campo Endereço cortado no editor de host:** a janela de 520px espremia a coluna do endereço
  (~20px). Janela 720px, redimensionável, `MinWidth` no campo e tooltip com exemplos.
- **IPv6 com colchetes quebrava a conexão:** `IPAddress.TryParse` aceita `[2001:db8::1]`, então
  o IPv6 era salvo COM colchetes e o SSH/Telnet falhava. `AddEndpoint` agora normaliza (trim +
  strip de colchetes) antes de classificar Ipv4/Ipv6/Fqdn; FQDN (ex.: `sn.mynetname.net`)
  continua suportado.

## [1.1.1] - 2026-07-02

### Corrigido

- **Conectar deixava de falhar em silêncio (#47):** `SessionLauncher.LaunchAsync` tinha todo
  caminho de falha mudo (host sem endpoint do protocolo → return vazio; endpoint sem credencial
  ou provider ausente → "aba morta" sem conexão; `WinBoxValidationException` morria numa Task
  descartada em `HostsViewModel`). Agora devolve `LaunchResult` com mensagem acionável em pt-BR
  e `HostsViewModel.ConnectAsync` observa o resultado — `LaunchFailed` → `MessageBox` no
  `MainWindow`. Aba morta eliminada.
- **WinBox: configurar o executável exigia reiniciar o app:** o `WinBoxToolManifest` era
  singleton materializado no startup; salvar caminho/hash em Configurações → Ferramentas
  externas não tinha efeito até reiniciar (e o fail-closed do manifesto stale era engolido).
  Novo `FreshManifestWinBoxRunner` (decorator de `IWinBoxRunner`) reconstrói o manifesto
  (Settings → env → default) a cada launch.
- **Editor de host confuso na edição:** o seletor de protocolo ficava preso em "ssh" sem
  seleção visível (parecia "checkboxes soltas"); agora sincroniza protocolo/porta com o
  endpoint salvo e a linha "adicionar endpoint" ganhou rótulos Protocolo/Endereço/Porta/
  Credencial.
- **Aba de terminal em "Conectando…" eterno:** `ConnectAsync` era fire-and-forget na View;
  falhas de conexão (host inacessível, autenticação) e falha de navegação do WebView2 agora
  aparecem como mensagem na própria aba (com retry ao reabrir).

## [1.1.0] - 2026-07-02

### Adicionado

- **Chaveiro (Keychain) com CRUD pela GUI (#44):** criar/editar/trocar senha/excluir credenciais
  login+senha, com os segredos no vault cifrado (envelope AES-256-GCM + DPAPI); a senha entra por
  `PasswordBox`, trafega como `char[]`/`ReadOnlyMemory<char>` até o vault e é zerada (`Array.Clear`)
  após o uso. Rótulos do Keychain em inglês. Novo `ILocalStore.UpdateCredentialRefAsync`
  (InMemory + SqlCipher). Seletor de credencial (opcional) na linha "adicionar endpoint" do editor
  de host, gravando `CredentialRefId`.
- **WinBox configurável pela GUI (#44):** Configurações → Ferramentas externas → **Procurar** o
  `.exe`; o app calcula e fixa o SHA-256 (`HashUtil.Sha256File`) e valida no launch (fail-closed se
  divergir), com **Re-fixar hash**. `WinBoxManifestResolver.Resolve` resolve na precedência
  Configurações → variável de ambiente (`WINBOX_EXE_PATH`/`WINBOX_SHA256`) → caminho padrão.
- **Aba Novidades (changelog) (#45):** changelog curado embutido no binário
  (`Resources/operator-changelog.json`, `EmbeddedResource`, offline), com cartões por versão, chip
  "novo" e badge no avatar controlado por `AppSettings.LastSeenChangelogVersion` (marcado como visto
  ao abrir a aba). Comparação SemVer reaproveita `Update/AppVersion`.
- **Aba Reportar problema (bug report) (#45):** e-mail pré-preenchido (`mailto:suporte@innet.tec.br`,
  `Uri.AbsoluteUri` para preservar o encoding) + cópia local em `%AppData%\RemoteOps\bug-reports\`.
  Diagnósticos secret-free (device id, versão do app/SO, últimas 30 linhas de `LogsViewModel.Events`)
  são **opt-in** com **preview** do texto exato antes de enviar; `Submit` é independente do `Save` do
  modal.

### Segurança

- Segredos nunca na UI, log, e-mail, arquivo ou commit. Diagnósticos só de fontes secret-free; texto
  livre do operador é revisado no preview antes de enviar (e vai a e-mail interno, não público).

## [0.10.0-desktop-smoke-runbook] - 2026-07-01

### Corrigido

- **Crash de startup com `ndesk.enabled` ligada:** `src/RemoteOps.Desktop/NDesk/NDeskTabView.xaml`
  tinha 5 bindings `<Run Text="{Binding ...}">` sem `Mode=OneWay` explícito. `Run.Text` tem
  `BindsTwoWayByDefault=true` no WPF; `PermissionsRequestedText`
  (`NDeskAssistedViewModel.cs`) é uma propriedade somente-leitura, então o WPF lançava
  `System.InvalidOperationException` ao anexar o binding assim que a árvore visual era
  layoutada — mesmo com o painel `Visibility=Collapsed` (WPF anexa bindings independente de
  visibilidade) e sem nenhuma sessão NDesk iniciada. Como isso acontecia dentro de
  `App.OnStartup` (síncrono, antes do dispatcher bombear mensagens), nenhum handler capturava a
  exceção e o processo terminava sem diálogo nenhum (confirmado via Windows Event Log, provider
  `.NET Runtime`, evento 1026). Corrigidas as 5 bindings com `Mode=OneWay` explícito.

### Adicionado

- **`App.xaml.cs` — rede de segurança contra crash silencioso** (defesa em profundidade, além da
  correção da causa raiz acima): try/catch em volta do corpo de `OnStartup` (mensagem amigável via
  `MessageBox` + `Shutdown(1)` em vez de crash cru quando vault/DPAPI/SQLCipher/DI/primeira janela
  falham), `DispatcherUnhandledException` (erros na UI thread depois do startup, ex. um
  `async void` de evento — mostra aviso e deixa o app continuar) e
  `AppDomain.CurrentDomain.UnhandledException` (último recurso, best-effort, para exceções fora da
  UI thread).
- **`docs/26-runbook-teste-local.md`:** como rodar localmente, como ligar cada feature flag —
  inclui nota explícita sobre os **dois mecanismos distintos** hoje no Desktop
  (`REMOTEOPS_FEATURE_FLAGS=ndesk.enabled,rdp.enabled` via `IFeatureFlags`, vs.
  `REMOTEOPS_CLOUD_SYNC_ENABLED=true` + `REMOTEOPS_CLOUD_URL`/`REMOTEOPS_CLOUD_WORKSPACE_ID` lidos
  direto em `App.xaml.cs`, sem passar por `IFeatureFlags`) —, o que cada aba faz, limitações
  conhecidas e um checklist de smoke por aba (terminal, mikrotik/winbox, rdp, ndesk).
- Testes xUnit (`tests/RemoteOps.UnitTests/Desktop/NDesk/NDeskTabViewRenderTests.cs`): renderizam
  `NDeskTabView` de verdade — thread STA manual + `Window` real minúscula (`ShowInTaskbar=false`),
  sem depender de nenhum pacote NuGet novo — reproduzindo o stack trace exato do crash acima antes
  da correção (TDD vermelho→verde) e cobrindo tanto o estado inicial (sem consentimento pendente,
  o cenário que crashava) quanto o painel visível com um consentimento pendente. Técnica extraída
  para `tests/RemoteOps.UnitTests/Desktop/StaThreadRunner.cs` (compartilhada pelos três arquivos
  de teste de renderização abaixo).
- `CompositionRootSmokeTests.Resolve_INDeskBrokerClient`: gap de cobertura notado durante a
  investigação (nenhum teste de resolução DI cobria o broker NDesk).
- **Revisão do `qa-agent`** encontrou um risco latente da mesma classe do bug acima, ainda não
  ativo: as colunas de `HostListView.xaml` fazem bind de propriedades somente-leitura de
  `AssetViewModel` (`Name`, `PrimaryProtocol`, `PrimaryAddress`, `Vendor`, `Tags`) — hoje inofensivo
  porque `DataGrid.IsReadOnly="True"` impede o `TextBox` de edição (TwoWay por padrão) de ser
  instanciado, mas um duplo-clique numa célula reproduziria o mesmo crash se essa trava for
  removida sem proteção equivalente. Fechado com `tests/RemoteOps.UnitTests/Desktop/HostListViewRenderTests.cs`
  (confirma `IsReadOnly` efetivo no grid e em cada coluna + teste de reflexão que falha se alguma
  propriedade ganhar setter) — nenhuma mudança em `src/`, é só uma invariante travada por teste.
- `tests/RemoteOps.UnitTests/Desktop/NDesk/NDeskTabViewConsentContentTests.cs` (`qa-agent`): fecha
  uma lacuna dos testes de render acima — eles provam "não lança exceção", mas não que o
  consentimento continua **visível** (exigência do `CLAUDE.md`). Um caminho de binding errado não
  lançaria nada, só renderizaria vazio silenciosamente. Este teste lê o texto de fato renderizado
  (`TextBlock.Inlines`) e confere que os 5 campos do `NDeskConsentRequest` aparecem tal como o
  broker os forneceu.

### Módulo

- `src/RemoteOps.Desktop/App.xaml.cs`, `src/RemoteOps.Desktop/NDesk/NDeskTabView.xaml` — dono:
  `desktop-shell-agent`

## [0.11.0-packaging-velopack] - 2026-07-01

### Adicionado

- **Empacotamento e atualização do Desktop via Velopack (`adr/ADR-019-empacotamento-atualizacao-velopack.md`, Aceita):**
  - Licença verificada em fonte primária (`LICENSE` oficial de `velopack/velopack`): MIT, sem
    cláusula adicional — mesmo padrão de verificação adversarial já aplicado a RustDesk
    (`ADR-015`, AGPL) e SIPSorcery (`ADR-017`, cláusula BDS não padrão).
  - Modelo: instalador (`Setup.exe`) + versão portátil (zip, sem auto-update) + delta updates +
    atualização sob demanda (nunca baixa nada sem ação/notificação visível) + atualização
    forçada (versão instalada abaixo da mínima exigida bloqueia o uso com prompt obrigatório).
  - `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj`: `PackageReference Velopack 1.2.0`;
    `Properties/PublishProfiles/win-x64-velopack.pubxml` (self-contained win-x64, sem
    single-file — preserva o ganho de banda do delta update).
  - `src/RemoteOps.Desktop/Update/`: `AppVersion`/`UpdatePolicy` (SemVer e gate de atualização
    forçada, lógica pura), `IUpdatePolicyFeedSource`/`HttpUpdatePolicyFeedSource` (versão mínima
    exigida via JSON estático, fail-open em qualquer falha de rede/parse), `IUpdateService`/
    `VelopackUpdateService` (verificação sob demanda + aplicação via `UpdateManager`).
  - `App.xaml.cs`/`RemoteOps.Desktop.csproj`: entry point custom (`App.xaml` de
    `ApplicationDefinition` para `Page` + `<StartupObject>`) com `Main()` chamando
    `VelopackApp.Build().SetArgs(args).Run()` como primeira instrução — padrão oficial do
    Velopack para WPF, confirmado localmente via `vpk pack` (o aviso de entry point não
    reconhecido, emitido quando a chamada estava no construtor de `App`, vira uma verificação
    positiva com o `Main()` explícito). Prompt bloqueante de atualização forçada em
    `OnStartup`, antes de abrir a `MainWindow`, quando a política exige.
  - `AppCompositionRoot.RegisterUpdateService`: registra `IUpdateService` só quando
    `REMOTEOPS_UPDATE_FEED_REPO_URL`/`REMOTEOPS_UPDATE_POLICY_URL` estão configurados —
    fail-open, sem alterar o comportamento do app quando não configurado.
  - `docs/26-empacotamento-atualizacao-velopack.md`: comandos `dotnet publish`/`vpk pack`/`vpk
    upload github`, variáveis de ambiente, feed via GitHub Releases (fonte nativa do Velopack,
    sem servidor próprio), nenhum token em código. Inclui seção "Validação local" com a
    evidência de uma rodada real de `dotnet publish` + `vpk pack` neste projeto (Setup.exe +
    zip portátil gerados de fato, não só documentados).
  - Testes (`tests/RemoteOps.UnitTests/Desktop/Update/`): comparação SemVer, gate de atualização
    forçada, combinação de resultado de checagem, e parsing/fail-open do feed de política —
    lógica pura, sem dependência de instalação real do Velopack.

## [0.10.0-desktop-design-system] - 2026-07-01

### Adicionado

- **Sistema de design do Desktop — tema escuro base, sem toolkit de terceiro
  (`src/RemoteOps.Desktop/Themes/`):**
  - `Themes/Tokens/`: `Colors.xaml` (paleta "Slate Signal" + sobrescrita das chaves
    `SystemColors.*BrushKey` usadas internamente pelos templates padrão do WPF),
    `Typography.xaml` (Segoe UI Variable Text/Segoe UI + Consolas para dados técnicos),
    `Spacing.xaml` (escala de 4px), `Icons.xaml` (glifos Segoe MDL2 Assets — licenciamento
    verificado em duas páginas oficiais da Microsoft Learn, não em memória; ver docs/06
    §Ícones).
  - `Themes/Controls/`: estilos/templates para Button, TextBox, ComboBox, TabControl/TabItem,
    DataGrid, TreeView/TreeViewItem, ScrollBar, Separator, ToolTip.
  - `Themes/DarkTheme.xaml` mesclado em `App.xaml` (único ponto de merge; `App.xaml.cs` não foi
    alterado).
  - Aplicado a `MainWindow.xaml`, às Views do shell (`SidebarView`, `HostListView`,
    `InspectorView`, `TabsView`) e às três Views de aba de sessão (`TerminalTabView`,
    `RdpTabView`, `NDeskTabView`) — só XAML/recursos; nenhum ViewModel ou code-behind alterado,
    nenhum binding removido ou renomeado.
  - Corrige a inconsistência visual existente antes desta mudança (sidebar/lista de
    hosts/inspector claros ao lado de abas de sessão escuras); `HostListView` não tinha nenhum
    plano de fundo definido e herdava o branco padrão do WPF.
  - Indicador de status de sync (barra superior) e de estado de sessão NDesk
    (`NDeskSessionState`, lados operador e atendido) ganham cor semântica via `DataTrigger`
    sobre os bindings que já existiam (`MainViewModel.SyncStatus`, `State`) — sem binding nem
    converter novo.
  - Ícones aplicados às ações rápidas do Inspector (SSH/Telnet/RDP/WinBox), aos selos de
    protocolo das abas de sessão, ao indicador de aba fixada (`SessionTabViewModel.IsPinned`,
    antes só desabilitava o botão de fechar silenciosamente) e ao aviso de erro do WinBox.
  - `docs/06-desktop-ui-ux.md`: nova seção "Sistema de design" — paleta, decisão de ícones (com
    fontes primárias citadas), e o racional para não adotar WPF-UI/MahApps/HandyControl nem o
    `ThemeMode` Fluent nativo do .NET 9/10 (experimental, descartado por ora).

### Restrições respeitadas

- Nenhum ViewModel, code-behind ou contrato alterado — mudança inteiramente em XAML/recursos.
- `App.xaml.cs` não foi tocado (fora do escopo do `desktop-shell-agent` nesta frente).
- Nenhum segredo em log, binding, fixture ou screenshot.
- `TreeView.ItemContainerStyle` (Sidebar) e os `Button.Style`/`TabControl.Style` locais
  pré-existentes (Inspector, TabsView) foram atualizados para `BasedOn="{StaticResource
  {x:Type T}}"` — sem isso, o Style local substituiria por completo o Style implícito do tema em
  vez de estendê-lo, e esses elementos específicos voltariam a renderizar com o chrome claro
  padrão do WPF.

### Módulo

- `src/RemoteOps.Desktop/Themes/` — dono: `desktop-shell-agent`
- Depends-on: (nenhum)

## [0.10.0-spike-ndesk-webrtc-capture] - 2026-07-01

### Adicionado

- **SPIKE-017 — NDesk: stack de transporte WebRTC + captura DXGI para agente .NET (Win10/11):**
  - `docs/spikes/SPIKE-017-ndesk-webrtc-captura-win10.md`: relatório com matriz de decisão (SIPSorcery, `libdatachannel`, Microsoft.MixedReality.WebRTC, `libwebrtc` completo para transporte; Vortice.Windows vs SharpDX para captura; coturn vs eturnal para TURN), fontes primárias citadas e verificação adversarial de licença — inclusive uma verificada diretamente pelo orquestrador (cláusula BDS não padrão na licença do SIPSorcery).
  - `adr/ADR-017-ndesk-stack-transporte-midia.md` (Status "Proposta"): decisão — `libdatachannel` (MPL-2.0) via P/Invoke direto para transporte, `Vortice.Windows` (MIT) para captura DXGI, `coturn` (BSD-3) para TURN self-hosted; complementa `ADR-005` e resolve a lacuna deixada em aberto por `ADR-016`.
  - `tools/spikes/ndesk-webrtc/`: PoC descartável (.NET, fora de `RemoteOps.sln`) **construído e executado de fato** em Windows 11 — captura 1 monitor via DXGI Desktop Duplication (`Vortice.Direct3D11`/`Vortice.DXGI`), codec placeholder (diff de frame + GZip, deliberadamente não H.264/VP8), transporte via `libdatachannel` P/Invoke (bindings escritos internamente contra a API C pública `rtc.h`) em loopback local (2 `PeerConnection`s no mesmo processo, sem broker/signaling), com medição real de latência/FPS/bitrate.
  - `docs/15-pesquisa-e-spikes.md`: adicionada entrada do SPIKE-017 com resultado.

### Segurança

- Verificação adversarial confirmou que o `LICENSE.md` oficial do SIPSorcery contém, além do BSD-3-Clause, uma cláusula adicional não padrão de restrição de campo de uso (geopolítica) — motivo suficiente para desqualificá-lo como transporte WebRTC do NDesk, mesma classe de risco que o AGPL do RustDesk no `SPIKE-016`. O PoC usa um binário nativo de terceiro (`datachannel.dll` via pacote `DataChannelDotnet`) apenas para fins de spike; produção exige buildar `libdatachannel` a partir do código-fonte oficial, conforme controle já registrado em `ADR-015`.

## [0.10.0-ndesk-pivo-win10] - 2026-07-01

### Alterado

- **ADR-016 — NDesk pivota para Windows 10/11 + agente temporário .NET moderno (DOC-ONLY, sem código de produto):**
  - `adr/ADR-016-ndesk-pivo-win10-net.md`: nova ADR (Aceita) — supera `ADR-007`; revisa `ADR-005`/`ADR-015`. Alvo passa a ser exclusivamente Windows 10 (21H2+) e Windows 11; Windows 7/8/8.1 saem de escopo. Agente temporário deixa de ser Win32/C++ nativo e passa a ser .NET moderno, single-file self-contained, permanecendo temporário (sem serviço, sem persistência silenciosa). Captura via DXGI Desktop Duplication, com captura/input abstraídos atrás de interface para roadmap futuro de portabilidade (Linux/PipeWire, macOS/`CGDisplayStream`).
  - `adr/ADR-007-ndesk-agente-legado-win32.md`: status → "Superada pela ADR-016"; arquivo mantido para histórico, aponta para a decisão vigente.
  - `adr/ADR-005-acesso-remoto-webrtc.md`: nova seção "Atualização (ADR-016)" — remove a restrição de Windows 7 e reabre a escolha de stack de transporte (`libwebrtc`/`libdatachannel` nativa vs. stack C# gerenciada), a resolver em `SPIKE-017`/`ADR-017`.
  - `docs/09-acesso-remoto-ndesk.md`, `docs/22-ndesk-performance-legacy-windows.md`: substituídas as seções de captura por versão Win7/Win8.1/Win10-11 e a matriz de plataformas por uma matriz única Windows 10/11, com nota de roadmap cross-platform; critérios de aceite e listas de componentes ajustados para o agente .NET self-contained. Histórico de decisão anterior preservado, marcado como revisto pela `ADR-016`.

### Reavaliado

- **Reavaliação buy-vs-build (`ADR-015`) sob o critério de reversão disparado pela remoção de Windows 7:** RustDesk continua desqualificado por licença AGPL-3.0 + modo oculto configurável; MeshCentral continua desqualificado por consentimento silenciável + Intel AMT out-of-band + instalação padrão como serviço persistente — os três bloqueios são independentes de Windows 7. Conclusão de `ADR-015` ("construir") mantida; apenas a tecnologia do agente pivota de Win32/C++ para .NET. O risco de toolchain VS2026/Windows 7 registrado em `ADR-007`/`ADR-015` deixa de existir (eliminado, não mitigado).

## [0.10.0-ndesk-broker-signaling] - 2026-07-01

### Adicionado

- **NDesk Broker/Signaling — `src/RemoteOps.NDesk.Broker` (ASP.NET Core):** servidor de
  rendezvous/signaling que **nunca transporta mídia** (docs/09 §Broker/Signaling, ADR-018).
  - `NDeskTicketService`: emissão de convite temporário (`POST /ndesk/tickets`, TTL curto,
    padrão 10 min/máx. 30 min), resgate de uso único (`POST /ndesk/tickets/redeem`) e
    expiração lazy. O link token é gerado com `RandomNumberGenerator`, devolvido uma única vez
    na emissão e **nunca persistido em claro** — só o hash SHA-256, no mesmo padrão de
    `RefreshTokenEntity.TokenHash` do `RemoteOps.Cloud`.
  - `NDeskPermissionGrantService`: valida e persiste o consentimento
    (`contracts/ndesk-permission-grant.schema.json`) — nunca concede mais do que o ticket
    solicitou; `IsSessionAuthorizedAsync` é o gate único consultado antes de qualquer troca de
    signaling.
  - `NDeskSignalingHub` (SignalR, `/hubs/ndesk`): `JoinSession`/`SendSignal`/`EndSession`
    repassam envelopes opacos de SDP/ICE entre operador e agente; `SendSignal` recusa
    (`HubException`) quando não há consentimento válido para a sessão, revogação incluída.
  - `NDeskTelemetryService`: grava amostras de `contracts/ndesk-session-telemetry.schema.json`
    por sessão, sem conteúdo de tela/input.
  - `NDeskAuditService`: auditoria sanitizada (convite criado/resgatado, consentimento
    concedido/negado/revogado, sessão encerrada), mesmo padrão de redação de
    `RemoteOps.Cloud.Audit.AuditService`.
  - `adr/ADR-018-ndesk-signaling-api.md`: contrato da API REST + protocolo do hub.
- Testes xUnit (`tests/RemoteOps.UnitTests/NDesk/`): emissão/expiração/single-use de ticket,
  recusa de sessão sem grant (nível de serviço e nível de Hub via fakes de
  `IHubCallerClients`/`HubCallerContext`), e guarda de regressão de que o link token nunca
  aparece em nenhuma mensagem de log (`NoSecretInLogTests`).

### Segurança

- Nenhuma sessão de signaling é alcançável sem passar por três portões em sequência: ticket
  válido (TTL + uso único) → consentimento explícito (subconjunto do solicitado no convite) →
  gate de autorização checado a cada `SendSignal` (não só na entrada da sessão), garantindo que
  uma revogação tenha efeito imediato.
- Débito conhecido registrado em ADR-018: o resgate de ticket não é atômico sob concorrência
  entre múltiplas instâncias do broker (correto para instância única do MVP); e o broker ainda
  não valida `workspaceId ↔ operador` contra uma tabela de memberships (vive só no
  `RemoteOps.Cloud`, não referenciado aqui para não misturar módulos).

## [0.10.0-ndesk-viewer-gui] - 2026-07-01

### Adicionado

- **NDesk Viewer GUI (fake broker) — aba "NDesk" clicável atrás da feature flag `ndesk.enabled` (default OFF):**
  - `INDeskBrokerClient`/`INDeskAgentSession` (`src/RemoteOps.Desktop/NDesk/`): seam estável para o broker real da Frente 3; hoje só existe `LoopbackNDeskBrokerClient`/`LoopbackNDeskAgentSession`, fake in-memory sem rede.
  - Fluxo do operador: gerar/inserir ticket, conectar, ver estado (`Idle→AwaitingConsent→Connected→Ended`), superfície de vídeo placeholder, banner permanente + botão "Encerrar" sempre acessível durante a sessão.
  - Painel mock do lado atendido (`NDeskAssistedViewModel`) demonstrando o fluxo consentido de ponta a ponta na própria GUI: aceitar/recusar o pedido de consentimento.
  - `LoopbackNDeskAgentSession` só alcança `Connected` via `RespondConsentAsync(true)` partindo de `AwaitingConsent` — não existe caminho para pular o consentimento (CLAUDE.md princípio 3).
  - `NDeskTabViewModel` (aba pinada) liga `MainViewModel` a `TabsViewModel.OpenNdeskTab`; DI wiring em `AppCompositionRoot`; `TabsView.xaml` ganha DataTemplate para `NDeskTabViewModel`.
  - Feature flag `ndesk.enabled` (`FeatureFlagNames`), mesmo mecanismo de `rdp.enabled`.

## [0.9.0-spike-ndesk-buy-vs-build] - 2026-06-30

### Adicionado

- **SPIKE-016 — NDesk: comprar solução self-hosted open-source vs construir do zero:**
  - `docs/spikes/SPIKE-016-ndesk-buy-vs-build.md`: relatório com matriz de decisão (RustDesk self-hosted, MeshCentral, Apache Guacamole, construir) por licença, requisitos inegociáveis do `CLAUDE.md`, Windows 7 SP1, NAT/CGNAT, segurança/CVEs, esforço de adaptação e custo operacional, com fontes primárias citadas (LICENSE oficial, NVD, GitHub Security Advisories) e verificação adversarial.
  - `adr/ADR-015-ndesk-buy-vs-build.md`: decisão — confirma o caminho "construir" (`ADR-005`/`ADR-007`); nenhum candidato de compra adotado.

### Alterado

- `adr/ADR-005-acesso-remoto-webrtc.md`: status → confirmada pelo SPIKE-016; nova seção "Atualização" recomendando `libdatachannel`+`coturn`/`eturnal` em vez de embarcar o `libwebrtc` completo do Chromium (incompatível com Windows 7).
- `adr/ADR-007-ndesk-agente-legado-win32.md`: confirmada pelo SPIKE-016; critério de revisão futura ganha o risco de toolchain — Visual Studio 2026 removeu Windows 7 como plataforma de deployment, time depende do VS2022 (suporte mainstream até ~jan/2027).
- `docs/15-pesquisa-e-spikes.md`: adicionada entrada do SPIKE-016 com resultado.

### Segurança

- `security-agent` revisou os quatro candidatos sob a ótica dos princípios do `CLAUDE.md` (consentimento visível, revogação imediata, auditoria, sem modo oculto/persistência silenciosa/bypass/evasão de AV/captura de credenciais) e entregou checklist de 21 controles obrigatórios de produção, incorporado à `ADR-015` e ao relatório do spike — nenhuma implementação foi feita nesta frente (spike de pesquisa/decisão).

## [0.9.0-integration-rdp] - 2026-06-30

### Adicionado

- **INT-RDP — Sessão RDP real (MSTSCAX/ActiveX) em aba viva, atrás da feature flag `rdp.enabled` (default OFF):**
  - `RdpConnectionConfigBuilder`/`RdpConnectionConfig`/`RdpRedirectionPolicy` (`src/RemoteOps.Rdp/`): camada pura de configuração de conexão (host/porta/usuário/redirecionamentos) — sem COM, testável em qualquer CI.
  - `RdpSessionProvider`: resolve endpoint/usuário, audita `SessionOpened`/`SessionClosed` via `IRdpAuditSink`, devolve `SessionHandle`; **nunca toca o vault**.
  - `RdpTabViewModel`/`RdpTabView` (`src/RemoteOps.Desktop/Rdp/`): ciclo de vida da aba; `RdpTabView.xaml.cs` hospeda `AxMSTSCLib.AxMsRdpClient9NotSafeForScripting` via `WindowsFormsHost`, aplica `RdpConnectionConfig` 1:1 em `AdvancedSettings9`.
  - Feature flag `rdp.enabled` (`IFeatureFlags`/`EnvironmentFeatureFlags`, lida de `REMOTEOPS_FEATURE_FLAGS`) gateia visibilidade do botão "Conectar RDP" no Inspector **e** o roteamento em `MainViewModel.OnSessionRequested` — defesa em profundidade.
  - DI wiring em `AppCompositionRoot`; `TabsView.xaml` ganha DataTemplate para `RdpTabViewModel`.
- `adr/ADR-014-rdp-hospedagem-activex-e-politicas.md`: hospedagem ActiveX, lifetime de senha, políticas de redirecionamento (default OFF), NLA obrigatório/certificado, feature flag, e o pivot de empacotamento do interop COM (Decisão 6) com os comandos de regeneração via uma única invocação de `AxImp.exe` (ver ADR-014 Decisão 6 para os comandos exatos).

### Alterado

- ADR-004 status: Proposta inicial → Aceita, com seção "Implementação MVP" deixando explícito que o spike FreeRDP não foi executado nesta frente.
- `docs/08-rdp-terminal-server.md`: nova seção "Status de implementação" cobrindo o que está real vs. pendente (auditoria de certificado, USB redirection, verificação manual end-to-end) e referência aos comandos de regeneração do interop.
- `RemoteOps.Rdp.csproj`/`RemoteOps.Desktop.csproj`: interop MSTSCAX consumido via `<Reference>`/`HintPath` apontando para binários checados em `src/RemoteOps.Rdp/lib/` (`MSTSCLib.dll`, `AxInterop.MSTSCLib.dll`) — **não** `<COMReference>`, que não é suportado por `dotnet build` (`MSB4803`); ver ADR-014. `RemoteOps.Desktop.csproj` ganha `<Using Remove="System.Windows.Forms" />` para resolver ambiguidade `CS0104` (`UserControl`/`Application`) introduzida por `UseWindowsForms=true` + `UseWPF=true` simultâneos.

### Segurança

- Senha RDP resolvida do vault apenas no momento do connect real (`RdpTabViewModel.ResolvePasswordAsync`, dentro de `RdpTabView.InitAndConnectAsync`), nunca retida em campo de ViewModel — mesma mitigação de lifetime mínimo do ADR-009 §FIX-3.
- Redirecionamentos (clipboard/drive/printer/áudio) OFF por padrão (`RdpRedirectionPolicy.Default`); aplicados sempre 1:1 a partir da política resolvida, nunca hardcoded "on". USB redirection permanece sem efeito (gap de MVP de wiring rastreado — `IMsRdpClientAdvancedSettings8` já expõe `RedirectDevices`/`RedirectPOSDevices`, equivalente PnP mais próximo de USB neste controle; `RdpTabView` só ainda não conecta `UsbRedirectionEnabled` a essa propriedade — ver ADR-014 Decisão 3/Decisão 7).
- NLA obrigatório (`EnableCredSspSupport=true`) e `AuthenticationLevel=2`; prompt nativo de certificado inválido nunca suprimido. **Pendência:** auditoria de aceitar/rejeitar certificado (`RdpActions.CertificateAccepted/Rejected`) ainda não emitida — gancho de evento MSTSCAX não confirmado/conectado nesta frente.
- Guards de `IsLoaded` após os `await` de `RdpTabView.InitAndConnectAsync` fecham a sessão corretamente (`RdpTabViewModel.CloseAsync()`) se a aba for fechada em pleno connect, evitando orfanar uma conexão credenciada viva.
- Habilitar `rdp.enabled` em produção requer revisão do `security-agent` (CLAUDE.md §Atualizações de arquitetura v2); verificação manual end-to-end contra host/lab real ainda não realizada (pendência, ver ADR-014).
## [0.9.0-integration-cloud-sync] - 2026-06-30

### Adicionado

- **INT-5 — Cliente de sincronização remoto (Desktop ↔ Cloud), atrás da feature flag `cloud.sync.enabled` (default OFF):**
  - `RemoteOps.Contracts/Sync/`: `PullResponse`, `PushRequest`, `PushResult`, `ConflictDetail` movidos de `RemoteOps.Cloud` (forma JSON inalterada — compatibilidade total da API).
  - `RemoteOps.Sync/Remote/CloudSyncApiClient`: HTTP push/pull/login/refresh sobre `HttpClient` injetável; `Authorization: Bearer` + `X-Device-Id`; refresh automático em 401 + retry único; 409 = `PushResult` de conflito; erros viram `CloudSyncException` sem vazar token.
  - `SyncOrchestrator`: drena outbox → push → trata conflitos/cursor → pull → aplica → avança cursores; estado Offline/Syncing/Synced/Error + contagem de conflitos.
  - `LocalEntitiesChangeApplier`: aplica mudanças puxadas em `local_entities` (idempotente, monotônico via UPSERT `version >=`, sem re-emitir no outbox).
  - `SqliteSyncMetadataStore`: cursores (server + `outbox_cursor`) e `ConflictDetail` em `conflicts`, com migração aditiva/idempotente sobre o schema legado.
  - `VaultTokenStore`: tokens guardados como segredo no vault; apenas o envelopeId no `.tokenref`; rotação revoga o envelope anterior.
  - `SignalRSyncHintChannel` + `SyncSession`: hints `workspace.changed` → pull incremental; laço por intervalo resiliente (sincroniza mesmo sem WebSocket).
  - `adr/ADR-013-cliente-sync-remoto.md`; schemas em `contracts/` (`sync-push-request`, `sync-push-result`, `sync-pull-response`, `conflict-detail`).
  - Testes cross-platform em `tests/RemoteOps.UnitTests/Sync/`: round-trip push/pull, refresh em 401, conflito 409, applier created/updated/deleted, idempotência/monotonicidade, cursores, migração compatível, token store sem segredo, e prova de no-secret-in-log.

### Alterado

- `RemoteOps.Cloud/Sync/SyncModels.cs` removido; Cloud passa a usar os DTOs de `RemoteOps.Contracts.Sync` (sem mudança de forma JSON).
- `RemoteOps.Sync.csproj`: nova dependência `Microsoft.AspNetCore.SignalR.Client` (ADR-010/ADR-013).
- `src/RemoteOps.Desktop/App.xaml.cs`: monta a `SyncSession` atrás da flag e conecta `MainViewModel.SyncStatus` ao orquestrador (Dispatcher na UI thread); descarte no `OnExit`.
- `docs/04-modelo-dados-sync.md` e `docs/10-backend-cloud-sync.md` atualizados (migração + arquitetura do cliente + flag).

### Segurança

- Nenhum token/segredo/patch em log, exceção, fixture ou commit; `CloudSyncException` expõe só o status HTTP.
- Tokens via vault (DPAPI/envelope, ADR-003); `.tokenref` guarda só o envelopeId.
- `SecretEnvelope` nunca sofre auto-merge no cliente (espelha `secret-envelope.no-auto-merge`).
- TLS sempre validado; `X-Device-Id` em toda request; feature flag default OFF (revisão do `security-agent`).
- **Revisão de segurança (security-agent):** `SyncSessionFactory`/Desktop exigem **HTTPS** na URL do Cloud (M-1 — rejeita `http://`, fail-closed), evitando Bearer/refresh token em claro; `TokenSet.ToString()` redatado (L-1 — não expõe tokens); revogação de envelope no `VaultTokenStore` documentada como best-effort (L-3).
- **Revisão adversarial (orquestrador Opus) — hardening de concorrência/perda-de-dados:**
  - `SyncOrchestrator.SyncOnceAsync` agora é **serializado** (`SemaphoreSlim`): o laço por intervalo e o hint SignalR compartilham o mesmo outbox/cursores; sem exclusão mútua, dois ciclos concorrentes faziam read-modify-write não atômico do outbox/server cursor e podiam **pular mudanças locais** ou regredir o server cursor.
  - `SqliteSyncMetadataStore`: gravação de cursor **monotônica** (`MAX(cursor, excluded.cursor)`) — defesa em profundidade contra regressão.
  - `LocalEntitiesChangeApplier` agora **segrega `SecretEnvelope`** também no pull (ignora; nunca aplica no cache `local_entities`), coerente com a política de não auto-merge.
  - `SyncSession.DisposeAsync` descarta o canal de hints **antes** do `CancellationTokenSource` e `OnHintAsync` é blindado contra `ObjectDisposedException` numa corrida de shutdown.
  - +5 testes (serialização, monotonicidade ×2, segregação de SecretEnvelope ×2).

### Módulo

- `src/RemoteOps.Sync/Remote/*` — dono: `cloud-sync-agent`

## [0.9.0-integration-terminal-ui] - 2026-06-30

### Adicionado

- **INT-2 — Aba de terminal real (WebView2 + xterm.js ↔ `ITerminalSessionProvider`):**
  - `Terminal/wwwroot/`: frontend local com xterm.js 5.3.0 + xterm-addon-fit 0.8.0, empacotados via esbuild (sem CDN). Assets em `js/terminal.bundle.js` + `css/xterm.css`.
  - `Terminal/TerminalTabViewModel.cs`: gerencia ciclo de vida de sessão SSH/Telnet (OpenAsync → pump de leitura → CloseAsync) independentemente da View.
  - `Terminal/TerminalTabView.xaml/.cs`: WebView2 + virtual host `https://terminal.local/`. Bridge C#↔JS via PostWebMessageAsString/WebMessageReceived (Base64). CSP `default-src 'none'`, DevTools desabilitados em Release, `AreHostObjectsAllowed=false`.
  - `adr/ADR-012-webview2-xterm-terminal-ui.md`.

### Alterado

- `RemoteOps.Desktop.csproj`: adiciona `Microsoft.Web.WebView2 1.0.2849.39` e os Content items do `wwwroot` (as refs de projeto já vêm do INT-1).
- `MainViewModel`: recebe os provedores SSH/Telnet como **keyed services** (`[FromKeyedServices(RemoteProtocol.Ssh/Telnet)]`, resolvidos pelo `AppCompositionRoot` do INT-1) além do `IWinBoxRunner` (INT-4); cria `TerminalTabViewModel` em `SessionRequested`.
- `TabsViewModel`: `OpenTerminalTab`, `CloseTab` chama `CloseAsync` em tabs terminais.
- `TabsView.xaml`: DataTemplates implícitos por tipo em vez de `ContentTemplate` fixo.
- Os adaptadores de seam (endpoint/credential/security-context/audit/host-key/telnet-consent) reutilizam as implementações do INT-1 já registradas no composition root; as variantes duplicadas trazidas originalmente pelo INT-2 foram removidas na integração.

### Segurança

- Output do terminal: bytes brutos Base64 via bridge — nunca `innerHTML`.
- `IHostKeyConfirmation`: TaskCompletionSource genuíno (FIX 1 / ADR-009).
- `ITelnetConsentProvider`: bloqueia TCP até ack explícito (FIX 2 / ADR-009).

## [0.9.0-integration-mikrotik-desktop] - 2026-06-30

### Adicionado

- `OpenWinBoxCommand` no `InspectorViewModel` (INT-4):
  - Visível apenas quando o asset tem pelo menos um endpoint com protocolo `mikrotik`.
  - Monta `ExternalToolLaunchRequest` a partir do `Asset`/`Endpoint` (IPv4/IPv6/FQDN, porta, credentialRefId).
  - Endereço IPv6 encaminhado com família `"ipv6"` para `WinBoxArgumentBuilder` adicionar colchetes.
  - `IncludePasswordArgument = false` por padrão (Modo A); senha nunca passada automaticamente sem política explícita.
  - `WinBoxValidationException` exibida na UI via propriedade `WinBoxError`/`HasWinBoxError`; nunca propagada silenciosamente.
  - Erro de validação limpo automaticamente ao selecionar outro host.
  - `RequestedBy = "local-user"` (placeholder até autenticação de usuário).
- Botão "Abrir WinBox" em `InspectorView.xaml` (protocolo `mikrotik` → visível; demais → collapsed), com feedback de erro em vermelho abaixo dos botões de ação.
- `tools/winbox/manifest.json` com `sha256: null` — fail-closed em dev; instrução de substituição documentada no campo `_note`.
- 8 novos casos de teste em `InspectorViewModelTests`: sem runner, sem endpoint mikrotik, endpoint mikrotik detectado, WinBoxValidationException → WinBoxError, sucesso limpa erro, request com endereço correto, IPv6 com família correta, troca de asset limpa erro anterior.

### Alterado

- `MainViewModel` aceita `IWinBoxRunner?` opcional e repassa ao `InspectorViewModel`. O `IWinBoxRunner` é resolvido pelo `AppCompositionRoot` (INT-1, `WinBoxRunner.Create` com manifesto por variável de ambiente) e injetado no `MainViewModel` via DI — sem fiação manual em `App.xaml.cs`. A referência a `RemoteOps.MikroTik` no `RemoteOps.Desktop.csproj` já vem do INT-1.

### Segurança

- Senha via argumento bloqueada por padrão (`IncludePasswordArgument = false`) — Modo A conforme ADR-006.
- Auditoria delegada ao `IWinBoxRunner` (nenhum segredo na camada de ViewModel).
- Manifesto sem sha256 válido bloqueia execução (fail-closed) antes de iniciar processo.

## [0.9.0-storage-encrypted] - 2026-06-30

### Adicionado

- `SqlCipherLocalStore : ILocalStore` em `src/RemoteOps.Desktop/Infrastructure/`:
  - Substituição persistente e criptografada do `InMemoryLocalStore` (ADR-008/ADR-003).
  - Tables SQLCipher por workspace no mesmo banco `sync-{workspaceId}.db`: `asset_groups`, `assets`, `endpoints`, `credential_refs`.
  - Toda mutação grava simultaneamente no outbox `local_outbox` via `ISyncClient.PushAsync` com `ClientChangeId` único — pronto para consumo pelo INT-5 (cloud sync).
  - **Metadados apenas**: `SecretEnvelopeId` é referência ao envelope; nenhum segredo persistido.
  - Implementa `GetEndpointAsync` e `GetCredentialRefAsync` (contrato `ILocalStore` estendido pelo INT-1).
- `WorkspaceContext` em `src/RemoteOps.Sync/`: classe pública que agrupa `ISyncClient` + `OpenConnectionAsync()`, expondo acesso ao banco sem vazar `IDbConnectionFactory` (internal) ao Desktop.
- `LocalSyncClientFactory.OpenWorkspaceAsync()`: novo método público que derive a chave uma única vez (via `VaultDbKeyProvider`) e devolve `WorkspaceContext` com sync + conexão reutilizando a mesma chave.

### Alterado

- `src/RemoteOps.Desktop/App.xaml.cs`: `async void OnStartup` inicializa vault (DPAPI + `FileVaultStore`), `LocalSyncClientFactory` e `WorkspaceContext`, cria o `SqlCipherLocalStore` e o injeta — junto com o `CredentialVault` de produção — no `AppCompositionRoot.Build(vault, store)` (integração com ADR-011). O composition root resolve o restante do grafo (adapters de terminal/WinBox, providers keyed, `MainViewModel`).
- `AppCompositionRoot`: novo overload `Build(CredentialVault vault, ILocalStore store)` que registra o vault e o store de produção como instâncias, mantendo `Build()` (in-memory) para os smoke tests. Sub-grafo de vault in-memory só no caminho de teste.
- `RemoteOps.Desktop.csproj`: adicionada referência a `RemoteOps.Sync`.
- `InMemoryLocalStore`: mantida para testes de ViewModel; não é mais usada na produção (`App.xaml.cs`).
- `docs/04-modelo-dados-sync.md`: seção de schema local atualizada para incluir tabelas `asset_groups`, `assets`, `endpoints`, `credential_refs`.
- `DeleteAssetAsync` agora executa os dois DELETEs (`endpoints` e `assets`) dentro de uma única transação SQLite — elimina janela de corrupção em caso de falha entre as duas instruções.
- Exceção lançada pelo `PRAGMA key` em `SqliteConnectionFactory` agora é sanitizada antes de propagar: nova `InvalidOperationException` com código de erro SQLite mas sem `hexKey` na mensagem nem no inner exception — impede vazamento da chave via logs de exceção.
- `LocalSyncClientFactory.OpenWorkspaceAsync` valida `workspaceId` contra `Path.GetInvalidFileNameChars()` antes de qualquer operação de arquivo — previne path traversal.
- `GetAssetsAsync` agora lança `InvalidOperationException` descritiva ao exceder 900 ativos por workspace, evitando erro genérico do SQLite ao superar `SQLITE_LIMIT_VARIABLE_NUMBER`.

### Segurança

- Chave AES-256 do banco derivada uma única vez por `VaultDbKeyProvider` (DPAPI/envelope, ADR-003); `WorkspaceContext` mantém a fábrica em memória sem re-acessar o vault por operação.
- `hexKey` nunca aparece em log, exceção, string de conexão ou commit (ADR-008 regras derivadas); `SqliteConnectionFactory` sanitiza exceção do PRAGMA key para garantir isso mesmo em falhas de abertura.
- Banco ilegível sem a chave do vault — verificado por teste `Db_Is_Unreadable_Without_Key`.
- `Pooling=False` preserva isolamento de chave por workspace (sem reuso de conexão já decifrada entre workspaces).
- Queries todas parametrizadas (`$param`) — sem SQL injection.
- `CredentialRef.SecretEnvelopeId` incluído no outbox patch como referência (não o segredo); `CredentialMetadata` serializada como JSON sem campos de segredo.
- `workspaceId` validado contra path traversal (`../evil`) antes de montar caminhos de arquivo.

### Módulo

- `src/RemoteOps.Desktop/Infrastructure/SqlCipherLocalStore.cs` — dono: `cloud-sync-agent`
- Depends-on: `feature/integration-composition`

## [0.9.0-integration-composition] - 2026-06-30

### Adicionado

- **Composition root com DI** em `App.xaml.cs` via `AppCompositionRoot` (ADR-011): substitui `new InMemoryLocalStore()` manual por `ServiceCollection`/`ServiceProvider`. Shutdown faz `Dispose` do provider.
- **`Microsoft.Extensions.DependencyInjection`** via `PackageReference` 10.0.0 (DI não faz parte do framework WPF, só do ASP.NET Core); project references novas para `RemoteOps.Security`, `RemoteOps.Terminal` e `RemoteOps.MikroTik` adicionadas ao Desktop.
- **Adaptadores em `src/RemoteOps.Desktop/Integration/`:**
  - `LocalStoreEndpointResolver` — resolve `EndpointId` via `ILocalStore.GetEndpointAsync`.
  - `StoreCredentialRefResolver` — resolve `CredentialRefId` via `ILocalStore.GetCredentialRefAsync`.
  - `AppTerminalSecurityContext` — contexto de segurança MVP (`local-user` / hostname); substituível em INT-3.
  - `StructuredTerminalAuditSink` — auditoria de sessões SSH/Telnet em `Trace` sem segredos.
  - `ModalHostKeyConfirmation` — diálogo WPF TOFU assíncrono via `TaskCompletionSource`; destaca `isChanged=true` com ícone de aviso (ADR-009 §FIX-1).
  - `ModalTelnetConsentProvider` — consentimento WPF bloqueante antes de qualquer conexão TCP Telnet (ADR-009 §FIX-2).
  - `StoreWinBoxCredentialResolver` — resolve senha WinBox via vault; `VaultSecret` descartado imediatamente (ADR-009 §FIX-3).
  - `StructuredWinBoxAuditSink` — auditoria WinBox em `Trace` sem senhas.
- **`ILocalStore` estendido** com `GetEndpointAsync(string endpointId)` e `GetCredentialRefAsync(string credentialRefId)`; `InMemoryLocalStore` implementa os novos métodos.
- **Provedores SSH e Telnet registrados** como `ITerminalSessionProvider` com chave de protocolo (keyed services, `AddKeyedSingleton`).
- **`IWinBoxRunner` registrado** via `WinBoxRunner.Create()` com manifesto configurável por variável de ambiente (`WINBOX_EXE_PATH`, `WINBOX_SHA256`).
- **`adr/ADR-011-dependency-injection-desktop.md`** — documenta adoção de DI, regras de uso e alternativas consideradas.
- **`CompositionRootSmokeTests`** em `tests/RemoteOps.UnitTests/Desktop/`: 16 testes verificando resolução completa do grafo sem abrir sessão real.
- **`IntegrationAdapterTests`** — testes unitários para os dois novos métodos do `ILocalStore` e caminhos de erro dos adaptadores.
- `InternalsVisibleTo("RemoteOps.UnitTests")` no Desktop para acesso a `AppCompositionRoot` nos testes.

### Segurança

- Segredos nunca registrados como instâncias no container; credenciais só via `IVault`.
- `StructuredTerminalAuditSink` e `StructuredWinBoxAuditSink` auditam sem segredo: `TerminalAuditEvent` e `AuditEvent` excluem campos de senha por construção.
- `ModalHostKeyConfirmation` usa `TaskCompletionSource` assíncrono para evitar deadlock no thread de conexão SSH (ADR-009 §FIX-1).
- `ModalTelnetConsentProvider` bloqueia conexão TCP até ack explícito do usuário (ADR-009 §FIX-2).
- `StoreWinBoxCredentialResolver` usa `using var secret` (lifetime mínimo) ao revelar o vault secret (ADR-009 §FIX-3).
- Nenhuma sessão remota aberta nesta frente (INT-2 pendente).

## [0.8.0-mikrotik-winbox-v2] - 2026-06-30

### Adicionado

- Re-integração do WinBox Runner na estrutura canônica do repositório (branch `feature/mikrotik-winbox-v2`):
  - `WinBoxRunner : IWinBoxRunner` — implementa `LaunchAsync(ExternalToolLaunchRequest, CancellationToken)` com `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`.
  - `WinBoxToolManifest` — valida SHA-256 do executável; fail-closed quando sha256 ausente, inválido ou placeholder (nunca `NullReferenceException`).
  - `WinBoxArgumentBuilder` — monta argumentos posicionais IPv4/IPv6 sem `ArgumentList.Add(string.Empty)`.
  - `WinBoxPolicy` / `LocalWinBoxPolicyProvider` — política com deny real por workspace/host; `PasswordArgumentAllowed=false` por padrão (Modo A).
  - `IWinBoxAuditSink` / `IWinBoxCredentialResolver` / `IWinBoxProcessLauncher` — interfaces injetáveis para produção e testes.
  - Eventos de auditoria: `winbox_tool_validated`, `winbox_open_requested`, `winbox_open_started`, `winbox_open_failed`, `winbox_password_argument_used`, `winbox_ipv6_target_used`; nenhum com segredo.
- Testes em `tests/RemoteOps.UnitTests/MikroTik/`:
  - `WinBoxArgumentBuilderTests` — IPv4/IPv6 global/link-local, porta, sem `argv` vazio, senha vazia/espaços/policy-deny.
  - `WinBoxRunnerTests` — manifesto sem sha256, manifesto placeholder, policy deny (host/workspace/senha), RoMON recusado, IPv6 audit event, no-password-in-audit-events.

### Alterado

- `adr/ADR-006-mikrotik-winbox-externo.md` — documentadas decisões de deferimento (workspace posicional e RoMON não confirmados contra CLI oficial), risco de exposição de senha em tabela de processos e controles implementados. Sign-off do `security-agent` pendente para merge.

### Segurança

- **FIX 1 — sem argv vazio**: senha só é adicionada quando `!string.IsNullOrEmpty(password)` AND login presente AND política permite; nunca há placeholder `""` nos argumentos.
- **FIX 2 — RoMON deferido**: `Romon.Enabled=true` é recusado com `WinBoxValidationException` auditada (`reason=romon_not_confirmed_official_cli`) até validação da sintaxe oficial.
- **FIX 3 — manifesto fail-closed**: sha256 nulo, vazio ou com menos de 64 hex chars → exceção de validação + evento auditado; nunca `NullReferenceException`.
- **FIX 4 — policy deny real**: `LocalWinBoxPolicyProvider` nega por workspace/host; `IncludePasswordArgument=true` sem `PasswordArgumentAllowed` na política lança exceção explícita e auditada.
- Senha via argumento de processo documentada como risco na ADR-006 (visível na tabela de processos local); desativada por padrão; Modo B requer habilitação explícita por política de workspace.

## [0.7.3-terminal-ssh-telnet] - 2026-06-30

### Adicionado

- Adaptadores SSH e Telnet re-integrados na estrutura canônica (`src/RemoteOps.Terminal`, Opção A):
  - `SshSessionProvider` (SSH.NET 2024.x, `Renci.SshNet`): `Protocol`, `OpenAsync`, `CloseAsync`,
    `WriteAsync`, `ReadAsync`, `ResizeAsync` conforme `ITerminalSessionProvider`.
  - `TelnetSessionProvider` (TcpClient + IAC state-machine própria): mesma interface.
- Interfaces públicas novas: `IEndpointResolver`, `ICredentialRefResolver`, `IHostKeyConfirmation`,
  `ITelnetConsentProvider`, `ITerminalAuditSink`, `ITerminalSecurityContext`.
- `TerminalAuditEvent` + `TerminalActions`: auditoria de sessão sem conteúdo de terminal.
- `TelnetNegotiator`: parser RFC 854/855 (IAC/WILL/WONT/DO/DONT/SB, ECHO, SGA, NAWS).
- `HostKeyStore`: cache em memória de host keys TOFU por sessão de provider.
- Testes unitários em `tests/RemoteOps.UnitTests/Terminal/`: 12 casos cobrindo protocol,
  OpenAsync, TOFU bloqueante, consentimento Telnet, resize, round-trip e auditoria sem segredo.
- `adr/ADR-009-ssh-telnet-libs-e-credenciais.md`.

### Segurança

- **FIX 1 — TOFU assíncrono:** callback `HostKeyReceived` é síncrono (captura fingerprint e
  rejeita); `ConfirmAsync` genuinamente assíncrono acontece **fora** do callback, sem
  `.GetAwaiter().GetResult()`. Evita deadlock com UI thread do WebView2.
- **FIX 2 — Consentimento Telnet bloqueante:** `ITelnetConsentProvider.RequestConsentAsync`
  deve resolver via `TaskCompletionSource` da UI; a conexão TCP não é aberta até ack explícito.
  Telnet desabilitado por padrão.
- **FIX 3 — Higiene de senha:** `VaultSecret` descartado imediatamente após autenticação.
  Limitação de Renci.SshNet (`PasswordAuthenticationMethod` exige `string`) documentada no
  ADR-008 §FIX-3 com mitigantes adotados. Nenhum log/fixture/evento de auditoria contém senha.
- **FIX 5 — Auditoria de host key alterada:** `terminal.hostkey.changed` emitido com fingerprint
  **antes** de perguntar ao usuário. `terminal.hostkey.accepted/rejected` auditados.
- **FIX 6 — `"default-group"` removido:** grupo vem do `ITerminalSecurityContext`; pendente issue
  para conectar ao RBAC real (referenciada no ADR-008 §FIX-6).

### Restrições respeitadas

- Estrutura canônica preservada: root `RemoteOps.sln`, `src/RemoteOps.Contracts` inalterado.
- Sem redefinição de `IRemoteSessionProvider`, `SessionRequest`, `SessionHandle`, `RemoteProtocol`.
- Sem segredo em log, fixture ou commit.
- `RemoteOps.Terminal` (`net10.0` cross-platform) não referencia nada Windows-specific.

## [0.7.2-cloud-backend] - 2026-06-30

### Adicionado

- `src/RemoteOps.Cloud`: backend evoluído de `GET /health` para servidor completo com auth, RBAC, sync e auditoria.
  - **EF Core + Npgsql**: `AppDbContext` com 13 entidades (tenants, workspaces, users, memberships, asset_groups, assets, endpoints, credential_refs, secret_envelopes, changelog, audit_events, devices, refresh_tokens) e migrations pendentes de aplicação.
  - **Auth JWT**: `POST /auth/login`, `POST /auth/refresh`, `POST /auth/logout`. Tokens emitidos com PBKDF2-SHA256 (310k iterações). Refresh token armazenado como hash SHA-256. Chave de assinatura JWT via variável de ambiente `Jwt__SigningKey`.
  - **RBAC server-side**: `PermissionEvaluator` avalia 8 etapas (usuário ativo → device → workspace → role → membro → grupo → aprovação). Negação explícita vence herança. 10 papéis padrão com permissões granulares de `docs/18`.
  - **Sync pull/push**: `GET /sync/pull?workspaceId=&cursor=` (paginado, cursor por `changelog.id`); `POST /sync/push` (conflito por `BaseVersion`, idempotência por `ClientChangeId`, SecretEnvelope nunca merge automático).
  - **SignalR**: `SyncHub` em `/hubs/sync` emite hint `workspace.changed` com `workspaceId`, `cursor`, `entityType`, `entityId`. Broadcast escopado ao grupo do workspace. Sem payload completo (ADR-002).
  - **Auditoria**: `AuditService` persiste `AuditEvent` (tipo canônico de `RemoteOps.Contracts.Audit`) em toda ação sensível. `Metadata` sanitizado — chaves com "password", "secret", "token", "key", "hash" são `[REDACTED]`.
  - **ProblemDetails**: `CloudExceptionHandler` + `CorrelationIdMiddleware`. Todos os erros retornam `application/problem+json` com `correlationId`. Sem stack trace em produção.
- `adr/ADR-010-backend-ef-npgsql-signalr.md`: ADR justificando EF Core, Npgsql, JWT Bearer e SignalR (pré-requisito obrigatório de CLAUDE.md).
- Testes em `tests/RemoteOps.UnitTests/Cloud/`: `RbacTests` (11 cenários — allow/deny, negação explícita, device/workspace/membership/cross-tenant), `SyncTests` (pull paginado, push ok/conflito/idempotente, SecretEnvelope bloqueado), `AuditTests` (gravação, sanitização de segredos, mapeamento para contrato canônico).

### Segurança

- Servidor **nunca descriptografa segredos**: `SecretEnvelopeEntity` armazena apenas `ciphertext`, `nonce`, `tag`, `algorithm`, `keyVersion` — sem WDK, CEK ou plaintext. Conforme ADR-003.
- Senha/chave JWT nunca em `appsettings*.json` — obrigatoriamente via variável de ambiente ou secret store.
- Refresh token armazenado como `SHA-256(valor)` — vazamento do banco não permite uso do token.
- Auditoria registra toda ação sensível (login, push, grant/revoke); `Metadata` com sanitização defensiva de palavras-chave sensíveis.
- `AuditService.SanitizeMetadata` bloqueia chaves contendo "password", "secret", "token", "key", "credential", "plaintext", "hash" mesmo se o chamador cometer o erro de incluí-las.
- `SyncService` rejeita push de `SecretEnvelope` com `secret-envelope.no-auto-merge`.

### Correções de segurança (pós security-review)

- **[HIGH] `TokenService.RefreshAsync`**: adicionada verificação de status do device antes de emitir novo JWT. Device revogado bloqueia o refresh imediatamente e revoga o refresh token em cascade. Antes, a revogação do device não interrompia refresh tokens existentes (até 30 dias de validade).
- **[MEDIUM] `SyncHub.JoinWorkspace`**: adicionada verificação de membership antes de adicionar o cliente ao grupo SignalR. Antes, qualquer usuário autenticado podia assinar hints de workspaces aos quais não pertencia.
- **[MEDIUM] `SyncEndpoints`**: `X-Device-Id` header passou a ser **obrigatório** em `GET /sync/pull` e `POST /sync/push` (retorna 400 se ausente). Garante que a verificação de device revocation no `PermissionEvaluator` seja sempre executada, sem possibilidade de bypass por omissão do header.

## [0.7.1-sync-local] - 2026-06-30

### Adicionado

- `LocalSyncClient` em `src/RemoteOps.Sync`: implementação completa de `ISyncClient` sobre SQLite/SQLCipher local.
  - `PushAsync`: grava lote no outbox local (`local_outbox`), idempotente por `ClientChangeId` via `INSERT OR IGNORE`.
  - `PullAsync(fromCursor, limit)`: lê o outbox paginado a partir do cursor, ordenado por `id ASC`; atualiza `CurrentCursor`.
  - Schema local: `local_outbox`, `local_entities`, `sync_cursor`, `conflicts` (conforme `docs/04`).
  - Índice em `(entity_id, entity_type)` para lookup de entidades.
- `LocalSyncClientFactory`: cria instâncias de `LocalSyncClient` com chave do banco protegida pelo vault.
- `VaultDbKeyProvider` (`Storage/`): obtém/cria chave AES-256 do banco via `ICredentialVault` (DPAPI/envelope, ADR-003); persiste apenas o `envelopeId` no arquivo `.keyref`, nunca o material de chave em claro.
- `SqliteConnectionFactory` (`Storage/`): abre conexão SQLCipher via `PRAGMA key = "x'hexbytes'"` como primeiro comando (bytes raw, sem PBKDF2).
- `IDbConnectionFactory` e `IDbKeyProvider`: abstrações internas que permitem substituição em testes.
- Suíte de testes `tests/RemoteOps.UnitTests/Sync/`: round-trip Push→Pull, idempotência por `ClientChangeId`, cursor/paginação monotônica, criptografia do banco (DB ilegível sem chave do vault) e ausência de segredo em logs.
- `ADR-008-sqlite-local-sync-storage.md`: documenta a escolha de SQLite/SQLCipher via NuGet, derivação de chave, fallback e regras.
- `docs/04-modelo-dados-sync.md`: seção de schema local adicionada com DDL completo e descrição do cursor monotônico.

### Segurança

- A chave AES-256 do banco local nunca é persistida em claro: fica protegida pelo vault (DPAPI/envelope) e referenciada por `envelopeId` no arquivo `.keyref`.
- `PRAGMA key` usa bytes raw (`x'...'`), evitando PBKDF2 desnecessário.
- Nenhum material de chave, plaintext ou patch sensível aparece em log, exceção, fixture ou commit.
- Teste `Encrypted_Db_Is_Unreadable_Without_Key` verifica fisicamente que o arquivo `.db` é ilegível sem a chave do vault (requer SQLCipher presente; skip automático caso contrário).
- Teste `KeyRef_File_Contains_Only_EnvelopeId_Not_Secret` garante que o `.keyref` nunca contém os 64 hex chars da chave do banco.

### Módulo

- `src/RemoteOps.Sync` — dono: `cloud-sync-agent`
- Depends-on: `feature/contracts-skeleton`, `feature/security-vault`
## [0.7.0-desktop-shell] - 2026-06-30

### Corrigido

- Endpoint com endereço **IPv6** agora é gravado no campo `Ipv6` (antes ia para `Ipv4`, pois `IPAddress.TryParse` aceita literais IPv6); detecção por `AddressFamily`.
- Endpoint recém-adicionado **reflete imediatamente** no `AssetViewModel`/DataGrid via novo `ILocalStore.GetAssetAsync` + `AssetViewModel.Refresh` (antes só aparecia após reload).
- Teste `AddEndpoint_StoresEndpoint` fortalecido (verifica persistência e campos, não só limpeza do input) e novos casos para IPv6 e FQDN.

### Adicionado

- Shell WPF/MVVM inicial em `src/RemoteOps.Desktop/`:
  - **Janela principal** com 4 regiões redimensionáveis (GridSplitter): sidebar de grupos, lista de hosts, área de abas de sessão, inspector.
  - **Domain:** `AssetGroup` (grupo local), `AddAssetRequest`.
  - **Infrastructure:** `ILocalStore` + `InMemoryLocalStore` — CRUD de grupo/host/endpoint/credentialRef em memória; sem segredo.
  - **ViewModels:** `BaseViewModel` (INotifyPropertyChanged), `RelayCommand` (ICommand), `SidebarViewModel`, `AssetGroupViewModel`, `HostListViewModel`, `AssetViewModel`, `InspectorViewModel`, `TabsViewModel`, `SessionTabViewModel`, `MainViewModel` (mediador).
  - **Views (UserControls):** `SidebarView` (árvore de grupos), `HostListView` (DataGrid de hosts com filtro e CRUD), `InspectorView` (detalhes + adicionar endpoint + ações rápidas SSH/Telnet/RDP), `TabsView` (TabControl de sessões placeholder).
  - `App.xaml.cs` instancia `InMemoryLocalStore` + `MainViewModel` sem DI externo.
- Testes de ViewModel em `tests/RemoteOps.UnitTests/Desktop/`:
  - `SidebarViewModelTests`, `HostListViewModelTests`, `InspectorViewModelTests`, `TabsViewModelTests`, `MainViewModelTests`.
  - Projeto de testes migrado para `net10.0-windows` (necessário para referenciar projeto WPF; DPAPI tests já têm guard `OperatingSystem.IsWindows()`).

### Restrições respeitadas

- Nenhuma dependência de protocolo real; usa `IRemoteSessionProvider` apenas como interface de contratos.
- Nenhum segredo em log ou UI; credencial exibe apenas nome e metadata (nunca senha).
- Sem nova dependência NuGet externa (sem ADR necessária).

## [0.6.0-orchestration-fix] - 2026-06-30

### Alterado

- `merge-guard` (`.github/workflows/automerge.yml`) reconhece dependências mergeadas via **squash** (consulta PR mergeado em vez de ancestralidade) — corrige falso-negativo que bloqueava PRs com `Depends-on:` mesmo com a dependência já em `main`.
- **Auto-merge desligado**: o merge passa a ser **manual, feito pelo orquestrador**, com CI verde + revisão. O workflow foi renomeado de `automerge` para `merge-guard`.
- `docs/24-orquestracao-multiagente-paralela.md` e `CONTRIBUTING.md` atualizados para o fluxo de merge manual.

### Segurança

- Reduz risco de `main` quebrada por merge automático prematuro — auto-merge em CI verde já havia mergeado o vault (#8) antes das correções de segurança/build, exigindo o PR de remediação #11.

## [0.5.0-security-vault] - 2026-06-29

### Adicionado

- Camada de cofre de credenciais em `src/RemoteOps.Security` com envelope encryption por workspace e proteção da chave local por DPAPI no Windows (ver `docs/25-credential-vault.md`).
  - `Vault/`: `IVault`/`CredentialVault` (API rica: store/retrieve/rotate/revoke com `ReadOnlyMemory<char>` e contexto de auditoria), `SecretEnvelope`, `VaultSecret` (IDisposable que zera o buffer, `ToString()` redigido), `VaultModels`, `VaultException`.
  - `Crypto/`: `EnvelopeCipher` (CEK por segredo em AES-256-GCM, embrulhada pela Workspace Data Key; AAD ligando envelope/workspace/versão), `IWorkspaceKeyRing`/`WorkspaceKeyRing`, `WorkspaceKey`, `ILocalKeyProtector`, `DpapiKeyProtector` (P/Invoke a `crypt32.dll`, escopo CurrentUser, sem NuGet externo).
  - `Storage/`: `ICredentialStore`, `IWorkspaceKeyStore`, `InMemoryStores`, `FileVaultStore`.
  - `Audit/`: `IVaultAuditSink`, `VaultAuditEvent`, `InMemoryVaultAuditSink` — auditoria estruturada sem segredo.
- Suíte de testes `tests/RemoteOps.UnitTests/Security/`: round-trip, ausência de plaintext, detecção de adulteração (AEAD), persistência após restart, isolamento usuário/máquina, rotação/revogação, auditoria sem segredo e DPAPI real (Windows).

### Alterado

- `src/RemoteOps.Security/ICredentialVault.cs`: removido TODO; documentado que o contrato fino é implementado por `CredentialVault` (assinaturas inalteradas — sem mudança de contrato público).
- `adr/ADR-003-credenciais-e2ee.md`: status `Proposta inicial` → `Aceita`; adicionada seção de implementação (hierarquia de chaves, AAD, DPAPI, rotação/revogação, alternativas).
- `docs/13-plano-testes-qa.md`: registrado o plano de testes do cofre na seção de Segurança.

### Segurança

- Nenhum segredo, senha ou chave privada em texto puro: plaintext só vive dentro de `VaultSecret` (zerado no `Dispose`); buffers transitórios alugados de `ArrayPool` e zerados no `finally`.
- WDK nunca persistida em claro — apenas blob protegido por DPAPI (CurrentUser + entropia por workspace) → cache local não abre em outro usuário/máquina.
- AAD impede troca/replay de envelope entre workspaces, downgrade de versão e troca de `type` (campo `type` autenticado no AAD).
- Auditoria, exceções e `ToString()` não contêm segredo (inclui `WorkspaceKey`); erros DPAPI expõem apenas o código Win32.
- Hardening da revisão de segurança (security-agent): `plaintext`/WDK zerados também nos caminhos de exceção; `IVaultAuditSink` obrigatório (sem default silencioso); rotação emite `credential.revoke` do envelope antigo; testes de adulteração de AAD e de apagamento no tombstone.

## [0.4.0-skeleton] - 2026-06-29

### Adicionado

- `RemoteOps.sln` na raiz — solution .NET 10 SDK-style com 9 projetos.
- `Directory.Build.props` com `Nullable=enable`, `LangVersion=latest`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`.
- `.editorconfig` com estilo de código C#, JSON, YAML e shell.
- `src/RemoteOps.Contracts` (classlib net10.0): POCOs imutáveis gerados a partir de `contracts/*.schema.json` e `docs/17` — `SessionRequest`, `SessionHandle`, `SyncChange`, `Asset`, `Endpoint`, `CredentialRef`, `AuditEvent`, `NDeskTicket`, `NDeskPermissionGrant`, `NDeskSessionTelemetry`, `ExternalToolLaunchRequest`. Interface `IRemoteSessionProvider` conforme `docs/02`.
- `src/RemoteOps.Security` (classlib net10.0): stub `ICredentialVault` com TODO.
- `src/RemoteOps.Terminal` (classlib net10.0): stub `ITerminalSessionProvider` com TODO.
- `src/RemoteOps.MikroTik` (classlib net10.0): stubs `IMikroTikSessionProvider` e `IWinBoxRunner` com TODO.
- `src/RemoteOps.Sync` (classlib net10.0): stub `ISyncClient` com TODO.
- `src/RemoteOps.Desktop` (WPF net10.0-windows): janela vazia compilável.
- `src/RemoteOps.Rdp` (classlib net10.0-windows): stub `IRdpSessionProvider` com TODO.
- `src/RemoteOps.Cloud` (ASP.NET Core net10.0): app mínimo com endpoint `GET /health`.
- `src/deferred/RemoteOps.NDesk.Viewer` e `RemoteOps.NDesk.Relay`: stubs marcados como deferred, fora da solution, à espera das frentes feature/ndesk-*.
- `tests/RemoteOps.UnitTests` (xUnit net10.0): 13 smoke tests cobrindo todos os projetos cross-platform.

### Alterado

- `.github/workflows/ci.yml`: removidos guards `if (Test-Path *.sln)` do job `dotnet` — build, test e format passam a rodar de verdade.

### Segurança

- Nenhum segredo, senha ou chave privada adicionado.
- `ICredentialVault` deixa explícito que nunca expõe segredo em logs; `CredentialRef.SecretEnvelopeId` documenta que só a referência ao envelope é armazenada nos POCOs.

## [0.3.0-planning] - 2026-06-29

### Adicionado

- Modelo de orquestração multiagente paralela: `docs/24-orquestracao-multiagente-paralela.md` com frentes (worktree por módulo), agentes donos, ondas de execução e ordem de merge.
- Scripts `tools/dev/worktrees.sh` e `tools/dev/worktrees.ps1` para criar/remover worktrees por frente.
- `CONTRIBUTING.md` com fluxo de frentes, convenção `Depends-on:`, Definition of Done e settings/hook recomendados.
- Hooks de sessão em `.claude/hooks/` (`session-start.sh` e `block-destructive.sh`).
- Workflow `.github/workflows/automerge.yml` com `merge-guard` (valida `Depends-on:`) e auto-merge em CI verde.

### Alterado

- Subagentes em `.claude/agents/` passam a ter escrita habilitada (`Edit, Write`) para atuarem como donos de frentes.
- `.github/workflows/ci.yml` reforçado com jobs `secret-scan` e `security-gate` (label `security-reviewed` para pastas sensíveis) e checagem de changelog mais flexível.

### Segurança

- Auto-merge total só é habilitado com o CI como portão real: secret scan, gate de revisão de segurança em pastas sensíveis e guarda de ordem de dependência.
- Hook bloqueia comandos destrutivos (`rm -rf` em raiz/home/wildcard, force-push em `main`, remoção recursiva forçada).

## [0.2.0-planning] - 2026-06-29

### Adicionado

- Decisão de tratar MikroTik via WinBox oficial externo no MVP.
- Documento `docs/21-mikrotik-winbox-runner.md` com runner, argumentos, riscos e critérios de aceite.
- Decisão de criar agente temporário NDesk Win32 nativo para Windows 7/10 sem Java, WebView2 ou .NET moderno.
- Documento `docs/22-ndesk-performance-legacy-windows.md` com NAT, relay, conexão lenta, codec adaptativo e modos de permissão.
- Documento `docs/23-governanca-versionamento-changelog.md` para changelog, versionamento, branches, PRs e releases.
- ADRs 006 e 007.
- Contratos de lançamento de ferramenta externa, concessão de permissão NDesk e telemetria de sessão.
- Prompts de sprint para WinBox Runner, NDesk legado/performance e governança de release.
- Agente `release-manager-agent`.

### Alterado

- Stack principal continua C#/.NET/WPF para o desktop da empresa, mas o agente temporário NDesk legado passa a ser tratado como componente nativo separado.
- Módulo MikroTik deixa de depender de API-SSL/REST no MVP e passa a priorizar WinBox oficial externo.
- Pipeline de PR passa a exigir avaliação de changelog e versionamento.

### Segurança

- Adicionado alerta sobre risco de senha em argumento de processo ao abrir WinBox.
- Adicionado modelo de permissões NDesk: básico, controle, transferência e administrador, sempre com consentimento explícito.

## [0.1.0-planning] - 2026-06-29

### Adicionado

- Planejamento inicial do RemoteOps Suite.
- Módulos SSH/Telnet, RDP, MikroTik, sync, segurança, NDesk, DevOps, QA e agentes.
