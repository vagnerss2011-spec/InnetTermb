# Front "Novidades + Reportar problema" — Design

**Data:** 2026-07-02
**Branch/worktree:** `feature/gui-changelog-bugreport` (`C:\dev\remoteops-changelog-bugreport`), baseado em `feature/gui-credentials-winbox` (tip `e1886d3`).
**Depends-on:** PR #44 (`feature/gui-credentials-winbox`) — compartilha `SettingsWindow`, `SettingsViewModel`, `AppSettings`, `AppCompositionRoot`; deve mergear depois do #44 (guard "Depends-on order").

## Objetivo

Duas telas voltadas ao operador de campo, como abas novas no **modal de Configurações**:

1. **Novidades (changelog):** o operador vê, em linguagem simples, o que mudou entre versões.
2. **Reportar problema (bug report):** o operador descreve um problema e envia por e-mail pré-preenchido ao suporte, opcionalmente anexando diagnósticos (sem segredo), com preview antes de enviar.

## Decisões (travadas no brainstorming)

| Decisão | Escolha |
|---|---|
| Fonte do changelog | **Notas curadas embutidas** — arquivo enxuto no binário, offline. Sem API do GitHub, sem parser do `CHANGELOG.md` técnico. |
| Destino do bug report | **E-mail pré-preenchido (`mailto:`)** + cópia local. Sem backend, sem conta. |
| Colocação | **Abas no modal de Configurações** (vizinhas de Atualização/Sobre). Sem mexer na rail. |
| Diagnósticos | **Anexar com opt-in + preview** — título/descrição sempre; device id + versão + últimas N linhas de log via checkbox, com preview exato. |
| Indicador de novidades | **Sim** — `LastSeenChangelogVersion` no `AppSettings` + badge sutil no menu do avatar. |
| E-mail de suporte | `suporte@innet.tec.br` (um único `const` no código). |

## Restrições globais (herdadas do projeto)

- `.NET 10`, WPF (`net10.0-windows`), `TreatWarningsAsErrors=true` → build **0/0**. `Nullable=enable`, `ImplicitUsings=enable`.
- Testes xUnit (`[Fact]`/`Assert`); namespace espelha a pasta (`RemoteOps.UnitTests.Desktop.*`); a suíte existente (**437**) deve ficar verde.
- **CI roda `dotnet format --verify-no-changes`** (gate "Format check") **em Release** — rodar `dotnet format` local antes de commitar/push (ver memória `feedback-remoteops-ci-format-gate`).
- **Nenhum segredo** em UI, log, e-mail, fixture ou commit. Diagnósticos usam só fontes secret-free (device id, versão do app/OS, linhas de log já redigidas). Texto livre do operador é mostrado no preview antes de enviar.
- WorkspaceId local fixo: `"ws-local"`.
- Rótulos: o resto do app é pt-BR; estas telas também em **pt-BR** (o inglês foi só no Keychain).

## Arquitetura

Padrão MVVM já existente (`BaseViewModel`/`RelayCommand`). O `SettingsWindow` (modal `TabControl`) ganha 2 `TabItem`s. O `SettingsViewModel` passa a expor dois VMs-filho de responsabilidade única: `Changelog` (`ChangelogViewModel`) e `BugReport` (`BugReportViewModel`). Cada um consome um serviço pequeno e puro. Nada de rede, vault ou tabela SQLCipher nova.

### Unidade 1 — Novidades (changelog)

**Dado (recurso embutido):** `src/RemoteOps.Desktop/Resources/operator-changelog.json`, marcado `<EmbeddedResource>` no csproj. Formato:

```json
{
  "entries": [
    {
      "version": "1.0.0",
      "date": "2026-07-02",
      "highlights": [
        "Chaveiro: crie login e senha direto no app (aba Keychain).",
        "WinBox: escolha o executável em Configurações → Ferramentas externas; o app fixa o hash automaticamente.",
        "Anexe uma credencial a um host no editor de endpoints."
      ]
    }
  ]
}
```

> **Convenção de manutenção:** a cada release, atualizar `operator-changelog.json` (destaques de operador) junto com o `CHANGELOG.md` técnico. Responsável: `release-manager-agent`.

