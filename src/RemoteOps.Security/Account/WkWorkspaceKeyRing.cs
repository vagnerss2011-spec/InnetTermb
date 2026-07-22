using System.Security.Cryptography;

using RemoteOps.Security.Crypto;
using RemoteOps.Security.Storage;
using RemoteOps.Security.Vault;

namespace RemoteOps.Security.Account;

/// <summary>
/// A TERCEIRA raiz de chave do cofre, e a primeira que existe para ser COMPARTILHADA: a WK
/// (Workspace Key) do time. Ao contrário das outras duas, ela não deriva de nada —
/// <see cref="WorkspaceKeyRing"/> sorteia e protege por DPAPI (presa à máquina) e
/// <see cref="AmkWorkspaceKeyRing"/> deriva da AMK (presa à CONTA). Justamente por derivar da conta,
/// a raiz da AMK não serve a um time: dois membros do MESMO workspace chegariam a chaves
/// DIFERENTES e um não abriria o cofre do outro. Não é bug, é o E2EE funcionando — e o servidor não
/// pode "dar acesso" porque não tem chave nenhuma.
///
/// <para>Aqui a WK é 32 bytes de CSPRNG, sorteados uma única vez por workspace. Sendo um segredo
/// (e não uma derivação), ela pode ser ENTREGUE cifrada a cada membro — é isso que a Fatia 1
/// compra. Em disco ela nunca aparece em claro: fica embrulhada sob a AMK de quem a guarda
/// (<see cref="AccountKeyService.WrapKey"/>), com AAD próprio por workspace.</para>
///
/// <para><b>Este ring não distribui nem recebe WK de ninguém.</b> Ele só sorteia, guarda e devolve.
/// A entrega ao convidado (embrulho sob a chave do convite) é do estágio do convite — separar as
/// duas coisas mantém o núcleo de cripto testável sem rede.</para>
/// </summary>
public sealed class WkWorkspaceKeyRing : IWorkspaceKeyRing, IDisposable
{
    private const int AmkSize = 32;
    private const int WorkspaceKeySize = 32; // AES-256

    /// <summary>
    /// O app compartilha UM <see cref="IWorkspaceKeyStore"/> entre as raízes (o mesmo
    /// <c>FileVaultStore</c> serve o ring do DPAPI e este). Sem prefixo, gravar a WK de um workspace
    /// sobrescreveria o blob DPAPI de mesmo id — que é PERDA DE CHAVE, não erro de leitura.
    /// </summary>
    private const string StoreKeyPrefix = "wk:";

    /// <summary>AAD do embrulho da WK sob a AMK: prende o blob ao workspace a que ele pertence.</summary>
    private const string WrapContextPrefix = "wk|";

    private readonly IWorkspaceKeyStore _store;
    private readonly byte[] _amk;
    private readonly bool _allowKeyCreation;

    // WKs já desembrulhadas nesta sessão. Sem o cache, cada operação do cofre faria um GCM
    // desnecessário; com ele, o ring é ESTÁVEL — a mesma chave do começo ao fim da sessão.
    private readonly Dictionary<string, byte[]> _unwrapped = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _disposed;

    /// <summary>Guarda uma CÓPIA da AMK (zerada no <see cref="Dispose"/>) — o chamador segue dono da dele.</summary>
    /// <param name="allowKeyCreation">
    /// <c>false</c> = <b>fail-closed</b>: sem WK guardada, o ring RECUSA em vez de sortear. É como o
    /// cofre do time roda no app, e o motivo é o defeito número um desta fatia: o
    /// <c>CredentialVault</c> chama <see cref="GetOrCreateWorkspaceKeyAsync"/> em TODA operação, então
    /// qualquer coisa que toque o cofre antes de o convite ser aceito ganharia uma WK ALEATÓRIA — e o
    /// convidado passaria semanas cadastrando senhas que ninguém do time consegue abrir, sem um único
    /// erro na tela. <c>true</c> (o padrão) é o caminho de quem CRIA o time: ali o sorteio é o ato
    /// legítimo que faz a chave nascer.
    /// </param>
    public WkWorkspaceKeyRing(IWorkspaceKeyStore store, ReadOnlySpan<byte> amk, bool allowKeyCreation = true)
    {
        _allowKeyCreation = allowKeyCreation;
        ArgumentNullException.ThrowIfNull(store);
        if (amk.Length != AmkSize)
        {
            throw new ArgumentException($"A AMK precisa ter {AmkSize} bytes (recebidos {amk.Length}).", nameof(amk));
        }

        _store = store;
        _amk = amk.ToArray();
    }

