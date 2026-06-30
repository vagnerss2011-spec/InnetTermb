# ADR-014 — RDP: hospedagem ActiveX em aba, políticas de redirecionamento, feature flag e empacotamento do interop COM

## Status

Aceita.

## Contexto

A frente `feature/integration-rdp` implementa a sessão RDP real descrita em
ADR-004 e `docs/08-rdp-terminal-server.md`: abrir o controle Microsoft Remote
Desktop ActiveX (MSTSCAX/`mstscax.dll`) numa aba viva do Desktop, com
credencial resolvida do vault, redirecionamentos sensíveis desligados por
padrão, NLA obrigatório e auditoria de início/fim de sessão.

O plano original (task brief desta frente) previa consumir o MSTSCAX via
`<COMReference Include="MSTSCLib">` no `.csproj` — o mecanismo MSBuild padrão
para gerar interop COM tipado automaticamente a cada build. Esse plano não
sobreviveu ao contato com o toolchain real do repositório: `<COMReference>`
depende da task MSBuild `ResolveComReference`, que **só existe na build
completa do .NET Framework que acompanha o Visual Studio** — não está
implementada no MSBuild do .NET SDK usado por `dotnet build`/`dotnet test`,
que é o que este repositório usa tanto localmente quanto no CI
(`dotnet build --configuration Release` em `windows-latest`,
`.github/workflows/ci.yml`). A tentativa de build falhou com `MSB4803`
("This task depends on the .NET Framework version of MSBuild...") mesmo com o
typelib do MSTSCAX devidamente registrado na máquina de desenvolvimento — ou
seja, não era um problema de registro de COM ausente, e sim uma
incompatibilidade arquitetural entre projetos SDK-style/`dotnet build` e o
mecanismo `<COMReference>` legado. Esta ADR documenta a decisão tomada para
resolver isso (Decisão 6) e as demais decisões de produto/segurança da
hospedagem RDP, porque uma revisão de código anterior identificou que essa
mudança de empacotamento — que adiciona binários `.dll` versionados a um
repositório C# até então livre de binários — não estava documentada em
nenhuma ADR.

## Decisão 1 — Separação lógica pura / COM-UI

`RdpConnectionConfigBuilder` (host/porta/usuário/políticas de redirecionamento)
é uma classe pura em `RemoteOps.Rdp`, sem dependência de COM ou UI — testável
em qualquer plataforma/CI. `RdpSessionProvider` faz apenas o trabalho
não-visual (resolve endpoint+usuário, monta `RdpConnectionConfig`, audita
`SessionOpened`/`SessionClosed` via `IRdpAuditSink`, devolve `SessionHandle`)
e **nunca toca o vault**. A camada COM (`RdpTabView.xaml.cs`,
`AxMsRdpClient9NotSafeForScripting`) fica isolada no Desktop, não testável em
headless (COM/ActiveX não roda sem UI thread/STA) — coberta apenas por
verificação manual, ainda não realizada nesta etapa (ver Pendências).

## Decisão 2 — Senha com lifetime mínimo, resolvida no momento de conectar

Ao contrário do SSH (que resolve a senha dentro do próprio `OpenAsync`, já que
essa chamada é o connect), o RDP separa "abrir handle/auditar"
(`RdpSessionProvider.OpenAsync`, sem vault) de "conectar visualmente"
(`RdpTabView`, disparado em `OnLoaded` do `WindowsFormsHost`). A senha só é
lida do vault em `RdpTabView.InitAndConnectAsync`, via
`RdpTabViewModel.ResolvePasswordAsync()`, imediatamente antes de ser atribuída
a `AdvancedSettings9.ClearTextPassword` — a variável local `password` sai de
escopo logo em seguida, usada exatamente uma vez. Mesma mitigação de lifetime
mínimo documentada em ADR-009 §FIX-3 (a string gerenciada persiste até o GC;
não há alternativa sem mudar a API exposta pelo MSTSCAX, que exige `string`
em `ClearTextPassword`).