**Serviço:**
- `Changelog/ChangelogEntry.cs` — `sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<string> Highlights)`.
- `Changelog/IChangelogSource.cs` — `IReadOnlyList<ChangelogEntry> Load()`.
- `Changelog/EmbeddedChangelogSource.cs` — lê o stream do assembly (`Assembly.GetManifestResourceStream`), desserializa via `System.Text.Json`. Falha de leitura/parse → lista vazia (nunca lança).

**ViewModel:** `ViewModels/ChangelogViewModel.cs`
- Ctor recebe `IChangelogSource`, `ISettingsStore` e a versão atual do app (`string currentVersion`).
- `ObservableCollection<ChangelogItemViewModel> Entries` — cada item tem `Version`, `Date`, `Highlights`, `IsNew`.
- `IsNew` = `entry.Version > LastSeenChangelogVersion` via `AppVersion.TryParse(string?, out AppVersion)` + `CompareTo` (`Update/AppVersion.cs` — `readonly record struct : IComparable<AppVersion>`). Parse inválido (qualquer lado) → tratado como não-novo.
- `MarkAllSeen()` — chamado quando a aba é aberta: grava `AppSettings.LastSeenChangelogVersion = versão mais recente` via o padrão `_settings with { … }` + `_store.Save`.

**Badge:** `BrowserViewModel.HasUnreadChangelog` (bool) — compara a versão embutida mais recente com `AppSettings.LastSeenChangelogVersion`; liga um pontinho no `ContextMenu` do avatar (`BrowserView.xaml`). Recalcula quando o modal fecha.

**View:** `TabItem "Novidades"` no `SettingsWindow.xaml` — `ScrollViewer` + `ItemsControl` de cartões (cabeçalho versão + data; bullets dos `Highlights`; chip "novo" quando `IsNew`). Estado vazio: "Sem novidades para mostrar."

### Unidade 2 — Reportar problema (bug report)

**Contrato/serviço:**
- `Reporting/SupportContact.cs` — `public const string Email = "suporte@innet.tec.br";`.
- `Reporting/BugReport.cs` — `sealed record BugReport(string Title, string Description, bool IncludeDiagnostics)`.
- `Reporting/IDiagnosticsProvider.cs` — `string BuildDiagnostics()` monta o bloco secret-free: device id (`%AppData%\RemoteOps\device.id`, se existir), versão do app, versão do Windows (`RuntimeInformation.OSDescription`), últimas **N=30** entradas de `LogsViewModel.Events` (`ObservableCollection<string>`, singleton no DI; `LogsViewModel : BaseViewModel, IUiLogSink`). `DiagnosticsProvider` recebe o `LogsViewModel` (leitura) e as strings de versão por injeção — puro e testável com um `LogsViewModel` populado.
- `Reporting/IBugReportComposer.cs` / `MailtoBugReportComposer.cs`:
  - `string BuildPreview(BugReport report)` — texto **exato** que irá no corpo (inclui diagnósticos só se `IncludeDiagnostics`).
  - `Uri BuildMailtoUri(BugReport report)` — `mailto:suporte@innet.tec.br?subject=<enc>&body=<enc>`, componentes URL-encoded. Se o corpo exceder o limite seguro (`~1800` chars totais), trunca **só a seção de diagnósticos** com nota "[diagnóstico truncado — cópia completa salva localmente]"; título/descrição nunca truncados.
  - `Task<string> SaveLocalCopyAsync(BugReport report)` — grava o preview completo (sem truncar) em `%AppData%\RemoteOps\bug-reports\{yyyyMMdd-HHmmss}.txt`; devolve o caminho.

**ViewModel:** `ViewModels/BugReportViewModel.cs`
- Props: `Title`, `Description`, `IncludeDiagnostics` (default `true`), `PreviewText` (computado sob demanda), `StatusMessage`.
- `PreviewCommand` — atualiza `PreviewText` via `composer.BuildPreview`.
- `SubmitCommand` — **independente do `SaveCommand` do modal**: `SaveLocalCopyAsync` → abre `BuildMailtoUri` (`Process.Start` com `UseShellExecute=true`); em falha do mailto (sem cliente), seta `StatusMessage` orientando "Copiar" e não perde o texto. `CanSubmit` = `Title` e `Description` não vazios.
- `CopyCommand` — copia o preview completo pra área de transferência (fallback do mailto).

