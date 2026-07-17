using System.Security.Cryptography;
using System.Text;
using RemoteOps.Security.Account;
using Xunit;

namespace RemoteOps.UnitTests.Security;

public class AmkKeyDerivationTests
{
    [Fact]
    public void SameAmk_SameWorkspace_DerivesSameWdk_AndSecretRoundTripsAcrossDevices()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);

        // "Device A" e "Device B" derivam a WDK da MESMA AMK — sem sincronizar chave nenhuma.
        byte[] wdkA = AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-1");
        byte[] wdkB = AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-1");
        Assert.Equal(wdkA, wdkB);

        byte[] secret = Encoding.UTF8.GetBytes("senha-do-switch");
        byte[] sealedBlob = AccountKeyService.WrapKey(secret, wdkA, "cek");
        byte[] opened = AccountKeyService.UnwrapKey(sealedBlob, wdkB, "cek");
        Assert.Equal(secret, opened);
    }

    [Fact]
    public void DifferentWorkspaces_DeriveDifferentWdks()
    {
        byte[] amk = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(
            AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-1"),
            AmkKeyDerivation.DeriveWorkspaceKey(amk, "ws-2"));
    }

    [Fact]
    public void DifferentAmks_DeriveDifferentWdks()
    {
        byte[] a = RandomNumberGenerator.GetBytes(32);
        byte[] b = RandomNumberGenerator.GetBytes(32);
        Assert.NotEqual(
            AmkKeyDerivation.DeriveWorkspaceKey(a, "ws"),
            AmkKeyDerivation.DeriveWorkspaceKey(b, "ws"));
    }
}
