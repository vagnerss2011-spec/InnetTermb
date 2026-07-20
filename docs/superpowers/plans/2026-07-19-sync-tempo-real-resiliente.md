# Sync em tempo real resiliente — Plano de Implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado)
> ou superpowers:executing-plans para implementar tarefa a tarefa. Passos usam checkbox (`- [ ]`).

**Objetivo:** o que é cadastrado num PC aparece no outro em segundos, sem fechar e abrir o app, e
degrada com elegância (teto previsível) se o WebSocket cair.

**Arquitetura:** duas camadas com modos de falha diferentes — (1) canal SignalR resiliente, com
auto-join garantido no servidor; (2) laço de polling blindado como rede de segurança. Ver spec:
`docs/superpowers/specs/2026-07-19-sync-tempo-real-resiliente-design.md`.

**Stack:** .NET 10, C#, WPF (cliente), ASP.NET Core + SignalR + EF/Npgsql (servidor), xUnit.

## Restrições Globais

- **Repo:** `C:\dev\remoteops-native`, branch `feat/sync-tempo-real` (já criada, a partir de `152d6d6`).
- **Gates (rodar ANTES de cada push):** `dotnet build -c Release`, `dotnet test`,
  `dotnet format --verify-no-changes`. `TreatWarningsAsErrors=true` e `EnforceCodeStyleInBuild=true`
  (`Directory.Build.props:5,7`) — warning vira erro.
- **Nenhum log novo pode imprimir a URL do hub** — o JWT vai na query (ADR-013).
- **Ordem metadados → segredos é intocável** (`SyncOrchestrator.cs:94-99`).
- **Comentários e textos de UI em português do Brasil**, no tom do código existente (explicam *por
  que*, não *o quê*).
- **Nada toca cripto, cofre ou envelopes.** E2EE permanece intacto por construção.
- **Compatibilidade:** o auto-join é aditivo — cliente antigo (que chama `JoinWorkspace` explícito)
  continua funcionando.

---

## Estrutura de Arquivos

**Servidor:**
- Modificar: `src/RemoteOps.Cloud/Hubs/SyncHub.cs` — auto-join em `OnConnectedAsync` + canonicalizar
  o nome do grupo em `JoinWorkspace`.
- Criar: `tests/RemoteOps.UnitTests/Cloud/SyncHubTests.cs` — cobre auto-join e canonicalização.

**Cliente — transporte:**
- Modificar: `src/RemoteOps.Sync/Remote/SyncSession.cs` — laço blindado, backoff em erro, debounce do
  hint, filtro case-insensitive, exposição do estado do canal.
- Modificar: `src/RemoteOps.Sync/Remote/ISyncHintChannel.cs` — expor estado do canal (`IsRealTime` +
  evento de mudança).
- Modificar: `src/RemoteOps.Sync/Remote/SignalRSyncHintChannel.cs` — handlers `Reconnected`/`Closed`
  com re-join, retry do connect inicial, publicação do estado.
- Modificar: `src/RemoteOps.Sync/Remote/SyncSessionFactory.cs` — `Interval` default 2min → 45s.
- Modificar: `tests/RemoteOps.UnitTests/Sync/SyncSessionTests.cs` — ajustar aos hints com debounce.
- Modificar: `tests/RemoteOps.UnitTests/Sync/FakeSyncHintChannel.cs` (ou onde o fake mora) —
  implementar os membros novos da interface.

**Cliente — app/UI:**
- Modificar: `src/RemoteOps.Desktop/App.xaml.cs` — normalizar o workspaceId do caminho env-var.
- Modificar: `src/RemoteOps.Desktop/ViewModels/SyncStatusViewModel.cs` — estado "Tempo real"/"Periódico".
- Modificar: a view da barra de status de sync — exibir o novo estado.

---

## Task 1: Servidor — auto-join no hub + grupo canônico

**Arquivos:**
- Modificar: `src/RemoteOps.Cloud/Hubs/SyncHub.cs`
- Criar: `tests/RemoteOps.UnitTests/Cloud/SyncHubTests.cs`

**Interfaces:**
- Consome: `AppDbContext.Memberships` (`m.UserId`, `m.WorkspaceId`).
- Produz: nada de novo para outras tasks — mudança de comportamento apenas.

**Contexto:** `OnConnectedAsync` hoje é um stub que lê o `sub` e joga fora (`_ = userId;`). Como o
grupo é por `ConnectionId` e reconexão gera ConnectionId novo, o cliente sai do grupo e **nunca
volta** — o tempo real morre calado. Além disso, `JoinWorkspace` adiciona ao grupo usando a string
**crua** (linha 32) enquanto o broadcast usa `workspaceId.ToString()` (`SyncService.cs:135`, formato
"D" minúsculo): um GUID em maiúsculas entra num grupo diferente do que recebe o broadcast.

- [ ] **Passo 1: Escrever os testes que falham**

