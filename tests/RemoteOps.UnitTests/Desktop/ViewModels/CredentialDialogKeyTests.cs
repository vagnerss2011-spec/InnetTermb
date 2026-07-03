using RemoteOps.Desktop.ViewModels;
using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

public sealed class CredentialDialogKeyTests
{
    [Fact]
    public void Add_DefaultsToPassword_TypePickerVisible()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.Add);
        Assert.False(vm.IsKeyType);
        Assert.True(vm.ShowTypePicker);
        Assert.True(vm.ShowPassword);
        Assert.False(vm.ShowPrivateKey);
    }

    [Fact]
    public void SwitchToKey_TogglesPanels()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.Add) { IsKeyType = true };
        Assert.True(vm.ShowPrivateKey);
        Assert.False(vm.ShowPassword);
    }

    [Fact]
    public void ChangePasswordMode_HidesTypePicker()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.ChangePassword);
        Assert.False(vm.ShowTypePicker);
    }

    [Fact]
    public void EditMode_HidesPasswordAndTypePicker()
    {
        var vm = new CredentialDialogViewModel(CredentialDialogMode.Edit);
        Assert.False(vm.ShowTypePicker);
        Assert.False(vm.ShowPassword);
    }
}
