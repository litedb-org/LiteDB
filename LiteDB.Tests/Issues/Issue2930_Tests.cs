using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2930_Tests
    {
        private const string COLLECTION = "MaterialDto";
        private const long EIGHT_HOURS_MS = 8 * 3_600_000L;
        private const long MIN_MS = -62135596800000L;
        private const long MAX_MS = 253402300800000L;

        private static readonly DateTime _first = new DateTime(2020, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime _damaged = new DateTime(2021, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime _last = new DateTime(2022, 5, 1, 10, 0, 0, DateTimeKind.Utc);

        public class MaterialDto
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public DateTime CreateDate { get; set; }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void One_out_of_range_date_does_not_hide_the_other_documents(bool high)
        {
            using var file = new TempFile();
            CreateDamagedFile(file, high, indexed: false);

            using var db = new LiteDatabase(file.Filename);
            var typed = db.GetCollection<MaterialDto>(COLLECTION).FindAll().OrderBy(x => x.Id).ToList();
            var raw = db.GetCollection(COLLECTION).FindAll().OrderBy(x => x["_id"].AsInt32).ToList();

            typed.Select(x => x.Id).Should().Equal(1, 2, 3);
            typed.Select(x => x.Name).Should().Equal("a", "b", "c");
            typed[0].CreateDate.ToUniversalTime().Should().Be(_first);
            typed[1].CreateDate.Should().Be(Clamped(high));
            typed[2].CreateDate.ToUniversalTime().Should().Be(_last);

            raw.Select(x => x["_id"].AsInt32).Should().Equal(1, 2, 3);
            raw[0]["CreateDate"].AsDateTime.ToUniversalTime().Should().Be(_first);
            raw[1]["CreateDate"].AsDateTime.Should().Be(Clamped(high));
            raw[2]["CreateDate"].AsDateTime.ToUniversalTime().Should().Be(_last);

            db.GetCollection<MaterialDto>(COLLECTION).FindById(2).CreateDate.Should().Be(Clamped(high));
            db.Execute("SELECT $ FROM " + COLLECTION).ToList().Should().HaveCount(3);

            var json = JsonSerializer.Serialize(new BsonArray(raw));
            JsonSerializer.Deserialize(json).AsArray[1]["CreateDate"].AsDateTime.ToUniversalTime().Year.Should().Be(high ? 9999 : 1);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rewriting_the_damaged_document_stores_a_valid_date(bool high)
        {
            using var file = new TempFile();
            CreateDamagedFile(file, high, indexed: false);
            var repaired = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<MaterialDto>(COLLECTION);
                var doc = col.FindById(2);
                doc.CreateDate = repaired;
                col.Update(doc).Should().BeTrue();
            }

            File.ReadAllBytes(file.Filename).AsSpan().IndexOf(BitConverter.GetBytes(DamagedMilliseconds(high))).Should().Be(-1);

            using (var db = new LiteDatabase(file.Filename))
            {
                var all = db.GetCollection<MaterialDto>(COLLECTION).FindAll().OrderBy(x => x.Id).ToList();
                all.Select(x => x.CreateDate.ToUniversalTime()).Should().Equal(_first, repaired, _last);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Indexed_out_of_range_date_can_be_queried_updated_and_deleted(bool high)
        {
            using var file = new TempFile();
            CreateDamagedFile(file, high, indexed: true);
            var repaired = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<MaterialDto>(COLLECTION);

                col.Find(Query.All("CreateDate")).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 2, 3);
                col.Find(Query.All("CreateDate", Query.Descending)).Select(x => x.Id).OrderBy(x => x).Should().Equal(1, 2, 3);

                // the damaged node still sits between its old neighbours, so until it is rewritten a seek may take a
                // wrong turn at it (depending on its random skip-list level) and miss a key: it never throws or loops
                col.Find(x => x.CreateDate == _first).Select(x => x.Id).Should().BeSubsetOf(new[] { 1 });
                col.Find(x => x.CreateDate == _last).Select(x => x.Id).Should().BeSubsetOf(new[] { 3 });
                col.Find(x => x.CreateDate == Clamped(high)).Select(x => x.Id).Should().BeSubsetOf(new[] { 2 });

                var doc = col.FindById(2);
                doc.CreateDate = repaired;
                col.Update(doc).Should().BeTrue();

                col.Find(x => x.CreateDate == _first).Select(x => x.Id).Should().Equal(1);
                col.Find(x => x.CreateDate == _last).Select(x => x.Id).Should().Equal(3);
                col.Find(x => x.CreateDate > _first).Select(x => x.Id).Should().Equal(3, 2);
                col.Find(x => x.CreateDate == repaired).Select(x => x.Id).Should().Equal(2);
                col.Find(Query.All("CreateDate")).Select(x => x.Id).Should().Equal(1, 3, 2);
                col.Find(Query.All("CreateDate", Query.Descending)).Select(x => x.Id).Should().Equal(2, 3, 1);
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<MaterialDto>(COLLECTION);

                col.Find(Query.All("CreateDate")).Select(x => x.Id).Should().Equal(1, 3, 2);
                col.Delete(2).Should().BeTrue();
                col.Find(Query.All("CreateDate")).Select(x => x.Id).Should().Equal(1, 3);
                col.Count().Should().Be(2);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Indexed_out_of_range_date_can_be_deleted_without_repairing_it_first(bool high)
        {
            using var file = new TempFile();
            CreateDamagedFile(file, high, indexed: true);

            using (var db = new LiteDatabase(file.Filename))
            {
                db.GetCollection<MaterialDto>(COLLECTION).Delete(2).Should().BeTrue();
            }

            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<MaterialDto>(COLLECTION);

                col.Find(Query.All("CreateDate")).Select(x => x.Id).Should().Equal(1, 3);
                col.Find(Query.All("CreateDate", Query.Descending)).Select(x => x.Id).Should().Equal(3, 1);
            }
        }

        private static DateTime Clamped(bool high) => high ? DateTime.MaxValue : DateTime.MinValue;

        private static long DamagedMilliseconds(bool high) => high ? MAX_MS + EIGHT_HOURS_MS : MIN_MS - EIGHT_HOURS_MS;

        private static long DamagedTicks(bool high)
        {
            var eightHours = TimeSpan.FromHours(8).Ticks;

            return high ? DateTime.MaxValue.Ticks + eightHours : -eightHours;
        }

        /// <summary>
        /// Writes three documents through the engine, then overwrites the stored date of document 2 on disk:
        /// the BSON milliseconds inside the data block and, when indexed, the UTC ticks inside the index node.
        /// </summary>
        private static void CreateDamagedFile(TempFile file, bool high, bool indexed)
        {
            using (var db = new LiteDatabase(file.Filename))
            {
                var col = db.GetCollection<MaterialDto>(COLLECTION);

                if (indexed) col.EnsureIndex(x => x.CreateDate);

                col.Insert(new MaterialDto { Id = 1, Name = "a", CreateDate = _first });
                col.Insert(new MaterialDto { Id = 2, Name = "b", CreateDate = _damaged });
                col.Insert(new MaterialDto { Id = 3, Name = "c", CreateDate = _last });
            }

            var bytes = File.ReadAllBytes(file.Filename);
            var milliseconds = (long)(_damaged - BsonValue.UnixEpoch).TotalMilliseconds;

            Patch(bytes, milliseconds, DamagedMilliseconds(high)).Should().BeGreaterThan(0);

            if (indexed)
            {
                Patch(bytes, _damaged.Ticks, DamagedTicks(high)).Should().BeGreaterThan(0);
            }

            // Preserve valid page checksums to exercise legacy date decoding, not damaged storage.
            for (var offset = 0; offset < bytes.Length; offset += 8192)
                LiteDB.Engine.PageChecksum.Write(new BufferSlice(bytes, offset, 8192));
            File.WriteAllBytes(file.Filename, bytes);
        }

        private static int Patch(byte[] bytes, long stored, long damaged)
        {
            var pattern = BitConverter.GetBytes(stored);
            var replacement = BitConverter.GetBytes(damaged);
            var count = 0;

            for (var pos = bytes.AsSpan().IndexOf(pattern); pos >= 0; count++)
            {
                replacement.CopyTo(bytes, pos);

                var next = bytes.AsSpan(pos + 8).IndexOf(pattern);
                pos = next < 0 ? -1 : pos + 8 + next;
            }

            return count;
        }
    }
}