Criar `tests/RemoteOps.UnitTests/Cloud/SyncHubTests.cs`. Usar o mesmo padrão de DbContext em memória
já usado pelos outros testes de Cloud (procure `UseInMemoryDatabase` ou o helper existente em
`tests/RemoteOps.UnitTests/Cloud/` e siga-o; se houver um `CloudTestDb` helper, reutilize).

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Moq;
using RemoteOps.Cloud.Hubs;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class SyncHubTests
{
    // OnConnectedAsync roda a CADA conexão nova — e reconexão gera ConnectionId novo. É o único
    // ponto onde dá pra garantir o grupo sem depender de o cliente lembrar de re-entrar.
    [Fact]
    public async Task OnConnected_AutoJoins_All_User_Workspaces()
    {
        var userId = Guid.NewGuid();
        var ws1 = Guid.NewGuid();
        var ws2 = Guid.NewGuid();
        await using var db = CloudTestDb.Create();
        db.Memberships.Add(new MembershipEntity { UserId = userId, WorkspaceId = ws1 });
        db.Memberships.Add(new MembershipEntity { UserId = userId, WorkspaceId = ws2 });
        db.Memberships.Add(new MembershipEntity { UserId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        var groups = new Mock<IGroupManager>();
        var hub = new SyncHub(db)
        {
            Context = FakeContext(userId, "conn-1"),
            Groups = groups.Object,
        };

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync("conn-1", ws1.ToString(), default), Times.Once);
        groups.Verify(g => g.AddToGroupAsync("conn-1", ws2.ToString(), default), Times.Once);
        groups.Verify(g => g.AddToGroupAsync("conn-1", It.IsAny<string>(), default), Times.Exactly(2));
    }

    // Grupo do SignalR é string case-sensitive; o broadcast usa ToString() ("D" minúsculo).
    // Entrar com o GUID cru em maiúsculas colocaria o cliente num grupo que nunca recebe nada.
    [Fact]
    public async Task JoinWorkspace_Uses_Canonical_Group_Name()
    {
        var userId = Guid.NewGuid();
        var wsId = Guid.NewGuid();
        await using var db = CloudTestDb.Create();
        db.Memberships.Add(new MembershipEntity { UserId = userId, WorkspaceId = wsId });
        await db.SaveChangesAsync();

        var groups = new Mock<IGroupManager>();
        var hub = new SyncHub(db)
        {
            Context = FakeContext(userId, "conn-1"),
            Groups = groups.Object,
        };

        await hub.JoinWorkspace(wsId.ToString().ToUpperInvariant());

        groups.Verify(g => g.AddToGroupAsync("conn-1", wsId.ToString(), default), Times.Once);
    }

    [Fact]
    public async Task JoinWorkspace_NonMember_Does_Not_Join()
    {
        var userId = Guid.NewGuid();
        await using var db = CloudTestDb.Create();
        var groups = new Mock<IGroupManager>();
        var hub = new SyncHub(db)
        {
            Context = FakeContext(userId, "conn-1"),
            Groups = groups.Object,
        };

        await hub.JoinWorkspace(Guid.NewGuid().ToString());

        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    private static HubCallerContext FakeContext(Guid userId, string connectionId)
    {
        var ctx = new Mock<HubCallerContext>();
        ctx.SetupGet(c => c.ConnectionId).Returns(connectionId);
        ctx.SetupGet(c => c.User).Returns(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", userId.ToString())], "test")));
        return ctx.Object;
    }
}
```

**Nota:** se `Moq` não estiver referenciado em `RemoteOps.UnitTests`, NÃO adicione a dependência —
escreva um `FakeGroupManager : IGroupManager` e um `FakeHubCallerContext : HubCallerContext` à mão
(padrão preferido neste repo, que usa fakes escritos à mão). Verifique com:
`grep -rn "Moq" tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj`.
Se `CloudTestDb`/`MembershipEntity` tiverem outro nome, use os nomes reais (confira em
`src/RemoteOps.Cloud/Data/`).

- [ ] **Passo 2: Rodar e ver falhar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~SyncHubTests" --nologo
```
Esperado: FALHA — `OnConnected_AutoJoins_All_User_Workspaces` não chama `AddToGroupAsync` nenhuma vez;
`JoinWorkspace_Uses_Canonical_Group_Name` entra no grupo em MAIÚSCULAS.

- [ ] **Passo 3: Implementar**

Substituir o corpo de `SyncHub.cs` (mantendo o cabeçalho de arquivo e o `[Authorize]`):

```csharp
    /// <summary>
    /// Cliente chama ao conectar para entrar no grupo do workspace.
    /// Requer que o usuário autenticado seja membro do workspace.
    /// </summary>
    public async Task JoinWorkspace(string workspaceId)
    {
        if (!Guid.TryParse(workspaceId, out var wsId)) return;

        var userIdStr = Context.User?.FindFirstValue("sub");
        if (!Guid.TryParse(userIdStr, out var userId)) return;

        var isMember = await db.Memberships.AsNoTracking()
            .AnyAsync(m => m.WorkspaceId == wsId && m.UserId == userId);
        if (!isMember) return;

        // Nome CANÔNICO ("D" minúsculo), não a string crua: grupo do SignalR é case-sensitive e o
        // broadcast usa wsId.ToString() (SyncService). Entrar com o GUID em maiúsculas colocaria o
        // cliente num grupo que nunca recebe nada — falha 100% silenciosa.
        await Groups.AddToGroupAsync(Context.ConnectionId, wsId.ToString());
    }

    public async Task LeaveWorkspace(string workspaceId)
    {
        // Mesma canonicalização do Join: sair com outra grafia deixaria o cliente no grupo.
        string group = Guid.TryParse(workspaceId, out var wsId) ? wsId.ToString() : workspaceId;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>
    /// Entra automaticamente nos grupos de TODOS os workspaces do usuário autenticado.
    ///
    /// <para>Por que aqui e não só no cliente: o grupo é por <c>ConnectionId</c> e toda reconexão gera
    /// um ConnectionId novo. Antes disto, um cliente que reconectava ficava fora do grupo PARA SEMPRE
    /// (o <c>JoinWorkspace</c> só era chamado no connect inicial) e o tempo real morria em silêncio até
    /// o app reiniciar. Como <c>OnConnectedAsync</c> roda a cada conexão nova, o join vira invariante do
    /// servidor: fecha a classe inteira de bugs, não um exemplar. O cliente segue chamando
    /// <c>JoinWorkspace</c> — redundância barata e compatível com versões antigas.</para>
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        var userIdStr = Context.User?.FindFirstValue("sub");
        if (!Guid.TryParse(userIdStr, out var userId)) return;

        List<Guid> workspaceIds = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .ToListAsync();

        foreach (Guid wsId in workspaceIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, wsId.ToString());
        }
    }
```

- [ ] **Passo 4: Rodar e ver passar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~SyncHubTests" --nologo
```
Esperado: PASSA (3 testes).

- [ ] **Passo 5: Commit**

```bash
cd /c/dev/remoteops-native
git add src/RemoteOps.Cloud/Hubs/SyncHub.cs tests/RemoteOps.UnitTests/Cloud/SyncHubTests.cs
git commit -m "fix(cloud): hub entra no grupo do workspace a cada conexao (auto-join) e canonicaliza o nome"
```

---

## Task 2: Cliente — laço de polling blindado + backoff em erro

**Arquivos:**
- Modificar: `src/RemoteOps.Sync/Remote/SyncSession.cs`
- Modificar: `tests/RemoteOps.UnitTests/Sync/SyncSessionTests.cs`

**Interfaces:**
- Consome: `SyncOrchestrator.StatusChanged` (evento `Action<SyncStatus>`), `SyncStatus(SyncState State, int ConflictCount)`.
- Produz: construtor de `SyncSession` ganha `TimeSpan? errorRetry = null` (default 5s). As tasks 3 e 5
  também mexem neste arquivo — fazer em ordem.

**Contexto:** `RunLoopAsync` (linha 226-240) chama `SyncOnceAsync` **fora** de try/catch. `SyncOnceAsync`
não relança erros de rede (fica em `SyncState.Error`), mas `SetStatus` dispara `StatusChanged`, cujo
assinante no Desktop faz `Dispatcher.Invoke` (`App.xaml.cs:780`) — uma exceção ali escapa e **mata o
laço de polling em silêncio**. A rede de segurança tem o mesmo defeito que deveria cobrir. Além disso,
um ciclo que termina em `Error` espera o intervalo inteiro antes de tentar de novo.

- [ ] **Passo 1: Escrever os testes que falham**

Acrescentar a `SyncSessionTests.cs`:

```csharp
    // O laço é a REDE DE SEGURANÇA do canal de hints: ele não pode morrer por causa de um assinante
    // que lançou. Antes deste teste, uma exceção de StatusChanged (ex.: Dispatcher em shutdown)
    // encerrava o polling em silêncio e o device parava de sincronizar até reiniciar.
    [Fact]
    public async Task Polling_Loop_Survives_Subscriber_Exception()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        SyncOrchestrator orchestrator = Orchestrator(api);
        bool threwOnce = false;
        orchestrator.StatusChanged += _ =>
        {
            if (!threwOnce)
            {
                threwOnce = true;
                throw new InvalidOperationException("assinante quebrado");
            }
        };

        await using var session = new SyncSession(
            orchestrator, hints, "ws-1", TimeSpan.FromMilliseconds(20),
            errorRetry: TimeSpan.FromMilliseconds(10));
        await session.StartAsync();

        // Se o laço tivesse morrido, os pulls parariam no primeiro ciclo.
        await WaitUntilAsync(() => api.Pulls.Count >= 2);
        Assert.True(api.Pulls.Count >= 2);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("condição não satisfeita no tempo esperado");
    }
```

- [ ] **Passo 2: Rodar e ver falhar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~SyncSessionTests" --nologo
```
Esperado: FALHA — `Polling_Loop_Survives_Subscriber_Exception` estoura no `WaitUntilAsync`
(TimeoutException), porque o laço morreu na primeira exceção. (Também não compila até o parâmetro
`errorRetry` existir — isso é esperado no passo vermelho.)

- [ ] **Passo 3: Implementar**

Em `SyncSession.cs`, adicionar campos e parâmetro; substituir `RunLoopAsync`:

```csharp
    private readonly TimeSpan _errorRetry;
    private volatile bool _lastCycleFailed;
```

No construtor, novo parâmetro opcional **ao final** (não quebra chamadores):
```csharp
        TimeSpan? errorRetry = null)
```
e no corpo:
```csharp
        // Ciclo que termina em Error esperava o intervalo INTEIRO (2 min) — um blip de rede custava
        // caro. Com o retry curto o erro transitório se resolve em segundos.
        _errorRetry = errorRetry ?? TimeSpan.FromSeconds(5);
        _orchestrator.StatusChanged += OnOrchestratorStatus;
```

Handler + laço:
```csharp
    private void OnOrchestratorStatus(SyncStatus status) => _lastCycleFailed = status.State == SyncState.Error;

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int consecutiveErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _orchestrator.SyncOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Este laço é a REDE DE SEGURANÇA do canal de hints — ele NÃO pode morrer.
                // SyncOnceAsync já engole erro de rede (fica em Error), mas um assinante de
                // StatusChanged que lança (ex.: Dispatcher.Invoke durante o shutdown) escaparia por
                // aqui e encerraria o polling em silêncio: o device pararia de sincronizar sem nenhum
                // sinal. Ver docs/superpowers/specs/2026-07-19-sync-tempo-real-resiliente-design.md.
                _lastCycleFailed = true;
            }

            // Backoff só enquanto der erro; o primeiro sucesso volta ao intervalo normal.
            TimeSpan delay;
            if (_lastCycleFailed)
            {
                consecutiveErrors++;
                double factor = Math.Pow(2, Math.Min(consecutiveErrors - 1, 3)); // 1x,2x,4x,8x
                var backoff = TimeSpan.FromMilliseconds(_errorRetry.TotalMilliseconds * factor);
                delay = backoff < _interval ? backoff : _interval;
            }
            else
            {
                consecutiveErrors = 0;
                delay = _interval;
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
```

Em `DisposeAsync`, antes de cancelar o CTS, remover o handler:
```csharp
        _orchestrator.StatusChanged -= OnOrchestratorStatus;
```

- [ ] **Passo 4: Rodar e ver passar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~SyncSession" --nologo
```
Esperado: PASSA (todos os testes de SyncSession, incluindo os antigos).

- [ ] **Passo 5: Commit**

```bash
cd /c/dev/remoteops-native
git add src/RemoteOps.Sync/Remote/SyncSession.cs tests/RemoteOps.UnitTests/Sync/SyncSessionTests.cs
git commit -m "fix(sync): laco de polling nunca morre por excecao de assinante + retry curto apos erro"
```

---

## Task 3: Cliente — debounce no caminho do hint

**Arquivos:**
- Modificar: `src/RemoteOps.Sync/Remote/SyncSession.cs`
- Modificar: `tests/RemoteOps.UnitTests/Sync/SyncSessionTests.cs`

**Interfaces:**
- Consome: nada novo.
- Produz: construtor ganha `TimeSpan? hintDebounce = null` (default 500ms).

**Contexto:** `OnHintAsync` chama `SyncOnceAsync` **direto** (linha 214). O servidor emite **um hint por
change** (`SyncService.cs:130-142`), então importar 200 hosts no PC A enfileira ~200 ciclos completos no
gate do PC B — cada um re-enumerando o cofre. O timer de debounce já existe no arquivo, mas só está
ligado ao `LocalChangePushed` e **só é criado quando `_localChanges is not null`**.

Decisão: criar um timer de debounce **dedicado ao hint** (sempre presente), com janela curta (500ms) —
curta o bastante para continuar parecendo tempo real, longa o bastante para engolir a rajada.

- [ ] **Passo 1: Escrever o teste que falha**

```csharp
    // Servidor emite 1 hint por change: importar 200 hosts viraria ~200 ciclos completos enfileirados.
    // A janela de debounce agrupa a rajada num único ciclo.
    [Fact]
    public async Task Hint_Burst_Coalesces_Into_Single_Sync()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(120));

        for (int i = 0; i < 25; i++)
        {
            await hints.RaiseAsync(new WorkspaceChangedHint("ws-1", i, "asset", $"e{i}"));
        }

        await WaitUntilAsync(() => api.Pulls.Count > 0);
        await Task.Delay(250); // deixa a janela fechar de vez
        Assert.Single(api.Pulls);
    }
