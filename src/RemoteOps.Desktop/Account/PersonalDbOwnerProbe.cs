using Microsoft.Data.Sqlite;

using RemoteOps.Sync;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// ⚠️ <b>O que o PRÓPRIO banco pessoal diz sobre de quem ele é.</b>
///
/// <para><b>O problema que isto resolve:</b> o marcador <c>sync-local.owner</c> só existe a partir da
/// primeira abertura que o grava. Antes dele, o resolvedor de escopo tinha UMA evidência — "o arquivo
/// <c>sync-local.db</c> existe" — e disso concluía que o workspace da vez era o dono do banco. Era
/// verdade enquanto não havia como criar workspace de time; deixou de ser nesta fatia. Com o operador
/// abrindo o TIME, aquela conclusão amarrava o banco com os ~700 clientes dele ao workspace dos
/// colegas, offline e sem uma linha na tela.</para>
///
/// <para><b>A evidência autoritativa está DENTRO do banco:</b> <c>sync_cursor.workspace_id</c> é o
/// workspace de SERVIDOR contra o qual aquele banco vinha sincronizando. Um banco que já sincronizou
/// responde a pergunta sozinho, sem rede e sem palpite.</para>
///
/// <para>⚠️ <b>Isso só vale porque UMA linha aqui não pode ser fabricada por uma sessão de time</b>,
/// e a garantia tem três pernas — dizer "quem grava é o <c>SqliteSyncMetadataStore</c>" sem elas
/// seria afirmar escritor único sem a restrição que o sustenta:
/// <list type="number">
///   <item><b>O banco da sessão é escolhido pelo MESMO escopo.</b> Uma sessão de time abre
///   <c>team-{W}.db</c> (<c>SessionVaultScope.DbName</c>), nunca <c>sync-local.db</c> — então o
///   <c>SyncOrchestrator</c> de um time não tem como escrever o id dele AQUI.</item>
///   <item><b>Um processo por vez</b> (<c>SingleInstanceGuard</c>): não existem duas sessões, com
///   escopos diferentes, gravando no mesmo arquivo ao mesmo tempo.</item>
///   <item><b><c>ResetSecretsCursorAsync</c> é <c>UPDATE</c>, nunca <c>INSERT</c></b> — é o único
///   caminho que toca <c>sync_cursor</c> com o id de um TIME estando dentro da sessão PESSOAL (o
///   aceite de convite). Um <c>INSERT</c> ali fabricaria a linha "este banco sincronizava com o
///   time", e evidência positiva adota o dono sem rede. Está fixado no doc daquele método.</item>
/// </list>
/// </para>
///
/// <para><b>Nada é criado e nada é escrito</b> — mesma disciplina do
/// <see cref="OtherVaultOutboxProbe"/>, e aqui ela é ainda mais crítica: esta sondagem roda ANTES de
/// o app saber qual banco vai abrir. Abrir pelo caminho normal (<c>OpenWorkspaceAsync</c>) passaria
/// pelo <c>VaultDbKeyProvider</c>, que é um <c>GetOrCreate</c>: sem conseguir ler o envelope da
/// chave ele sorteia outra e <b>sobrescreve o <c>.keyref</c></b> — o banco dos ~700 continuaria no
/// disco, cifrado com a chave que acabou de ser jogada fora, numa sessão que talvez nem vá usá-lo.
/// A consulta também é crua de propósito: passar pelo <c>SqliteSyncMetadataStore</c> traria o
/// <c>EnsureSchema</c> (<c>CREATE TABLE</c>/<c>ALTER TABLE</c>) junto.</para>
/// </summary>
internal sealed class PersonalDbOwnerProbe
{
    private readonly LocalSyncClientFactory _factory;
    private readonly string _dbName;

    /// <param name="dbName">
    /// O banco PESSOAL (<see cref="AppRuntime.DbWorkspace"/>). É sempre ele: a pergunta é de quem é
    /// o <c>sync-local.db</c> desta máquina, e nenhum outro banco responde por ele.
    /// </param>
    internal PersonalDbOwnerProbe(LocalSyncClientFactory factory, string dbName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dbName);

        _factory = factory;
        _dbName = dbName;
    }

    /// <summary>
    /// Os workspaces de servidor com que o banco pessoal já sincronizou.
    /// </summary>
    /// <returns>
    /// <c>null</c> — <b>não deu para olhar</b> (banco/<c>.keyref</c> ausente, envelope da chave
    /// ilegível, banco corrompido). Nunca confundir com lista vazia: aqui ninguém mediu nada.
    /// <para>Lista VAZIA — o banco abriu e <b>nunca sincronizou com ninguém</b>. É uma medição.</para>
    /// <para>Lista com ids — foi com ESSES que ele sincronizou.</para>
    /// </returns>
    internal async Task<IReadOnlyList<string>?> ReadSyncedWorkspacesAsync(CancellationToken ct = default)
    {
        try
        {
            WorkspaceContext? workspace = await _factory
                .TryOpenExistingWorkspaceAsync(_dbName, ct)
                .ConfigureAwait(false);

            if (workspace is null)
            {
                return null;
            }

            using SqliteConnection conn = await workspace.OpenConnectionAsync(ct).ConfigureAwait(false);

            if (!await TableExistsAsync(conn, "sync_cursor", ct).ConfigureAwait(false))
            {
                // Banco anterior à tabela de cursores: ele abriu, e o que há para ler é "nunca
                // sincronizou". Devolver `null` aqui inventaria uma falha de leitura que não houve.
                return [];
            }

            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT workspace_id FROM sync_cursor;";

            var ids = new List<string>();
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sem detalhe da exceção (ADR-013) e sem inventar lista vazia: "não deu para ler" é uma
            // resposta própria, e quem chama sabe que ela NÃO autoriza afirmar nada sobre o dono.
            return null;
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection conn, string table, CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
    }
}
