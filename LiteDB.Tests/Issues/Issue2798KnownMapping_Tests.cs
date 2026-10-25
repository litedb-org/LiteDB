using System;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2798KnownMapping_Tests
    {
        public interface IRecord { string Key { get; set; } int Number { get; set; } }
        public class KeyRecord : IRecord
        {
            [BsonId] public string Key { get; set; }
            public int Number { get; set; }
        }
        public class OtherKeyRecord : IRecord
        {
            [BsonId] public string Key { get; set; }
            public int Number { get; set; }
        }
        public class NumberRecord : IRecord
        {
            public string Key { get; set; }
            [BsonId] public int Number { get; set; }
        }

        public abstract class AbstractRecord
        {
            public abstract string Key { get; set; }
            public abstract int Number { get; set; }
        }
        public class AbstractKeyRecord : AbstractRecord
        {
            [BsonId] public override string Key { get; set; }
            public override int Number { get; set; }
        }
        public class AbstractOtherKeyRecord : AbstractRecord
        {
            [BsonId] public override string Key { get; set; }
            public override int Number { get; set; }
        }
        public class AbstractNumberRecord : AbstractRecord
        {
            public override string Key { get; set; }
            [BsonId] public override int Number { get; set; }
        }

        [Fact]
        public void Initializing_implementation_is_not_waited_on_or_used_for_inference()
        {
            var mapper = new BsonMapper();
            mapper.ResolveMember = (type, info, member) =>
            {
                if (type == typeof(KeyRecord))
                    mapper.GetExpression<IRecord, bool>(row => row.Key == "one").Source.Should().Contain("$.Key");
            };
            mapper.ToDocument(new KeyRecord { Key = "one" });
            mapper.GetExpression<IRecord, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Abstract_implementations_require_agreement_and_respect_explicit_mapping()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new AbstractKeyRecord { Key = "one" });
            mapper.ToDocument(new AbstractOtherKeyRecord { Key = "two" });
            mapper.GetExpression<AbstractRecord, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
            mapper.ToDocument(new AbstractNumberRecord { Key = "three", Number = 3 });
            Action resolve = () => mapper.GetExpression<AbstractRecord, bool>(row => row.Key == "one");
            resolve.Should().Throw<NotSupportedException>().WithMessage("*disagree*_id*");
            mapper.Entity<AbstractRecord>().Id(row => row.Key);
            mapper.GetExpression<AbstractRecord, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Known_implementations_must_agree_and_explicit_interface_mapping_wins()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new KeyRecord { Key = "one" });
            mapper.ToDocument(new OtherKeyRecord { Key = "two" });
            mapper.GetExpression<IRecord, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
            mapper.GetExpression<IRecord, bool>(row => row.Number == 1).Source.Should().Contain("$.Number");
            mapper.ToDocument(new NumberRecord { Key = "three", Number = 3 });
            Action resolve = () => mapper.GetExpression<IRecord, bool>(row => row.Key == "one");
            resolve.Should().Throw<NotSupportedException>().WithMessage("*disagree*_id*");
            mapper.Entity<IRecord>().Id(row => row.Key);
            mapper.GetExpression<IRecord, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
        }

        [Fact]
        public void Inference_does_not_mutate_declared_mapping_or_other_mappers()
        {
            var mapper = new BsonMapper();
            mapper.ToDocument(new KeyRecord { Key = "one" });
            mapper.GetExpression<IRecord, bool>(row => row.Key == "one").Source.Should().Contain("$._id");
            mapper.GetEntityMapper(typeof(IRecord)).Id.Should().BeNull();
            new BsonMapper().GetExpression<IRecord, bool>(row => row.Key == "one").Source.Should().Contain("$.Key");
        }
    }
}