```

E **ajustar os dois testes existentes** que assumem sync síncrono no hint
(`Hint_For_Workspace_Triggers_Sync` e `Hint_For_Other_Workspace_Is_Ignored`):

```csharp
    [Fact]
    public async Task Hint_For_Workspace_Triggers_Sync()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(20));

        await hints.RaiseAsync(new WorkspaceChangedHint("ws-1", 5, "asset", "e1"));

        await WaitUntilAsync(() => api.Pulls.Count > 0);
        Assert.NotEmpty(api.Pulls);
    }

    [Fact]
    public async Task Hint_For_Other_Workspace_Is_Ignored()
    {
        var api = new FakeCloudSyncApi();
        var hints = new FakeSyncHintChannel();
        await using var session = new SyncSession(
            Orchestrator(api), hints, "ws-1", TimeSpan.FromHours(1),
            hintDebounce: TimeSpan.FromMilliseconds(20));

        await hints.RaiseAsync(new WorkspaceChangedHint("ws-OTHER", 5, "asset", "e1"));

        await Task.Delay(100); // tempo de sobra para um sync indevido aparecer
        Assert.Empty(api.Pulls);
    }
```

- [ ] **Passo 2: Rodar e ver falhar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~SyncSessionTests" --nologo
```
Esperado: FALHA — `Hint_Burst_Coalesces_Into_Single_Sync` vê 25 pulls em vez de 1 (e não compila até
`hintDebounce` existir).

