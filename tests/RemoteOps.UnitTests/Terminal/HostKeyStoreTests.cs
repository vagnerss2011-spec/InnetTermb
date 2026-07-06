using System.IO;
using RemoteOps.Terminal.Ssh;
using Xunit;

namespace RemoteOps.UnitTests.Terminal;

public sealed class HostKeyStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "known_hosts.json");

    [Fact]
    public void Trust_PersistsAcrossInstances()
    {
        string path = TempPath();
        var store1 = new HostKeyStore(path);
        store1.Trust("10.0.0.1", "aabbcc");

        var store2 = new HostKeyStore(path);
        Assert.True(store2.IsKnown("10.0.0.1", "aabbcc"));
        Assert.True(store2.HasAnyKey("10.0.0.1"));
    }

    [Fact]
    public void IsKnown_DifferentFingerprint_False()
    {
        string path = TempPath();
        var store = new HostKeyStore(path);
        store.Trust("h", "aaa");
        Assert.False(store.IsKnown("h", "bbb"));
        Assert.True(store.HasAnyKey("h")); // key mudou → detecta como HasAnyKey mas não IsKnown
    }

    [Fact]
    public void CorruptFile_StartsEmpty()
    {
        string path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var store = new HostKeyStore(path);
        Assert.False(store.HasAnyKey("anything"));
    }

    [Fact]
    public void NullPath_InMemoryOnly_NoThrow()
    {
        var store = new HostKeyStore(path: null);
        store.Trust("h", "fp");
        Assert.True(store.IsKnown("h", "fp"));
    }
}
