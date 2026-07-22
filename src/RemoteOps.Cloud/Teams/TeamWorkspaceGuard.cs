using Microsoft.EntityFrameworkCore;
using RemoteOps.Cloud.Data;
using RemoteOps.Cloud.Data.Entities;

namespace RemoteOps.Cloud.Teams;

/// <summary>
/// Operação de TIME pedida num workspace que é cofre PESSOAL. Tem tipo próprio (e não um
/// <see cref="ArgumentException"/> genérico) porque a resposta precisa ser reconhecível dos dois
/// lados: o <c>CloudExceptionHandler</c> a transforma em <b>422</b> com o motivo
/// <see cref="ReasonCode"/>, e o cliente mostra a frase ao operador em vez de "não foi possível
/// concluir a operação" — que é o recado que faz alguém tentar de novo achando que foi a rede.
/// </summary>
public sealed class PersonalWorkspaceException : Exception
{
    /// <summary>
    /// Motivo em forma de máquina, no <c>reason</c> do ProblemDetails. O texto do
    /// <see cref="Exception.Message"/> é para o humano e pode mudar; é por este código que o cliente
    /// decide o que fazer.
    /// </summary>
    public const string ReasonCode = "workspace.personal";

    /// <param name="consequence">
    /// O que aconteceria se a operação passasse — a metade da frase que muda de um endpoint para o
    /// outro. O resto do texto é UM só: duas cópias da mesma explicação envelhecem torto, e esta é
    /// justamente a frase que o operador vai ler quando estiver prestes a compartilhar o acervo.
    /// </param>
    public PersonalWorkspaceException(string consequence)
        : base($"Este workspace é o COFRE PESSOAL da sua conta, e não um time. {consequence} "
               + "Crie um time — ele nasce vazio — e refaça a operação dentro dele.")
    {
    }
}

/// <summary>
/// A guarda de defesa em profundidade da Fatia 1: <b>o servidor recusa sozinho</b>.
///
/// <para>Antes desta marca, <c>WorkspaceEntity</c> não distinguia cofre pessoal de time — convite e
/// <c>PUT /key</c> eram aceitos em QUALQUER workspace onde o chamador tivesse a permissão. Com a
/// interface consertada isso já não acontece no uso normal, mas a interface é a camada que um
/// cliente com bug (ou adulterado) não respeita. Aqui a decisão sai do banco.</para>
///
/// <para><b>Ordem importa:</b> chame esta guarda SEMPRE depois do
/// <see cref="Rbac.PermissionEvaluator"/>. Antes dele, a resposta diferenciada ("este workspace é
/// pessoal") viraria um oráculo: qualquer conta autenticada descobriria, um GUID por vez, se um
/// workspace alheio existe e de que tipo ele é. Depois do RBAC, quem recebe a frase já é membro com
/// permissão — e um membro não descobre nada de novo sobre o próprio workspace.</para>
/// </summary>
internal static class TeamWorkspaceGuard
{
    /// <summary>
    /// Recusa alto se <paramref name="workspaceId"/> não for um workspace de TIME.
    ///
    /// <para><b>Workspace inexistente também recusa</b> (a consulta devolve <c>null</c>, que não é
    /// <see cref="WorkspaceKinds.Team"/>): "não sei o que é" nunca pode virar "pode compartilhar".
    /// Na prática o RBAC já barrou antes — ele exige membership —, e esta linha é a que sobra em pé
    /// caso alguém um dia chame a guarda de outro lugar.</para>
    /// </summary>
    /// <exception cref="PersonalWorkspaceException">O workspace é pessoal (ou desconhecido).</exception>
    internal static async Task RequireTeamAsync(
        AppDbContext db,
        Guid workspaceId,
        string operation,
        string consequence,
        ILogger logger,
        CancellationToken ct)
    {
        var kind = await db.Workspaces.AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => w.Kind)
            .FirstOrDefaultAsync(ct);

        if (WorkspaceKinds.IsTeam(kind))
        {
            return;
        }

        // O log leva o motivo inteiro porque quem o lê é o operador do servidor socorrendo alguém —
        // e não há segredo nenhum aqui: id de workspace e a marca de nascimento (ADR-013).
        logger.LogWarning(
            "Team operation {Operation} REFUSED on workspace {WorkspaceId}: it is not a team workspace "
            + "(kind {Kind}). Sharing here would expose the whole personal inventory of its owner.",
            operation, workspaceId, kind ?? "<inexistente>");

        throw new PersonalWorkspaceException(consequence);
    }
}