- [ ] **Passo 3: Implementar**

Campos novos:
```csharp
    private readonly TimeSpan _hintDebounce;
    private readonly Timer _hintTimer;
```

No construtor (parâmetro opcional ao final) e corpo:
```csharp
        _hintDebounce = hintDebounce ?? TimeSpan.FromMilliseconds(500);
        // Sempre presente (diferente do _pushTimer, que só existe com _localChanges): o hint chega do
        // servidor independentemente de haver fonte local de mudanças.
        _hintTimer = new Timer(_ => OnHintDebounceElapsed(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
```

Substituir o corpo de `OnHintAsync` (mantendo a documentação existente sobre o CTS, que continua
valendo para o disparo):
```csharp
    private Task OnHintAsync(WorkspaceChangedHint hint)
    {
        // OrdinalIgnoreCase: o nome do grupo/id trafega como string e já houve divergência de grafia
        // entre o caminho env-var (GUID cru) e o broadcast (formato "D" minúsculo). Comparar sem
        // diferenciar caixa evita descartar um hint legítimo por causa de maiúsculas.
        if (!string.Equals(hint.WorkspaceId, _workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        // NÃO sincroniza aqui: o servidor emite UM hint POR MUDANÇA, então uma importação de 200 hosts
        // enfileiraria ~200 ciclos completos no gate do orquestrador. Rearma a janela; quando os hints
        // param por _hintDebounce, roda UM ciclo. 500ms é imperceptível e engole a rajada inteira.
        lock (_pushGate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _hintTimer.Change(_hintDebounce, Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    private void OnHintDebounceElapsed()
    {
        lock (_pushGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Reutiliza o mesmo caminho guardado do push-ao-mudar (captura defensiva do CTS + engole tudo).
        _ = RunPushSyncAsync();
    }
```

