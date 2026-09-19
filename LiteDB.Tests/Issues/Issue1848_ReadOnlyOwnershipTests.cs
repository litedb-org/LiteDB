using System;
using System.IO;
using System.Linq;

using FluentAssertions;

using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1848_ReadOnlyOwnershipTests
    {
        [Fact]
        public void Independent_direct_reader_is_rejected_while_the_writer_owns_the_file()
        {
            using var file = new TempFile();
            using (var setup = new LiteDatabase(file.Filename))
            {
                setup.GetCollection("rows").Insert(Enumerable.Range(1, 31).Select(Row));
                setup.GetCollection("rows").EnsureIndex("token", true).Should().BeTrue();
            }

            // The very same read-only configuration must work without a live writer.
            using (var control = OpenReader(file.Filename))
            {
                AssertLedger(control, 31);
            }

            Exception ownershipFailure;
            using (var writer = new LiteDatabase(file.Filename))
            {
                writer.CheckpointSize = 0;
                var rows = writer.GetCollection("rows");
                rows.Insert(Row(32));
                AssertLedger(writer, 32);

                ownershipFailure = Record.Exception(() =>
                {
                    using var independent = OpenReader(file.Filename);
                    // Materialize a real query: rejecting a lazy reader on its first
                    // operation is also acceptable, but an inert constructor is not proof.
                    independent.GetCollection("rows").FindAll().ToArray();
                });

                // Rejecting the extra connection must not close the real owner's engine,
                // discard its committed WAL, or prevent subsequent writes.
                AssertLedger(writer, 32);
                rows.Insert(Row(33));
                writer.Checkpoint();
                AssertLedger(writer, 33);
            }

            using (var reopened = OpenReader(file.Filename))
            {
                AssertLedger(reopened, 33);
            }

            Assert.NotNull(ownershipFailure);
            if (ownershipFailure is IOException io)
            {
                (io.HResult & 0xffff).Should().BeOneOf(new[] { 11, 32, 33 },
                    "the refusal must identify file ownership, not an unrelated I/O failure");
            }
            else
            {
                ownershipFailure.Should().BeOfType<LiteException>();
                var message = ownershipFailure.Message.ToLowerInvariant();
                (message.Contains("ownership") || message.Contains("writer") ||
                    message.Contains("in use") || message.Contains("read-only") && message.Contains("direct"))
                    .Should().BeTrue("the error must explain the unsupported independent connection");
            }
        }

        private static LiteDatabase OpenReader(string path)
        {
            return new LiteDatabase(new ConnectionString
            {
                Filename = path,
                Connection = ConnectionType.Direct,
                ReadOnly = true
            });
        }

        private static BsonDocument Row(int id)
        {
            return new BsonDocument
            {
                ["_id"] = id,
                ["token"] = "receipt-" + id,
                ["payload"] = id + ":" + new string((char)('A' + id % 26), 257)
            };
        }

        private static void AssertLedger(LiteDatabase database, int count)
        {
            var rows = database.GetCollection("rows");
            rows.FindAll().Select(row => row["_id"].AsInt32).Should().Equal(Enumerable.Range(1, count));
            rows.Count().Should().Be(count);
            for (var id = 1; id <= count; id++)
            {
                var token = "receipt-" + id;
                var payload = id + ":" + new string((char)('A' + id % 26), 257);
                rows.FindById(id)["payload"].AsString.Should().Be(payload);
                var indexed = rows.Find(Query.EQ("token", token)).ToArray();
                indexed.Should().ContainSingle();
                indexed[0]["_id"].AsInt32.Should().Be(id);
                indexed[0]["payload"].AsString.Should().Be(payload);
            }
        }
    }
}
