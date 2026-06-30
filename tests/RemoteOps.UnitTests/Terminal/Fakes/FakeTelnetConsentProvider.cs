using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class FakeTelnetConsentProvider : ITelnetConsentProvider
{
    private readonly bool _consents;

    public int CallCount { get; private set; }

    public FakeTelnetConsentProvider(bool consents) => _consents = consents;

    public Task<bool> RequestConsentAsync(string host, int port, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(_consents);
    }
}
