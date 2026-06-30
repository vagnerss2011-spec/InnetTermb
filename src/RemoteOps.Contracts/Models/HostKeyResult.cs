namespace RemoteOps.Contracts.Models;

public enum HostKeyVerdict { Accepted, RejectedByUser, RejectedKeyChanged }

public sealed record HostKeyInfo(
    string Host,
    string FingerprintSha256,
    string KeyType,
    bool IsKnown,
    bool HasChanged);
