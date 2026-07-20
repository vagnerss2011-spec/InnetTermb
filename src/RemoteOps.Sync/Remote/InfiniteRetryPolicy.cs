using Microsoft.AspNetCore.SignalR.Client;

namespace RemoteOps.Sync.Remote;

/// <summary>
/// Política de reconexão do canal de hints que NUNCA desiste.
///
/// <para>A default do SignalR tenta 4 vezes (0/2/10/30s) e então dispara <c>Closed</c>: uma queda de
/// rede mais longa que ~42s — trocar de Wi-Fi, VPN caindo, notebook suspenso — matava o tempo real em
/// definitivo, e o app só voltava a receber hints se fosse reiniciado. Aqui a espera cresce e satura
/// no teto, tentando para sempre: uma tentativa ociosa a cada 30s não custa nada perto de um device
/// que para de enxergar as mudanças dos outros.</para>
/// </summary>
public sealed class InfiniteRetryPolicy(TimeSpan max) : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // 1, 2, 4, 8, 16, 32s e daí em diante saturado no teto. Nunca devolve null — null é
        // justamente como se diz "desisti" ao SignalR.
        double seconds = Math.Pow(2, Math.Min(retryContext.PreviousRetryCount, 5));
        var delay = TimeSpan.FromSeconds(seconds);
        return delay < max ? delay : max;
    }
}
