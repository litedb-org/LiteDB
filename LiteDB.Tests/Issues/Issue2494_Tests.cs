using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Xunit;

namespace LiteDB.Tests.Issues;

public class Issue2494_Tests
{
    [Fact]
    public static void Test()
    {
        var original = "../../../Resources/Issue_2494_EncryptedV4.db";
        using var filename = new TempFile(original);

        var connectionString = new ConnectionString(filename)
        {
            Password = "pass123",
            Upgrade = true,
        };

        using (var db = new LiteDatabase(connectionString)) // <= throws as of version 5.0.18
        {
            var col = db.GetCollection<PlayerDto>();
            col.FindAll();
        }
    }

    public class PlayerDto
    {
        [BsonId]
        public Guid Id { get; set; }

        public string Name { get; set; }

        public PlayerDto(Guid id, string name)
        {
            Id = id;
            Name = name;
        }

        public PlayerDto()
        {
        }
    }
}