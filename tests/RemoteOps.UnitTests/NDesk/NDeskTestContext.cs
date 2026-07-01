using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.NDesk.Broker.Audit;
using RemoteOps.NDesk.Broker.Consent;
using RemoteOps.NDesk.Broker.Data;
using RemoteOps.NDesk.Broker.Telemetry;
using RemoteOps.NDesk.Broker.Tickets;

namespace RemoteOps.UnitTests.NDesk;

/// <summary>TimeProvider mutável — testes de expiração avançam o relógio sem Task.Delay real.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    public DateTimeOffset UtcNowValue { get; set; } = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => UtcNowValue;
}

/// <summary>Captura todas as mensagens formatadas logadas — usado para provar ausência de segredo em log.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>Contexto de teste compartilhado: NDeskDbContext InMemory + serviços reais + relógio controlável.</summary>
internal sealed class NDeskTestContext : IDisposable
{
    public NDeskDbContext Db { get; }
    public FakeTimeProvider Clock { get; } = new();
    public NDeskAuditService Audit { get; }
    public NDeskTicketService Tickets { get; }
    public NDeskPermissionGrantService Grants { get; }
    public NDeskTelemetryService Telemetry { get; }

    private static int _counter;

    public NDeskTestContext()
    {
        var dbName = $"ndesk-test-{Interlocked.Increment(ref _counter)}";
        var opts = new DbContextOptionsBuilder<NDeskDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        Db = new NDeskDbContext(opts);
        Audit = new NDeskAuditService(Db, Clock, NullLogger<NDeskAuditService>.Instance);
        Tickets = new NDeskTicketService(Db, Audit, Clock, NullLogger<NDeskTicketService>.Instance);
        Grants = new NDeskPermissionGrantService(
            Db, Tickets, Audit, Clock, NullLogger<NDeskPermissionGrantService>.Instance);
        Telemetry = new NDeskTelemetryService(Db, Clock);
    }

    public void Dispose() => Db.Dispose();
}
