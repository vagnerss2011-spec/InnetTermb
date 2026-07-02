using System;
using System.IO;
using System.Security.Cryptography;

namespace RemoteOps.Desktop.Infrastructure;

/// <summary>Utilitário de hashing para fixar/validar o executável de ferramentas externas (WinBox).</summary>
public static class HashUtil
{
    /// <summary>SHA-256 de um arquivo, em hexadecimal minúsculo.</summary>
    public static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
