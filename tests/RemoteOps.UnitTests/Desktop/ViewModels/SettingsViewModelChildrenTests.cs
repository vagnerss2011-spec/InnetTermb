using System.IO;
using RemoteOps.Desktop.Changelog;
using RemoteOps.Desktop.Infrastructure;
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class SettingsViewModelChildrenTests
{
    private sealed class FakeDiag : IDiagnosticsProvider { public string BuildDiagnostics() => "d"; }

    [Fact]
    public void Exposes_Changelog_And_BugReport_WhenProvided()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "s.json"));
        var changelog = new ChangelogViewModel(new EmbeddedChangelogSource(), store);
        var bug = new BugReportViewModel(new MailtoBugReportComposer(new FakeDiag()));
        var vm = new SettingsViewModel(store, updateService: null, changelog: changelog, bugReport: bug);
        Assert.Same(changelog, vm.Changelog);
        Assert.Same(bug, vm.BugReport);
    }

    [Fact]
    public void OldCtor_StillWorks_ChildrenNull()
    {
        var store = new JsonSettingsStore(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "s.json"));
        var vm = new SettingsViewModel(store);
        Assert.Null(vm.Changelog);
        Assert.Null(vm.BugReport);
    }
}