Em `DisposeAsync`, junto do `_pushTimer?.Dispose();`:
```csharp
        _hintTimer.Dispose();
```

- [ ] **Passo 4: Rodar e ver passar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~SyncSession" --nologo
```
Esperado: PASSA.

- [ ] **Passo 5: Commit**

```bash
cd /c/dev/remoteops-native
git add src/RemoteOps.Sync/Remote/SyncSession.cs tests/RemoteOps.UnitTests/Sync/SyncSessionTests.cs
git commit -m "perf(sync): agrupa rajada de hints numa janela curta em vez de um ciclo por mudanca"
```

---

## Task 4: Cliente — canal resiliente (re-join na reconexão, retry do connect, estado)

**Arquivos:**
- Modificar: `src/RemoteOps.Sync/Remote/ISyncHintChannel.cs`
- Modificar: `src/RemoteOps.Sync/Remote/SignalRSyncHintChannel.cs`
- Modificar: o fake do canal nos testes (procure com
  `grep -rn "class FakeSyncHintChannel" tests/`)
- Criar: `tests/RemoteOps.UnitTests/Sync/SyncHintChannelStateTests.cs`

**Interfaces:**
- Produz: `ISyncHintChannel.IsRealTime` (bool) e `event Action<bool>? RealTimeChanged`, consumidos pela
  Task 6.

**Contexto:** `WithAutomaticReconnect()` sem handler `Reconnected` → ao reconectar o cliente não
re-entra no grupo (a Task 1 já cobre isso no servidor; aqui é a redundância barata). A política default
desiste após 4 tentativas e o connect inicial é engolido sem retry (`SyncSession.cs:67-74`).

- [ ] **Passo 1: Ampliar a interface**

```csharp
public interface ISyncHintChannel : IAsyncDisposable
{
    event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    /// <summary>
    /// O canal está entregando hints em tempo real? <c>false</c> = caiu para o laço por intervalo.
    /// Existe para o operador diagnosticar em campo (rede que bloqueia WebSocket) sem depender de log.
    /// </summary>
    bool IsRealTime { get; }

