using System;

namespace RemoteOps.Desktop.Infrastructure;

public enum PrivateKeyKind { Valid, PuttyPpk, Invalid }

/// <summary>Classifica o texto de uma chave privada colada/importada. Puro (sem IO).</summary>
public static class PrivateKeyInput
{
    public static PrivateKeyKind Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PrivateKeyKind.Invalid;
        }

        string t = text.TrimStart();
        if (t.StartsWith("PuTTY-User-Key-File", StringComparison.Ordinal))
        {
            return PrivateKeyKind.PuttyPpk;
        }

        return t.StartsWith("-----BEGIN", StringComparison.Ordinal)
            ? PrivateKeyKind.Valid
            : PrivateKeyKind.Invalid;
    }
}
