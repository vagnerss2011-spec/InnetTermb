using System.Text.Json;

using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Sync;

public sealed class SyncHintParserTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void Parses_All_Fields()
    {
        WorkspaceChangedHint? hint = SyncHintParser.Parse(
            Json("""{"workspaceId":"ws-1","cursor":42,"entityType":"asset","entityId":"e1"}"""));

        Assert.NotNull(hint);
        Assert.Equal("ws-1", hint!.WorkspaceId);
        Assert.Equal(42, hint.Cursor);
        Assert.Equal("asset", hint.EntityType);
        Assert.Equal("e1", hint.EntityId);
    }

    [Fact]
    public void Returns_Null_When_WorkspaceId_Missing()
    {
        Assert.Null(SyncHintParser.Parse(Json("""{"cursor":1}""")));
    }

    [Fact]
    public void Returns_Null_For_Non_Object()
    {
        Assert.Null(SyncHintParser.Parse(Json("123")));
    }
}
