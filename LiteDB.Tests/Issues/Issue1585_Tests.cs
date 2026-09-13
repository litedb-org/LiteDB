using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class PlayerDto
    {
        [BsonId]
        public Guid Id { get; }
        public string Name { get; }

        public PlayerDto(Guid id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    public class Issue1585a_Tests
    {
        [Fact]
        public void Dto_Read()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var id = Guid.NewGuid();
                var col = db.GetCollection<PlayerDto>();
                col.Insert(new PlayerDto(id, "Bob"));
                var player = col.FindOne(x => x.Id == id);
                Assert.NotNull(player);
            }
        }

        [Fact]
        public void Dto_Read1()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var id = Guid.NewGuid();
                var col = db.GetCollection<PlayerDto>();
                col.Insert(new PlayerDto(id, "Bob"));
                var player = col.FindOne(x => x.Id == id);
                Assert.NotNull(player);
            }
        }

        [Fact]
        public void Dto_Read2()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var id = Guid.NewGuid();
                var col = db.GetCollection<PlayerDto>();
                col.Insert(new PlayerDto(id, "Bob"));
                var player = col.FindOne(x => x.Id == id);
                Assert.NotNull(player);
            }
        }
    }

    public class Issue1585b_Tests
    {
        [Fact]
        public void Dto_Read3()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var id = Guid.NewGuid();
                var col = db.GetCollection<PlayerDto>();
                col.Insert(new PlayerDto(id, "Bob"));
                var player = col.FindOne(x => x.Id == id);
                Assert.NotNull(player);
            }
        }

        [Fact]
        public void Dto_Read4()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var id = Guid.NewGuid();
                var col = db.GetCollection<PlayerDto>();
                col.Insert(new PlayerDto(id, "Bob"));
                var player = col.FindOne(x => x.Id == id);
                Assert.NotNull(player);
            }
        }

        [Fact]
        public void Dto_Read5()
        {
            using (var db = new LiteDatabase(new MemoryStream()))
            {
                var id = Guid.NewGuid();
                var col = db.GetCollection<PlayerDto>();
                col.Insert(new PlayerDto(id, "Bob"));
                var player = col.FindOne(x => x.Id == id);
                Assert.NotNull(player);
            }
        }

    }
}