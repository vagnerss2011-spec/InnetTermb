using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using RemoteOps.Security.Crypto;

namespace RemoteOps.Desktop.Account;

/// <summary>
/// <see cref="IAmkCache"/> em arquivo, com a AMK protegida por DPAPI CurrentUser (spec §4.3).
///
/// <para><b>Formato:</b> JSON com a identidade em CLARO (e-mail, workspace, versão) + a AMK num blob
/// DPAPI. A identidade fica legível de propósito: é ela que o app precisa ANTES de abrir o blob (pra
/// saber de quem é a sessão e montar a entropia), e não é segredo — o segredo é a AMK.</para>
///
/// <para><b>Entropia ligada à conta/workspace</b> (spec §4.3, mesmo padrão do
/// <c>WorkspaceKeyRing</c>): o blob só abre sob a identidade com que foi salvo. Isso amarra as duas
/// metades do arquivo — adulterar o e-mail em claro (que é editável com o bloco de notas) não faz o
/// app abrir a AMK de uma conta sob a identidade de outra: a entropia muda e o DPAPI recusa.</para>
///
/// <para><b>Threat model</b> (spec §10): notebook roubado sem a senha DO WINDOWS não abre o blob —
/// o DPAPI CurrentUser deriva a chave da credencial do usuário. É defesa em profundidade, não a
/// raiz da segurança: quem perde a senha da conta E a chave de recuperação continua sem cofre.</para>
/// </summary>
public sealed class DpapiAmkCache : IAmkCache
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly ILocalKeyProtector _protector;

    /// <param name="path">Arquivo do cache (ex.: <c>%APPDATA%\RemoteOps\account.amk</c>).</param>
    /// <param name="protector">DPAPI em produção; injetável pra teste (igual ao WorkspaceKeyRing).</param>
    public DpapiAmkCache(string path, ILocalKeyProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = path;
        _protector = protector;
    }

    /// <summary>Blob no disco. A AMK só existe aqui dentro de <see cref="ProtectedAmk"/>.</summary>
    private sealed record CacheFile(string Email, string WorkspaceId, int AmkKeyVersion, byte[] ProtectedAmk);

    public async Task<CachedAccount?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        CacheFile? file;
        try
        {
            await using FileStream stream = File.OpenRead(_path);
            file = await JsonSerializer.DeserializeAsync<CacheFile>(stream, s_json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Cache ilegível (escrita interrompida, edição à mão) não pode derrubar o startup:
            // vale como "sem cache" — o app pede login e reescreve. Fail-open (offline-first).
            return null;
        }

        if (file is null
            || string.IsNullOrWhiteSpace(file.Email)
            || string.IsNullOrWhiteSpace(file.WorkspaceId)
            || file.ProtectedAmk is not { Length: > 0 })
        {
            return null;
        }

        // ⚠️ O UNPROTECT FICA DENTRO DO FAIL-OPEN, e não fora dele. O contrato do
        // ILocalKeyProtector é explícito: ele LANÇA quando o blob foi protegido por outro
        // usuário/máquina — a pasta %APPDATA%\RemoteOps restaurada de backup noutro computador, o
        // perfil do Windows recriado, o arquivo editado à mão. Enquanto esta linha ficava de fora do
        // try, essa exceção subia por TryActivateFromCacheAsync → AccountBootPath.EnterAsync até o
        // App, que a tratava como "não foi possível ativar a conta", caía no
        // IsVaultAmkRootedAsync (o cofre É AMK-rooted) e ENCERRAVA o processo mandando "abra o
        // RemoteOps de novo e entre na conta" — só que reabrir relê o mesmo blob e repete tudo. O
        // operador ficava sem app e sem caminho de volta, com os ~700 equipamentos intactos e
        // inalcançáveis.
        //
        // Cair para "sem cache" é a resposta certa e não afrouxa nada: a AMK é PORTÁVEL (o escrow
        // vive no servidor), então o login pede a senha e o cofre abre. O que se perde é pular a
        // senha uma vez — o que já é o desfecho do cache ausente ou ilegível, logo acima.
        byte[] amk;
        try
        {
            amk = _protector.Unprotect(file.ProtectedAmk, Entropy(file.Email, file.WorkspaceId));
        }
        catch (CryptographicException)
        {
            // Sem detalhe da exceção (ADR-013) e sem inventar sessão: "este blob não é meu" é
            // resposta, e a resposta é pedir login.
            return null;
        }

        return new CachedAccount(file.Email, file.WorkspaceId, file.AmkKeyVersion, amk);
    }

    public async Task SaveAsync(CachedAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        byte[] protectedAmk = _protector.Protect(
            account.Amk, Entropy(account.Email, account.WorkspaceId));

        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var file = new CacheFile(account.Email, account.WorkspaceId, account.AmkKeyVersion, protectedAmk);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(file, s_json), ct).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        // Sem sobrescrever antes de apagar: em SSD com wear-leveling isso é teatro (o controlador
        // remapeia o bloco), e o conteúdo é um blob DPAPI — inútil noutra máquina/usuário. Quem
        // zera material de chave VIVO é o CachedAccount.Dispose.
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Entropia DPAPI = rótulo versionado + identidade. Mesmo padrão do <c>WorkspaceKeyRing</c>
    /// (<c>"remoteops:wdk:" + workspaceId</c>): não é segredo, é DOMÍNIO — impede que um blob salvo
    /// para uma conta/workspace seja aceito noutro contexto.
    /// </summary>
    private static byte[] Entropy(string email, string workspaceId) =>
        Encoding.UTF8.GetBytes($"remoteops:amk-cache:v1:{email}:{workspaceId}");
}
