using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;

namespace RemoteOps.Cloud.Hubs;

/// <summary>
/// Hub SignalR que emite hints de mudança. NÃO carrega payload completo.
/// O cliente reage fazendo GET /sync/pull (ADR-002).
/// Clientes são agrupados por workspaceId para broadcast escopado.
/// </summary>
[Authorize]
public sealed class SyncHub(AppDbContext db) : Hub
{
    /// <summary>
    /// Cliente chama ao conectar para entrar no grupo do workspace.
    /// Requer que o usuário autenticado seja membro do workspace.
    /// </summary>
    public async Task JoinWorkspace(string workspaceId)
    {
        if (!Guid.TryParse(workspaceId, out var wsId)) return;

        var userIdStr = Context.User?.FindFirstValue("sub");
        if (!Guid.TryParse(userIdStr, out var userId)) return;

        var isMember = await db.Memberships.AsNoTracking()
            .AnyAsync(m => m.WorkspaceId == wsId && m.UserId == userId);
        if (!isMember) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, workspaceId);
    }

    public async Task LeaveWorkspace(string workspaceId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, workspaceId);
    }

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirstValue("sub");
        await base.OnConnectedAsync();
        _ = userId;
    }
}