    /// <summary>Disparado quando <see cref="IsRealTime"/> muda.</summary>
    event Action<bool>? RealTimeChanged;

    Task ConnectAsync(string workspaceId, CancellationToken ct = default);
}
```

- [ ] **Passo 2: Escrever o teste que falha**

Criar `tests/RemoteOps.UnitTests/Sync/SyncHintChannelStateTests.cs` — testa o que É testável sem
`HubConnection` (que é construído dentro do construtor e não é mockável): a propagação do estado pelo
fake e a política de retry infinita.

```csharp
using RemoteOps.Sync.Remote;
using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncHintChannelStateTests
{
    // A política default do SignalR desiste após 4 tentativas (0/2/10/30s) e dispara Closed. Numa
    // queda de rede mais longa que ~42s o tempo real morria PARA SEMPRE. A nossa política nunca desiste.
    [Fact]
    public void Retry_Policy_Never_Gives_Up()
    {
        var policy = new InfiniteRetryPolicy(TimeSpan.FromSeconds(30));

        Assert.NotNull(policy.NextRetryDelay(new(TimeSpan.Zero, 0, null!)));
        Assert.NotNull(policy.NextRetryDelay(new(TimeSpan.Zero, 50, null!)));
        Assert.NotNull(policy.NextRetryDelay(new(TimeSpan.Zero, 10_000, null!)));
    }

    [Fact]
    public void Retry_Policy_Backs_Off_But_Caps()
    {
        var policy = new InfiniteRetryPolicy(TimeSpan.FromSeconds(30));

        TimeSpan first = policy.NextRetryDelay(new(TimeSpan.Zero, 0, null!))!.Value;
        TimeSpan later = policy.NextRetryDelay(new(TimeSpan.Zero, 20, null!))!.Value;

        Assert.True(first < later);
        Assert.True(later <= TimeSpan.FromSeconds(30));
    }
}
```

- [ ] **Passo 3: Implementar**

Criar a política (arquivo novo `src/RemoteOps.Sync/Remote/InfiniteRetryPolicy.cs`):

```csharp
using Microsoft.AspNetCore.SignalR.Client;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Política de reconexão que NUNCA desiste. A default do SignalR tenta 4 vezes (0/2/10/30s) e então
/// dispara <c>Closed</c> — numa queda de rede mais longa que isso o canal de hints morria em definitivo
/// e o app só voltava ao tempo real reiniciando. Aqui o backoff cresce e satura no teto, tentando para
/// sempre: o custo de uma tentativa ociosa é desprezível perto de um device que para de sincronizar.
/// </summary>
public sealed class InfiniteRetryPolicy(TimeSpan max) : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        double seconds = Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5)); // 1,2,4,8,16,32
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < max ? delay : max;
    }
}
```

Reescrever `SignalRSyncHintChannel.cs`:

```csharp
using System.Text.Json;

using Microsoft.AspNetCore.SignalR.Client;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Implementação de <see cref="ISyncHintChannel"/> sobre <c>Microsoft.AspNetCore.SignalR.Client</c>
/// (ADR-010/ADR-013). Conecta ao hub <c>/hubs/sync</c> com o JWT via <c>access_token</c> na query
/// (WebSocket não envia header Authorization), chama <c>JoinWorkspace</c> e levanta
/// <see cref="WorkspaceChanged"/> ao receber <c>workspace.changed</c>. TLS validado; nunca loga token
/// nem a URL do hub (o token viaja nela).
/// </summary>
public sealed class SignalRSyncHintChannel : ISyncHintChannel
{
    private readonly HubConnection _connection;
    private string? _workspaceId;
    private bool _isRealTime;

