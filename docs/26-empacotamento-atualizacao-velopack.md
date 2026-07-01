# 26 — Empacotamento e atualização do Desktop (Velopack)

Companion de `adr/ADR-019-empacotamento-atualizacao-velopack.md` — aqui ficam os comandos e o
runbook; lá fica a decisão e o porquê.

## Visão geral

`RemoteOps.Desktop` é empacotado e atualizado via **Velopack** (`vpk` CLI, pacote NuGet
`Velopack`, licença MIT verificada em `ADR-019`). Cada release gera:

- `RemoteOpsDesktop-<canal>-Setup.exe` (canal padrão `win`) — instalador padrão, com auto-update.
- `RemoteOpsDesktop-<canal>-Portable.zip` — versão portátil, sem instalação e **sem** auto-update.
- `*-full.nupkg` / `*-delta.nupkg` — pacotes completo e incremental, consumidos pelo
  `UpdateManager` em tempo de execução. Delta só é gerado quando já existe uma release anterior
  em `--outputDir` (ou no feed, ao publicar direto contra o GitHub) — a primeira release de um
  canal é sempre full.
- `releases.<canal>.json` — índice de releases.

Nomes e comportamento confirmados rodando `dotnet publish` + `vpk pack` localmente contra este
projeto (ver `## Validação local` no fim deste documento).

## Pré-requisitos locais

- .NET SDK 10 (mesmo usado pelo restante do repositório).
- CLI `vpk`, instalada como ferramenta .NET global:

```powershell
dotnet tool install -g vpk
```

## Publicar (self-contained win-x64)

Usa o perfil `src/RemoteOps.Desktop/Properties/PublishProfiles/win-x64-velopack.pubxml`
(self-contained, sem single-file — Velopack calcula delta arquivo a arquivo, então um único
executável elimina o ganho de banda do delta):

```powershell
dotnet publish src/RemoteOps.Desktop/RemoteOps.Desktop.csproj `
  -c Release `
  /p:PublishProfile=win-x64-velopack `
  -o publish/win-x64
```

## Empacotar (`vpk pack`)

```powershell
vpk pack `
  --packId RemoteOpsDesktop `
  --packVersion 0.10.0 `
  --packDir publish/win-x64 `
  --mainExe RemoteOps.Desktop.exe `
  --packTitle "RemoteOps Desktop" `
  --outputDir Releases
```

- `--packVersion` segue o esquema SemVer de `VERSIONING.md` (ex.: `0.10.0`, `0.10.0-beta.1`).
- `vpk pack` gera automaticamente o pacote delta em relação à release anterior presente em
  `--outputDir` (ou já publicada no feed, ao empacotar direto contra o GitHub — ver abaixo).
- Assinatura de código (`--signParams`/`--signTemplate`) fica de fora deste comando até a frente
  de assinatura estar pronta (`ADR-019` — fora de escopo).

## Feed: GitHub Releases

Hospedagem via **GitHub Releases** — suporte nativo do Velopack, sem infraestrutura própria
(`ADR-019` §4). Publicar uma release:

```powershell
vpk upload github `
  --repoUrl https://github.com/<org>/<repo> `
  --token $env:VPK_GITHUB_TOKEN `
  --outputDir Releases `
  --releaseName "RemoteOps Desktop 0.10.0" `
  --publish
```

- **`$env:VPK_GITHUB_TOKEN` nunca é definido em arquivo versionado.** Em CI, vem de um GitHub
  Environment com approval (mesmo padrão de `docs/11` §Secret management no CI); localmente, o
  operador exporta a variável na própria sessão de shell antes de rodar o comando.
- Se o repositório de releases for público, o cliente (`GithubSource` no `UpdateManager`) não
  precisa de token nenhum. Só é necessário token se o repositório for privado — e nesse caso o
  token do **cliente** (leitura) é diferente do token de **upload** usado aqui, e também nunca é
  embutido em código (ver `REMOTEOPS_UPDATE_FEED_TOKEN` abaixo).

## Consumo em runtime (`UpdateService`)

`src/RemoteOps.Desktop/Update/` implementa:

- `AppVersion` / `UpdatePolicy`: lógica pura de SemVer e do gate de atualização forçada — sem
  I/O, testada em `tests/RemoteOps.UnitTests/Desktop/Update/`.
- `IUpdatePolicyFeedSource` / `HttpUpdatePolicyFeedSource`: lê a versão mínima exigida de um JSON
  estático (`{"minimumRequiredVersion": "1.0.0"}`), hospedado como arquivo simples (ex.: via
  `raw.githubusercontent.com/<org>/<repo>/main/update-policy.json`) — separado do
  `releases.<canal>.json` do Velopack porque a política de "versão mínima" é uma decisão
  operacional que muda independente de qualquer release específica.
- `IUpdateService` / `VelopackUpdateService`: verificação sob demanda
  (`CheckForUpdatesAsync`, nunca baixa nada sozinha) e aplicação (`ApplyUpdateAsync`, baixa +
  reinicia via `UpdateManager` do Velopack).
