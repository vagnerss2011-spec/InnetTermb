using RemoteOps.Desktop.Update;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.Update;

public sealed class UpdatePolicyTests
{
    [Fact]
    public void Evaluate_NoMinimumRequiredVersion_NeverBlocks()
    {
        UpdatePolicyResult result = UpdatePolicy.Evaluate(
            currentVersion: AppVersion.Parse("0.1.0"),
            minimumRequiredVersion: null);

        Assert.False(result.MustUpdate);
        Assert.Null(result.MinimumRequiredVersion);
    }

    [Fact]
    public void Evaluate_CurrentBelowMinimum_MustUpdateIsTrue()
    {
        UpdatePolicyResult result = UpdatePolicy.Evaluate(
            currentVersion: AppVersion.Parse("0.9.0"),
            minimumRequiredVersion: AppVersion.Parse("1.0.0"));

        Assert.True(result.MustUpdate);
    }

    [Fact]
    public void Evaluate_CurrentEqualsMinimum_DoesNotBlock()
    {
        UpdatePolicyResult result = UpdatePolicy.Evaluate(
            currentVersion: AppVersion.Parse("1.0.0"),
            minimumRequiredVersion: AppVersion.Parse("1.0.0"));

        Assert.False(result.MustUpdate);
    }

    [Fact]
    public void Evaluate_CurrentAboveMinimum_DoesNotBlock()
    {
        UpdatePolicyResult result = UpdatePolicy.Evaluate(
            currentVersion: AppVersion.Parse("1.5.0"),
            minimumRequiredVersion: AppVersion.Parse("1.0.0"));

        Assert.False(result.MustUpdate);
    }

    [Fact]
    public void Evaluate_PreReleaseBelowStableMinimum_MustUpdateIsTrue()
    {
        UpdatePolicyResult result = UpdatePolicy.Evaluate(
            currentVersion: AppVersion.Parse("1.0.0-beta.1"),
            minimumRequiredVersion: AppVersion.Parse("1.0.0"));

        Assert.True(result.MustUpdate);
    }
}