    public SignalRSyncHintChannel(Uri hubUrl, Func<Task<string?>> accessTokenProvider)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => options.AccessTokenProvider = accessTokenProvider)
            .WithAutomaticReconnect(new InfiniteRetryPolicy(TimeSpan.FromSeconds(30)))
            .Build();

        _connection.On<JsonElement>("workspace.changed", async payload =>
        {
            WorkspaceChangedHint? hint = SyncHintParser.Parse(payload);
            Func<WorkspaceChangedHint, Task>? handler = WorkspaceChanged;
            if (hint is not null && handler is not null)
            {
                await handler(hint);
            }
        });

        // Reconexão gera ConnectionId NOVO — e o grupo do SignalR é por ConnectionId. Sem re-entrar,
        // o cliente ficava fora do grupo para sempre. O servidor também faz auto-join no
        // OnConnectedAsync (defesa principal); isto aqui é a redundância barata do lado do cliente,
        // que segura o caso de servidor antigo.
        _connection.Reconnected += async _ =>
        {
            if (_workspaceId is not null)
            {
                try
                {
                    await _connection.InvokeAsync("JoinWorkspace", _workspaceId);
                }
                catch (Exception)
                {
                    // Best-effort: o auto-join do servidor cobre, e o laço por intervalo é a rede de segurança.
                }
            }

            SetRealTime(true);
        };

        _connection.Reconnecting += _ => { SetRealTime(false); return Task.CompletedTask; };
        _connection.Closed += _ => { SetRealTime(false); return Task.CompletedTask; };
    }

    public event Func<WorkspaceChangedHint, Task>? WorkspaceChanged;

    public event Action<bool>? RealTimeChanged;

    public bool IsRealTime => _isRealTime;

    public async Task ConnectAsync(string workspaceId, CancellationToken ct = default)
    {
        _workspaceId = workspaceId;
        await _connection.StartAsync(ct);
        await _connection.InvokeAsync("JoinWorkspace", workspaceId, ct);
        SetRealTime(true);
    }

    private void SetRealTime(bool value)
    {
        if (_isRealTime == value)
        {
            return;
        }

        _isRealTime = value;
        RealTimeChanged?.Invoke(value);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
```

Retry do connect inicial em `SyncSession.StartAsync` — substituir o try/catch atual:

```csharp
        // O laço por intervalo começa primeiro: a sincronização não depende dos hints em tempo real,
        // que podem falhar em redes que bloqueiam WebSocket (ADR-010). Hints são best-effort.
        _loop = RunLoopAsync(_cts.Token);

        // Connect com retry em fundo: antes, uma falha no PRIMEIRO connect era engolida e o canal
        // nunca mais tentava — o app ficava em polling puro pelo resto da sessão sem nenhum sinal.
        _ = ConnectHintsWithRetryAsync(_cts.Token);
```

E o método novo:
```csharp
    private async Task ConnectHintsWithRetryAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        var max = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _hints.ConnectAsync(_workspaceId, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Sem hints em tempo real por ora; o laço por intervalo continua sincronizando.
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay < max ? TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, max.TotalMilliseconds)) : max;
        }
    }
```

**Atenção:** `Start_Connects_The_Hint_Channel` e `Start_Still_Syncs_When_Hint_Connect_Fails` viram
assíncronos — usar `await WaitUntilAsync(() => hints.Connected)` no primeiro. Atualizar também o fake
para implementar `IsRealTime`/`RealTimeChanged`.

- [ ] **Passo 4: Rodar e ver passar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --filter "FullyQualifiedName~Sync" --nologo
```

- [ ] **Passo 5: Commit**

```bash
cd /c/dev/remoteops-native
git add -A src/RemoteOps.Sync tests/RemoteOps.UnitTests/Sync
git commit -m "fix(sync): canal de hints reconecta sem desistir, re-entra no grupo e expoe o estado"
```

---

## Task 5: Cliente — normalizar o workspaceId do caminho env-var

**Arquivos:**
- Modificar: `src/RemoteOps.Desktop/App.xaml.cs` (região `TryBuildSyncOptions`, ~linhas 813-826)
- Criar/Modificar: teste do parsing (procure onde `TryBuildSyncOptions` é testado; se não houver, extrair
  a normalização para um método `internal static` testável e cobrir).

**Contexto:** o caminho legado por env var passa `REMOTEOPS_CLOUD_WORKSPACE_ID` **verbatim**; o servidor
emite o hint com `ToString()` ("D" minúsculo). GUID em maiúsculas = grupo diferente. O caminho da CONTA
já é imune (`AccountSyncCoordinator` usa o workspaceId da sessão do servidor).

- [ ] **Passo 1: Teste**

```csharp
    [Theory]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void WorkspaceId_Is_Canonicalized(string input, string expected)
        => Assert.Equal(expected, App.NormalizeWorkspaceId(input));

    [Theory]
    [InlineData("nao-e-guid")]
    [InlineData("")]
    public void Invalid_WorkspaceId_Is_Rejected(string bad)
        => Assert.Null(App.NormalizeWorkspaceId(bad));
```

- [ ] **Passo 2: Implementar**

```csharp
    /// <summary>
    /// Devolve o workspaceId no formato CANÔNICO ("D" minúsculo) ou <c>null</c> se não for GUID.
    /// O grupo do SignalR é uma string case-sensitive e o servidor faz broadcast com
    /// <c>Guid.ToString()</c>: um id em maiúsculas vindo de env var entraria num grupo que nunca
    /// recebe hint — falha 100% silenciosa. Fail-closed: id inválido desliga o sync em vez de
    /// sincronizar contra um workspace errado.
    /// </summary>
    internal static string? NormalizeWorkspaceId(string? raw)
        => Guid.TryParse(raw, out Guid id) ? id.ToString() : null;
```
e usar no `TryBuildSyncOptions`, abortando (retornando `null`/false, conforme a forma do método) quando
a normalização devolver `null`.

