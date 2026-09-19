using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue1444IndexCompatibility_Tests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Legacy_signed_order_is_rejected_without_rewriting(bool pidBoundary, bool readOnly)
        {
            using var file = new TempFile();
            var original = new ObjectId(pidBoundary ? "800000001122330000667788" : "000000001122334455667788");
            var replacement = new ObjectId(pidBoundary ? "800000001122338000667788" : "800000001122334455667788");
            var second = new ObjectId(pidBoundary ? "800000001122337fff667788" : "7fffffff1122334455667788");
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
                db.GetCollection("rows").Insert(new[]
                {
                    new BsonDocument { ["_id"] = original }, new BsonDocument { ["_id"] = second }
                });
            var bytes = File.ReadAllBytes(file.Filename);
            Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            // Preserve the stored links while replacing both the data and index key.
            // The resulting sequence is valid under the former signed comparison.
            var from = original.ToByteArray();
            var to = replacement.ToByteArray();
            var changed = 0;
            for (var offset = 0; offset <= bytes.Length - from.Length; offset++)
            {
                if (!bytes.Skip(offset).Take(from.Length).SequenceEqual(from)) continue;
                Array.Copy(to, 0, bytes, offset, to.Length);
                changed++;
            }
            changed.Should().Be(2, "both the primary-index key and BSON _id must change");
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = readOnly }); };
            open.Should().Throw<LiteException>().WithMessage("*index ordering*Export*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Fact]
        public void Previous_ordinal_stamp_is_rejected_even_with_identical_culture_options()
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 42 });
            var bytes = File.ReadAllBytes(file.Filename);
            // Actual Ordinal stamp from the previous comparer implementation.
            const uint previousStamp = 764264600;
            CollationFingerprint.Compute(Collation.Binary).Should().NotBe(previousStamp);
            Array.Copy(BitConverter.GetBytes(previousStamp), 0, bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(file.Filename); };
            open.Should().Throw<LiteException>().WithMessage("*index ordering*Export*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Compatible_legacy_and_current_indexes_reopen_with_unsigned_order(bool legacy)
        {
            using var file = new TempFile();
            var ids = new[]
            {
                new ObjectId("000000001122334455667788"), new ObjectId("7fffffff1122334455667788"),
                new ObjectId("800000001122337fff667788"), new ObjectId("800000001122338000667788"),
                new ObjectId("ffffffff1122334455667788")
            };
            using (var db = new LiteDatabase(file.Filename))
                db.GetCollection("rows").Insert(ids.AsEnumerable().Reverse().Select(id => new BsonDocument { ["_id"] = id }));
            var bytes = File.ReadAllBytes(file.Filename);
            if (legacy)
            {
                Array.Clear(bytes, EnginePragmas.P_COLLATION_STAMP, 4);
                File.WriteAllBytes(file.Filename, bytes);
            }
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }))
            {
                var rows = db.GetCollection("rows");
                rows.FindAll().Select(x => x["_id"].AsObjectId).Should().Equal(ids);
                foreach (var id in ids) rows.FindById(id)["_id"].AsObjectId.Should().Be(id);
            }
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }
    }
}
