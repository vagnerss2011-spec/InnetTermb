using System;
using System.Collections.Generic;
using System.IO;
using RemoteOps.Desktop.Infrastructure;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Infrastructure;

public sealed class CloudConfigTests
{
    private static Func<string, string?> Env(params (string Key, string? Value)[] pairs)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) map[k] = v;
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    // ── Round-trip das novas settings ──────────────────────────────────────────

    [Fact]
    public void CloudFields_RoundTrip()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "settings.json");
        var store = new JsonSettingsStore(path);

        store.Save(new AppSettings { CloudSyncEnabled = true, CloudServerUrl = "https://sync.exemplo.com" });
        AppSettings loaded = store.Load();

        Assert.True(loaded.CloudSyncEnabled);
        Assert.Equal("https://sync.exemplo.com", loaded.CloudServerUrl);
    }

    // ── Resolver: Configurações vencem ─────────────────────────────────────────

    [Fact]
    public void Settings_TakePrecedence_OverEnv()
    {
        var settings = new AppSettings { CloudSyncEnabled = true, CloudServerUrl = "https://das-settings.com" };
        var env = Env((CloudConfig.UrlEnvVar, "https://do-env.com"), (CloudConfig.EnabledEnvVar, "true"));

        var (enabled, url) = CloudConfig.Resolve(settings, env);

        Assert.True(enabled);
        Assert.Equal("https://das-settings.com/", url!.ToString());
    }

    [Fact]
    public void FallsBackToEnv_WhenSettingsEmpty()
    {
        var settings = new AppSettings(); // CloudSyncEnabled=false, CloudServerUrl=null
        var env = Env((CloudConfig.EnabledEnvVar, "true"), (CloudConfig.UrlEnvVar, "https://do-env.com"));

        var (enabled, url) = CloudConfig.Resolve(settings, env);

        Assert.True(enabled);
        Assert.Equal("https://do-env.com/", url!.ToString());
    }

    [Fact]
    public void EnabledIsTrue_IfEitherSourceEnables()
    {
        // Settings liga mesmo sem env.
        var (e1, _) = CloudConfig.Resolve(
            new AppSettings { CloudSyncEnabled = true }, Env());
        Assert.True(e1);

        // Env liga mesmo com settings default.
        var (e2, _) = CloudConfig.Resolve(
            new AppSettings(), Env((CloudConfig.EnabledEnvVar, "true")));
        Assert.True(e2);
    }

    [Fact]
    public void NoConfig_AtAll_IsDisabledAndNoUrl()
    {
        var (enabled, url) = CloudConfig.Resolve(new AppSettings(), Env());
        Assert.False(enabled);
        Assert.Null(url);
    }

    [Theory]
    [InlineData("http://inseguro.com")] // http:// não liga (fail-closed, ADR-013)
    [InlineData("nao-e-url")]
    [InlineData("ftp://x.com")]
    public void NonHttpsUrl_IsRejected(string bad)
    {
        var settings = new AppSettings { CloudSyncEnabled = true, CloudServerUrl = bad };

        var (enabled, url) = CloudConfig.Resolve(settings, Env());

        Assert.True(enabled);   // o flag está ligado…
        Assert.Null(url);       // …mas sem URL válida a nuvem não sobe.
    }

    [Fact]
    public void HttpsUrl_FromSettings_IsAccepted()
    {
        var (_, url) = CloudConfig.Resolve(
            new AppSettings { CloudServerUrl = "https://innetsync.innetsolutions.net.br" }, Env());

        Assert.Equal("https://innetsync.innetsolutions.net.br/", url!.ToString());
    }
}
