using System.IO;

using Microsoft.Data.Sqlite;

using RemoteOps.Sync;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// O que ficou esperando na fila do cofre que esta sessão <b>não</b> abriu.
/// </summary>
/// <param name="CheckFailed">
/// Houve escopo que não deu para verificar. Existe como campo PRÓPRIO — e não colapsado em zero —
/// porque "não consegui olhar" e "não há nada" pedem frases diferentes na tela. Colapsar os dois é
/// exatamente como um erro vira estado vazio, o defeito estrutural desta base.
/// </param>
internal sealed record OtherVaultOutboxReport(int PendingPersonal, int PendingTeam, bool CheckFailed)
{
    internal static OtherVaultOutboxReport Empty { get; } = new(0, 0, CheckFailed: false);

    internal int Total => PendingPersonal + PendingTeam;
}

/// <summary>
/// ⚠️ <b>Conta o que está parado na fila do OUTRO escopo.</b>
///
/// <para><b>O problema, que é real e é do operador:</b> desde o 1j há <b>um banco por escopo</b>
/// (<c>sync-local.db</c> no pessoal, <c>sync-team-{W}.db</c> em cada time). O outbox mora no banco, e
/// o sync de uma sessão só drena o banco DAQUELA sessão. Então editar um cliente no cofre pessoal e
/// depois abrir o RemoteOps no time deixa aquelas edições <b>paradas</b> até ele reabrir no pessoal —
/// sem uma linha na tela. O operador conclui que sincronizou. É a queixa que ele já abriu duas vezes
/// ("as credenciais não sincronizaram").</para>
///
/// <para><b>Por que varrer a pasta em vez de perguntar os ids:</b> o app sabe o workspace ATIVO e
/// mais nada — o id do outro cofre só existiria depois de um login que esta sessão não vai fazer.
/// Os arquivos, por outro lado, estão todos ali, e o nome deles já diz o escopo
/// (<see cref="AppRuntime.IsTeamDbName"/>). Varrer também cobre de graça o operador com dois times.</para>
///
/// <para><b>Nada é criado e nada é escrito.</b> A abertura é a que só OLHA
/// (<c>TryOpenExistingWorkspaceAsync</c>), e as consultas são cruas de propósito: passar pelo
/// <c>SqliteSyncMetadataStore</c> traria o <c>EnsureSchema</c> junto, que faz <c>CREATE TABLE</c> e
/// <c>ALTER TABLE</c> — migrar o banco do outro cofre por causa de um aviso seria trocar de lugar o
/// risco que este aviso existe para reduzir.</para>
/// </summary>
internal sealed class OtherVaultOutboxProbe
{
    private const string DbPrefix = "sync-";
    private const string DbSuffix = ".db";

    private readonly LocalSyncClientFactory _factory;
    private readonly string _dataDir;

    internal OtherVaultOutboxProbe(LocalSyncClientFactory factory, string dataDir)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDir);

        _factory = factory;
        _dataDir = dataDir;
    }

    /// <param name="currentDbName">
    /// O banco DESTA sessão, que é justamente o único que não interessa: o sync desta sessão já o
    /// drena, e contá-lo faria o aviso acender no exato caso em que não há nada errado.
    /// </param>
    internal async Task<OtherVaultOutboxReport> ScanAsync(
        string currentDbName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDbName);

        List<string> outros;
        try
        {
            outros = OtherDbNames(currentDbName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nem a pasta deu para listar. "Não sei" fica ESCRITO no relatório; devolver o relatório
            // vazio afirmaria "não há nada pendente", que é a afirmação que ninguém mediu.
            return new OtherVaultOutboxReport(0, 0, CheckFailed: true);
        }

        int pessoal = 0;
        int time = 0;
        bool falhou = false;

        foreach (string dbName in outros)
        {
            int? pendentes = await TryCountPendingAsync(dbName, ct).ConfigureAwait(false);
            if (pendentes is not { } quantos)
            {
                falhou = true;
                continue;
            }

            if (AppRuntime.IsTeamDbName(dbName))
            {
                time += quantos;
            }
            else
            {
                // Tudo que não é `team-…` é o banco pessoal desta máquina (hoje só existe `local`).
                // Um nome inesperado cai aqui de propósito: contá-lo como pessoal produz um aviso a
                // mais, enquanto ignorá-lo produziria o silêncio que este aviso existe para matar.
                pessoal += quantos;
            }
        }

        return new OtherVaultOutboxReport(pessoal, time, falhou);
    }

    private List<string> OtherDbNames(string currentDbName)
    {
        var nomes = new List<string>();
        if (!Directory.Exists(_dataDir))
        {
            return nomes;
        }

        foreach (string caminho in Directory.EnumerateFiles(_dataDir, DbPrefix + "*" + DbSuffix))
        {
            string arquivo = Path.GetFileName(caminho);

            // Filtro explícito: o padrão do EnumerateFiles casa nomes 8.3 no Windows e deixaria
            // passar `sync-local.db-wal`. O nome do banco tem de sair da forma EXATA.
            if (!arquivo.StartsWith(DbPrefix, StringComparison.OrdinalIgnoreCase)
                || !arquivo.EndsWith(DbSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string dbName = arquivo[DbPrefix.Length..^DbSuffix.Length];
            if (dbName.Length == 0
                || string.Equals(dbName, currentDbName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nomes.Add(dbName);
        }

        return nomes;
    }

    /// <summary>
    /// Quantas edições estão DEPOIS do cursor do outbox naquele banco, ou <c>null</c> quando não deu
    /// para verificar (sem <c>.keyref</c>, envelope da chave ausente, banco ilegível).
    /// </summary>
    private async Task<int?> TryCountPendingAsync(string dbName, CancellationToken ct)
    {
        try
        {
            WorkspaceContext? workspace = await _factory
                .TryOpenExistingWorkspaceAsync(dbName, ct)
                .ConfigureAwait(false);

            if (workspace is null)
            {
                return null;
            }

            using SqliteConnection conn = await workspace.OpenConnectionAsync(ct).ConfigureAwait(false);

            // A contagem em si mora no OutboxBacklog: a MESMA pergunta é feita pelo VaultSwitch sobre
            // o banco DESTA sessão (o único que esta sonda pula), e duas cópias do SQL divergiriam.
            // Zero daqui é sempre MEDIDO — o "não sei" sai pelo catch, como null.
            return await OutboxBacklog.CountPendingAsync(conn, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem detalhe da exceção (ADR-013) e sem inventar zero: quem chama marca "não verificado"
            // e a tela diz isso com todas as letras.
            return null;
        }
    }

}
