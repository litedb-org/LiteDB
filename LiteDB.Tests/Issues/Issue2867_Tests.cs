using System;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2867_Tests
    {
        public class InheritedInvoiceBase
        {
            public int InheritedInvoiceId { get; set; }
        }

        public class InheritedInvoice : InheritedInvoiceBase
        {
            public string Payload { get; set; }
        }

        public class DirectInvoice
        {
            public int DirectInvoiceId { get; set; }
            public string Payload { get; set; }
        }

        public class IdPrecedenceBase
        {
            public int Id { get; set; }
            public int IdPrecedenceInvoiceId { get; set; }
        }

        public class IdPrecedenceInvoice : IdPrecedenceBase
        {
        }

        public class UnrelatedSuffixBase
        {
            public int SomeoneElsesId { get; set; }
        }

        public class UnrelatedSuffixInvoice : UnrelatedSuffixBase
        {
        }

        public class LegacyBase
        {
            public int LegacyBaseId { get; set; }
        }

        public class LegacyDerived : LegacyBase
        {
        }

        public class ExplicitIdInvoice : InheritedInvoiceBase
        {
            [BsonId]
            public int Key { get; set; }
            public int ExplicitIdInvoiceId { get; set; }
        }

        [Fact]
        public void Legacy_declaring_type_convention_and_explicit_id_precedence_remain_supported()
        {
            var mapper = new BsonMapper();
            var legacy = mapper.ToDocument(new LegacyDerived { LegacyBaseId = 81 });
            legacy["_id"].AsInt32.Should().Be(81);
            mapper.ToObject<LegacyDerived>(new BsonDocument { ["_id"] = 82 }).LegacyBaseId.Should().Be(82);
            var explicitId = mapper.ToDocument(new ExplicitIdInvoice
            {
                Key = 91, ExplicitIdInvoiceId = 92, InheritedInvoiceId = 93
            });
            explicitId["_id"].AsInt32.Should().Be(91);
            explicitId[nameof(ExplicitIdInvoice.ExplicitIdInvoiceId)].AsInt32.Should().Be(92);
            explicitId[nameof(ExplicitIdInvoice.InheritedInvoiceId)].AsInt32.Should().Be(93);
        }

        [Fact]
        public void Inherited_mapped_type_id_round_trips_through_the_id_field()
        {
            var member = typeof(InheritedInvoice).GetProperty(nameof(InheritedInvoice.InheritedInvoiceId));
            member.DeclaringType.Should().Be(typeof(InheritedInvoiceBase));
            member.ReflectedType.Should().Be(typeof(InheritedInvoice),
                "the regression requires a member reflected from the mapped derived type");

            var mapper = new BsonMapper();
            var encoded = mapper.ToDocument(new InheritedInvoice
            {
                InheritedInvoiceId = 7301,
                Payload = "mapper-authored"
            });

            encoded.Keys.Should().BeEquivalentTo("_id", "Payload");
            encoded["_id"].IsInt32.Should().BeTrue();
            encoded["_id"].AsInt32.Should().Be(7301);
            encoded.ContainsKey(nameof(InheritedInvoice.InheritedInvoiceId)).Should().BeFalse();

            var decoded = mapper.ToObject<InheritedInvoice>(new BsonDocument
            {
                ["_id"] = 7302,
                ["Payload"] = "independently-authored"
            });

            decoded.InheritedInvoiceId.Should().Be(7302);
            decoded.Payload.Should().Be("independently-authored");
        }

        [Fact]
        public void Inherited_mapped_type_id_is_the_persisted_key_and_query_path()
        {
            using var file = new TempFile();

            using (var db = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var invoices = db.GetCollection<InheritedInvoice>("invoices");
                var firstId = invoices.Insert(new InheritedInvoice
                {
                    InheritedInvoiceId = 7301,
                    Payload = "first"
                });
                var secondId = invoices.Insert(new InheritedInvoice
                {
                    InheritedInvoiceId = 7302,
                    Payload = "second"
                });

                firstId.IsInt32.Should().BeTrue();
                firstId.AsInt32.Should().Be(7301);
                secondId.AsInt32.Should().Be(7302);
            }

            using (var reopened = new LiteDatabase(file.Filename, new BsonMapper()))
            {
                var raw = reopened.GetCollection("invoices");
                var persisted = raw.FindById(7301);

                ((object)persisted).Should().NotBeNull();
                persisted.Keys.Should().BeEquivalentTo("_id", "Payload");
                persisted["_id"].AsInt32.Should().Be(7301);
                persisted["Payload"].AsString.Should().Be("first");
                persisted.ContainsKey(nameof(InheritedInvoice.InheritedInvoiceId)).Should().BeFalse();
                raw.Find(Query.EQ("_id", 7302)).Single()["Payload"].AsString.Should().Be("second");

                var invoices = reopened.GetCollection<InheritedInvoice>("invoices");
                invoices.FindById(7301).Payload.Should().Be("first");
                invoices.Find(x => x.InheritedInvoiceId == 7302)
                    .Select(x => x.Payload).Should().Equal("second");
                invoices.Find(x => ((InheritedInvoiceBase)x).InheritedInvoiceId == 7302)
                    .Select(x => x.Payload).Should().Equal("second");
                invoices.Find(x => (x as InheritedInvoiceBase).InheritedInvoiceId == 7302)
                    .Select(x => x.Payload).Should().Equal("second");
                var projected = invoices.Query().Where(x => x.InheritedInvoiceId == 7302)
                    .Select(x => new InheritedInvoice { InheritedInvoiceId = x.InheritedInvoiceId, Payload = x.Payload })
                    .ToArray().Single();
                projected.InheritedInvoiceId.Should().Be(7302);
                projected.Payload.Should().Be("second");
            }
        }

        [Fact]
        public void Duplicate_inherited_mapped_type_ids_are_duplicate_primary_keys()
        {
            using var db = new LiteDatabase(":memory:", new BsonMapper());
            var invoices = db.GetCollection<InheritedInvoice>("invoices");

            invoices.Insert(new InheritedInvoice { InheritedInvoiceId = 7301, Payload = "original" });
            Action insertDuplicate = () => invoices.Insert(new InheritedInvoice
            {
                InheritedInvoiceId = 7301,
                Payload = "duplicate"
            });

            insertDuplicate.Should().Throw<LiteException>()
                .Which.ErrorCode.Should().Be(LiteException.INDEX_DUPLICATE_KEY);
            db.GetCollection("invoices").Count().Should().Be(1);
        }

        [Fact]
        public void Existing_id_precedence_and_suffix_boundaries_are_preserved()
        {
            var mapper = new BsonMapper();

            var direct = mapper.ToDocument(new DirectInvoice
            {
                DirectInvoiceId = 41,
                Payload = "direct control"
            });
            direct["_id"].AsInt32.Should().Be(41);
            direct.ContainsKey(nameof(DirectInvoice.DirectInvoiceId)).Should().BeFalse();

            var precedence = mapper.ToDocument(new IdPrecedenceInvoice
            {
                Id = 51,
                IdPrecedenceInvoiceId = 52
            });
            precedence["_id"].AsInt32.Should().Be(51,
                "the existing Id convention has priority over the mapped-type-name convention");
            precedence[nameof(IdPrecedenceInvoice.IdPrecedenceInvoiceId)].AsInt32.Should().Be(52);

            var unrelated = mapper.ToDocument(new UnrelatedSuffixInvoice { SomeoneElsesId = 61 });
            unrelated.ContainsKey("_id").Should().BeFalse();
            unrelated[nameof(UnrelatedSuffixInvoice.SomeoneElsesId)].AsInt32.Should().Be(61,
                "an arbitrary inherited *Id property is not the mapped type's id");
        }
    }
}
