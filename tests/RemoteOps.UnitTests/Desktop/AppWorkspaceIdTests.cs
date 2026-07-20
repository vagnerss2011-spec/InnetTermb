using RemoteOps.Desktop;

using Xunit;

namespace RemoteOps.UnitTests.Desktop;

/// <summary>
/// Cobre a normalização do workspaceId lido de variável de ambiente. O grupo do SignalR é uma string
/// case-sensitive e o servidor faz broadcast com <c>Guid.ToString()</c>: um id em outra grafia entra
/// num grupo que nunca recebe hint, e a falha é 100% silenciosa — daí o teste ser por caractere.
/// </summary>
public sealed class AppWorkspaceIdTests
{
    [Theory]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void WorkspaceId_Is_Canonicalized(string input, string expected)
        => Assert.Equal(expected, App.NormalizeWorkspaceId(input));

    // Fail-closed: preferimos o app sem sync a um app sincronizando contra o workspace errado.
    [Theory]
    [InlineData("nao-e-guid")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Invalid_WorkspaceId_Is_Rejected(string? bad)
        => Assert.Null(App.NormalizeWorkspaceId(bad));
}
