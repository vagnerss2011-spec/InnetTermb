namespace RemoteOps.Sync.Remote;

/// <summary>
/// Em qual metade do ciclo o item ficou para trás. Não é detalhe cosmético: "não sobe" e "não desce"
/// são problemas diferentes (um é o cofre local, o outro é o que o servidor guarda) e pedem ações
/// diferentes de quem for investigar.
/// </summary>
public enum SecretSyncPhase
{
    Push,
    Pull,
}

/// <summary>
/// Um envelope que o ciclo PULOU em vez de deixar travar o canal inteiro.
///
/// <para><b>Só isto pode ser registrado (ADR-013):</b> o id do envelope e o TIPO do erro. Nunca a
/// mensagem do servidor, nunca campo do envelope, nunca token — o id é identificador, não material
/// criptográfico.</para>
/// </summary>
public sealed record SecretSyncSkip(string EnvelopeId, SecretSyncPhase Phase, string ErrorType);

/// <summary>
/// O que aconteceu num ciclo do canal de segredos.
///
/// <para>Existe porque o ciclo deixou de ser tudo-ou-nada: agora ele pode terminar TENDO PULADO
/// itens, e quem chama precisa saber disso. Sem este retorno, um envelope malformado sumiria da
/// vista — o ciclo diria "deu certo" enquanto uma senha ficava para trás, que é a mesma falha
/// silenciosa que o isolamento por item veio consertar.</para>
/// </summary>
public sealed record SecretSyncReport(IReadOnlyList<SecretSyncSkip> Skipped);
