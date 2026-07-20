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

        // Nome CANÔNICO ("D" minúsculo), não a string crua: grupo do SignalR é case-sensitive e o
        // broadcast usa wsId.ToString() (SyncService). Entrar com o GUID em maiúsculas colocaria o
        // cliente num grupo que nunca recebe nada — falha 100% silenciosa.
        await Groups.AddToGroupAsync(Context.ConnectionId, wsId.ToString());
    }

    public async Task LeaveWorkspace(string workspaceId)
    {
        // Mesma canonicalização do Join: sair com outra grafia deixaria o cliente no grupo.
        string group = Guid.TryParse(workspaceId, out var wsId) ? wsId.ToString() : workspaceId;
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    /// <summary>
    /// Entra automaticamente nos grupos de TODOS os workspaces do usuário autenticado.
    ///
    /// <para>Por que aqui e não só no cliente: o grupo é por <c>ConnectionId</c> e toda reconexão gera
    /// um ConnectionId novo. Antes disto, um cliente que reconectava ficava fora do grupo PARA SEMPRE
    /// (o <c>JoinWorkspace</c> só era chamado no connect inicial) e o tempo real morria em silêncio até
    /// o app reiniciar. Como <c>OnConnectedAsync</c> roda a cada conexão nova, o join vira invariante do
    /// servidor: fecha a classe inteira de bugs, não um exemplar. O cliente segue chamando
    /// <c>JoinWorkspace</c> — redundância barata e compatível com versões antigas.</para>
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();

        var userIdStr = Context.User?.FindFirstValue("sub");
        if (!Guid.TryParse(userIdStr, out var userId)) return;

        List<Guid> workspaceIds = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.WorkspaceId)
            .ToListAsync();

        foreach (Guid wsId in workspaceIds)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, wsId.ToString());
        }
    }
}
