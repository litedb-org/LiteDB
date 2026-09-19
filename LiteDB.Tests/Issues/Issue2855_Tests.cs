using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FluentAssertions.Execution;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2855_Tests
    {
        private const string HeaderMarker = "** This is a LiteDB file **";
        private const string FixtureSha256 = "af6864cf7ba5a3f836b34ea736934e5d7a38e78071eeba30ef58c0c61f9b7348";
        private const string StoredText = "This is a test file. If you're reading this, FileStorage is working properly.";

        [Fact(Skip = "Known bug #2855")]
        public void Upgrade_true_converts_v4_data_stream_and_preserves_the_full_ledger()
        {
            var source = LoadV4Fixture();
            AssertV4Fixture(source);

            using var stream = ExpandableStream(source);
            stream.Position = 2855;

            using (var db = OpenWithUpgrade(stream))
            {
                AssertFixtureLedger(db, false);
                db.GetCollection("upgrade_probe").Insert(new BsonDocument
                {
                    ["_id"] = 2855,
                    ["marker"] = "persisted after v4 stream upgrade"
                });
                db.Checkpoint();
            }

            var upgraded = stream.ToArray();
            AssertV8Datafile(upgraded);
            upgraded.Should().NotEqual(source, "conversion must replace the v4 data rather than only accepting it in memory");

            stream.Position = 57;
            using (var reopened = OpenWithUpgrade(stream))
            {
                AssertFixtureLedger(reopened, true);
                JsonSerializer.Serialize(reopened.GetCollection("upgrade_probe").FindById(2855))
                    .Should().Be("{\"_id\":2855,\"marker\":\"persisted after v4 stream upgrade\"}");
            }

            stream.ToArray().Should().Equal(upgraded,
                "Upgrade=true on an already-upgraded stream must be an idempotent reopen, not another rebuild");
        }

        [Fact(Skip = "Known bug #2855")]
        public void Stream_constructor_exposes_and_honours_an_upgrade_option()
        {
            var constructor = typeof(LiteDatabase).GetConstructors()
                .FirstOrDefault(x =>
                {
                    var parameters = x.GetParameters();
                    return parameters.Length > 0 &&
                        parameters[0].ParameterType == typeof(Stream) &&
                        parameters.Any(p => p.ParameterType == typeof(bool) && p.Name == "upgrade");
                });

            constructor.Should().NotBeNull(
                "a stream-based LiteDatabase needs the explicit upgrade option described by issue #2855");
            if (constructor == null) return;

            var source = LoadV4Fixture();
            using var stream = ExpandableStream(source);
            var parameters = constructor.GetParameters();
            var arguments = parameters.Select(DefaultArgument).ToArray();
            arguments[0] = stream;
            arguments[Array.FindIndex(parameters, p => p.ParameterType == typeof(bool) && p.Name == "upgrade")] = true;

            using (var db = (LiteDatabase)constructor.Invoke(arguments))
            {
                AssertFixtureLedger(db, false);
            }

            AssertV8Datafile(stream.ToArray());
        }

        [Fact(Skip = "Known bug #2855")]
        public void Upgrade_true_rejects_a_read_only_v4_stream_without_changing_it()
        {
            var source = LoadV4Fixture();
            AssertV4Fixture(source);
            using var stream = new MemoryStream(source, false);

            var error = Record.Exception(() =>
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = stream, Upgrade = true });
            });

            error.Should().NotBeNull("the caller's stream cannot be replaced when it is read-only");
            error.Message.Should().ContainEquivalentOf("writ");
            stream.ToArray().Should().Equal(source);
        }

        [Fact(Skip = "Known bug #2855")]
        public void Upgrade_true_rejects_a_non_seekable_v4_stream_without_changing_it()
        {
            var source = LoadV4Fixture();
            AssertV4Fixture(source);
            using var stream = new NonSeekableWriteStream(source);

            var error = Record.Exception(() =>
            {
                using var engine = new LiteEngine(new EngineSettings { DataStream = stream, Upgrade = true });
            });

            error.Should().NotBeNull("the v4 header and converted replacement cannot be positioned on a non-seekable stream");
            error.Message.Should().ContainEquivalentOf("seek");
            stream.Snapshot().Should().Equal(source);
        }

        private static LiteDatabase OpenWithUpgrade(Stream stream)
        {
            return new LiteDatabase(new LiteEngine(new EngineSettings
            {
                DataStream = stream,
                Upgrade = true
            }));
        }

        private static void AssertFixtureLedger(LiteDatabase db, bool includeProbe)
        {
            var expectedCollections = new[] { "_chunks", "_files", "col1", "col2", "customers" }
                .Concat(includeProbe ? new[] { "upgrade_probe" } : new string[0])
                .OrderBy(x => x, StringComparer.Ordinal);

            var expectedIndexes = new[]
            {
                "{\"collection\":\"_chunks\",\"name\":\"_id\",\"expression\":\"$._id\",\"unique\":true}",
                "{\"collection\":\"_files\",\"name\":\"_id\",\"expression\":\"$._id\",\"unique\":true}",
                "{\"collection\":\"col1\",\"name\":\"_id\",\"expression\":\"$._id\",\"unique\":true}",
                "{\"collection\":\"col2\",\"name\":\"_id\",\"expression\":\"$._id\",\"unique\":true}",
                "{\"collection\":\"customers\",\"name\":\"_id\",\"expression\":\"$._id\",\"unique\":true}",
                "{\"collection\":\"customers\",\"name\":\"CustomerChildChildName\",\"expression\":\"$.CustomerChild.ChildName\",\"unique\":false}"
            }.Concat(includeProbe
                ? new[] { "{\"collection\":\"upgrade_probe\",\"name\":\"_id\",\"expression\":\"$._id\",\"unique\":true}" }
                : new string[0]);

            using (new AssertionScope())
            {
                db.GetCollectionNames().OrderBy(x => x, StringComparer.Ordinal)
                    .Should().Equal(expectedCollections);
                Documents(db, "col1").Should().Equal(
                    "{\"_id\":1,\"Name\":\"John Doe\",\"Age\":20}",
                    "{\"_id\":2,\"Name\":\"Jane Doe\",\"Age\":25}",
                    "{\"_id\":3,\"Name\":\"Fulano da Silva\",\"Age\":30}");
                Documents(db, "col2").Should().Equal(
                    "{\"_id\":1,\"Name\":\"Microsoft\",\"Country\":\"USA\"}",
                    "{\"_id\":2,\"Name\":\"Petrobras\",\"Country\":\"Brazil\"}",
                    "{\"_id\":3,\"Name\":\"Nokia\",\"Country\":\"Finland\"}");
                Documents(db, "customers").Should().Equal(
                    "{\"_id\":1,\"Name\":\"John Doe\",\"CustomerChild\":{\"_id\":1,\"ChildName\":\"John Doe's Son\"}}");

                var indexedChild = db.GetCollection("customers")
                    .FindOne(Query.EQ("CustomerChild.ChildName", "John Doe's Son"));
                indexedChild["_id"].AsInt32.Should().Be(1);

                using var indexes = db.Execute("SELECT $ FROM $indexes ORDER BY collection, name");
                indexes.ToEnumerable().Select(x => JsonSerializer.Serialize(x)).Should().Equal(expectedIndexes);

                var file = db.FileStorage.FindById("testfile.txt");
                file.Should().NotBeNull();
                file.Filename.Should().Be("testfile.txt");
                file.MimeType.Should().Be("text/plain");
                file.Length.Should().Be(77);
                file.Chunks.Should().Be(1);
                file.UploadDate.Should().Be(new DateTime(2020, 2, 3, 17, 8, 9, 647, DateTimeKind.Utc));
                file.Metadata.Count.Should().Be(0);

                using var downloaded = new MemoryStream();
                db.FileStorage.Download("testfile.txt", downloaded);
                Encoding.UTF8.GetString(downloaded.ToArray()).Should().Be(StoredText);

                var chunk = db.GetCollection("_chunks").FindAll().Single();
                chunk.Keys.OrderBy(x => x).Should().Equal("_id", "data");
                chunk["_id"].AsDocument["f"].AsString.Should().Be("testfile.txt");
                chunk["_id"].AsDocument["n"].AsInt32.Should().Be(0);
                Encoding.UTF8.GetString(chunk["data"].AsBinary).Should().Be(StoredText);
            }
        }

        private static string[] Documents(LiteDatabase db, string collection)
        {
            return db.GetCollection(collection).FindAll()
                .OrderBy(x => x["_id"])
                .Select(x => JsonSerializer.Serialize(x))
                .ToArray();
        }

        private static object DefaultArgument(ParameterInfo parameter)
        {
            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            return parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null;
        }

        private static byte[] LoadV4Fixture()
        {
            var path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "Resources", "v4.db"));
            File.Exists(path).Should().BeTrue("the repository's real v4 fixture must be present at {0}", path);
            return File.ReadAllBytes(path);
        }

        private static MemoryStream ExpandableStream(byte[] source)
        {
            var stream = new MemoryStream(source.Length);
            stream.Write(source, 0, source.Length);
            stream.Position = 0;
            stream.CanSeek.Should().BeTrue();
            stream.CanWrite.Should().BeTrue();
            return stream;
        }

        private static void AssertV4Fixture(byte[] bytes)
        {
            bytes.Should().HaveCount(73728);
            Sha256(bytes).Should().Be(FixtureSha256);
            Encoding.UTF8.GetString(bytes, 25, HeaderMarker.Length).Should().Be(HeaderMarker);
            bytes[52].Should().Be(7);
        }

        private static void AssertV8Datafile(byte[] bytes)
        {
            bytes.Length.Should().BeGreaterOrEqualTo(8192);
            (bytes.Length % 8192).Should().Be(0);
            Encoding.UTF8.GetString(bytes, 32, HeaderMarker.Length).Should().Be(HeaderMarker);
            bytes[59].Should().Be(8);
            Encoding.UTF8.GetString(bytes, 25, HeaderMarker.Length).Should().NotBe(HeaderMarker);
        }

        private static string Sha256(byte[] bytes)
        {
            using var algorithm = SHA256.Create();
            return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private sealed class NonSeekableWriteStream : Stream
        {
            private readonly MemoryStream _inner;

            public NonSeekableWriteStream(byte[] source)
            {
                _inner = ExpandableStream(source);
            }

            public byte[] Snapshot() => _inner.ToArray();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException("injected positioning failure");
            }

            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("injected positioning failure");
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
