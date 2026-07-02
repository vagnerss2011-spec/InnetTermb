using System;
using System.Threading.Tasks;
using RemoteOps.Desktop.Reporting;
using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class BugReportViewModelTests
{
    private sealed class FakeComposer : IBugReportComposer
    {
        public bool ThrowOnSave;
        public string BuildPreview(BugReport r) => "PREVIEW:" + r.Description;
        public Uri BuildMailtoUri(BugReport r) => new("mailto:suporte@innet.tec.br?subject=x");
        public Task<string> SaveLocalCopyAsync(BugReport r)
            => ThrowOnSave ? throw new System.IO.IOException("disk") : Task.FromResult("C:/tmp/r.txt");
    }

    [Fact]
    public void CanSubmit_RequiresTitleAndDescription()
    {
        var vm = new BugReportViewModel(new FakeComposer());
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.Title = "t";
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.Description = "d";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Submit_OpensMailto_EvenIfSaveFails()
    {
        Uri? opened = null;
        var composer = new FakeComposer { ThrowOnSave = true };
        var vm = new BugReportViewModel(composer, uri => opened = uri, _ => { })
        {
            Title = "t",
            Description = "d",
        };
        await vm.SubmitForTestAsync();
        Assert.NotNull(opened);
        Assert.StartsWith("mailto:", opened!.ToString());
    }

    [Fact]
    public void Copy_RefreshesPreview_AndCopies()
    {
        string? copied = null;
        var vm = new BugReportViewModel(new FakeComposer(), _ => { }, text => copied = text)
        {
            Description = "abc",
        };
        vm.CopyCommand.Execute(null);
        Assert.Equal("PREVIEW:abc", copied);
    }
}
