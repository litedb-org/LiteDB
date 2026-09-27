using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    /// <summary>
    /// File storage maps LiteFileInfo with hand-written code instead of runtime member discovery. Data files and
    /// behaviour must stay exactly what the reflection mapper produced.
    /// </summary>
    public class Storage_Mapping_Tests
    {
        private sealed class CompositeId
        {
            public int Tenant { get; set; }
            public string Name { get; set; }
        }

        private static MemoryStream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

        [Fact]
        public void A_Custom_String_Id_Converter_Is_Applied_In_Both_Directions()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<string>(value => "key:" + value, bson => bson.AsString.Substring(4));
            using var db = new LiteDatabase(new MemoryStream(), mapper);

            db.FileStorage.Upload("one", "a.txt", Content("first"));
            db.GetCollection("_files").FindAll().Single()["_id"].AsString.Should().Be("key:one");
            db.FileStorage.FindById("one").Id.Should().Be("one");
            using var content = new MemoryStream();
            db.FileStorage.Download("one", content);
            Encoding.UTF8.GetString(content.ToArray()).Should().Be("first");
        }

        [Fact]
        public void Stored_File_Document_Matches_The_Reflection_Mapper()
        {
            using var db = new LiteDatabase(new MemoryStream());

            db.FileStorage.Upload("docs/readme.txt", "readme.txt", Content("hello"), new BsonDocument { ["owner"] = "ada" });

            var stored = db.GetCollection("_files").FindById("docs/readme.txt");
            var file = db.FileStorage.FindById("docs/readme.txt");
            var reflected = db.Mapper.ToDocument(file);

            JsonSerializer.Serialize(stored).Should().Be(JsonSerializer.Serialize(reflected));
            stored.Keys.Should().Equal("_id", "filename", "mimeType", "length", "chunks", "uploadDate", "metadata");
        }

        [Fact]
        public void Files_Written_Through_The_Reflection_Mapper_Are_Read_Back()
        {
            using var db = new LiteDatabase(new MemoryStream());

            // what a data file written by an earlier LiteDB version contains
            db.GetCollection("_files").Insert(new BsonDocument
            {
                ["_id"] = 7,
                ["filename"] = "legacy.bin",
                ["mimeType"] = "application/octet-stream",
                ["length"] = 3L,
                ["chunks"] = 1,
                ["uploadDate"] = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ["metadata"] = new BsonDocument { ["k"] = "v" }
            });
            db.GetCollection("_chunks").Insert(new BsonDocument
            {
                ["_id"] = new BsonDocument { ["f"] = 7, ["n"] = 0 },
                ["data"] = new byte[] { 1, 2, 3 }
            });

            var storage = db.GetStorage<int>();
            var file = storage.FindById(7);

            file.Filename.Should().Be("legacy.bin");
            file.Length.Should().Be(3);
            file.Metadata["k"].AsString.Should().Be("v");
            file.UploadDate.ToUniversalTime().Should().Be(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            using var target = new MemoryStream();
            storage.Download(7, target);
            target.ToArray().Should().Equal(1, 2, 3);
        }

        [Fact]
        public void Typed_Predicates_Resolve_File_Members()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var storage = db.GetStorage<Guid>("myFiles", "myChunks");
            var first = Guid.NewGuid();

            storage.Upload(first, "a.png", Content("a"), new BsonDocument { ["kind"] = "image" });
            storage.Upload(Guid.NewGuid(), "b.txt", Content("bb"));

            var minimum = 2L;
            storage.Find(x => x.Filename.EndsWith(".png")).Select(x => x.Id).Should().Equal(first);
            storage.Find(x => x.Length >= minimum).Select(x => x.Filename).Should().Equal("b.txt");
            storage.Find(x => x.Metadata["kind"] == "image").Select(x => x.Id).Should().Equal(first);
            storage.Find("_id = @0", first).Should().ContainSingle();
            storage.Exists(first).Should().BeTrue();
            storage.Delete(first).Should().BeTrue();
            storage.FindAll().Select(x => x.Filename).Should().Equal("b.txt");
        }

        [Fact]
        public void A_Customized_Mapper_Does_Not_Affect_File_Storage()
        {
            var mapper = new BsonMapper().UseCamelCase();
            mapper.IncludeFields = true;
            mapper.SerializeNullValues = true;
            mapper.RegisterType<Version>(v => v.ToString(), v => Version.Parse(v.AsString));

            using var db = new LiteDatabase(new MemoryStream(), mapper);
            var storage = db.GetStorage<Version>();

            storage.Upload(new Version(1, 2), "release-notes", Content("notes"));

            var stored = db.GetCollection("_files").FindAll().Single();
            stored["_id"].AsString.Should().Be("1.2");
            stored["mimeType"].AsString.Should().Be("application/octet-stream");
            storage.FindById(new Version(1, 2)).Id.Should().Be(new Version(1, 2));
        }

        [Fact]
        public void A_Registered_Converter_Keeps_Existing_Files_With_An_Application_Type_Id_Readable()
        {
            var mapper = new BsonMapper();
            mapper.RegisterType<CompositeId>(
                id => new BsonDocument { ["Tenant"] = id.Tenant, ["Name"] = id.Name },
                bson => new CompositeId { Tenant = bson["Tenant"].AsInt32, Name = bson["Name"].AsString });

            using var db = new LiteDatabase(new MemoryStream(), mapper);

            // what earlier versions stored for GetStorage<CompositeId>(): the id mapped member by member
            var storedId = new BsonDocument { ["Tenant"] = 1, ["Name"] = "a" };
            db.GetCollection("_files").Insert(new BsonDocument
            {
                ["_id"] = storedId,
                ["filename"] = "a.txt",
                ["mimeType"] = "text/plain",
                ["length"] = 2L,
                ["chunks"] = 1,
                ["uploadDate"] = DateTime.UtcNow,
                ["metadata"] = new BsonDocument()
            });
            db.GetCollection("_chunks").Insert(new BsonDocument
            {
                ["_id"] = new BsonDocument { ["f"] = storedId, ["n"] = 0 },
                ["data"] = new byte[] { 4, 2 }
            });

            var storage = db.GetStorage<CompositeId>();
            var key = new CompositeId { Tenant = 1, Name = "a" };

            using var existing = new MemoryStream();
            storage.Download(key, existing);
            existing.ToArray().Should().Equal(4, 2);

            storage.Upload(new CompositeId { Tenant = 2, Name = "b" }, "b.txt", Content("new"));
            storage.FindAll().Select(x => x.Id.Tenant).OrderBy(x => x).Should().Equal(1, 2);
        }

        [Fact]
        public void An_Application_Type_As_File_Id_Is_Mapped_Member_By_Member_As_Before()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var storage = db.GetStorage<CompositeId>();
            var key = new CompositeId { Tenant = 1, Name = "a" };

            storage.Upload(key, "a.txt", Content("abc"));

            // the id document is what the reflection mapper has always written for this class
            var stored = db.GetCollection("_files").FindAll().Single();
            JsonSerializer.Serialize(stored["_id"]).Should().Be("{\"Tenant\":1,\"Name\":\"a\"}");

            var file = storage.FindById(new CompositeId { Tenant = 1, Name = "a" });
            file.Id.Tenant.Should().Be(1);
            file.Id.Name.Should().Be("a");

            using var target = new MemoryStream();
            storage.Download(key, target);
            Encoding.UTF8.GetString(target.ToArray()).Should().Be("abc");
            storage.Delete(key).Should().BeTrue();
            db.GetCollection("_chunks").Count().Should().Be(0);
        }
    }
}
