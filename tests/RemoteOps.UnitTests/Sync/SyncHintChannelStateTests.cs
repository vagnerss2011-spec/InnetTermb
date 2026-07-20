using Microsoft.AspNetCore.SignalR.Client;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

/// <summary>
/// Cobre o que é testável do canal de hints sem um servidor: a política de reconexão. O
/// <see cref="SignalRSyncHintChannel"/> constrói o <c>HubConnection</c> dentro do próprio construtor,
/// então os handlers de reconexão só se exercitam contra um hub de verdade — isso fica para a
/// validação em campo (o operador derruba a rede e confirma que volta sozinho), conforme o spec.
/// </summary>
public sealed class SyncHintChannelStateTests
{
    // A política default do SignalR tenta 4 vezes (0/2/10/30s) e então dispara Closed: qualquer queda
    // de rede mais longa que ~42s matava o tempo real ATÉ O APP REINICIAR. A nossa nunca desiste.
    [Fact]
    public void Retry_Policy_Never_Gives_Up()
    {
        var policy = new InfiniteRetryPolicy(TimeSpan.FromSeconds(30));

        Assert.NotNull(policy.NextRetryDelay(Context(0)));
        Assert.NotNull(policy.NextRetryDelay(Context(50)));
        Assert.NotNull(policy.NextRetryDelay(Context(10_000)));
    }

    // Insistir para sempre só é barato se a espera crescer: sem backoff, um servidor fora viraria uma
    // tentativa por segundo pelo resto da sessão.
    [Fact]
    public void Retry_Policy_Backs_Off_But_Caps()
    {
        var max = TimeSpan.FromSeconds(30);
        var policy = new InfiniteRetryPolicy(max);

        TimeSpan first = policy.NextRetryDelay(Context(0))!.Value;
        TimeSpan later = policy.NextRetryDelay(Context(20))!.Value;

        Assert.True(first < later);
        Assert.True(later <= max);
    }

    // Fail-closed: enquanto não conectou, o canal NÃO está em tempo real. O estado vai para a barra de
    // sync, e um canal que se declarasse em tempo real antes da hora faria a UI mentir justamente para
    // o operador que está tentando descobrir se a rede dele bloqueia WebSocket.
    [Fact]
    public async Task Fresh_Channel_Is_Not_RealTime_Before_Connecting()
    {
        // Construir não abre conexão nenhuma — o StartAsync é que fala com a rede.
        await using var channel = new SignalRSyncHintChannel(
            new Uri("https://exemplo.invalido/hubs/sync"), () => Task.FromResult<string?>(null));

        Assert.False(channel.IsRealTime);
    }

    private static RetryContext Context(long previousRetryCount)
        => new() { PreviousRetryCount = previousRetryCount, ElapsedTime = TimeSpan.Zero };
}
