namespace RemoteOps.Cloud.Data.Entities;

public sealed class EndpointEntity
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public required string Protocol { get; set; }
    public string? Fqdn { get; set; }
    public string? Ipv4 { get; set; }
    public string? Ipv6 { get; set; }
    public int Port { get; set; }
    public bool PreferIpv6 { get; set; }
    public Guid? CredentialRefId { get; set; }
    public string? ProfileJson { get; set; }
    public int Version { get; set; }

    public AssetEntity Asset { get; set; } = null!;
}
