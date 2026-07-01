using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class UpdateCheckResultFactoryTests
{
    [Fact]
    public void Create_NoAvailableVersion_UpdateAvailableIsFalse()
    {
        UpdateCheckResult result = UpdateCheckResultFactory.Create(
            currentVersion: AppVersion.Parse("1.0.0"),
            availableVersion: null,
            minimumRequiredVersion: null);

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.AvailableVersion);
        Assert.False(result.Policy.MustUpdate);
    }

    [Fact]
    public void Create_AvailableVersionNewerThanCurrent_UpdateAvailableIsTrue()
    {
        UpdateCheckResult result = UpdateCheckResultFactory.Create(
            currentVersion: AppVersion.Parse("1.0.0"),
            availableVersion: AppVersion.Parse("1.1.0"),
            minimumRequiredVersion: null);

        Assert.True(result.UpdateAvailable);
        Assert.Equal(AppVersion.Parse("1.1.0"), result.AvailableVersion);
    }

    [Fact]
    public void Create_AvailableVersionSameAsCurrent_UpdateAvailableIsFalse()
    {
        UpdateCheckResult result = UpdateCheckResultFactory.Create(
            currentVersion: AppVersion.Parse("1.0.0"),
            availableVersion: AppVersion.Parse("1.0.0"),
            minimumRequiredVersion: null);

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public void Create_CurrentBelowMinimumRequired_PolicyMustUpdateIsTrueRegardlessOfAvailable()
    {
        UpdateCheckResult result = UpdateCheckResultFactory.Create(
            currentVersion: AppVersion.Parse("0.5.0"),
            availableVersion: null,
            minimumRequiredVersion: AppVersion.Parse("1.0.0"));

        Assert.True(result.Policy.MustUpdate);
        Assert.False(result.UpdateAvailable);
    }
}
