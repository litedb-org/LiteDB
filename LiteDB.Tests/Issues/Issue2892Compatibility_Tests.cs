using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2892Compatibility_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Previous_comparer_stamps_are_rejected_without_modifying_the_file(bool canonicalDocuments)
        {
            using var file = new TempFile();
            using (var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, Collation = Collation.Binary }))
                db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1 });
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            {
                writer.Write("LiteDB collation v1");
                writer.Write("unsigned ObjectId ordering v1");
                if (canonicalDocuments) writer.Write("canonical document ordering v1");
                writer.Write((int)CompareOptions.Ordinal);
            }
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(buffer.ToArray());
            var previous = (uint)(digest[0] | digest[1] << 8 | digest[2] << 16 | digest[3] << 24);
            if (previous == 0) previous = 1;
            previous.Should().NotBe(CollationFingerprint.Compute(Collation.Binary));
            var bytes = File.ReadAllBytes(file.Filename);
            Array.Copy(BitConverter.GetBytes(previous), 0, bytes, EnginePragmas.P_COLLATION_STAMP, 4);
            File.WriteAllBytes(file.Filename, bytes);
            Action open = () => { using var db = new LiteDatabase(new ConnectionString { Filename = file.Filename, ReadOnly = true }); };
            open.Should().Throw<LiteException>().WithMessage("*ordering/collation*");
            File.ReadAllBytes(file.Filename).Should().Equal(bytes);
        }
    }
}
