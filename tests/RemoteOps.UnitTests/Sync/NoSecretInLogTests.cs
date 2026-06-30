using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;

using RemoteOps.Contracts.Sync;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Garante a regra do CLAUDE.md/ADR-013: nenhum token, segredo ou patch sensível chega a log.
/// Os componentes de sync não logam — este teste é um guarda de regressão: captura todo o Trace
/// emitido durante um fluxo completo de push (com token + patch sensível) e prova que nada vaza.
/// </summary>
public sealed class NoSecretInLogTests
{
    private sealed class CapturingTraceListener : TraceListener
    {
        public StringBuilder Output { get; } = new();

        public override void Write(string? message) => Output.Append(message);

        public override void WriteLine(string? message) => Output.AppendLine(message);
    }

    [Fact]
    public async Task Token_And_Patch_Never_Appear_In_Trace_During_Push()
    {
        const string accessToken = "ACCESS-TOKEN-SUPER-SECRET";
        const string refreshToken = "REFRESH-TOKEN-SUPER-SECRET";
        const string sensitivePatchValue = "PLAINTEXT-NOTE-DO-NOT-LEAK";

        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            var tokenStore = new FakeTokenStore(
                new TokenSet(accessToken, refreshToken, DateTimeOffset.UtcNow.AddMinutes(5)));
            var handler = new FakeHttpMessageHandler(_ =>
                FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"ok","newCursor":1}"""));
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.local") };
            var client = new CloudSyncApiClient(http, Guid.NewGuid(), tokenStore);

            await client.PushAsync(new PushRequest("ws-1",
            [
                new SyncChange
                {
                    EntityType = "credential_ref",
                    EntityId = "c1",
                    Operation = "updated",
                    Patch = new Dictionary<string, object?> { ["note"] = sensitivePatchValue },
                },
            ]));

            Trace.Flush();
            string traced = listener.Output.ToString();
            Assert.DoesNotContain(accessToken, traced);
            Assert.DoesNotContain(refreshToken, traced);
            Assert.DoesNotContain(sensitivePatchValue, traced);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void TokenSet_ToString_Does_Not_Expose_Tokens()
    {
        var tokens = new TokenSet("ACCESS-SECRET-XYZ", "REFRESH-SECRET-XYZ", DateTimeOffset.UtcNow);

        string text = tokens.ToString();

        Assert.DoesNotContain("ACCESS-SECRET-XYZ", text);
        Assert.DoesNotContain("REFRESH-SECRET-XYZ", text);
    }
}
