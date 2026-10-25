using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LiteDB.Engine;

namespace LiteDB.Client.Shared;

internal static class SharedMutexNameFactory
{
    // Effective Windows limit for named mutexes (conservative).
    private const int WINDOWS_MUTEX_NAME_MAX = 250;

    // If the caller adds "Global\" (7 chars) + the name + ".Mutex" (7 chars) to the mutex name,
    // we account for it conservatively here without baking it into the return value.
    // Adjust if your caller prepends something longer.
    private const int CONSERVATIVE_EXTERNAL_PREFIX_LENGTH = 13; // e.g., "Global\\" + name + ".Mutex"

    // Unix runtimes keep a named mutex as a file: the name after "Global\" has at most 255
    // characters. The longest name SharedEngine derives adds ".Turn" + ".Mutex" (11 characters).
    // Longer escaped names never worked with this version (its ".Turn" mutex failed), so hashing
    // them keeps every identity it could open. Prereleases without the turnstile could open
    // names of 245..249 characters; running them together with this version is unsupported.
    private const int UNIX_MUTEX_NAME_MAX = 255;
    private const int LONGEST_SUFFIX_LENGTH = 11;

    internal static string Create(string fileName, SharedMutexNameStrategy strategy)
    {
        return strategy switch
        {
            SharedMutexNameStrategy.Default => CreateUsingUriEncodingWithFallback(fileName),
            SharedMutexNameStrategy.UriEscape => CreateUsingUriEncoding(fileName),
            SharedMutexNameStrategy.Sha1Hash => CreateUsingSha1(fileName),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null)
        };
    }

    private static string CreateUsingUriEncodingWithFallback(string fileName)
    {
        var normalized = Normalize(fileName);
        var uri = Uri.EscapeDataString(normalized);

        if (ExceedsMutexNameLimit(uri))
        {
            // Short, stable fallback well under the limit.
            return "sha1-" + ComputeSha1Hex(normalized);
        }

        return uri;
    }

    private static string CreateUsingUriEncoding(string fileName)
    {
        var normalized = Normalize(fileName);
        var uri = Uri.EscapeDataString(normalized);

        if (ExceedsMutexNameLimit(uri))
        {
            // Fallback to SHA to avoid ArgumentException from the Mutex constructor.
            return "sha1-" + ComputeSha1Hex(normalized);
        }

        return uri;
    }

    private static bool ExceedsMutexNameLimit(string uri) => IsWindows()
        ? uri.Length + CONSERVATIVE_EXTERNAL_PREFIX_LENGTH > WINDOWS_MUTEX_NAME_MAX
        : uri.Length + LONGEST_SUFFIX_LENGTH > UNIX_MUTEX_NAME_MAX;

    private static bool IsWindows()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    internal static string CreateUsingSha1(string value)
    {
        var normalized = Normalize(value);
        return ComputeSha1Hex(normalized);
    }

    private static string Normalize(string path)
    {
        // Invariant casing + absolute path yields stable identity.
        return Path.GetFullPath(path).ToLowerInvariant();
    }

    private static string ComputeSha1Hex(string input)
    {
        var data = Encoding.UTF8.GetBytes(input);
        using var sha = SHA1.Create();
        var hashData = sha.ComputeHash(data);

        var sb = new StringBuilder(hashData.Length * 2);
        foreach (var b in hashData)
        {
            sb.Append(b.ToString("X2"));
        }

        return sb.ToString();
    }
}