**View:** `TabItem "Reportar problema"` — `TextBox` título, `TextBox` descrição (multi-linha, `AcceptsReturn`), `CheckBox` "Incluir diagnósticos", `Expander`/área read-only com o preview, botões **Enviar por e-mail** (`SubmitCommand`) e **Copiar** (`CopyCommand`), `TextBlock` de status.

## Fluxo de dados

- **Changelog:** JSON embutido → `EmbeddedChangelogSource.Load()` → `ChangelogViewModel` → View. `LastSeen` persistido via `ISettingsStore`. Badge lido pelo `BrowserViewModel`.
- **Bug report:** form → `BugReportViewModel` → `MailtoBugReportComposer` → (`Process.Start` do `mailto:` + arquivo `.txt` local). Sem rede, sem vault, sem segredo.

## Tratamento de erro

- Changelog: recurso ausente/corrompido → lista vazia + estado vazio; SemVer inválido → não-novo. Nunca crasha.
- Bug report: sem cliente de e-mail → `StatusMessage` + botão Copiar; falha ao gravar cópia → avisa, **mantém** o texto; corpo longo → trunca só diagnóstico (cópia local completa).
- Independência do `IUpdateService`: estas telas **não** dependem dele (100% offline), então funcionam em Debug/ZIP portátil onde `IUpdateService` é nulo.

## Segurança

- Diagnósticos só de fontes secret-free; texto livre é do operador e revisado no preview antes de enviar (e vai a e-mail interno, não público). Opt-in + preview satisfazem a mitigação de vazamento.
- Sem segredo em log/e-mail/arquivo/commit (regra do projeto).

## Testes (TDD)

- `EmbeddedChangelogSource` — parse do JSON embutido (contagem, ordem, highlights); recurso ausente → lista vazia.
- `ChangelogViewModel` — `IsNew` vs `LastSeenChangelogVersion` (SemVer); `MarkAllSeen()` persiste a versão mais recente.
- `AppSettings.LastSeenChangelogVersion` — round-trip via `JsonSettingsStore`.
- `BrowserViewModel.HasUnreadChangelog` — verdadeiro quando embutido > seen; falso quando igual.
- `MailtoBugReportComposer` — mailto correto e URL-encoded; inclui/exclui diagnóstico conforme opt-in; trunca só diagnóstico quando longo (título/descrição intactos); `BuildPreview` bate com o corpo; `SaveLocalCopyAsync` grava o arquivo.
- `BugReportViewModel` — `CanSubmit` (gating por título+descrição); `Submit` não depende do `Save` do modal.
- `IDiagnosticsProvider` — bloco contém versão/OS/últimas N linhas; **não** contém termos de segredo (teste de guarda com blocklist).

## Arquivos

**Novos:**
- `src/RemoteOps.Desktop/Resources/operator-changelog.json` (embedded)
- `src/RemoteOps.Desktop/Changelog/{ChangelogEntry,IChangelogSource,EmbeddedChangelogSource}.cs`
- `src/RemoteOps.Desktop/ViewModels/ChangelogViewModel.cs`
- `src/RemoteOps.Desktop/Reporting/{SupportContact,BugReport,IDiagnosticsProvider,DiagnosticsProvider,IBugReportComposer,MailtoBugReportComposer}.cs`
- `src/RemoteOps.Desktop/ViewModels/BugReportViewModel.cs`
- Testes em `tests/RemoteOps.UnitTests/Desktop/{Changelog,Reporting,ViewModels}/`

**Modificados:**
- `src/RemoteOps.Desktop/Views/SettingsWindow.xaml(.cs)` — 2 `TabItem`s + fiação
- `src/RemoteOps.Desktop/ViewModels/SettingsViewModel.cs` — expõe `Changelog`/`BugReport`
- `src/RemoteOps.Desktop/Infrastructure/AppSettings.cs` — `+ LastSeenChangelogVersion`
- `src/RemoteOps.Desktop/ViewModels/BrowserViewModel.cs` + `Views/BrowserView.xaml` — badge de novidades
- `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs` — DI dos novos serviços/VMs
- `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj` — embed do JSON

## Fora de escopo (YAGNI)

Sem API do GitHub, sem parser de markdown, sem tabela SQLCipher, sem endpoint cloud, sem anexos no e-mail. Curado + `mailto:` + arquivo. Manutenção do JSON curado é convenção de release, não código.
