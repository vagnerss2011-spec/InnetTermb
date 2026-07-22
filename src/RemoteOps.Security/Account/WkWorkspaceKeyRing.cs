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
    private readonly IVaultRootingStore _rooting;
    private readonly byte[] _amk;

    // WKs já desembrulhadas nesta sessão. Sem o cache, cada operação do cofre faria um GCM
    // desnecessário; com ele, o ring é ESTÁVEL — a mesma chave do começo ao fim da sessão.
    private readonly Dictionary<string, byte[]> _unwrapped = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _disposed;

    /// <summary>Guarda uma CÓPIA da AMK (zerada no <see cref="Dispose"/>) — o chamador segue dono da dele.</summary>
    /// <param name="rooting">
    /// Onde o marcador de raiz do cofre é gravado. Vem para o construtor — e não como parâmetro de
    /// cada método — porque chave e marcador precisam aterrissar SEMPRE no mesmo lugar: é essa
    /// coincidência que impede os dois de divergirem. No app é o <b>mesmo objeto</b> do
    /// <paramref name="store"/> (o <c>FileVaultStore</c> atende as duas portas).
    /// </param>
    public WkWorkspaceKeyRing(IWorkspaceKeyStore store, IVaultRootingStore rooting, ReadOnlySpan<byte> amk)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(rooting);
        if (amk.Length != AmkSize)
        {
            throw new ArgumentException($"A AMK precisa ter {AmkSize} bytes (recebidos {amk.Length}).", nameof(amk));
        }

        _store = store;
        _rooting = rooting;
        _amk = amk.ToArray();
    }

    /// <summary>
    /// O carimbo desta raiz. Continua público no tipo CONCRETO (o <c>LocalVaultMigrator</c> e os
    /// testes o afirmam sobre instâncias concretas), mas saiu do <see cref="IWorkspaceKeyRing"/>:
    /// quem responde "sob qual esquema este envelope foi selado" é a CHAVE que selou
    /// (<see cref="WorkspaceKey.AlgorithmId"/>), e não o chaveiro — que, num cofre multi-raiz, não
    /// tem UM algoritmo para declarar.
    /// </summary>
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
    /// Recupera a WK do workspace. <b>Nunca cria</b> — e é isto que torna o cofre do time
    /// fail-closed por ESTRUTURA, e não por bandeira: o <c>CredentialVault</c> chama este método em
    /// TODA operação, então um caminho de criação aqui faria qualquer toque no cofre antes do
    /// convite sortear uma WK aleatória. O convidado passaria semanas cadastrando senhas que ninguém
    /// do time abre, sem um único erro na tela. Quem faz a chave nascer é o
    /// <see cref="MintWorkspaceKeyAsync"/>, que de propósito não está no
    /// <see cref="IWorkspaceKeyRing"/>.
    ///
    /// <para>Lança também se a WK existe mas não abre com esta AMK (blob de outra conta) — falhar
    /// alto é obrigatório: devolver null ali faria o chamador tratar cofre alheio como cofre vazio.</para>
    /// </summary>
    /// <exception cref="VaultException">
    /// Não há WK guardada aqui. A mensagem é acionável de propósito: o operador precisa saber que
    /// falta ACEITAR o convite, e não que "ocorreu um erro".
    /// </exception>
    public async Task<WorkspaceKey> GetOrCreateWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            byte[]? existing = await LoadAsync(workspaceId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                return Copy(existing);
            }

            // FAIL-CLOSED, e com UM caminho só. Uma guarda que pode ser desligada por parâmetro é
            // uma guarda que alguém desliga por engano numa fiação de DI — aqui não existe o que
            // desligar.
            throw new VaultException(
                $"O cofre do time '{workspaceId}' ainda não tem a chave neste computador. "
                + "Aceite o convite (identificador + código recebido por outro canal) antes de "
                + "cadastrar ou abrir senhas do time.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sorteia a WK e <b>FUNDA</b> o time. NÃO está em <see cref="IWorkspaceKeyRing"/> de propósito:
    /// criar chave é o ato de fundar, e só o fluxo de criação/convite pode praticá-lo. O
    /// <c>CredentialVault</c> enxerga apenas a interface, então nenhum caminho do cofre alcança este
    /// método — o fail-closed deixa de depender de uma bandeira que alguém pode construir errado e
    /// passa a ser uma propriedade do TIPO.
    ///
    /// <para>Idempotente: se já existe WK para o workspace, devolve a existente. Sortear "só mais
    /// esta vez" é exatamente a bifurcação silenciosa que a fatia inteira combate — a segunda pessoa
    /// convidada não pode receber uma chave diferente da primeira.</para>
    /// </summary>
    public async Task<WorkspaceKey> MintWorkspaceKeyAsync(string workspaceId, CancellationToken ct = default)
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

            byte[] fresh = RandomNumberGenerator.GetBytes(WorkspaceKeySize);
            try
            {
                byte[] wrapped = AccountKeyService.WrapKey(fresh, _amk, WrapContext(workspaceId));
                await _store.SaveAsync(StoreKey(workspaceId), wrapped, ct).ConfigureAwait(false);

                // Marcador na MESMA operação em que a chave aterrissa. É o que permite ao boot
                // responder "este workspace é de time" mesmo quando a chave sumiu — e recusar alto
                // em vez de silenciosamente cair no cofre pessoal, que seria escrever no lugar errado.
                await _rooting.SaveKeyRootingAsync(workspaceId, VaultKeyRooting.WkRandom, ct)
                    .ConfigureAwait(false);

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
    public Task<byte[]> ImportWorkspaceKeyAsync(
        string workspaceId, ReadOnlyMemory<byte> workspaceKey, CancellationToken ct = default)
        => AdoptAsync(workspaceId, workspaceKey, preWrapped: null, ct);

    /// <summary>
    /// O embrulho <b>como está no disco</b>, ou <c>null</c> quando não há chave de time guardada
    /// aqui. É este blob que sobe no <c>PUT /workspaces/{id}/key</c>.
    ///
    /// <para><b>Por que os bytes guardados, e não um embrulho novo:</b> o servidor não tem AMK
    /// nenhuma, então a única coisa que ele consegue comparar é BYTE. Publicar um re-embrulho (nonce
    /// novo a cada vez) faria toda republicação parecer uma chave diferente — e o conflito, que
    /// existe para denunciar bifurcação, viraria rotina diária até ninguém mais olhar para ele.</para>
    ///
    /// <para>O blob é <b>validado</b> antes de sair: se ele não abre com esta AMK, estoura. Publicar
    /// às cegas plantaria na conta um embrulho que nem o próprio dono consegue abrir depois.</para>
    /// </summary>
    public async Task<byte[]?> TryGetWrappedWorkspaceKeyAsync(
        string workspaceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Passa pelo LoadAsync de propósito: é ele que ABRE o blob (e estoura com AMK errada).
            if (await LoadAsync(workspaceId, ct).ConfigureAwait(false) is null)
            {
                return null;
            }

            return await _store.LoadAsync(StoreKey(workspaceId), ct).ConfigureAwait(false);
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
    ///
    /// <para><b>O blob do servidor é guardado como veio</b>, sem re-embrulhar. Não é economia de
    /// CPU: é o que mantém disco e servidor byte a byte iguais, e portanto o que faz a republicação
    /// deste device ser reconhecida como no-op em vez de conflito.</para>
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
            await AdoptAsync(workspaceId, wk, wrapped.ToArray(), ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wk);
        }
    }

    /// <summary>
    /// O núcleo da adoção de uma WK vinda de fora, comum ao convite e à restauração.
    /// </summary>
    /// <param name="preWrapped">
    /// O embrulho a gravar. <c>null</c> = embrulhe agora sob esta AMK (caminho do convite, em que só
    /// existe a chave crua). Quando ele vem pronto (caminho da restauração), é gravado <b>como
    /// está</b> — ver <see cref="RestoreWrappedWorkspaceKeyAsync"/>.
    /// </param>
    private async Task<byte[]> AdoptAsync(
        string workspaceId, ReadOnlyMemory<byte> workspaceKey, byte[]? preWrapped, CancellationToken ct)
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
            byte[] wrapped = preWrapped ?? AccountKeyService.WrapKey(adopted, _amk, WrapContext(workspaceId));
            await _store.SaveAsync(StoreKey(workspaceId), wrapped, ct).ConfigureAwait(false);

            // Chave e marcador, no mesmo lugar e na mesma operação — os três caminhos em que a WK
            // aterrissa (nascer, chegar pelo convite, voltar do servidor) gravam os dois. Um marcador
            // escrito em outro ponto divergiria da chave, e um cofre de time sem chave voltaria a ser
            // lido como PESSOAL: o app passaria a escrever no banco errado sem nada na tela.
            await _rooting.SaveKeyRootingAsync(workspaceId, VaultKeyRooting.WkRandom, ct)
                .ConfigureAwait(false);

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
    private static WorkspaceKey Copy(byte[] wk) => new((byte[])wk.Clone(), VaultAlgorithms.WkRootedV1);

    private static string StoreKey(string workspaceId) => StoreKeyPrefix + workspaceId;

    private static string WrapContext(string workspaceId) => WrapContextPrefix + workspaceId;
}
