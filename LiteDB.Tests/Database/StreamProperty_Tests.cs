using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class StreamProperty_Tests
    {
        [Fact]
        public void Stream_property_should_round_trip_through_file_storage()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var collection = db.GetCollection<StreamModel>("models");
            var content = Enumerable.Range(0, LiteFileStream<string>.MAX_CHUNK_SIZE + 100)
                .Select(x => (byte)(x % 251))
                .ToArray();

            collection.Insert(new StreamModel
            {
                Id = 1,
                Name = "large stream",
                Content = new MemoryStream(content)
            });

            var document = db.GetCollection("models").FindById(1);
            var reference = document["Content"].AsDocument;
            var fileId = reference["$id"].AsString;
            var file = db.FileStorage.FindById(fileId);

            reference["$ref"].Should().Be("_files");
            file.Should().NotBeNull();
            file.Length.Should().Be(content.Length);
            file.Chunks.Should().Be(2);

            var model = collection.FindById(1);

            using (model.Content)
            using (var copy = new MemoryStream())
            {
                model.Content.Should().BeOfType<LiteFileStream<string>>();
                model.Content.CopyTo(copy);
                copy.ToArray().Should().Equal(content);
            }

            using var projected = collection.Query().Select(x => x.Content).First();
            ReadAll(projected).Should().Equal(content);
        }

        [Fact]
        public void Updating_loaded_model_should_keep_existing_file_reference()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var collection = db.GetCollection<StreamModel>("models");

            collection.Insert(new StreamModel
            {
                Id = 1,
                Content = new MemoryStream(new byte[] { 1, 2, 3 })
            });

            var originalReference = db.GetCollection("models")
                .FindById(1)["Content"].AsDocument["$id"].AsString;
            var model = collection.FindById(1);

            model.Content.ReadByte().Should().Be(1);
            model.Name = "updated";
            collection.Update(model).Should().BeTrue();

            var updatedReference = db.GetCollection("models")
                .FindById(1)["Content"].AsDocument["$id"].AsString;

            updatedReference.Should().Be(originalReference);
            db.FileStorage.FindAll().Should().ContainSingle();
            model.Content.Dispose();

            model.Content = new MemoryStream(new byte[] { 9, 8, 7 });
            collection.Update(model).Should().BeTrue();

            var replacementReference = db.GetCollection("models")
                .FindById(1)["Content"].AsDocument["$id"].AsString;
            var replaced = collection.FindById(1);

            replacementReference.Should().NotBe(originalReference);
            ReadAll(replaced.Content).Should().Equal(9, 8, 7);
        }

        [Fact]
        public void Nested_and_bulk_stream_properties_should_be_supported()
        {
            using var db = new LiteDatabase(new MemoryStream());
            var collection = db.GetCollection<NestedStreamModel>("nested_models");

            collection.Upsert(new[]
            {
                new NestedStreamModel
                {
                    Id = 1,
                    Attachment = new Attachment
                    {
                        Content = new MemoryStream(new byte[] { 10, 20 })
                    }
                },
                new NestedStreamModel
                {
                    Id = 2,
                    Attachment = new Attachment
                    {
                        Content = new MemoryStream(new byte[] { 30, 40 })
                    }
                }
            }).Should().Be(2);

            var models = collection.FindAll().ToArray();

            models.Should().HaveCount(2);
            ReadAll(models[0].Attachment.Content).Should().Equal(10, 20);
            ReadAll(models[1].Attachment.Content).Should().Equal(30, 40);
            db.FileStorage.FindAll().Should().HaveCount(2);
        }

        private static byte[] ReadAll(Stream stream)
        {
            using (stream)
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }

        private sealed class StreamModel
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public Stream Content { get; set; }
        }

        private sealed class NestedStreamModel
        {
            public int Id { get; set; }
            public Attachment Attachment { get; set; }
        }

        private sealed class Attachment
        {
            public Stream Content { get; set; }
        }
    }
}
