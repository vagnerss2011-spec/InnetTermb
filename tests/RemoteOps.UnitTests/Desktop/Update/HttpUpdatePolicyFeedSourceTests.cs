using System.Net;
using System.Net.Http;
using RemoteOps.Desktop.Update;
using RemoteOps.UnitTests.Desktop.Update.Fakes;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class HttpUpdatePolicyFeedSourceTests
{
    private static readonly Uri PolicyUrl = new("https://example.invalid/update-policy.json");

    [Fact]
    public async Task GetMinimumRequiredVersionAsync_ValidDocument_ReturnsParsedVersion()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"minimumRequiredVersion":"1.2.3"}""");
        var source = new HttpUpdatePolicyFeedSource(new HttpClient(handler), PolicyUrl);

        AppVersion? version = await source.GetMinimumRequiredVersionAsync();

        Assert.Equal(AppVersion.Parse("1.2.3"), version);
    }

    [Fact]
    public async Task GetMinimumRequiredVersionAsync_NonSuccessStatus_FailsOpenReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.NotFound, body: null);
        var source = new HttpUpdatePolicyFeedSource(new HttpClient(handler), PolicyUrl);

        AppVersion? version = await source.GetMinimumRequiredVersionAsync();

        Assert.Null(version);
    }

    [Fact]
    public async Task GetMinimumRequiredVersionAsync_FieldMissing_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"note":"sem campo esperado"}""");
        var source = new HttpUpdatePolicyFeedSource(new HttpClient(handler), PolicyUrl);

        AppVersion? version = await source.GetMinimumRequiredVersionAsync();

        Assert.Null(version);
    }

    [Fact]
    public async Task GetMinimumRequiredVersionAsync_InvalidVersionString_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, """{"minimumRequiredVersion":"not-a-version"}""");
        var source = new HttpUpdatePolicyFeedSource(new HttpClient(handler), PolicyUrl);

        AppVersion? version = await source.GetMinimumRequiredVersionAsync();

        Assert.Null(version);
    }

    [Fact]
    public async Task GetMinimumRequiredVersionAsync_MalformedJson_FailsOpenReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "not json");
        var source = new HttpUpdatePolicyFeedSource(new HttpClient(handler), PolicyUrl);

        AppVersion? version = await source.GetMinimumRequiredVersionAsync();

        Assert.Null(version);
    }
}