- `AppCompositionRoot.RegisterUpdateService`: só registra `IUpdateService` na DI quando as
  variáveis de ambiente abaixo estão presentes — sem config, o app roda exatamente como hoje
  (fail-open, mesmo racional do `cloud.sync` em `App.OnStartup`).

### Variáveis de ambiente

| Variável | Obrigatória | Uso |
|---|---|---|
| `REMOTEOPS_UPDATE_FEED_REPO_URL` | Só se quiser habilitar update | URL do repositório GitHub usado pelo `GithubSource` (ex.: `https://github.com/<org>/<repo>`). |
| `REMOTEOPS_UPDATE_POLICY_URL` | Só se quiser habilitar update | URL absoluta do JSON de política (`minimumRequiredVersion`). |
| `REMOTEOPS_UPDATE_FEED_TOKEN` | Opcional | Token de leitura, só necessário se `REMOTEOPS_UPDATE_FEED_REPO_URL` apontar para repositório **privado**. Nunca hardcoded — variável de ambiente em tempo de execução da máquina do operador/imagem de deploy. |

Sem `REMOTEOPS_UPDATE_FEED_REPO_URL`/`REMOTEOPS_UPDATE_POLICY_URL`, `IUpdateService` não é
registrado e `App.xaml.cs` pula a checagem de update inteiramente.

### Comportamento de atualização forçada

Em `App.OnStartup`, antes de mostrar a `MainWindow`: se `IUpdateService` está registrado, o app
chama `CheckForUpdatesAsync()`. Se `UpdatePolicyResult.MustUpdate` for `true` (versão instalada
abaixo de `minimumRequiredVersion`), o app mostra um prompt obrigatório (sem opção de "lembrar
depois"), baixa e aplica a atualização, e encerra para reiniciar na nova versão — a `MainWindow`
nunca chega a abrir nessa condição. Falha na checagem (rede indisponível, feed fora do ar) é
fail-open: o app segue normalmente, sem bloquear o operador por uma verificação que não completou.

## Entry point custom (`Main()`, não o construtor de `App`)

`App.xaml` deixou de ser `ApplicationDefinition` (que gera um `Main()` automático) e virou
`Page`; `RemoteOps.Desktop.csproj` aponta `<StartupObject>` para `RemoteOps.Desktop.App`, que
define seu próprio `Main()` estático chamando `VelopackApp.Build().SetArgs(args).Run()` como
primeira instrução, antes de `InitializeComponent()`/`Run()`. Isso segue o padrão oficial do
Velopack para WPF (`docs.velopack.io/getting-started/csharp`) — colocar a chamada no construtor
de `App` (tentativa inicial deste PR) funciona, mas o próprio `vpk pack` **avisa** que não é o
padrão recomendado (`VelopackApp.Run() was found in method '...App::.ctor()', which does not
look like your application's entry point`); com o `Main()` explícito, o aviso vira uma
confirmação positiva (`Verified VelopackApp.Run() in '...App::Main(System.String)'`).

## Validação local

Rodado de fato neste projeto (não só documentado) em 2026-07-01, com .NET SDK 10.0.301 e
Velopack CLI 1.2.0 (`dotnet tool install -g vpk`):

```powershell
dotnet publish src/RemoteOps.Desktop/RemoteOps.Desktop.csproj -c Release -p:PublishProfile=win-x64-velopack -o publish/win-x64
vpk pack --packId RemoteOpsDesktop --packVersion 0.11.0 --packDir publish/win-x64 --mainExe RemoteOps.Desktop.exe --packTitle "RemoteOps Desktop" --outputDir Releases
```

Resultado confirmado:

- `publish/win-x64`: 310 arquivos, ~197 MB, self-contained, `RemoteOps.Desktop.exe` presente.
- `vpk pack` reporta `Verified VelopackApp.Run() in 'System.Void RemoteOps.Desktop.App::Main(System.String)'` (sem o aviso de entry point).
- `Releases/RemoteOpsDesktop-win-Setup.exe` gerado (~95 MB).
- `Releases/RemoteOpsDesktop-win-Portable.zip` gerado (~90 MB).
- `Releases/RemoteOpsDesktop-0.11.0-full.nupkg`, `Releases/releases.win.json`, `Releases/RELEASES` gerados.
- Nenhum pacote delta nesta rodada — esperado, não havia release anterior em `--outputDir`.
- Único aviso remanescente: `No signing parameters provided` — esperado, assinatura de código é
  frente separada (`ADR-019`).

Não validado neste PR (fora do alcance de uma máquina de desenvolvimento sem instalar o app de
fato): execução do `Setup.exe` instalando/desinstalando de verdade, e um ciclo real de
update-and-restart via `UpdateManager` contra um feed do GitHub publicado. Ambos exigem uma
segunda versão publicada e uma VM/máquina limpa — candidato a smoke test manual do checklist de
release (`docs/23-governanca-versionamento-changelog.md`).

## Fora de escopo (ver `ADR-019`)

- Assinatura de código do `Setup.exe`.
- Job de release automatizado no CI (`vpk pack`/`vpk upload` continuam manuais/scriptados
  localmente até essa frente ser aberta).
- Auto-update da versão portátil (não suportado pelo Velopack).
