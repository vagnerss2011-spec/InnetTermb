using System.Diagnostics;
using System.Globalization;

using RemoteOps.Contracts.Audit;
using RemoteOps.Contracts.ExternalTools;

namespace RemoteOps.MikroTik;

public sealed class WinBoxRunner : IWinBoxRunner
{
    private readonly WinBoxToolManifest _toolManifest;
    private readonly IWinBoxPolicyProvider _policyProvider;
    private readonly IWinBoxAuditSink _auditSink;
    private readonly IWinBoxCredentialResolver _credentialResolver;
    private readonly IWinBoxProcessLauncher _processLauncher;

    public WinBoxRunner(
        WinBoxToolManifest toolManifest,
        IWinBoxPolicyProvider policyProvider,
        IWinBoxAuditSink auditSink,
        IWinBoxCredentialResolver credentialResolver,
        IWinBoxProcessLauncher processLauncher)
    {
        _toolManifest = toolManifest;
        _policyProvider = policyProvider;
        _auditSink = auditSink;
        _credentialResolver = credentialResolver;
        _processLauncher = processLauncher;
    }

    public static WinBoxRunner Create(
        WinBoxToolManifest toolManifest,
        IWinBoxPolicyProvider policyProvider,
        IWinBoxAuditSink auditSink,
        IWinBoxCredentialResolver credentialResolver)
        => new(toolManifest, policyProvider, auditSink, credentialResolver, new DefaultProcessLauncher());

    public async Task<string> LaunchAsync(ExternalToolLaunchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var launchId = Guid.NewGuid().ToString("N");

        // Sintaxe --romon não confirmada contra CLI oficial WinBox — recusa auditada até validação. (ADR-006)
        if (request.Romon?.Enabled == true)
        {
            await Emit(request, launchId, "winbox_open_failed", new()
            {
                ["launchId"] = launchId,
                ["reason"] = "romon_not_confirmed_official_cli",
            }, ct);
            throw new WinBoxValidationException(
                "RoMON não confirmado contra CLI oficial WinBox neste sprint — execução recusada. (ADR-006)");
        }

        var policy = await _policyProvider.EvaluateAsync(
            request.WorkspaceId, request.HostId, request.RequestedBy, ct);

        if (!policy.Allowed)
        {
            await Emit(request, launchId, "winbox_open_failed", new()
            {
                ["launchId"] = launchId,
                ["reason"] = "policy_denied",
                ["denyReason"] = policy.DenyReason,
            }, ct);
            throw new WinBoxValidationException($"Política negou abertura WinBox: {policy.DenyReason}");
        }

        // Senha via argumento: negada explicitamente quando política proíbe
        if (request.IncludePasswordArgument && !policy.PasswordArgumentAllowed)
        {
            await Emit(request, launchId, "winbox_open_failed", new()
            {
                ["launchId"] = launchId,
                ["reason"] = "password_arg_denied_by_policy",
            }, ct);
            throw new WinBoxValidationException(
                "Senha via argumento de processo negada pela política — use Modo A (sem senha automática).");
        }

        try
        {
            _toolManifest.Validate();
            await Emit(request, launchId, "winbox_tool_validated", new()
            {
                ["validated"] = true,
                ["version"] = _toolManifest.Version,
            }, ct);
        }
        catch (WinBoxValidationException ex)
        {
            await Emit(request, launchId, "winbox_tool_validated", new()
            {
                ["validated"] = false,
                ["reason"] = ex.Message,
            }, ct);
            throw;
        }

        await Emit(request, launchId, "winbox_open_requested", new()
        {
            ["launchId"] = launchId,
            ["tool"] = request.Tool,
            ["hostId"] = request.HostId,
            ["addressFamily"] = request.Target.AddressFamily,
            ["policyDecisionId"] = request.PolicyDecisionId,
        }, ct);

        // Resolução de credencial — somente quando política e request permitem senha
        string? password = null;
        if (request.IncludePasswordArgument
            && policy.PasswordArgumentAllowed
            && !string.IsNullOrEmpty(request.CredentialRefId))
        {
            password = await _credentialResolver.ResolvePasswordAsync(request.CredentialRefId, ct);
        }

        var args = WinBoxArgumentBuilder.Build(request, password, policy.PasswordArgumentAllowed);

        // Auditoria de target IPv6
        if (WinBoxArgumentBuilder.IsIpv6Like(request.Target.Address))
        {
            await Emit(request, launchId, "winbox_ipv6_target_used", new()
            {
                ["launchId"] = launchId,
            }, ct);
        }

        // ProcessStartInfo com ArgumentList — nunca interpola senha na linha de comando
        var psi = new ProcessStartInfo
        {
            FileName = _toolManifest.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        // Argcount apenas — nunca logar os argumentos em si (podem conter senha)
        await Emit(request, launchId, "winbox_open_started", new()
        {
            ["launchId"] = launchId,
            ["argCount"] = args.Count,
        }, ct);

        string processId;
        try
        {
            processId = await _processLauncher.StartAsync(psi, ct);
        }
        catch (Exception ex)
        {
            await Emit(request, launchId, "winbox_open_failed", new()
            {
                ["launchId"] = launchId,
                ["error"] = ex.GetType().Name,
            }, ct);
            throw;
        }

        // Emitir evento de senha-via-argumento APÓS o lançamento, sem a senha
        if (request.IncludePasswordArgument && policy.PasswordArgumentAllowed && !string.IsNullOrEmpty(password))
        {
            await Emit(request, launchId, "winbox_password_argument_used", new()
            {
                ["launchId"] = launchId,
                ["processId"] = processId,
            }, ct);
        }

        return launchId;
    }

    private Task Emit(
        ExternalToolLaunchRequest request,
        string launchId,
        string action,
        Dictionary<string, object?> metadata,
        CancellationToken ct)
    {
        var evt = new AuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkspaceId = request.WorkspaceId,
            ActorUserId = request.RequestedBy,
            Action = action,
            TargetType = "winbox_launch",
            TargetId = launchId,
            Metadata = metadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        return _auditSink.EmitAsync(evt, ct);
    }

    private sealed class DefaultProcessLauncher : IWinBoxProcessLauncher
    {
        public Task<string> StartAsync(ProcessStartInfo psi, CancellationToken ct)
        {
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Falha ao iniciar o processo WinBox.");
            return Task.FromResult(proc.Id.ToString(CultureInfo.InvariantCulture));
        }
    }
}
