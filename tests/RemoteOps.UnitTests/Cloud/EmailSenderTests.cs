using Microsoft.Extensions.Logging.Abstractions;
using RemoteOps.Cloud.Email;
using Xunit;

namespace RemoteOps.UnitTests.Cloud;

public sealed class EmailSenderTests
{
    [Fact]
    public async Task LoggingEmailSender_DoesNotThrow()
    {
        var sender = new LoggingEmailSender(NullLogger<LoggingEmailSender>.Instance);
        await sender.SendAsync(new EmailMessage("op@test.local", "Recuperação", "token: abc123"), default);
    }

    [Fact]
    public async Task FakeEmailSender_CapturesMessagesInOrder()
    {
        var fake = new FakeEmailSender();

        await fake.SendAsync(new EmailMessage("a@test.local", "S1", "B1"), default);
        await fake.SendAsync(new EmailMessage("b@test.local", "S2", "B2"), default);

        Assert.Equal(2, fake.Sent.Count);
        Assert.Equal("b@test.local", fake.Last!.ToEmail);
        Assert.Equal("S2", fake.Last.Subject);
    }
}
