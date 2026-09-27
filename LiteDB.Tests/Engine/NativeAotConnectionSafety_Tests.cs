#if NET8_0_OR_GREATER
#pragma warning disable LITEDB_EXPERIMENTAL_COORDINATOR
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAotConnectionSafety_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Connection_platform_contract_preserves_files(bool coordinated, bool existing)
        {
            var directory = Path.Combine(Path.GetTempPath(), "litedb-aot-ownership-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var filename = Path.Combine(directory, "data.db");
            try
            {
                File.WriteAllText(Path.Combine(directory, "unrelated"), "keep me");
                if (existing)
                {
                    using var direct = new LiteDatabase(filename);
                    direct.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "committed" });
                }

                var files = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
                if (!OperatingSystem.IsWindows() && !RuntimeFeature.IsDynamicCodeSupported)
                {
                    // Rejection is repeatable, occurs before file access, and does not
                    // create a database or rewrite an existing database or sidecar.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        var error = Assert.Throws<PlatformNotSupportedException>(() =>
                        {
                            using var connection = Open(filename, coordinated);
                            connection.GetCollection("rows").Count();
                        });
                        Assert.Equal(SharedMutexFactory.UnsupportedNativeAotMessage, error.Message);
                        Assert.Equal(files.Keys.OrderBy(path => path), Directory.GetFiles(directory).OrderBy(path => path));
                        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
                    }
                }
                else
                {
                    // Positive control: the same public entry points still work on
                    // managed Unix and Windows, including existing committed data.
                    using var connection = Open(filename, coordinated);
                    Assert.Equal(existing ? 1 : 0, connection.GetCollection("rows").Count());
                    connection.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 2 });
                }

                if (existing)
                {
                    using var reopened = new LiteDatabase(filename);
                    Assert.Equal("committed", reopened.GetCollection("rows").FindById(1)["value"].AsString);
                }
                Assert.Equal("keep me", File.ReadAllText(Path.Combine(directory, "unrelated")));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static LiteDatabase Open(string filename, bool coordinated) => coordinated
            ? new LiteDatabase(new CoordinatedEngine(filename))
            : new LiteDatabase(new ConnectionString { Filename = filename, Connection = ConnectionType.Shared });
    }
}
#endif
