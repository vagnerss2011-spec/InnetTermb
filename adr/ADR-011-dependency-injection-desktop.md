# ADR-011 — Dependency Injection no Desktop (Microsoft.Extensions.DI)

- **Status:** Aceita
- **Data:** 2026-06-30
- **Decidente:** desktop-shell-agent / remoteops-architect
- **Frente:** `feature/integration-composition`

---

## Contexto

O Desktop WPF iniciou com wiring manual em `App.xaml.cs` (`new InMemoryLocalStore()`). Com a chegada dos módulos de Terminal (ADR-009), Vault (ADR-003) e WinBox (ADR-006), o número de dependências cresceu para mais de dez interfaces — tornando o wiring manual insustentável e difícil de testar.

Além disso, os provedores SSH e Telnet precisam ser resolvidos por protocolo (`RemoteProtocol.Ssh` / `RemoteProtocol.Telnet`), o que sugere keyed services.

---

## Decisão

Adotar **`Microsoft.Extensions.DependencyInjection`** como container DI no Desktop, com wiring centralizado em `AppCompositionRoot.Build()` (padrão Composition Root).

### Regras de uso

1. **Composition Root único:** todo `AddSingleton`/`AddKeyedSingleton` ocorre em `AppCompositionRoot`; nenhum outro lugar chama o container diretamente (sem Service Locator).
2. **ServiceProvider descartado no `OnExit`:** `App.xaml.cs` chama `_serviceProvider?.Dispose()`.
3. **`validateOnBuild: false`:** `ISshConnectionFactory` e `ITelnetConnectionFactory` são `internal` ao `RemoteOps.Terminal`; os construtores públicos dos providers usam `factory: null` como default. A cobertura equivalente é garantida por `CompositionRootSmokeTests`.
4. **Nenhum segredo no container:** `CredentialVault` é registrado como singleton; os segredos trafegam apenas via `IVault.RetrieveAsync` → `VaultSecret` (IDisposable) em cada chamada de resolver.
5. **Nova lib exige ADR:** qualquer PackageReference novo para o container (ex.: Autofac, LightInject) exige revisão desta ADR antes da adição.

---

## Alternativas consideradas

| Opção | Motivo de rejeição |
|---|---|
| Wiring manual continuado | Insustentável com >10 interfaces; testabilidade ruim |
| Autofac | Dependência NuGet extra sem benefício claro no escopo atual |
| Microsoft.Extensions.Hosting | Overhead de `IHost`/`IHostedService` desnecessário em WPF sem background services |
| Pure DI (código gerado) | Produtividade baixa; pouco benefício sobre MEDI no escopo do projeto |

**`Microsoft.Extensions.DependencyInjection`** é adicionado via `PackageReference` (versão 10.0.0). Diferente do ASP.NET Core (que o traz no shared framework `Microsoft.AspNetCore.App`), o WPF (`Microsoft.WindowsDesktop.App`) **não** inclui o container — o pacote é necessário. É uma lib oficial Microsoft alinhada ao runtime .NET 10, sem dependências transitivas relevantes.

---

## Consequências

- **Positivas:** grafo de dependências explícito e testável; keyed services para SSH/Telnet sem ambiguidade; `Dispose` em cascata no shutdown.
- **Negativas:** `validateOnBuild: false` reduz checagem estática; mitigado por `CompositionRootSmokeTests` que verifica a resolução de cada serviço.
- **Neutras:** `MainViewModel` continua como mediador MVVM; DI injeta o store e os providers sem alterar a interface do ViewModel.

---

## Implementação

- `src/RemoteOps.Desktop/Integration/AppCompositionRoot.cs` — ponto único de wiring.
- `src/RemoteOps.Desktop/App.xaml.cs` — chama `Build()` no `OnStartup`, `Dispose` no `OnExit`.
- `tests/RemoteOps.UnitTests/Desktop/CompositionRootSmokeTests.cs` — 16 testes de resolução.
- `InternalsVisibleTo("RemoteOps.UnitTests")` no `.csproj` para acesso a `AppCompositionRoot`.