Depois de uma rodada de code review identificar um bug real, `RdpTabView`
também tem guards de `IsLoaded` logo após os dois pontos de `await`
(`PrepareAsync()` e `ResolvePasswordAsync()`): se a aba for fechada
no meio do connect, o código chama `RdpTabViewModel.CloseAsync()` em vez de
abandonar uma sessão já aberta e auditada — evita órfão de conexão
credenciada viva.

## Decisão 3 — Redirecionamentos OFF por padrão

`RdpRedirectionPolicy.Default` desliga clipboard, drive, impressora e áudio.
`RdpTabView` aplica esses campos 1:1 em `AdvancedSettings9` — nunca há valor
hardcoded como "on"; tudo é config-driven a partir da política resolvida.

`RdpRedirectionPolicy.UsbRedirectionEnabled` existe no record mas **não é
aplicado em nenhum lugar do `RdpTabView`**: isso é um gap de MVP rastreado, não
um bug de implementação — não há nenhuma chamada esquecida ou comentada, o
wiring simplesmente não foi feito ainda. Correção (confirmada por reflexão,
ver Decisão 7): o surface real do assembly gerado
(`IMsRdpClientAdvancedSettings8`, retornado por `AdvancedSettings9`) **expõe**
`RedirectDevices`/`RedirectPOSDevices` — a propriedade padrão do MSTSCAX para
redirecionamento de dispositivos Plug-and-Play, que é o equivalente mais
próximo disponível neste controle a "redirecionamento USB" (clientes RDP
modernos roteiam dispositivos classe USB via redirecionamento PnP, não uma API
USB dedicada). Ou seja, o gap é puramente de wiring — a interface já em uso
já tem onde conectar `UsbRedirectionEnabled` — não uma limitação estrutural da
interface disponível.

Não há perfil/política de override nesta PR — habilitar redirecionamentos
para grupos específicos exige uma decisão de produto futura (RBAC por grupo,
conforme `docs/08` §Políticas recomendadas), tratada como trabalho
subsequente.

## Decisão 4 — NLA obrigatório, certificado nunca ignorado silenciosamente