- [ ] **Passo 3: Rodar, ver passar, commitar**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --nologo
git add -A && git commit -m "fix(cliente): normaliza o workspaceId do env-var para o formato do grupo do hub"
```

---

## Task 6: UI — estado "Tempo real" / "Periódico" na barra de sync

**Arquivos:**
- Modificar: `src/RemoteOps.Desktop/ViewModels/SyncStatusViewModel.cs`
- Modificar: a view da barra de sync (encontre com
  `grep -rln "SyncStatusViewModel\|StatusText" src/RemoteOps.Desktop/Views`)
- Modificar: `tests/RemoteOps.UnitTests/Desktop/Views/SyncStatusBarRenderTests.cs`

**Contexto:** o operador precisa saber, em campo, se está em tempo real ou caiu para o polling — sem
depender de log (ADR-013 proíbe logar a URL do hub).

- [ ] **Passo 1: Teste do ViewModel**

```csharp
    [Fact]
    public void ChannelText_Reflects_RealTime()
    {
        var vm = new SyncStatusViewModel(new FakeSyncController());

        vm.SetRealTime(true);
        Assert.Equal("Tempo real", vm.ChannelText);

        vm.SetRealTime(false);
        Assert.Equal("Periódico", vm.ChannelText);
    }
```

- [ ] **Passo 2: Implementar**

```csharp
    private bool _isRealTime;

    /// <summary>
    /// O canal de hints está entregando em tempo real, ou caímos no laço por intervalo? Exibido na
    /// barra de sync para o operador diagnosticar rede que bloqueia WebSocket sem precisar de log.
    /// </summary>
    public string ChannelText => _isRealTime ? "Tempo real" : "Periódico";

    public void SetRealTime(bool value)
    {
        if (Set(ref _isRealTime, value))
        {
            RaisePropertyChanged(nameof(ChannelText));
        }
    }
```
(Confira a assinatura real de `Set`/`RaisePropertyChanged` em `BaseViewModel` e siga-a.)

No `App.xaml.cs`, ligar `session.Hints.RealTimeChanged` → `Dispatcher` → `vm.SetRealTime(...)`, usando o
MESMO padrão de marshalling já usado para `StatusChanged` (`App.xaml.cs:780`). Exponha o canal na
`SyncSession` se necessário (`public ISyncHintChannel Hints => _hints;`).

Na view, acrescentar o texto ao lado do status existente, usando os estilos de tipografia por
`DynamicResource` (NUNCA `FontSize` literal — lição da v1.2.24).

- [ ] **Passo 3: Render test STA + rodar tudo + commit**

```bash
cd /c/dev/remoteops-native && dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --nologo
git add -A && git commit -m "feat(cliente): barra de sync mostra se esta em tempo real ou periodico"
```

---

## Task 7: Polling default 45s

**Arquivos:** `src/RemoteOps.Sync/Remote/SyncSessionFactory.cs`

- [ ] **Passo 1: Teste**

```csharp
    [Fact]
    public void Default_Interval_Is_45_Seconds()
        => Assert.Equal(TimeSpan.FromSeconds(45), new SyncSessionOptions
        {
            Workspace = null!, WorkspaceId = "ws", CloudBaseUrl = new Uri("https://x/"),
            DeviceId = Guid.Empty, Vault = null!, TokenRefPath = "t",
        }.Interval);
```

- [ ] **Passo 2: Implementar**

```csharp
    /// <summary>
    /// Teto de staleness quando o canal de hints está morto (rede sem WebSocket). Era 2 min, o que
    /// fazia o operador achar que o sync tinha travado. 45s é rede de segurança, não o caminho
    /// principal — o tempo real vem dos hints. Opção de CÓDIGO: não vai à tela de Configurações.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(45);
```

- [ ] **Passo 3: Rodar e commitar**

---

## Task 8: Validação final, changelog e PR

- [ ] **Passo 1: Gates completos**

```bash
cd /c/dev/remoteops-native
dotnet build -c Release --nologo
dotnet test tests/RemoteOps.UnitTests/RemoteOps.UnitTests.csproj --nologo
dotnet format --verify-no-changes
```
Os três precisam sair limpos.

- [ ] **Passo 2: Versão + changelogs**

Bump de `<Version>` em `src/RemoteOps.Desktop/RemoteOps.Desktop.csproj` para **1.4.0** (feature +
correções de comportamento). Entrada em `CHANGELOG.md` e em
`src/RemoteOps.Desktop/Resources/operator-changelog.json` (linguagem de operador: "o que você cadastra
num PC aparece no outro em segundos; antes precisava fechar e abrir").

- [ ] **Passo 3: PR**

```bash
cd /c/dev/remoteops-native
git push -u origin feat/sync-tempo-real
gh pr create --base main --head feat/sync-tempo-real --title "fix(sync): tempo real resiliente + laco de polling blindado (v1.4.0)"
```

- [ ] **Passo 4: Deploy do backend**

O auto-join é mudança de **servidor** — depois do merge, rebuildar e reiniciar a API em
`/opt/innet/remoteops-sync/` (`docker compose build api && docker compose up -d api`). O Nextcloud não é
afetado (containers separados). Validar com `/health` e um teste real de dois devices.
