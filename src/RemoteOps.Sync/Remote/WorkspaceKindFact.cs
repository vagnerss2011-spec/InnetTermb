namespace RemoteOps.Sync.Remote;

/// <summary>
/// ⚠️ <b>O que se SABE sobre a natureza de um workspace de servidor — incluindo "não sei".</b>
///
/// <para><b>Por que um tri-estado, e não um <c>bool</c>:</b> o app decidia isto com
/// <c>bool IsTeamWorkspace</c>, e o <c>false</c> acumulava dois significados incompatíveis — "o
/// servidor disse que NÃO é time" e "eu não consegui descobrir". A única fonte era
/// <c>GET /workspaces/{id}/key</c>, cujo 404 significa <b>"a SUA CONTA não guarda embrulho neste
/// workspace"</b> — e um 404 de INFRAESTRUTURA (proxy sem a rota, URL errada, cliente novo falando
/// com backend velho) é indistinguível disso. Com um <c>bool</c>, esse "não sei" virava
/// afirmação, e a afirmação autorizava gravar o dono do banco pessoal com o GUID do TIME: os ~700
/// equipamentos do operador passariam a subir para o cofre dos colegas.</para>
///
/// <para><b>A regra que vale em toda leitura deste tipo:</b> <see cref="Unknown"/> nunca autoriza
/// uma escrita irreversível (marcador de dono, adoção de cofre). Ele autoriza perguntar, recusar
/// alto e avisar — nunca afirmar.</para>
/// </summary>
public enum WorkspaceKindFact
{
    /// <summary>
    /// <b>Não sei.</b> Campo ausente (backend anterior a esta versão), 404 que não distingue
    /// "sem embrulho" de "sem rota", resposta que este binário não conhece. É o valor mais fraco —
    /// e o default de propósito: quem não perguntou nada fica aqui.
    /// </summary>
    Unknown,

    /// <summary>O servidor afirmou: cofre PESSOAL (nasce no <c>/auth/register</c>, raiz AMK).</summary>
    Personal,

    /// <summary>O servidor afirmou: TIME (nasce em <c>POST /workspaces</c>, chave compartilhada).</summary>
    Team,
}

/// <summary>
/// Traduz o <c>kind</c> que vem no fio para o <see cref="WorkspaceKindFact"/>.
///
/// <para>Espelha os valores de <c>RemoteOps.Cloud.Data.Entities.WorkspaceKinds</c> — o cliente não
/// referencia o assembly do servidor (mesma razão do <c>AccountContracts</c>), e o contrato é a
/// string no fio. Um drift futuro aparece em <c>AccountContractsWireTests</c>.</para>
/// </summary>
public static class WorkspaceKindFacts
{
    /// <summary>Valor do fio para o cofre pessoal.</summary>
    public const string PersonalKind = "personal";

    /// <summary>Valor do fio para o time.</summary>
    public const string TeamKind = "team";

    /// <summary>
    /// ⚠️ <b>Lista de RECONHECIMENTO, não de negação.</b> Só as duas strings exatas viram fato;
    /// <c>null</c>, vazio, lixo ou um valor futuro que este binário não conhece viram
    /// <see cref="WorkspaceKindFact.Unknown"/>.
    ///
    /// <para>Escrito ao contrário (<c>kind != "team" ⇒ pessoal</c>), o campo ausente do backend
    /// velho passaria a AFIRMAR "pessoal" — e é exatamente essa afirmação que autoriza gravar o
    /// dono do banco dos ~700 com o workspace da vez. A janela em que isso acontece é real e
    /// datada: entre o deploy do backend e a atualização dos PCs.</para>
    /// </summary>
    public static WorkspaceKindFact From(string? kind) => kind switch
    {
        TeamKind => WorkspaceKindFact.Team,
        PersonalKind => WorkspaceKindFact.Personal,
        _ => WorkspaceKindFact.Unknown,
    };
}
