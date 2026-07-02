using System.Collections.Generic;
using RemoteOps.Contracts.ExternalTools;
using RemoteOps.Desktop.Integration;
using RemoteOps.MikroTik;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Integration;

public sealed class FreshManifestWinBoxRunnerTests
{
    private sealed class RecordingRunner : IWinBoxRunner
    {
        public ExternalToolLaunchRequest? LastRequest;
        public Task<string> LaunchAsync(ExternalToolLaunchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult("launch-id");
        }
    }

    private static ExternalToolLaunchRequest Request() => new()
    {
        Id = "r1",
        WorkspaceId = "ws-local",
        Tool = "winbox",
        HostId = "a1",
        Target = new ExternalToolTarget { Address = "10.0.0.1", AddressFamily = "ipv4", Port = 8291 },
        RequestedBy = "local-user",
        RequestedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task LaunchAsync_RebuildsManifest_OnEveryCall()
    {
        var manifests = new List<WinBoxToolManifest>();
        int factoryCalls = 0;
        var inner = new RecordingRunner();

        var runner = new FreshManifestWinBoxRunner(
            manifestFactory: () =>
            {
                factoryCalls++;
                var m = new WinBoxToolManifest
                {
                    Tool = "winbox",
                    Version = "unknown",
                    File = "winbox64.exe",
                    Sha256 = "hash-" + factoryCalls,
                    ExecutablePath = @"C:\wb\winbox64.exe",
                };
                manifests.Add(m);
                return m;
            },
            runnerFactory: m => inner);

        await runner.LaunchAsync(Request());
        await runner.LaunchAsync(Request());

        // O manifesto é reconstruído a cada launch — configurar o WinBox nas
        // Configurações passa a valer sem reiniciar o app.
        Assert.Equal(2, factoryCalls);
        Assert.Equal("hash-1", manifests[0].Sha256);
        Assert.Equal("hash-2", manifests[1].Sha256);
        Assert.NotNull(inner.LastRequest);
    }
}
