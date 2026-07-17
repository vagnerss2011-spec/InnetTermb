using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using RemoteOps.Desktop.ViewModels;
using RemoteOps.Sync.Remote;

using Xunit;

namespace RemoteOps.UnitTests.Desktop.ViewModels;

/// <summary>
/// UI de ativar/desativar 2FA (spec Fase 3, item 6): enroll → mostra segredo → confirm; disable exige
/// código. O IMfaApi é fake — a cripto/servidor é provada em MfaServiceTests/MfaApiClientTests.
/// </summary>
public sealed class MfaEnrollmentViewModelTests
{
    private sealed class FakeMfaApi : IMfaApi
    {
        public Exception? Throw;
        public int EnrollCalls;
        public int ConfirmCalls;
        public int DisableCalls;
        public string? LastConfirmCode;
        public string? LastDisableCode;
        public MfaEnrollResponse EnrollResponse =
            new("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", "otpauth://totp/RemoteOps:op@x?secret=GEZ...");

        public Task<MfaEnrollResponse> EnrollAsync(CancellationToken ct = default)
        {
            EnrollCalls++;
            return Throw is not null
                ? Task.FromException<MfaEnrollResponse>(Throw)
                : Task.FromResult(EnrollResponse);
        }

        public Task ConfirmAsync(MfaConfirmRequest request, CancellationToken ct = default)
        {
            ConfirmCalls++;
            LastConfirmCode = request.Code;
            return Throw is not null ? Task.FromException(Throw) : Task.CompletedTask;
        }

        public Task DisableAsync(MfaDisableRequest request, CancellationToken ct = default)
        {
            DisableCalls++;
            LastDisableCode = request.Code;
            return Throw is not null ? Task.FromException(Throw) : Task.CompletedTask;
        }
    }

    private static MfaEnrollmentViewModel NewVm(FakeMfaApi api, Action<string>? copy = null)
        => new(api, copy ?? (_ => { }));

    [Fact]
    public void StartsInIntro()
    {
        var vm = NewVm(new FakeMfaApi());
        Assert.True(vm.IsIntro);
        Assert.False(vm.IsShowSecret);
        Assert.False(vm.IsDone);
    }

    [Fact]
    public async Task BeginEnroll_ShowsSecret_AndUri()
    {
        var api = new FakeMfaApi();
        var vm = NewVm(api);

        await vm.BeginEnrollAsync();

        Assert.Equal(1, api.EnrollCalls);
        Assert.True(vm.IsShowSecret);
        // Segredo agrupado em blocos de 4 (contém espaço) mas preserva os caracteres.
        Assert.Contains(' ', vm.SecretBase32);
        Assert.Contains("GEZD", vm.SecretBase32);
        Assert.StartsWith("otpauth://totp/", vm.OtpauthUri);
        Assert.NotEmpty(vm.StatusMessage);
    }

    [Fact]
    public async Task Confirm_WithValidCode_CallsApi_AndFinishes()
    {
        var api = new FakeMfaApi();
        var vm = NewVm(api);
        await vm.BeginEnrollAsync();

        vm.ConfirmCode = "123456";
        await vm.ConfirmAsync();

        Assert.Equal(1, api.ConfirmCalls);
        Assert.Equal("123456", api.LastConfirmCode);
        Assert.True(vm.IsDone);
        Assert.Contains("ativada", vm.DoneMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("12ab56")]
    public async Task Confirm_WithMalformedCode_DoesNotCallApi(string code)
    {
        var api = new FakeMfaApi();
        var vm = NewVm(api);
        await vm.BeginEnrollAsync();

        vm.ConfirmCode = code;
        await vm.ConfirmAsync();

        Assert.Equal(0, api.ConfirmCalls);
        Assert.True(vm.HasError);
        Assert.True(vm.IsShowSecret); // segue na tela do segredo
    }

    [Fact]
    public async Task Enroll_WhenAlreadyActive_ShowsPtBrConflictMessage()
    {
        var api = new FakeMfaApi { Throw = new CloudSyncException(HttpStatusCode.Conflict) };
        var vm = NewVm(api);

        await vm.BeginEnrollAsync();

        Assert.True(vm.HasError);
        Assert.Contains("já está ativa", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsIntro); // não avançou
    }

    [Fact]
    public async Task Confirm_WithWrongCode_ShowsPtBrInvalidMessage()
    {
        var api = new FakeMfaApi();
        var vm = NewVm(api);
        await vm.BeginEnrollAsync();
        api.Throw = new CloudSyncException(HttpStatusCode.BadRequest);

        vm.ConfirmCode = "000000";
        await vm.ConfirmAsync();

        Assert.True(vm.HasError);
        Assert.Contains("inválido", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(vm.IsShowSecret);
    }

    [Fact]
    public async Task Disable_WithValidCode_CallsApi_AndFinishes()
    {
        var api = new FakeMfaApi();
        var vm = NewVm(api);

        vm.DisableCode = "654321";
        await vm.DisableAsync();

        Assert.Equal(1, api.DisableCalls);
        Assert.Equal("654321", api.LastDisableCode);
        Assert.True(vm.IsDone);
        Assert.Contains("desativada", vm.DoneMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Disable_WithMalformedCode_DoesNotCallApi()
    {
        var api = new FakeMfaApi();
        var vm = NewVm(api);

        vm.DisableCode = "12";
        await vm.DisableAsync();

        Assert.Equal(0, api.DisableCalls);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task CopySecret_PutsSecretOnClipboard()
    {
        string? copied = null;
        var api = new FakeMfaApi();
        var vm = NewVm(api, text => copied = text);
        await vm.BeginEnrollAsync();

        vm.CopySecretCommand.Execute(null);

        Assert.Equal(vm.SecretBase32, copied);
        Assert.NotEmpty(vm.StatusMessage);
    }

    [Fact]
    public void Close_RaisesCompleted()
    {
        var vm = NewVm(new FakeMfaApi());
        bool completed = false;
        vm.Completed += (_, _) => completed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(completed);
    }
}
