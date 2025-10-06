using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LiteDB.Engine;

namespace LiteDB.Client.Shared;

internal static class SharedMutexNameFactory
{
    internal static string Create(string fileName, SharedMutexNameStrategy strategy)
    {
        return strategy switch
        {
            SharedMutexNameStrategy.UriEscape or SharedMutexNameStrategy.Default => CreateUsingUriEncoding(fileName),
            SharedMutexNameStrategy.Sha1Hash => CreateUsingSha1(fileName),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
        };
    }

    private static string CreateUsingUriEncoding(string fileName)
    {
        var normalized = Path.GetFullPath(fileName).ToLowerInvariant();
        return Uri.EscapeDataString(normalized);
    }

    internal static string CreateUsingSha1(string value)
    {
        var normalized = Path.GetFullPath(value).ToLower();
        var data = Encoding.UTF8.GetBytes(normalized);
        
        using var sha = SHA1.Create();
        var hashData = sha.ComputeHash(data);
        var hash = new StringBuilder();

        foreach (var b in hashData)
        {
            hash.Append(b.ToString("X2"));
        }

        return hash.ToString();
    }
}