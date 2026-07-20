using System.IO;

using RemoteOps.Security.Storage;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncSessionFactoryTests
{
    [Fact]
    public async Task Create_Builds_An_Offline_Session_Without_Touching_The_Network()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-factory");
        var options = new SyncSessionOptions
        {
            Workspace = ctx.Workspace,
            WorkspaceId = "00000000-0000-0000-0000-000000000001",
            CloudBaseUrl = new Uri("https://cloud.local"),
            DeviceId = Guid.NewGuid(),
            Vault = ctx.Vault,
            TokenRefPath = ctx.DbPath + ".tokenref",
        };

        await using SyncSession session = SyncSessionFactory.Create(options);

        Assert.Equal(SyncState.Offline, session.Orchestrator.Status.State);
    }

    [Fact]
    public async Task Create_Rejects_Non_Https_Url()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-http");
        var options = new SyncSessionOptions
        {
            Workspace = ctx.Workspace,
            WorkspaceId = "00000000-0000-0000-0000-000000000001",
            CloudBaseUrl = new Uri("http://insecure.local"),
            DeviceId = Guid.NewGuid(),
            Vault = ctx.Vault,
            TokenRefPath = ctx.DbPath + ".tokenref",
        };

        Assert.Throws<ArgumentException>(() => SyncSessionFactory.Create(options));
    }

    /// <summary>
    /// O intervalo é o TETO de atraso quando o canal de hints está fora (rede sem WebSocket). Com 2
    /// min o operador concluía que o sync tinha travado e reiniciava o app; 45s cabe na paciência de
    /// quem está esperando a novidade aparecer na outra ponta.
    ///
    /// <para>Este teste é assumidamente um DETECTOR DE MUDANÇA, não cobertura de comportamento: ele
    /// afirma o default do record, e o número é uma decisão de produto. Quem mexer nele vai ter de
    /// mexer aqui também — que é o ponto. Por isso não monta workspace nem cofre: pagar I/O de
    /// SQLCipher para ler um literal seria custo sem cobertura.</para>
    /// </summary>
    [Fact]
    public void Default_Interval_Is_45_Seconds()
    {
        var options = new SyncSessionOptions
        {
            Workspace = null!,
            WorkspaceId = "00000000-0000-0000-0000-000000000001",
            CloudBaseUrl = new Uri("https://cloud.local"),
            DeviceId = Guid.NewGuid(),
            Vault = null!,
            TokenRefPath = "irrelevante.tokenref",
        };

        Assert.Equal(TimeSpan.FromSeconds(45), options.Interval);
    }

    // ── Canal de segredos ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sessão sem os campos de segredo continua montando (só metadados) — é o caminho de quem não
    /// tem conta E2EE: o cofre ainda está em DPAPI e não há o que sincronizar.
    /// </summary>
    [Fact]
    public async Task Create_WithoutSecretOptions_StillBuilds_MetadataOnlySession()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-nosecrets");

        await using SyncSession session = SyncSessionFactory.Create(new SyncSessionOptions
        {
            Workspace = ctx.Workspace,
            WorkspaceId = "00000000-0000-0000-0000-000000000001",
            CloudBaseUrl = new Uri("https://cloud.local"),
            DeviceId = Guid.NewGuid(),
            Vault = ctx.Vault,
            TokenRefPath = ctx.DbPath + ".tokenref",
        });

        Assert.Equal(SyncState.Offline, session.Orchestrator.Status.State);
    }

    /// <summary>
    /// Meio configurado é pior que desligado: um cofre sem escopo sincronizaria o workspace errado —
    /// e o workspace errado é onde moram a chave do banco e os tokens. Falha alto.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Create_WithHalfConfiguredSecretOptions_Throws(bool withStore, bool withWorkspace)
    {
        using var ctx = await SyncTestContext.CreateAsync($"ws-half-{withStore}-{withWorkspace}");
        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-half-{Guid.NewGuid():n}");
        try
        {
            var options = new SyncSessionOptions
            {
                Workspace = ctx.Workspace,
                WorkspaceId = "00000000-0000-0000-0000-000000000001",
                CloudBaseUrl = new Uri("https://cloud.local"),
                DeviceId = Guid.NewGuid(),
                Vault = ctx.Vault,
                TokenRefPath = ctx.DbPath + ".tokenref",
                EnvelopeStore = withStore ? new FileVaultStore(Path.Combine(dir, "vault.json")) : null,
                VaultWorkspaceId = withWorkspace ? "ws-local" : null,
            };

            Assert.Throws<ArgumentException>(() => SyncSessionFactory.Create(options));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Create_WithSecretOptions_BuildsSession()
    {
        using var ctx = await SyncTestContext.CreateAsync("ws-withsecrets");
        string dir = Path.Combine(Path.GetTempPath(), $"remoteops-withsec-{Guid.NewGuid():n}");
        try
        {
            await using SyncSession session = SyncSessionFactory.Create(new SyncSessionOptions
            {
                Workspace = ctx.Workspace,
                WorkspaceId = "00000000-0000-0000-0000-000000000001",
                CloudBaseUrl = new Uri("https://cloud.local"),
                DeviceId = Guid.NewGuid(),
                Vault = ctx.Vault,
                TokenRefPath = ctx.DbPath + ".tokenref",
                EnvelopeStore = new FileVaultStore(Path.Combine(dir, "vault.json")),
                VaultWorkspaceId = "ws-local",
            });

            Assert.Equal(SyncState.Offline, session.Orchestrator.Status.State);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
