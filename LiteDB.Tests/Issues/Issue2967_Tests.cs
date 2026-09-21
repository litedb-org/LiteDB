using System;
using System.IO;

using FluentAssertions;
using LiteDB.Engine;
using LiteDB.Tests.Utils;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2967_Tests
    {
        [Fact]
        public void Failed_password_rebuild_restores_the_original_data_and_wal_pair()
        {
            using var file = new TempFile();
            using (var seed = new LiteDatabase(file.Filename))
            {
                seed.CheckpointSize = 0;
                seed.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "old" });
            }

            RebuildService.SimulateInstallFailure = phase =>
            {
                if (phase == "after-temp-install") throw new IOException("injected");
            };
            try
            {
                using var db = new LiteDatabase(file.Filename);
                Action rebuild = () => db.Rebuild(new RebuildOptions { Password = "new-password" });
                rebuild.Should().Throw<IOException>();
            }
            finally
            {
                RebuildService.SimulateInstallFailure = null;
            }

            using var recovered = new LiteDatabase(file.Filename);
            recovered.GetCollection("rows").FindById(1)["value"].AsString.Should().Be("old");
        }
    }
}
