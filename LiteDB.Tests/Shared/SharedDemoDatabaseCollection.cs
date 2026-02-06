namespace LiteDB.Tests;

using System;
using System.IO;
using Xunit;

[CollectionDefinition("SharedDemoDatabase", DisableParallelization = true)]
public sealed class SharedDemoDatabaseCollection : ICollectionFixture<SharedDemoDatabaseFixture>
{
}

public sealed class SharedDemoDatabaseFixture : IDisposable
{
    private readonly string _filename;

    public SharedDemoDatabaseFixture()
    {
        _filename = Path.GetFullPath("Demo.db");
        TryDeleteFile();
    }

    public void Dispose()
    {
        TryDeleteFile();
    }

    private void TryDeleteFile()
    {
        try
        {
            if (File.Exists(_filename))
            {
                File.Delete(_filename);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
