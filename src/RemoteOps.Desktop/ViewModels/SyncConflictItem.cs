using System;
using System.Globalization;

namespace RemoteOps.Desktop.ViewModels;

/// <summary>
/// Um conflito de sync como a UI precisa mostrar. Existe separado do <c>StoredConflict</c> do
/// <c>RemoteOps.Sync.Remote</c> para manter a VM livre daquela dependência (mesmo motivo do
/// <see cref="ISyncController"/>), e para o texto em pt-BR ser testável sem tocar em banco.
/// </summary>
public sealed record SyncConflictItem(
    string EntityType,
    string EntityId,
    DateTimeOffset DetectedAt,
    string Reason)
{
    /// <summary>Tipo da entidade em português, para quem opera não ler jargão do esquema.</summary>
    public string TipoTexto => EntityType switch
    {
        "asset" => "Equipamento",
        "endpoint" => "Acesso do equipamento",
        "group" => "Grupo",
        "credential_ref" => "Credencial",
        "secret_envelope" => "Senha",
        _ => EntityType,
    };

    /// <summary>
    /// O MOTIVO em linguagem de operador. O que o servidor manda é jargão (<c>version_mismatch</c>);
    /// o que interessa a quem lê é o efeito prático: a edição feita aqui não subiu.
    /// </summary>
    public string MotivoTexto => Reason switch
    {
        "version_mismatch" =>
            "Este item já tinha sido alterado em outro computador. A versão de lá prevaleceu e a alteração feita aqui não subiu.",
        "secret_envelope" =>
            "Senha alterada nos dois computadores. Por segurança o app nunca mescla senha sozinho — a versão do servidor prevaleceu.",
        _ => $"A alteração feita aqui não subiu ({Reason}).",
    };

    public string QuandoTexto => DetectedAt == DateTimeOffset.MinValue
        ? "data desconhecida"
        : DetectedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"));
}
