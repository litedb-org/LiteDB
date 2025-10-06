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
            SharedMutexNameStrategy.UriEscape or SharedMutexNameStrategy.Default  => Uri.EscapeDataString(Path.GetFullPath(fileName).ToLowerInvariant()),
            SharedMutexNameStrategy.Sha1Hash => Sha1(fileName),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
        };
    }
    
    internal static string Sha1(string value)
    {
        var data = Encoding.UTF8.GetBytes(value);

        using (var sha = SHA1.Create())
        {
            var hashData = sha.ComputeHash(data);
            var hash = new StringBuilder();

            foreach (var b in hashData)
            {
                hash.Append(b.ToString("X2"));
            }

            return hash.ToString();
        }
    }
}