    public string AlgorithmId => VaultAlgorithms.WkRootedV1;

    /// <summary>
    /// Lê a WK existente, ou <c>null</c>. NÃO cria: sortear uma WK nova por baixo de segredos já
    /// selados mascararia a perda da chave do time como se fosse um cofre vazio.
    /// </summary>
    public async Task<WorkspaceKey?> TryGetWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[]? wk = await LoadAsync(workspaceId, ct).ConfigureAwait(false);
            return wk is null ? null : Copy(wk);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Recupera a WK do workspace; sorteia e guarda (embrulhada) na primeira vez. Lança se a WK
    /// existe mas não abre com esta AMK (blob de outra conta) — falhar alto é obrigatório: devolver
    /// null ali faria este ring sortear uma chave nova e "perder" o cofre do time em silêncio.
    /// </summary>
    public async Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // Sob o mesmo gate do TryGet: duas criações concorrentes sorteariam DUAS WKs e a segunda
        // gravação deixaria os segredos da primeira ilegíveis.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[]? existing = await LoadAsync(workspaceId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return Copy(existing);
            }

            // FAIL-CLOSED. Aqui é o ponto exato em que o cofre do time bifurcaria: sortear "só desta
            // vez" produz senhas que ninguém do time abre, e nada na tela denuncia. Recusar ALTO é a
            // única saída que o operador enxerga — e a mensagem diz o que fazer.
            if (!_allowKeyCreation)
            {
                throw new VaultException(
                    $"O cofre do time '{workspaceId}' ainda não tem a chave neste computador. "
                    + "Aceite o convite (identificador + código recebido por outro canal) antes de "
                    + "cadastrar ou abrir senhas do time.");
            }

            byte[] fresh = RandomNumberGenerator.GetBytes(WorkspaceKeySize);
            try
            {
                byte[] wrapped = AccountKeyService.WrapKey(fresh, _amk, WrapContext(workspaceId));
                await _store.SaveAsync(StoreKey(workspaceId), wrapped, ct).ConfigureAwait(false);
                _unwrapped[workspaceId] = fresh;
                return Copy(fresh);
            }
            catch
            {
                // Se o embrulho/persistência falhar, a WK bruta não entra no cache (que a zeraria no
                // Dispose): zere aqui para não deixar material de chave órfão na heap.
                CryptographicOperations.ZeroMemory(fresh);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adota uma WK que veio de FORA — o convite. É a peça que o estágio 1b deixou explicitamente
    /// para cá: sem ela o ring só sabe sortear, e sortear é exatamente o que o convidado não pode
    /// fazer.
    ///
    /// <para>Devolve o embrulho sob a AMK, que é <b>o mesmo blob</b> que sobe como <c>WrappedWk</c>
    /// da membership. Devolver daqui (em vez de deixar o chamador montar) mantém UMA definição do
    /// AAD do embrulho: uma constante de cripto copiada para a camada de cima é o tipo de coisa que
    /// diverge em silêncio e só aparece como "o cofre não abre" no PC do colega.</para>
    /// </summary>
    /// <exception cref="VaultException">
    /// Já existe outra WK guardada para este workspace. Trocar a chave por baixo de segredos já
    /// selados é perda de cofre disfarçada de sucesso — reimportar a MESMA, por outro lado, é no-op
    /// (o aceite pode ser repetido depois de uma queda de rede).
    /// </exception>
    public async Task<byte[]> ImportWorkspaceKeyAsync(
        string workspaceId, ReadOnlyMemory<byte> workspaceKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        if (workspaceKey.Length != WorkspaceKeySize)
        {
            throw new ArgumentException(
                $"A chave do time precisa ter {WorkspaceKeySize} bytes (recebidos {workspaceKey.Length}).",
                nameof(workspaceKey));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[]? existing = await LoadAsync(workspaceId, ct).ConfigureAwait(false);
            if (existing is not null && !CryptographicOperations.FixedTimeEquals(existing, workspaceKey.Span))
            {
                throw new VaultException(
                    $"O workspace '{workspaceId}' já tem uma chave de time DIFERENTE neste "
                    + "computador. Importar por cima deixaria os segredos já guardados ilegíveis.");
            }

            byte[] adopted = workspaceKey.ToArray();
            byte[] wrapped = AccountKeyService.WrapKey(adopted, _amk, WrapContext(workspaceId));
            await _store.SaveAsync(StoreKey(workspaceId), wrapped, ct).ConfigureAwait(false);

            // O cache passa a ser dono do buffer adotado (o Dispose o zera). A entrada antiga, quando
            // existe, é a MESMA chave — zere-a para não deixar duplicata de material na heap.
            if (_unwrapped.TryGetValue(workspaceId, out byte[]? previous) && !ReferenceEquals(previous, adopted))
            {
                CryptographicOperations.ZeroMemory(previous);
            }

            _unwrapped[workspaceId] = adopted;
            return wrapped;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Recoloca a WK a partir do embrulho que o SERVIDOR guarda (<c>GET /workspaces/{id}/key</c>).
    /// É o que faz o SEGUNDO device do membro abrir o cofre do time: a AMK é portável, mas o blob
    /// gravado em disco é local — sem isto o colega logaria em casa, sincronizaria e não abriria
    /// nada, sem erro nenhum.
    ///
    /// <para>Blob de outra conta (ou adulterado) faz o tag GCM falhar e ESTOURA. Falhar aqui é
    /// obrigatório: engolir o erro faria o ring seguir sem chave, e o cofre voltaria à situação que
    /// o fail-closed existe para impedir.</para>
    /// </summary>
    public async Task RestoreWrappedWorkspaceKeyAsync(
        string workspaceId, ReadOnlyMemory<byte> wrapped, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        // Abre ANTES de gravar: um blob que não abre não pode substituir o que já está no disco.
        byte[] wk = AccountKeyService.UnwrapKey(wrapped.ToArray(), _amk, WrapContext(workspaceId));
        try
        {
            await ImportWorkspaceKeyAsync(workspaceId, wk, ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wk);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (byte[] wk in _unwrapped.Values)
        {
            CryptographicOperations.ZeroMemory(wk);
        }

        _unwrapped.Clear();
        CryptographicOperations.ZeroMemory(_amk);

        // O gate NÃO é disposto de propósito: o SemaphoreSlim só precisa disso se alguém tiver
        // materializado o AvailableWaitHandle (ninguém aqui), e descartá-lo faria um Release em
        // andamento estourar ObjectDisposedException — mascarando o erro de verdade.
    }

    private async Task<byte[]?> LoadAsync(string workspaceId, CancellationToken ct)
    {
        if (_unwrapped.TryGetValue(workspaceId, out byte[]? cached))
        {
            return cached;
        }

        byte[]? wrapped = await _store.LoadAsync(StoreKey(workspaceId), ct).ConfigureAwait(false);
        if (wrapped is null)
        {
            return null;
        }

        // AMK errada (blob de outra conta) ou blob adulterado -> CryptographicException do tag GCM.
        byte[] wk = AccountKeyService.UnwrapKey(wrapped, _amk, WrapContext(workspaceId));
        _unwrapped[workspaceId] = wk;
        return wk;
    }

    /// <summary>
    /// Devolve sempre uma CÓPIA. O <c>CredentialVault</c> usa <c>using</c> na chave em toda
    /// operação, e o <see cref="WorkspaceKey.Dispose"/> ZERA o buffer: entregar o array do cache
    /// faria a segunda senha do dia ser selada com 32 zeros.
    /// </summary>
    private static WorkspaceKey Copy(byte[] wk) => new((byte[])wk.Clone());

    private static string StoreKey(string workspaceId) => StoreKeyPrefix + workspaceId;

    private static string WrapContext(string workspaceId) => WrapContextPrefix + workspaceId;
}