`RdpConnectionConfig.NlaRequired` é sempre `true` no MVP (sem opção de
desligar) e é repassado para `AdvancedSettings9.EnableCredSspSupport`.
`AdvancedSettings9.AuthenticationLevel` é hardcoded em `2`
("exigir/tentar autenticação de servidor, avisar o usuário em caso de
falha" — semântica documentada pela Microsoft para esse valor). Importante:
esse valor **não suprime** o prompt nativo de aviso de certificado inválido do
MSTSCAX — o diálogo continua aparecendo normalmente ao usuário.

`RdpActions.CertificateAccepted`/`CertificateRejected` já existem no enum de
`RdpAuditEvent` (`src/RemoteOps.Rdp/RdpAuditEvent.cs`), mas **não são emitidos
em nenhum lugar ainda**. O gancho de evento exato do MSTSCAX para capturar a
decisão do usuário no prompt de certificado não foi identificado/conectado
nesta implementação — é uma pendência explícita (ver Consequências), não algo
parcialmente implementado.

## Decisão 5 — Feature flag `rdp.enabled`, default OFF

`IFeatureFlags`/`EnvironmentFeatureFlags` (Desktop,
`src/RemoteOps.Desktop/Infrastructure/IFeatureFlags.cs`) lê flags habilitadas
da variável de ambiente `REMOTEOPS_FEATURE_FLAGS` (lista separada por
vírgula); sem a variável, nenhuma flag está habilitada. `FeatureFlagNames.RdpEnabled
= "rdp.enabled"`.

A flag gateia **dois pontos independentes** (defesa em profundidade, não só
ocultação de UI):

1. `InspectorViewModel` — visibilidade do botão "Conectar RDP".
2. `MainViewModel.OnSessionRequested` — só roteia para o fluxo RDP real
   (`RdpTabViewModel` + `Tabs.OpenRdpTab`) se `rdpEnabled` for verdadeiro
   *e* `_rdpProvider`/`_rdpCredentialResolver` estiverem presentes; caso
   contrário, o protocolo `rdp` cai no placeholder pré-existente, mesmo
   comportamento de antes desta PR.

Habilitar a flag em produção requer revisão do `security-agent`
(CLAUDE.md §Atualizações de arquitetura v2 — "Recursos sensíveis devem usar
feature flags e revisão do security-agent").

## Decisão 6 — Pivot de `<COMReference>` para binários de interop checados em `lib/`

**Este é o desvio mais importante em relação ao plano original desta frente.**

### O problema

`<COMReference Include="MSTSCLib">` é o jeito "automático" de consumir um
controle COM/ActiveX no MSBuild — gera o interop a cada build a partir do
typelib registrado na máquina. Funciona perfeitamente em builds feitas pelo
MSBuild completo do Visual Studio. **Não funciona em `dotnet build`**: a task
`ResolveComReference` que implementa esse comportamento é exclusiva do MSBuild
de .NET Framework; o SDK do .NET (usado por `dotnet build`/`dotnet test`, que
é o que `RemoteOps.sln` usa local e em CI) não a implementa. O build falhava
com `MSB4803` mesmo com o typelib do MSTSCAX confirmadamente registrado —
confirmando que era incompatibilidade arquitetural, não falta de registro ou
de ferramenta.

### A decisão

Após o usuário ser apresentado as opções, decidiu-se: instalar o Windows SDK
(que traz `TlbImp.exe`/`AxImp.exe`, as ferramentas do .NET Framework 4.8.1 SDK)
e gerar os assemblies de interop **uma única vez**, offline, diretamente a
partir de `C:\Windows\System32\mstscax.dll`, e **checar os binários
resultantes no repositório** em `src/RemoteOps.Rdp/lib/`:

- `MSTSCLib.dll` — interop COM bruto (raw RCW).
- `AxInterop.MSTSCLib.dll` — wrapper `AxHost`-derivado (WinForms), o que
  permite hospedar o controle dentro de `WindowsFormsHost`.

Os dois precisam ser gerados **na mesma invocação** de `AxImp.exe` (que
internamente também regenera o `MSTSCLib.dll` bruto), e não como duas chamadas
separadas de `TlbImp.exe` + `AxImp.exe`: gerar os dois assemblies
separadamente produz tipos com identidade incompatível entre si (o
`AxInterop` gerado não reconhece os tipos do `MSTSCLib` gerado numa invocação
distinta) — isso foi tentado primeiro e descartado.

### Comandos de regeneração

Só regenerar se o COM surface do MSTSCAX mudar (nova versão do cliente RDP da
Windows). A partir de um prompt com o Windows SDK / .NET Framework 4.8.1 SDK
Tools no `PATH` (ou usando os caminhos completos de
`C:\Program Files (x86)\Microsoft SDKs\Windows\...\Bin\`):

```bat
:: Gera AxInterop.MSTSCLib.dll e, na mesma invocação, MSTSCLib.dll —
:: manter as duas saídas desta ÚNICA chamada (não dividir em TlbImp + AxImp
:: separados: produz tipos com identidade incompatível entre si).
AxImp.exe C:\Windows\System32\mstscax.dll /out:AxInterop.MSTSCLib.dll
```

Em seguida, copiar `MSTSCLib.dll` e `AxInterop.MSTSCLib.dll` resultantes para
`src/RemoteOps.Rdp/lib/`, substituindo os existentes, e validar com
`dotnet build` mais o spike manual descrito em Decisão 1/Pendências antes de
commitar.

Typelib GUID do MSTSCAX, para referência ao localizar/confirmar a versão
correta do `mstscax.dll` de origem: `{8c11efa1-92c3-11d1-bc1e-00c04fa31489}`
(comentário já presente em `src/RemoteOps.Rdp/RemoteOps.Rdp.csproj`).

### Wiring no `.csproj`

`RemoteOps.Rdp.csproj` referencia os binários via `<Reference>` simples (não
`<COMReference>`):

```xml
<Reference Include="MSTSCLib">
  <HintPath>lib\MSTSCLib.dll</HintPath>
  <Private>true</Private>
  <EmbedInteropTypes>false</EmbedInteropTypes>
</Reference>
<Reference Include="AxMSTSCLib">
  <HintPath>lib\AxInterop.MSTSCLib.dll</HintPath>
  <Private>true</Private>
  <EmbedInteropTypes>false</EmbedInteropTypes>
</Reference>
```

`RemoteOps.Desktop.csproj` precisou da **mesma referência direta**, duplicada
(com `HintPath` relativo apontando para `..\RemoteOps.Rdp\lib\`): em projetos
SDK-style, itens `<Reference>` simples (ao contrário de `PackageReference`)
**não são transitivos** através de `ProjectReference`. Isso só foi descoberto
porque uma build inicial do Desktop falhou com `CS0246` (tipo não encontrado)
ao tentar usar `AxMsRdpClient9NotSafeForScripting` em `RdpTabView.xaml.cs` sem
essa referência direta.

### Por que isso resolve o risco que o plano original previa

O brief original desta frente previa como risco de CI "o typelib pode não
estar registrado no runner do GitHub Actions". Essa decisão **elimina esse
risco por completo**, porque o build deixa de tocar o typelib/registro COM em
qualquer momento — ele só vincula contra assemblies `.dll` já compilados e
versionados no Git, com `HintPath` relativo (sem caminho específico de
máquina). O build é portátil e CI-safe por construção, não por sorte de
ambiente.

## Decisão 7 — API real confirmada do MSTSCAX (via reflexão, não suposição)

A classe usada é `AxMSTSCLib.AxMsRdpClient9NotSafeForScripting`. **Correção
(confirmada por reflexão sobre `AxInterop.MSTSCLib.dll`):** ao contrário do que
uma versão anterior desta ADR afirmava, `AxMsRdpClient9NotSafeForScripting`
**não** é a classe de número mais alto totalmente instanciável — as classes
`AxMsRdpClient10NotSafeForScripting`, `AxMsRdpClient11NotSafeForScripting` e
`AxMsRdpClient12NotSafeForScripting` também existem como classes públicas,
não-abstratas, derivadas de `AxHost`, com construtor público sem parâmetros —
exatamente tão instanciáveis quanto a Ax9. Ax9 foi escolhida não por ser o
teto disponível, mas porque é uma versão estável e suficiente para as
necessidades deste MVP: `AdvancedSettings9` retorna exatamente o mesmo tipo,
`MSTSCLib.IMsRdpClientAdvancedSettings8`, tanto em `AxMsRdpClient9...` quanto
em `AxMsRdpClient12...` — não há diferença funcional, para a superfície de
propriedades que este código efetivamente usa, entre hospedar via Ax9 ou
Ax12. Isso não muda nada em `RdpTabView.xaml.cs` (que continua correto ao usar
Ax9) — só corrige a justificativa que esta ADR dava para essa escolha.

`AxMsRdpClient9NotSafeForScripting.AdvancedSettings9` retorna o tipo
`MSTSCLib.IMsRdpClientAdvancedSettings8` — o descompasso de número entre o
nome da propriedade (`9`) e o nome da interface (`8`) é nomenclatura real da
Microsoft, não erro de digitação. Essa interface expõe `RDPPort`,
`ClearTextPassword`, `AuthenticationLevel`, `EnableCredSspSupport`,
`NegotiateSecurityLayer`, `RedirectClipboard`, `RedirectDrives`,
`RedirectPrinters`, `AudioRedirectionMode` (`uint`: `0` = tocar localmente,
`2` = não tocar), e também `RedirectDevices`/`RedirectPOSDevices` (bool,
leitura/escrita) — o equivalente mais próximo a um controle de
redirecionamento USB disponível neste nível de interface (ver Decisão 3;
ainda não conectado em `RdpTabView`, mas a propriedade existe).

O evento real de desconexão é
`OnDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e)`, com a
razão em `e.discReason` — **não** um `int` solto como parâmetro. Isso foi
inicialmente registrado de forma incorreta durante a implementação (reflexão
acidental sobre o delegate de mesmo nome no namespace de interface bruta
`MSTSCLib`, em vez do delegate real exposto por `AxMSTSCLib`); foi corrigido e
reverificado por compilação bem-sucedida mais reflexão independente sobre o
assembly final.

## Decisão 8 — Conflito de implicit usings WinForms × WPF

Ligar `UseWindowsForms=true` junto de `UseWPF=true` (necessário em
`RemoteOps.Desktop.csproj` para hospedar `WindowsFormsHost`/MSTSCAX) faz o SDK
injetar **ambos** os namespaces `System.Windows.Forms` e `System.Windows`
como implicit global usings (já que `ImplicitUsings=enable`), causando
`CS0104` (referência ambígua) em `UserControl`/`Application` em todo arquivo
WPF pré-existente do projeto — nenhum dos quais referencia WinForms. Corrigido
com:

```xml
<Using Remove="System.Windows.Forms" />
```

Confirmado por grep que nada no projeto dependia do implicit using removido
(o único consumo de WinForms, `Rdp/RdpTabView.xaml.cs`, já qualifica os tipos
explicitamente via `using AxMSTSCLib;` e referências totalmente qualificadas
onde necessário).

## Consequências

### Positivas

- Lógica de configuração (`RdpConnectionConfigBuilder`/`RdpSessionProvider`)
  100% testável sem Windows/COM.
- Senha nunca retida em campo de ViewModel — lifetime mínimo, mesma mitigação
  já adotada para SSH/WinBox (ADR-009).
- Rollout controlado por feature flag com defesa em profundidade (UI +
  roteamento), sign-off de segurança obrigatório antes de habilitar em
  produção.
- Build determinístico e portátil: não depende de registro de typelib COM na
  máquina/runner — apenas de assemblies versionados no Git.
- Comandos de regeneração documentados nesta ADR (e referenciados em
  `docs/08`), fechando um gap que uma revisão de código anterior havia
  identificado.

### Negativas

- Dois binários `.dll` compilados (`MSTSCLib.dll`, `AxInterop.MSTSCLib.dll`)
  agora vivem no controle de versão de um repositório C# até então livre de
  binários — exige disciplina de regeneração documentada (Decisão 6) sempre
  que o MSTSCAX subjacente mudar, em vez de "simplesmente recompilar".
- `RdpTabView`/camada COM não tem cobertura de teste automatizado (COM/ActiveX
  não roda headless) — depende inteiramente de verificação manual em
  laboratório a cada mudança relevante.
- Duplicação de `<Reference>` entre `RemoteOps.Rdp.csproj` e
  `RemoteOps.Desktop.csproj` (não-transitividade de `ProjectReference`) é uma
  pegadinha que pode se repetir se outro projeto vier a depender do MSTSCAX.

### Pendências (rastrear como issue após merge)

- **Auditoria de certificado não implementada**: `RdpActions.CertificateAccepted`/
  `CertificateRejected` existem no enum mas não são emitidos em lugar nenhum —
  o gancho de evento exato do MSTSCAX para a decisão do usuário no prompt de
  certificado ainda não foi identificado/conectado. O prompt nativo continua
  aparecendo (não suprimido), mas a decisão do usuário não é auditada.
- **Verificação manual end-to-end ainda não realizada**: não há, até o
  momento desta ADR, uma sessão de conexão real contra um host/lab RDP
  validando o fluxo completo (`SPIKE-RDP-001`/`002`/`003` em `docs/08`); a
  validação feita até aqui é build-success + reflexão sobre o assembly, não
  uma conexão de fato. Necessário antes de habilitar `rdp.enabled` em
  qualquer ambiente real.
- `UsbRedirectionEnabled` em `RdpRedirectionPolicy` permanece sem efeito
  prático — mas conectar `RdpRedirectionPolicy.UsbRedirectionEnabled` →
  `AdvancedSettings9.RedirectDevices` é um follow-up pequeno e bem definido (a
  propriedade já existe na interface já em uso, `IMsRdpClientAdvancedSettings8`
  — ver Decisão 3/Decisão 7), **não** um gap estrutural que exija investigar
  uma interface MSTSCAX adicional.
- Decidir se a aba RDP deve ser "pinned" para sobreviver a um re-template de
  troca de aba, ou se reconectar é aceitável no MVP — não decidido nesta
  frente.
