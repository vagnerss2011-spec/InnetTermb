using RemoteOps.Terminal;

namespace RemoteOps.UnitTests.Terminal.Fakes;

internal sealed class FakeHostKeyConfirmation : IHostKeyConfirmation
{
    private readonly bool _response;

    public List<(string Host, string Fingerprint, bool IsChanged)> Calls { get; } = [];

    public FakeHostKeyConfirmation(bool response) => _response = response;

    public Task<bool> ConfirmAsync(string host, string fingerprintHex, bool isChanged, CancellationToken ct = default)
    {
        Calls.Add((host, fingerprintHex, isChanged));
        return Task.FromResult(_response);
    }
}
