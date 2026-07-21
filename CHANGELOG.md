# Changelog

Este projeto segue uma variação de [Keep a Changelog](https://keepachangelog.com/) e versionamento SemVer interno.

## [Unreleased]

## [1.4.7] - 2026-07-21

**Fatia 0** do recurso de times compartilhados (ver
`docs/superpowers/specs/2026-07-20-times-compartilhados-design.md`). Os dois defeitos valem sozinhos —
a revogação que não propagava é falha de segurança real, independente de equipe.

### Corrigido

- **Revogação de senha não propagava entre devices (segurança).** Ao trocar/revogar uma senha, o
  envelope antigo era tombstoneado **só localmente**: `IsSyncable` exigia `RevokedAt is null` e o
  servidor recusava material vazio (`SecretsService.Decode`). O segredo antigo ficava **vivo e
  decifrável no disco do outro device, para sempre**. Contrato de tombstone ponta a ponta: `RevokedAt`
  opcional no DTO e na entidade (migração aditiva), `allowEmpty` **apenas** para tombstone, codec
  levando a marca, e o device que recebe zera o material.
  - `TombstoneAsync` passou a gravar `Version + 1`: nascendo na mesma versão, a lápide era barrada
    **duas vezes** antes do fio (o ledger `secrets_pushed` pula `sentVersion >= Version`, e o servidor
    recusa por monotonicidade). Sem isso a correção não funcionaria.
  - **Nunca ressuscita:** guarda no servidor (`envelope.revoked`) e no cliente para o caso de **mesma
    versão**, que o guarda `>` não cobria.
- **Limite de 900 assets** (`GetAssetsAsync` lançava por causa do teto de 999 variáveis do SQLite).
  Endpoints passam a ser buscados em **lotes de 500**. Estourava no "Reenviar tudo" e em varreduras de
  workspace inteiro — a lista diária consulta por grupo e nunca encostou. O operador tem ~700 devices.

### Endurecimento (achado da revisão de segurança)

- **A lápide agora tem que ser VAZIA, não apenas poder ser.** A liberação de material vazio existia só
  para o tombstone, mas não o *exigia*: uma lápide com material deixaria ciphertext circulando por
  baixo da marca de revogação, e um device de versão anterior o gravaria como envelope **vivo**. Hoje
  esse material não abriria (a versão entra no AAD), mas com a chave de workspace **compartilhada** dos
  times isso viraria ressurreição de senha revogada. Recusado no servidor **e** descartado no cliente
  (defesa em profundidade contra servidor comprometido).

**Registrado como pré-requisito da próxima fatia** (não afeta esta versão): o upsert não tem token de
concorrência, então um upsert vivo concorrente com um tombstone pode limpar o `RevokedAt`. Hoje o
material plantado é indecifrável; com chave compartilhada deixa de ser. Detalhes e correção no spec.

### Compatibilidade (matriz conferida na revisão)

- `IsSyncable` foi **estritamente ampliado**: tudo que subia antes continua subindo.
- **v1.4.6 → servidor novo:** caminho idêntico ao atual; ao receber um tombstone, grava o material
  zerado que desce junto — o segredo deixa de abrir lá mesmo sem entender a marca.
- **v1.4.7 → servidor antigo:** o push do tombstone leva 400 e é **isolado por item** (entra em
  `SecretSyncReport.skipped`); os envelopes sadios seguem. ⚠️ **Sequenciar: backend primeiro, cliente
  depois** — a revogação só propaga de fato com o servidor atualizado.

### Testes

**1228** (+~25). Inclui prova por **mutação** no chunking (trocar o índice do lote faz o teste falhar) e
um E2E que **abre o cofre no device B e exige falha de decifração** — não se contenta com a marca.

## [1.4.6] - 2026-07-20

### Adicionado

- **Excluir grupo** (menu de contexto do card, botão direito). **Só grupo VAZIO**: com equipamentos
  dentro, o app recusa e diz *quantos* são. A contagem é lida do **store no momento do clique**, não do
  card — o card fica velho quando o sync adiciona hosts com a tela aberta, e um card marcado "0"
  autorizaria apagar um grupo já povoado. Re-checada também **depois** da confirmação (o diálogo fica
  aberto por tempo humano e o applier grava em thread de fundo).
- **"Sincronizar agora" passa a dizer o que aconteceu.** Carimbo `Última sincronização: HH:mm:ss` ao
  lado do botão; falha em vermelho `Não sincronizou às HH:mm:ss`. Com **segundos** de propósito: o
  texto muda a cada clique, então há sinal visível mesmo quando o estado final é igual ao inicial —
  o caso que tornava impossível saber se o botão fazia algo.

### Corrigido

- **`SyncStatusViewModel` descartava o `bool` de `SyncNowAsync`.** O botão sempre executou o ciclo real
  de push+pull (`OrchestratorSyncController.cs:31`), mas o resultado era jogado fora e o `catch`
  engolia: clicar já "Sincronizado" não mudava nada na tela, indistinguível de botão morto.
- **Delete de grupo vindo do OUTRO device apagava grupo com hosts locais.**
  `LocalEntitiesChangeApplier.DeleteAsync` apagava a linha do grupo incondicionalmente. Como a UI
  navega **exclusivamente por grupo** (a raiz lista cards; hosts só aparecem dentro de um), um asset
  com `group_id` apontando para um grupo apagado fica **invisível** — o dado existe e o operador não
  alcança. O comportamento é anterior a esta versão, mas **nada no app emitia `asset_group/deleted`**
  até a feature de excluir grupo: ela é a primeira produtora, então a guarda entra junto.
  Agora vale a **mesma invariante nas duas pontas**: grupo com hosts locais não é apagado, venha o
  delete de onde vier. Cobre o que o outro device não podia saber (hosts criados aqui, ainda não
  sincronizados); a divergência se resolve sozinha quando eles subirem.

### Notas

- Follow-up conhecido (não bloqueia): a guarda da UI tem janela de milissegundos entre a re-checagem e
  o `DELETE` (duas chamadas sem transação comum). Fechá-la exige mover a guarda para dentro de
  `SqlCipherLocalStore.DeleteGroupAsync` num `BEGIN IMMEDIATE`, o que muda o contrato de `ILocalStore`.
- Grupos com **subgrupos** não são cobertos pela trava (a contagem olha só assets). Hoje inalcançável —
  a UI cria todo grupo com `parentGroupId: null` —, mas vira bug no dia em que hierarquia for à tela.

### Adicionado

- **"Excluir grupo"** no menu de contexto (clique-direito) do card de grupo, com confirmação que nomeia
  o grupo. **Só exclui grupo VAZIO**: `ILocalStore.DeleteGroupAsync` apaga apenas a linha do grupo e não
  toca nos ativos, então excluir um grupo com equipamentos deixaria cada um com `group_id` apontando
  para um grupo inexistente — órfãos invisíveis na tela, propagados aos outros dispositivos pelo patch
  `asset_group`/`deleted`. Com equipamentos, a exclusão é bloqueada e o aviso diz **quantos são** e o
  que fazer ("Mova ou exclua os equipamentos antes de excluir o grupo"). A contagem é lida do store no
  momento do clique, não do card (que pode estar velho por causa do sync). Falha do store vira aviso na
  tela — nunca silêncio.

## [1.4.5] - 2026-07-20

Reportado em produção: **"as credenciais não sincronizaram"** — operador com ~700 devices, logado nos
dois PCs. Hosts sincronizavam; no PC B o Chaveiro **listava os nomes** das credenciais, o status dizia
**"Sincronizado"** (sem erro), mas conectar falhava com *"o endpoint não tem credencial"*.

Spec: `docs/superpowers/specs/2026-07-20-sync-credenciais-reparo-design.md`

### Causa raiz

`LocalSyncClient.cs:60` **congela o patch no momento da edição** (`patch_json`) e o envio relê o blob
(`:105`) — não reconstrói do registro atual. Os ~700 devices foram cadastrados numa versão com o bug
"endpoint sobe sem `credential_ref_id`" (corrigido na Fase 1); esses patches ficaram **incompletos para
sempre** na fila e foram drenados assim ao ligar a nuvem. O `credential_ref` tinha patch completo → subiu
certo (por isso o Chaveiro lista); o `endpoint` subiu sem o vínculo → `CredentialRefId` nulo no PC B.

Corrigir o código do patch **não repara** o que já subiu — daí o reenvio.

Backend verificado ao vivo (`GET /secrets` → 401 = existe; rota falsa → 404): o canal de segredos está
deployado e **não** era a causa.

### Adicionado

- **"Reenviar tudo para a nuvem"** (Configurações → Conta, com confirmação e progresso). Re-emite todo o
  acervo (grupos/assets/endpoints/credential_refs) pelo **caminho de update existente**, que já lê
  `baseVersion = versão local` e monta o patch completo com o código de hoje. **Pull antes é
  obrigatório**: o servidor rejeita `base_version < currentVersion` (`SyncService.cs:104`), então sem
  alinhar as versões o reparo viraria centenas de conflitos. Idempotente.
- `SecretChannelState` (Idle/Healthy/Degraded/Failed) em `SyncStatus` — a saúde do canal de segredos
  passa a ser **um eixo próprio**, separado do estado do changelog.
- `SqlCipherLocalStore.UpdateGroupAsync` — era o único tipo sem update de linha inteira.

### Corrigido

- **Rotação de segredo órfãnava a credencial (perda de dado).** `RotateAsync` cria envelope com **id
  novo** e tombstoneia o antigo, mas `ChangePasswordAsync`/`ReplaceKeyAsync`/`ChangePassphraseAsync`
  **descartavam o retorno**: o `CredentialRef` ficava apontando pro tombstone → conectar falhava **no
  próprio PC** ("Envelope revogado") e a troca **nunca chegava** ao outro device.
- **Canal de segredos era tudo-ou-nada e silencioso.** Um envelope malformado travava push **e** pull,
  para sempre. Agora: try/catch **por item** (veneno de item distinguido de queda de canal — 400/413/422
  e erro de contrato = item; 401/5xx/timeout = canal), o pull roda **mesmo se o push falhar**, e a falha
  do canal é reportada separada do changelog em vez de virar "Erro" genérico.
- **`SyncOnceAsync` engolia falha e o reenvio "concluía" com a nuvem fora** (achado da revisão
  adversarial). O orquestrador nunca relança (offline-first), então o pull inicial do reenvio podia
  falhar em silêncio e re-emitir ~700 itens sobre versões desalinhadas — a enxurrada de conflitos que o
  pull existe para impedir — enquanto a tela dizia *"Reenvio concluído"*. `SyncOnceAsync` passou a
  devolver o `SyncStatus` do próprio ciclo (lido dentro do gate) e o reenvio **aborta antes de
  re-emitir** se o pull falhar; drenagem final falha ⇒ não declara sucesso.

### Testes

- **1192 testes** (+29). Novo: `TwoDeviceCloudSyncTests` — dois devices sobre a **API real hospedada**
  (`WebApplicationFactory`, rotas/auth/RBAC reais), com asserção forte de que **o cofre do device B abre
  o segredo**; inclui o cenário que **reproduz o patch congelado** e prova o reparo.
  Era a única camada sem cobertura — e exatamente a que divergiu em produção.

## [1.4.4] - 2026-07-20

### Corrigido

Reportado em campo: **"a sugestão de atualização deveria aparecer para quem está com o sistema aberto;
não apareceu na outra máquina"**. A investigação mostrou que a v1.4.2 (rodando naquela máquina) estava
correta — o indicador e o timer foram verificados na própria tag. Duas causas reais, ambas de grau:

- **Intervalo longo demais: 3h → 30min.** Uma versão publicada de manhã só seria anunciada à tarde, o
  que na prática é indistinguível de "não funciona". O custo de checar é um GET no feed público.
- **O aviso era discreto demais para cumprir a função.** Era uma legenda do mesmo tamanho e peso do
  resto da barra; o operador simplesmente não a viu. Virou uma pílula com fundo de acento, borda,
  seta e peso semibold — continua sem roubar foco (é um botão na barra, não um modal), mas salta aos
  olhos. Decisão de formato reconfirmada com o operador antes de mudar: ele optou por manter o
  discreto e torná-lo mais visível, em vez de voltar ao diálogo modal.
- **O tooltip não dizia o que o clique faz.** Agora abre com "Clique para baixar e instalar" antes da
  data da última verificação.

**Nota:** máquinas em versões **anteriores à 1.4.2 não têm verificação periódica alguma** — nelas a
checagem só roda na abertura da janela, por construção. A primeira atualização até a 1.4.2+ precisa
ser manual; a partir daí o app se anuncia sozinho.

## [1.4.3] - 2026-07-20

### Corrigido

Reportado em campo: **"nesta máquina aparecem 18 conflitos, na outra não aparece nada — como resolver?"**
A investigação mostrou que o número não representava trabalho pendente e que não havia como resolvê-lo.

- **A contagem era um log histórico cumulativo apresentado como pendência.**
  `GetConflictCountAsync` é `SELECT COUNT(*) FROM conflicts`, e **não existia nenhum `DELETE` dessa
  tabela em todo o código**: cada push rejeitado gravava uma linha para sempre. O número só crescia,
  nunca voltava a zero, e incluía cicatrizes de bugs já corrigidos (o `baseVersion` fixo em `0`, por
  exemplo, fazia todo update conflitar). A assimetria entre máquinas é consequência disso — a tabela
  é local e só registra no device cujo push foi **rejeitado**.
- **Não havia caminho de resolução.** O comentário da política dizia "o usuário resolve depois"
  (`SyncOrchestrator.cs:198-202`), mas esse "depois" nunca foi construído: nenhuma tela, comando ou
  ação em lugar nenhum.
- **A perda de edição era silenciosa.** Com a política *record & advance*, a alteração local que
  conflitou é pulada e a versão do servidor sobrescreve a local — sem o operador jamais saber qual.

### Adicionado

- **Aviso clicável na barra de status**, com linguagem de efeito em vez de jargão: "18 alterações não
  subiram" (antes: "Sincronizado (18 conflito(s))" — a contagem saiu do texto de status).
- **Janela "Alterações que não subiram"**: lista tipo, data e motivo de cada item em pt-BR, explicando
  que a versão da nuvem prevaleceu para não sobrescrever o trabalho do outro computador, e que a
  alteração local foi descartada — para o operador refazer se ainda importar.
- **Ação "Já vi, pode limpar"**: `ClearConflictsAsync` no store e no orquestrador. Com ela a contagem
  passa a significar **pendência**, não histórico. Não desliga a detecção — conflito novo volta a
  aparecer (coberto por teste).

**Nota de honestidade de design:** a janela não oferece "manter a minha versão". Quando o conflito é
exibido, a alteração local **já foi descartada** e a versão do servidor já sobrescreveu a local;
oferecer essa escolha seria mentir. O que a tela faz é explicar o que se perdeu e permitir dispensar
o aviso.

## [1.4.2] - 2026-07-20

### Adicionado

- **Aviso de versão nova enquanto o app está aberto.** Indicador discreto e persistente na barra de
  status ("· Atualização 1.4.3 disponível"), clicável, que abre o diálogo de instalar já existente.
  O `ToolTip` mostra o horário da última verificação bem-sucedida. Ver
  `docs/superpowers/specs/2026-07-20-aviso-atualizacao-nao-intrusivo-design.md`.

### Corrigido

- **A verificação de atualização só acontecia uma vez, na abertura do app.** Como o RemoteOps é um
  console de operação que fica aberto o dia inteiro, uma versão publicada durante o expediente nunca
  era anunciada — o operador só saberia se fechasse e reabrisse. Agora há re-verificação periódica
  (a cada 3h) enquanto o app está aberto.
- **Aviso de atualização deixou de ser modal.** Antes, `MainWindow.Loaded` abria um diálogo Sim/Não
  que rouba foco. Num console de rede isso é risco operacional: se aparecer enquanto o operador digita
  num equipamento em produção, as teclas vão para o diálogo e um `Enter` destinado ao roteador
  confirmaria "atualizar agora", reiniciando o app no meio de uma manutenção. O diálogo agora só abre
  por clique do operador.
- **Falha de verificação deixou de ser indistinguível de "está atualizado".** O caminho antigo
  (`CheckForUpdatesQuietAsync`) engolia a exceção e devolvia `null`, sem deixar rastro na UI. O
  indicador agora carrega o horário da última checagem boa, e uma falha posterior não apaga um aviso
  já detectado.

### Removido

- `WorkspaceViewModel.CheckForUpdatesQuietAsync` — a verificação passou a viver no
  `UpdateNotificationViewModel` (que também guarda o estado do indicador). Manter os dois caminhos
  daria duas fontes de verdade divergindo com o tempo. A **aplicação** da atualização segue no
  `WorkspaceViewModel` e segue coberta por testes.

## [1.4.1] - 2026-07-19

### Corrigido

Reportado em campo logo após a v1.4.0: **o app não abria — o operador precisou reiniciar o Windows**.
Duas falhas em série, ambas corrigidas (camadas com modos de falha diferentes, como no sync).

- **Fechamento sem teto pendurava o processo.** `App.OnExit` bloqueava a UI thread em
  `_syncSession.DisposeAsync().AsTask().GetAwaiter().GetResult()` — sem limite. Esse descarte espera o
  `HubConnection` fechar, e a `InfiniteRetryPolicy` introduzida na v1.4.0 deixa a conexão **sempre**
  tentando reconectar (antes ela desistia em ~42s e ficava inerte, então descartar era trivial): a
  v1.4.0 tornou o travamento mais provável. Agora o descarte roda em `Task.Run` com teto de 3s — o
  mesmo padrão que o `FlushOutboxOnClose` já usava e que esta linha não tinha.
- **Processo pendurado tornava o app irrecuperável, em silêncio.** Um RemoteOps travado continua
  segurando o mutex de instância única, então toda nova tentativa de abrir encerrava **sem exibir
  nada** (`App.OnStartup`): clicar no ícone não produzia efeito algum e a única saída aparente era
  reiniciar o computador. `SingleInstanceGuard` ganhou um **handshake de ativação** — a segunda
  instância agora espera a confirmação de que a janela existente realmente veio para frente. Sem
  confirmação, ela mostra uma mensagem acionável ("finalize RemoteOps.Desktop.exe no Gerenciador de
  Tarefas; não é preciso reiniciar o computador") em vez de desaparecer.

## [1.4.0] - 2026-07-19

### Corrigido

Sync multi-device: o que era cadastrado num PC só aparecia no outro depois de fechar e abrir o app.
Diagnóstico do sintoma reportado em campo (ambos os devices já na v1.3.2, então **não** era o bug de
applier corrigido na v1.3.0). Ver `docs/superpowers/specs/2026-07-19-sync-tempo-real-resiliente-design.md`.

- **Canal de tempo real morria em silêncio e nunca voltava.** `SyncHub.OnConnectedAsync` era um stub
  (`_ = userId;`) e o grupo do SignalR é por `ConnectionId` — como toda reconexão gera ConnectionId
  novo e o `JoinWorkspace` só era chamado no connect inicial, o cliente saía do grupo **para sempre**
  na primeira oscilação de rede. O hub passa a fazer **auto-join** dos workspaces do usuário a cada
  conexão: o join vira invariante do servidor, em vez de depender de o cliente lembrar.
- **Nome de grupo divergente.** `JoinWorkspace` entrava no grupo com a string crua enquanto o
  broadcast usa `Guid.ToString()` — um GUID em maiúsculas (caminho por env var) entrava num grupo que
  nunca recebia nada. Canonicalizado no hub (join/leave/auto-join) e normalizado no cliente,
  fail-closed; filtro do hint agora é `OrdinalIgnoreCase`.
- **SignalR desistia de reconectar.** A política default para após 4 tentativas (~42s) e dispara
  `Closed` sem handler. Substituída por `InfiniteRetryPolicy` (backoff com teto de 30s, nunca
  desiste) + re-join no `Reconnected` + retry com backoff do connect inicial (antes era engolido:
  falhou uma vez, ficava em polling puro pelo resto da sessão).
- **O laço de polling podia morrer calado.** `RunLoopAsync` chamava `SyncOnceAsync` fora de
  try/catch; uma exceção de assinante de `StatusChanged` (o `Dispatcher.Invoke` do Desktop) escapava
  e encerrava o polling — a rede de segurança tinha o mesmo defeito que deveria cobrir. Blindado, com
  retry curto (backoff 5s→40s, teto no intervalo) em vez de esperar o ciclo inteiro após erro.
- **Rajada de mudanças = rajada de ciclos.** O servidor emite um hint por change, e o cliente
  sincronizava a cada um: importar 200 hosts enfileirava ~200 ciclos completos no outro device, cada
  um reenumerando o cofre. Agora uma janela de 500ms agrupa a rajada num único ciclo.
- **Teto de atraso previsível:** polling de fallback de 2 min → **45s**.

### Adicionado

- Barra de sincronização mostra **"Tempo real"** vs **"Periódico"**, para diagnosticar em campo se a
  rede está deixando o WebSocket passar sem depender de log (a URL do hub carrega o JWT — ADR-013).

## [1.3.2] - 2026-07-19

### Corrigido

Rodada de correções a partir de uma revisão adversarial multi-agente (Fable) do UI de nuvem — 19
achados confirmados, os relevantes corrigidos:

- **"Salvar e reiniciar" fechava o app e não reabria (crash de UX):** o relaunch acontecia ANTES de
  o processo liberar o mutex de instância única → a nova instância se via como 2ª e saía. Agora o
  `App.RestartApplication` libera o mutex, relança com atraso curto (evita corrida no SQLCipher) e só
  então encerra. Botão renomeado para "Aplicar e reiniciar".
- **`Button.Primary` sem cor de hover/pressed (HIGH):** as triggers do template setavam
  `Chrome.Background` direto, vencendo o `Background` das variantes — todo CTA primário ficava escuro
  com rótulo quase invisível no hover (~1.5:1). Movido pra `Style.Triggers` (Background via
  TemplateBinding); Primary/Danger voltam a sobrepor. Foco por teclado do Primary também ganhou anel
  visível.
- **`SingleInstanceGuard` derrubava o app com "Erro fatal" ao fechar+reabrir rápido:** o listener só
  engolia `ObjectDisposedException`; um `Dispatcher.Invoke` durante o shutdown lançava
  `TaskCanceledException`. Catch ampliado (ativação de janela é best-effort).
- **Configurações — abas quebravam em 2 fileiras e "pulavam"; aba Conta cortada:** janela para
  640×560, headers encurtados ("Ferramentas", "Problemas").
- **Config de nuvem:** `CloudConfig` ganhou `CloudSyncConfigured` — a GUI passa a VENCER a env var
  (inclusive pra DESLIGAR); o "Salvar" global some na aba Conta (só "Aplicar e reiniciar", que valida
  o HTTPS — antes dava pra salvar URL inválida em silêncio); `Save` relê do disco (não reverte mais o
  `LastSeenChangelogVersion` de outro gravador).
- **"Esqueci a senha":** status/erro movidos pro topo (a orientação "informe o código abaixo"
  apontava errado); nota da chave de recuperação passou a superfície neutra (não parece mais erro).
- Acabamentos: contraste do texto de erro (Text.Primary sobre superfície crítica), espaçamentos na
  grade de 4px, altura dos `PasswordBox` herdando do tema, gap entre os botões-fantasma do login.

## [1.3.1] - 2026-07-19

### Adicionado

- **Sincronização na nuvem configurável pela GUI (Configurações → Conta):** fim da dependência de
  variável de ambiente + reboot do Windows. Nova seção com checkbox "Ativar sincronização na nuvem"
  + campo do endereço do servidor (HTTPS) + botão "Salvar e reiniciar" (o app se relança sozinho via
  `Environment.ProcessPath`; a conta é ativada no startup, por ordem técnica AMK→cofre→banco).
  `AppSettings` ganha `CloudSyncEnabled`/`CloudServerUrl`; `CloudConfig.Resolve(settings, getEnv)`
  centraliza a precedência **Configurações → env var (fallback, compat)**, HTTPS-only (fail-closed,
  ADR-013). `App.TryBuildAccountConfig` passa a ler as settings. Coberto por testes do resolvedor
  (round-trip + precedência + rejeição de não-HTTPS), do VM (persistência + validação + restart) e
  o render STA das Configurações já percorre a aba nova.

## [1.3.0] - 2026-07-18

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

- **Cloud sync E2EE (Fases 1–4) — conta multi-device com cofre portável (OPT-IN):** o headline do
  1.3.0. "Logo numa conta em qualquer PC e tenho tudo, com as senhas dos equipamentos decifráveis",
  com o servidor NUNCA vendo nada em claro. Núcleo cripto (Fase 1): senha →Argon2id→ MasterKey
  →HKDF→ (AuthHash pro servidor + KEK no device); AMK (raiz portável do cofre) com escrow por senha
  e por chave de recuperação; sync de `SecretEnvelope` cifrados. Sync robusto (Fase 2): auto-refresh
  da lista pós-pull, push-ao-fechar/Alt+F4, botão forçar-sync + status no shell, validação de
  integridade no boot. 2FA/TOTP (Fase 3, RFC 6238, não participa da cripto do cofre). Recuperação
  por email (Fase 4, abaixo). Tudo atrás da flag `REMOTEOPS_CLOUD_SYNC_ENABLED` (default OFF) +
  `REMOTEOPS_CLOUD_URL` (https) — sem elas, o app é bit a bit o local de sempre (ADR-002). Backend
  ASP.NET Core + Postgres (`RemoteOps.Cloud`), deployável em Debian atrás de Caddy/HTTPS.
- **Cloud sync (Fase 4) — recuperação de senha por email, sem furar o E2EE:** tela "Esqueci a
  senha" com recuperação de **dois fatores** por design — o **código do email** restaura o
  ACESSO (autoriza trocar a prova de senha sem o AuthHash antigo) e a **chave de recuperação**
  reabre o COFRE (desembrulha a AMK e a re-embrulha sob a senha nova; a AMK não muda, os segredos
  seguem decifráveis). O servidor nunca vê a AMK/senha/chave — só valida um token de uso único
  (SHA-256, TTL 30 min) e grava o material que o cliente já computou. Endpoints anônimos
  `/auth/password/{forgot,reset-context,reset}` (`/forgot` sempre 202 — anti-enumeração), envio de
  email **plugável** (`IEmailSender`: `LoggingEmailSender` por padrão, `SmtpEmailSender` quando
  `Smtp:Host` está configurado — o operador pluga o SMTP pelo `.env`, o servidor nunca embute a
  credencial). Reset revoga todas as sessões. Prova cripto ponta a ponta (segredo selado antes do
  reset volta a abrir; email sem a chave de recuperação é inútil). Quem perde senha **e** chave de
  recuperação continua irrecuperável por design — a UI diz isso.

- **NDesk — operador descobre o `sessionId` pelo status do ticket (`ADR-020`):** o
  `GET /ndesk/tickets/{id}` passa a devolver o `sessionId` ao criador do ticket (campo novo,
  opcional, em `contracts/ndesk-ticket.schema.json` e `NDeskTicket`), destravando o fluxo real
  operador↔agente — antes só o agente recebia o `sessionId` no resgate e o operador não tinha
  como entrar no signaling. Endpoint já escopado ao criador (anti-IDOR, `ADR-018`), então o
  campo só chega a quem tem direito. Validado ao vivo: `tools/ndesk-signaling-check` agora
  descobre o `sessionId` por esse endpoint (10/10 checks contra Postgres real).

### Corrigido

- **Cloud sync (Fase 2) — a lista de hosts não recarregava quando o sync baixava dados novos:**
  no 1º launch do device B a lista aparecia **VAZIA** (os dados chegavam ao banco segundos depois
  e só apareciam no relaunch). O `HostsViewModel.LoadAsync()` rodava uma vez no `MainWindow.Loaded`
  e nada mandava recarregar quando um pull materializava os hosts. Agora o `SyncOrchestrator`
  levanta o evento `ChangesApplied` **apenas** quando um pull grava de fato nas tabelas locais que
  a UI lê (o applier passou a devolver as linhas afetadas; ciclo no-op não dispara nada). O App
  consome esse sinal com **debounce** de 300 ms (`DebouncedAction` — agrupa os vários lotes de um
  pull grande) e o marshala para o Dispatcher, chamando `HostsViewModel.ReconcileFromStoreAsync()`:
  uma recarga **incremental** que re-busca do store e reconcilia as coleções **por id** (adiciona
  novos, remove sumidos, atualiza mudados) preservando a **instância** — logo a seleção do host, o
  grupo aberto e o chip de filtro ativo sobrevivem à recarga (um `LoadAsync` cru os destruiria).
  Offline-first intacto: sem conta/sync o comportamento é bit a bit o de antes. Coberto por testes
  de VM, de orquestrador (fakes), device↔device (applier + SQLCipher reais) e um render STA que
  reconcilia sobre um `DataGrid` vivo sem quebrar o binding.
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

## [1.2.26] - 2026-07-16

### Alterado

- **Classificador de device: taxonomia enxugada por fabricante.** Removidos os papéis **Firewall**,
  **Load Balancer** e **Wireless** do seletor "Tipo" — a operação classifica por fabricante
  (Huawei/MikroTik têm roteador e switch, etc.), então esses três não são usados. Papéis restantes:
  Roteador, Switch, Servidor Linux, Servidor Windows, OLT e "Sem tipo". A detecção de vendor A10
  continua (ícone/cor), mas sem sugerir papel — o operador escolhe o Tipo se precisar.

## [1.2.25] - 2026-07-16

### Adicionado

- **Classificador de device (tipo + ícone + filtro na lista).** Cada host ganha um **papel**
  normalizado (roteador, switch, servidor Linux/Windows, OLT, firewall, load balancer, wireless)
  auto-sugerido a partir do **Fabricante/Modelo** por uma heurística LOCAL (`DeviceClassifier`,
  sem rede) — Huawei `NE*`→VRP8 roteador, `S*/CE*`→VRP5 switch, `MA5*`→OLT, MikroTik→RouterOS,
  Debian/Ubuntu→Linux, A10→load balancer, Cisco/Juniper… O operador confirma ou troca o Tipo. É o
  ponto único onde a detecção ATIVA futura (banner SSH/identidade RouterOS/SNMP) vai entrar.
  - **Ícone** (`DeviceIcon`): mostra o **logo do vendor** (`assets/logos/<vendor>.png`) quando o
    arquivo existe, senão cai num **glifo vetorial** de papel tingido pela cor do vendor — nunca
    fica sem ícone. Os PNGs de logo são de uso interno e ficam fora do repositório (`.gitignore`).
  - **Lista**: ícone colado ao nome, coluna **"Tipo"** e barra de **chips de filtro** por
    papel/vendor (só aparecem os presentes), ortogonais aos grupos.
  - **Editor de host**: campos Fabricante/Modelo (que antes não eram persistidos — gap corrigido)
    + seletor "Tipo" com sugestão automática e override manual.
- Campo `device_role` no armazenamento local (SqlCipher, com migração `ALTER TABLE` idempotente, +
  InMemory). Retrocompatível: hosts antigos ficam como "Sem tipo" até serem editados.

## [1.2.24] - 2026-07-15

### Corrigido

Varredura de coerência de tema: vários controles WPF não tinham estilo próprio e caíam no template
PADRÃO do WPF (Aero2, CLARO) sobre o app escuro. Uma auditoria multi-agente (com verificação
adversarial de cada achado) confirmou 6 pontos, todos corrigidos por estilos **implícitos** (sem
`x:Key`), que também melhoram todas as outras ocorrências dos mesmos controles no app:

- **`RadioButton`/`CheckBox`** (cadastro de host + Configurações): indicador desenhado por bitmaps
  de tema, que não reescala com nitidez entre monitores de DPI diferente — depois do Per-Monitor-V2
  (v1.2.23) ficavam de tamanhos diferentes, tortos e destoando. As opções de credencial ainda
  herdavam fonte maior que os rótulos do formulário.
- **`PasswordBox`** (campo "Senha" do modo "Senha só deste dispositivo" e do Chaveiro): caía numa
  caixa branca de altura diferente ao lado do `TextBox` "Usuário" temático — desalinhado.
- **`ContextMenu` + `MenuItem`** (menu de clique-direito da lista de hosts — Conectar via
  SSH/Telnet/RDP, Abrir WinBox, Editar, Excluir; "Novo grupo"; menu da conta): popup branco, gutter
  de ícone claro e realce azul de hover do Aero2. É o fluxo principal de conexão, então o menu claro
  aparecia o tempo todo.
- **`ListBoxItem`** (lista de Endpoints no editor de host + lista de Logs): hover/seleção em azul
  Windows-8 (`#26A0DA`) e cinza — cores HARDCODED no template padrão, que a sobrescrita de
  `SystemColors` não alcança — sobre o canvas escuro.
- **`Expander`** ("Ver o que será anexado", em Configurações → Reportar problema): botão de
  expandir/recolher claro com seta de sistema.

### Adicionado

- **Estilos de controle temáticos** (implícitos, mesclados em `DarkTheme.xaml`):
  - `Themes/Controls/ToggleControls.xaml` — `RadioButton`/`CheckBox` com indicador **vetorial** de
    16px (nítido em qualquer DPI, `SnapsToDevicePixels`), preenchimento de acento quando marcado,
    alinhamento central com o texto, e estados hover/foco/desabilitado.
  - `Themes/Controls/Menus.xaml` — `ContextMenu`/`MenuItem` escuros (popup em `Bg.SurfaceRaised`,
    borda forte, realce de hover em `Bg.Hover`, coluna de ícone e seta de submenu vetorial + suporte
    a submenu via `PART_Popup`, separador de menu via `SeparatorStyleKey`).
  - `Themes/Controls/ListBox.xaml` — `ListBoxItem` com hover `Bg.Hover` e seleção `Accent.Muted` +
    borda de acento, espelhando o `ComboBoxItem`/`DataGrid`.
  - `Themes/Controls/PasswordBox` (em `TextInputs.xaml`) — espelha o `TextBox` temático (mesma
    altura/borda/raio/foco).
  - `Themes/Controls/Expander.xaml` — cabeçalho retemplado com chevron vetorial que gira ao expandir.
- Testes de render STA para as superfícies antes não cobertas (menu de contexto aberto com submenu +
  ícone, `ListBox` com item selecionado, `Expander` expandido, editor de host em modo inline).
  Estilos com `x:Key` (ex.: `Rail.Item` do rail de navegação) continuam prevalecendo — não afetados.

## [1.2.23] - 2026-07-15

### Corrigido

Rodada de validação (revisão multi-agente + verificação adversarial) — estabilidade e polimento:

- **Fechar a aba do terminal DURANTE a conexão não vaza mais a sessão.** `CloseAsync` agora cancela
  a conexão em voo mesmo antes do handle existir; a View fecha a sessão se a aba sumiu no meio do
  connect (guarda pós-await, como o RDP). Antes, fechar durante um connect lento deixava a sessão
  SSH/Telnet viva e "sem dono" no equipamento — esgotando slots vty em MikroTik/OLT.
- **Queda de sessão no meio deixou de ser silenciosa.** Os providers propagam o erro de rede
  (`TryComplete(ex)`) e o terminal mostra `[Conexão perdida: …]` / `[Sessão encerrada]`. Antes o
  terminal congelava sem aviso e as teclas seguintes eram descartadas em silêncio.
- **Falha ao excluir host agora aparece.** `DeleteHostAsync` ganhou try/catch + aviso ao operador
  (antes uma falha do cofre/DB ao revogar a senha inline evaporava sem feedback).
- **RDP: o controle ActiveX (mstscax) e o WindowsFormsHost são descartados ao fechar a aba** — não
  vazam mais handles nativos a cada abrir/fechar.
- **DPI Per-Monitor-V2 (novo `app.manifest`):** a UI não fica mais borrada ao mover a janela entre
  monitores de escala diferente (comum em NOC multi-monitor).
- **"Verificar atualizações" abre direto na aba Atualização** (antes caía na aba padrão, igual a
  "Configurações", sem checar nada).
- **Modo "senha só deste dispositivo" exige usuário E senha** antes de adicionar — não dá mais pra
  salvar um host inconectável com credencial vazia.
- **Startup não trava mais numa rede lenta:** a checagem de update no boot tem timeout curto
  (fail-open), então a janela abre na hora mesmo com link instável/black-hole.

## [1.2.22] - 2026-07-08

### Adicionado

- **Senha só do dispositivo (credencial inline no cadastro do host).** Antes, todo device exigia uma
  credencial nomeada do Keychain. Agora o editor de host tem um seletor de credencial: *"Do Keychain
  (compartilhada)"* — para vários devices com o mesmo login — ou *"Senha só deste dispositivo"* —
  usuário+senha digitados ali mesmo, para equipamentos com login único, sem poluir o Keychain. A
  senha inline é guardada no **mesmo cofre** das demais (envelope encryption / DPAPI — nunca em texto
  puro), porém com `Scope="endpoint:<id>"`, o que a **esconde do Keychain e do dropdown** (os stores
  já filtram `scope IS NULL OR scope = workspace`); o provider resolve por id, então conecta normal.
  Ciclo de vida preso ao endpoint: apagar o device (ou remover o endpoint) **revoga e apaga** a
  credencial inline (sem envelope órfão). A senha entra por `PasswordBox` (`char[]`, zerada após
  guardar), nunca por binding. Novo `InlineCredentialService` centraliza a parte sensível. +7 testes.

## [1.2.21] - 2026-07-08

### Adicionado

- **Modo do Backspace por host (Padrão DEL ↔ Ctrl+H) — igual ao PuTTY.** Alguns equipamentos legados
  (ex.: OLT Huawei) não apagam com o Backspace padrão porque só entendem BS (0x08 = Ctrl+H) em vez do
  DEL (0x7F) do padrão VT/xterm. Agora a aba do terminal tem um seletor no topo ("Backspace: Padrão /
  Ctrl+H") que troca **ao vivo** — a próxima tecla já usa o novo código, sem reconectar — e **persiste
  por host** (fica lembrado para aquele equipamento). Implementação: `EndpointProfile.BackspaceMode`
  (`"del"`/`"ctrl-h"`, round-trip JSON no store, sem migração de schema), parâmetro
  `backspaceSendsControlH` no `TerminalInputMapper`, seletor ligado a `TerminalTabViewModel`
  (persiste via novo `ILocalStore.UpdateEndpointAsync`). +9 testes.

## [1.2.20] - 2026-07-08

### Corrigido

- **Barra de ESPAÇO no terminal:** digitar funcionava (v1.2.19), mas o espaço não saía — o operador
  tinha que completar comandos com Tab pra ganhar o espaço. Causa: quirk do WPF — com um `TextBox`
  focado (o `KeyboardSink`), a barra de espaço do teclado FÍSICO não dispara
  `PreviewTextInput`/`TextInput` (letras passam, espaço não; entrada sintética WM_CHAR passa, por
  isso os testes automatizados não pegaram). Correção: `Key.Space` mapeado no
  `TerminalInputMapper` (KeyDown) → 0x20; `Ctrl+Espaço` → NUL (0x00). De quebra, `Ctrl+letra`
  agora EXCLUI AltGr (que o Windows reporta como Ctrl+Alt): em teclados ABNT2/internacionais,
  AltGr+letra deixava de compor o caractere e virava byte de controle (ex.: AltGr+Q → 0x11/XON).
  Reproduzido e verificado ao vivo (Huawei NE8000) via injeção de scancode real. +7 testes.

## [1.2.19] - 2026-07-08

### Corrigido

- **Teclado no terminal (a causa RAIZ):** o foco já estava correto (v1.2.16–1.2.18) e as teclas eram
  capturadas, mas NADA chegava ao equipamento — só dava pra visualizar. Diagnóstico decisivo com log
  do caminho inteiro (tecla → handler → provider → `ShellStream` → eco): cada byte chegava ao
  `SshSessionProvider.WriteAsync`, mas o `await ShellStream.WriteAsync/FlushAsync` **travava pra
  sempre — nem completava nem lançava**. Motivo: a `ShellStream` do SSH.NET 2024.2.0 não sobrescreve
  os métodos assíncronos, e o `Stream` base serializa `ReadAsync`/`WriteAsync` no MESMO semáforo
  (`_asyncActiveSemaphore`, 1 permit); como o pump de leitura fica permanentemente parado em
  `ReadAsync` segurando esse semáforo, o `WriteAsync` nunca o adquiria. Correção: escrita SÍNCRONA
  (`Write`/`Flush`, que usa locks internos próprios, separados da leitura) drenada por uma **fila de
  escrita ordenada por sessão** — garante FIFO das teclas (sem corrida entre threads) sem bloquear a
  UI. Verificado ao vivo num Huawei NE8000: digitar, dar enter, saída paginada e sair do pager.
  Regressão coberta por `WriteAsync_ManyKeystrokes_PreservesByteOrderFifo` (588 testes no total).

## [1.2.18] - 2026-07-08

### Corrigido

- **Teclado no terminal (foco, parte 2):** com o `KeyboardSink` (v1.2.17) o teclado ainda não pegava
  porque o `TerminalScreenControl` continuava `Focusable=true` e ROUBAVA o foco do sink ao clicar.
  Agora o Surface é `Focusable=false` (o mouse ainda funciona — é hit-test, não precisa de foco), o
  `KeyboardSink` (TextBox editável, como o campo de busca que comprovadamente recebe teclado) segura
  o foco, e `FocusTerminal` foca de forma síncrona (com fallback no Dispatcher).

## [1.2.17] - 2026-07-08

### Corrigido

- **Teclado no terminal (a correção definitiva):** diagnóstico decisivo — digitar num `TextBox`
  normal do app funciona (foco confiável), mas o `TerminalScreenControl`/`NativeTerminalView` NÃO
  pegava foco de teclado dentro do `TabControlEx` (v1.2.13→1.2.16 não resolveram). Solução (padrão
  clássico de emuladores de terminal): um **`TextBox` invisível** (`KeyboardSink`, 4×4, opacity 0,
  read-only) é o sink de foco — TextBox pega/segura o foco de forma confiável no WPF. Os
  `PreviewKeyDown`/`PreviewTextInput` do `NativeTerminalView` interceptam a entrada (o char nunca
  entra no TextBox) e mandam pro host; o mouse continua no `Surface` (seleção/cópia). Foco reforçado
  em Loaded/IsVisibleChanged/PreviewMouseDown. UserControl passou a `Focusable=False`/`IsTabStop=False`.

## [1.2.16] - 2026-07-08

### Corrigido

- **Terminal nativo não recebia teclado (só EXIBIA):** dentro do `TabControlEx` keep-alive, dar
  foco a um `FrameworkElement` cru (`TerminalScreenControl`) não pegava foco de teclado — nem
  digitar/enter nem os atalhos de copiar/colar funcionavam (as tentativas de v1.2.13/14 falhavam
  pelo mesmo motivo; validado em campo). Correção: o **UserControl** (`NativeTerminalView`, um
  `Control` focável) passa a ser o alvo de foco e trata o teclado via `PreviewKeyDown`/
  `PreviewTextInput` (tunelam esteja o foco no UserControl ou num filho). Foco reforçado em
  `Loaded`, `IsVisibleChanged` e `PreviewMouseDown`. O mouse (seleção/cópia/colagem) fica no
  controle de tela (hit-test, independe de foco); `CopySelection`/`Paste` viraram públicos.

## [1.2.15] - 2026-07-08

### Adicionado

- **Copiar/colar no terminal nativo (2 modos):** (1) estilo PuTTY — SELECIONA com o botão esquerdo
  (arrastar) → copia ao soltar; botão DIREITO → cola. (2) atalhos — `Ctrl+Shift+C` copia,
  `Ctrl+Shift+V`/`Shift+Insert` colam (`Ctrl+C` sozinho continua sendo 0x03/interromper pro host).
  `TerminalScreenControl` trata seleção via mouse (highlight semi-transparente), copia via
  `Clipboard`, e cola convertendo quebras de linha para CR (colar config executa linha a linha).
  Lógica pura em `TerminalSelection` (ExtractText apara espaços à direita e une linhas com `\n`;
  NormalizePaste `\r\n`/`\n`→`\r`) com testes. Seleção limpa ao chegar saída nova.

## [1.2.14] - 2026-07-07

### Corrigido

- **Teclado no terminal nativo:** a v1.2.13 renderizava certo mas o teclado não chegava à sessão
  (rotear `PreviewKeyDown`/`TextInput` pelo `UserControl` pai era frágil quando o foco de teclado
  não estava no filho). Agora a entrada é tratada DIRETO no controle focado
  (`TerminalScreenControl.OnKeyDown`/`OnTextInput`), que ganha foco no clique (`OnMouseDown`→`Focus()`)
  e no load (`Keyboard.Focus`), emitindo `InputBytes` que o `NativeTerminalView` manda ao
  `TerminalTabViewModel.SendInputAsync`. Verificado em campo que a v1.2.13 não pegava tecla; este é o fix.

## [1.2.13] - 2026-07-07

### Adicionado

- **Terminal SSH NATIVO integrado (motor próprio, sem WebView2):** o SSH volta a abrir DENTRO do
  app, mas renderizado em WPF puro — `DrawingContext`/`FormattedText` numa `TerminalScreenControl`,
  sem HWND/swapchain/WebView2, então **imune ao MPO** (compõe na mesma árvore visual do app, que já
  desenha claro). Motor VT/ANSI **escrito do zero** (decisão do usuário: sem dependência externa):
  `TerminalScreen` (grade+cursor+caneta) + `AnsiParser` (texto/CR/LF/wrap/scroll, cursor, apagar
  linha/tela, SGR 16/256/RGB, bold/underline/inverse, UTF-8 incremental) — **18 testes**;
  `TerminalInputMapper` (teclado→bytes VT: Enter, Backspace=DEL, setas, F-keys, Ctrl+letra) —
  **9 testes**. `NativeTerminalView` liga tudo ao `TerminalTabViewModel` existente (reusa
  SSH.NET + cofre + TOFU/host-keys persistidos em `known_hosts.json` — não pergunta o fingerprint
  toda vez). `SessionLauncher` roteia SSH/Telnet para a aba nativa; o launcher externo (`ssh.exe`)
  segue disponível via `LaunchExternalAsync` como alternativa "abrir externo". DataTemplate de
  `TerminalTabViewModel` repontado de `TerminalTabView` (WebView2) para `NativeTerminalView`.

## [1.2.12] - 2026-07-07

### Alterado

- **SSH externo para de "pedir pra registrar" a cada host novo:** `WindowsExternalTerminalLauncher`
  passa `-o StrictHostKeyChecking=accept-new` ao `ssh.exe`. Aceita a chave de host na PRIMEIRA
  conexão sem o prompt `yes/no` e grava em `known_hosts`; hosts já conhecidos nem perguntam; uma
  chave TROCADA continua bloqueada (proteção MITM). Validado em campo: `~/.ssh/known_hosts` do
  usuário já persistia (95 hosts) — o prompt era o `yes/no` por host novo, agora suprimido.

## [1.2.11] - 2026-07-07

### Adicionado

- **Terminal SSH externo (fim da novela do terminal escuro):** após v1.2.5→v1.2.10 (foco,
  keep-alive, `--disable-gpu`, `--force-color-profile`, filtro CSS de brilho e
  `--disable-direct-composition`) NENHUM ajuste do WebView2 resolveu, em campo, o terminal
  **escuro + sem teclado + sem maximizar** (Win11 + NVIDIA). Decisão: SSH passa a abrir numa
  **janela de terminal REAL do Windows** (`ssh.exe` do OpenSSH via `UseShellExecute`), por fora
  do app — exatamente o padrão que o WinBox já usa. Por ser janela nativa do SO, resolve os três
  sintomas de uma vez (cor, teclado, maximizar). Novo `IExternalTerminalLauncher` +
  `WindowsExternalTerminalLauncher`; `SessionLauncher` roteia SSH pro launcher externo (resolve
  host/porta/usuário da credencial; a senha é digitada no prompt do ssh — cofre segue guardando,
  auto-preenchimento via SSH_ASKPASS numa etapa seguinte). Telnet segue no caminho atual por ora.
  O **terminal nativo integrado** (WPF, sem WebView2) segue em construção em paralelo.

## [1.2.10] - 2026-07-07

### Corrigido

- **Terminal "muito escuro" — CAUSA-RAIZ encontrada (Multi-Plane Overlay):** duas análises
  independentes convergiram: em Win11 + GPU NVIDIA, o driver promove a swapchain
  DirectComposition do WebView2 a um **plano de overlay de hardware (MPO)**, escurecido no
  scanout **depois** da composição do DWM. Isso explica todo o histórico: nenhum ajuste de
  conteúdo (tema, `--disable-gpu`, `--force-color-profile=srgb`, filtro CSS de brilho) mudava
  nada — todos agem *antes* da camada ser escurecida — e uma captura `CopyFromScreen` (lê o
  quadro do DWM) aparecia **clara** enquanto a tela mostrava **escuro** (assinatura clássica de
  MPO). Correção: `TerminalTabView` passa `--disable-direct-composition` ao WebView2, tirando o
  Chromium do caminho DComp/overlay e forçando o blit composto pelo DWM (distinto de
  `--disable-gpu`, que não remove o present via DComp). Remove o `filter: brightness` da v1.2.9
  (não era mais necessário — atacava o sintoma, não a causa). Fallback documentado:
  `--disable-gpu-compositing` se o render ficar em branco. Correção definitiva (terminal nativo
  sem WebView2) está em andamento em paralelo.

## [1.2.9] - 2026-07-07

### Corrigido

- **Terminal continuava "muito escuro" mesmo após v1.2.8:** verificado em campo que NÃO é HDR (o
  toggle Win+Alt+B não muda nada), nem GPU (`--disable-gpu`) nem perfil de cor (`--force-color-profile
  =srgb`) — em algumas máquinas o WebView2 rebaixa o brilho SÓ do terminal (~30% do esperado), por um
  quirk de gama/composição específico do driver/GPU que não isolamos. Como o sintoma é um
  rebaixamento uniforme de brilho, passamos a **compensar direto na página**: `terminal.css` aplica
  `filter: brightness(2) contrast(1.12)` em `#terminal-container`, restaurando a legibilidade
  independente da causa. Valor ajustável (pode ser afinado por feedback de campo).

## [1.2.8] - 2026-07-07

### Corrigido

- **Terminal renderizava "muito escuro / filtro escuro" (só o WebView2; o resto do app e apps
  nativos como o Termius normais):** a composição por GPU do WebView2 mapeava as cores SDR errado
  na tela (HDR / wide-gamut, comum em setup com GPU dedicada) — o fundo #1e1e1e virava preto e o
  texto #d4d4d4 saía quase preto (~20% do brilho), e a interação ficava prejudicada. Correção:
  `TerminalTabView` passa `--disable-gpu --force-color-profile=srgb` em `AdditionalBrowserArguments`
  (renderização por software é adequada para um terminal de texto e pinta as cores como no tema) e
  define `DefaultBackgroundColor` opaco. Só `--force-color-profile=srgb` não bastava porque os args
  só valem em processo novo do WebView2 (ele reaproveita por UserDataFolder); no auto-update o
  processo reinicia limpo, então passam a valer.

## [1.2.7] - 2026-07-07

### Corrigido

- **Terminal ficava preto / "fechava" ao trocar de aba:** o `TabControl` padrão do WPF usa um único
  `ContentPresenter` (`SelectedContent`) e **destrói e recria** o conteúdo da aba selecionada a cada
  troca. Como a aba de sessão hospeda estado vivo e pesado (`TerminalTabView` com WebView2 + xterm.js
  e a sessão SSH), ir para "Hosts" e voltar recriava a View **vazia** — o xterm perdia todo o
  histórico (só mostrava o que chegasse depois) e a sessão parecia ter fechado. As correções de foco
  da v1.2.5 não resolviam porque poliam uma View que estava sendo jogada fora.
  Correção: novo `TabControlEx` (keep-alive) mantém um `ContentPresenter` por aba **vivo** no visual
  tree (criado sob demanda, só alterna `Visibility`); o terminal, o WebView2 e a sessão sobrevivem à
  troca de aba. `TabControlExTests` prova que trocar de aba e voltar reutiliza o MESMO presenter (não
  recria). Fechar a aba (botão ×, item removido da coleção) continua descarregando a View normalmente.

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
