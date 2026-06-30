using FluentAssertions;
using NSubstitute;
using RemoteOps.Contracts;
using RemoteOps.Contracts.Models;
using RemoteOps.Terminal.Telnet;

namespace RemoteOps.Terminal.Tests;

public sealed class TelnetPolicyTests
{
    [Fact]
    public async Task ConnectAsync_WhenPolicyDenies_ThrowsTelnetNotAllowedException()
    {
        var policy = Substitute.For<ITelnetPolicy>();
        policy.IsTelnetAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(false);

        var provider = new TelnetSessionProvider(policy);
        var request = new SessionRequest
        {
            SessionId      = "test-01",
            Protocol       = RemoteProtocol.Telnet,
            Host           = "router.example.local",
            Port           = 23,
            CredentialRefId = "cred-01"
        };
        var cred = PlaintextCredential.WithPassword("admin", System.Text.Encoding.UTF8.GetBytes("pass"));

        Func<Task> act = () => provider.ConnectAsync(
            request, cred,
            input: EmptyInput(),
            output: _ => Task.CompletedTask,
            ct: CancellationToken.None);

        await act.Should().ThrowAsync<TelnetNotAllowedException>()
                 .WithMessage("*router.example.local*");

        cred.Dispose();
    }

    [Fact]
    public async Task ConnectAsync_WhenAllowed_RaisesTelnetWarning()
    {
        var policy = Substitute.For<ITelnetPolicy>();
        policy.IsTelnetAllowedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
              .Returns(true);

        var provider = new TelnetSessionProvider(policy);
        string? warningReceived = null;
        provider.TelnetWarning = msg => { warningReceived = msg; return Task.CompletedTask; };

        var request = new SessionRequest
        {
            SessionId       = "test-02",
            Protocol        = RemoteProtocol.Telnet,
            Host            = "olt.example.local",
            Port            = 23,
            CredentialRefId = "cred-02"
        };
        var cred = PlaintextCredential.WithPassword("admin", System.Text.Encoding.UTF8.GetBytes("pass"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        try
        {
            await provider.ConnectAsync(request, cred, EmptyInput(), _ => Task.CompletedTask, cts.Token);
        }
        catch (OperationCanceledException) { }

        warningReceived.Should().NotBeNullOrEmpty()
                       .And.Contain("não é criptografado");

        cred.Dispose();
        await provider.DisposeAsync();
    }

    private static async IAsyncEnumerable<byte[]> EmptyInput(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
