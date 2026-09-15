using System;

using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace LiteDB.Tests.Issues
{
    public class Issue2873_Tests
    {
        [Fact]
        public void Explicit_constructor_only_mapping_owns_state_without_running_mapped_setters()
        {
            const int id = 73;
            const string storedName = "  ada lovelace  ";
            const int storedRevision = 4;

            var factoryCalls = 0;
            var mapper = new BsonMapper();
            Func<BsonDocument, ConstructorOwnedRecord> factory = document =>
            {
                factoryCalls++;
                return ConstructorOwnedRecord.Restore(document);
            };

            var configuredApi = ConfigureConstructorOnly(mapper, factory);

            configuredApi.Should().NotBeNull(
                "issue #2873 requires an explicit CtorOnly(factory) or Ctor(factory, populateMembers: false) API");
            if (configuredApi == null)
            {
                return;
            }

            using var db = new LiteDatabase(":memory:", mapper);
            db.GetCollection("records").Insert(new BsonDocument
            {
                ["_id"] = id,
                ["Name"] = storedName,
                ["Revision"] = storedRevision
            });

            var raw = db.GetCollection("records").FindById(id);
            raw["_id"].AsInt32.Should().Be(id);
            raw["Name"].AsString.Should().Be(storedName);
            raw["Revision"].AsInt32.Should().Be(storedRevision);

            var actual = db.GetCollection<ConstructorOwnedRecord>("records").FindById(id);

            using (new AssertionScope())
            {
                factoryCalls.Should().Be(1, "the registered document factory is the sole construction path");
                actual.ConstructorCalls.Should().Be(1);
                actual.IdSetterCalls.Should().Be(0);
                actual.NameSetterCalls.Should().Be(0);
                actual.RevisionSetterCalls.Should().Be(0);
                actual.Id.Should().Be(id);
                actual.Name.Should().Be("ADA LOVELACE", "normalization belongs to the custom constructor");
                actual.Revision.Should().Be(104, "the custom constructor upgrades the stored revision");
                actual.ValidationPassed.Should().BeTrue();
                actual.IntegrityStamp.Should().Be("73:ADA LOVELACE:104");
                actual.HasValidIntegrity.Should().BeTrue("member population must not invalidate constructor-owned state");
            }
        }

        [Fact]
        public void Existing_ctor_mapping_keeps_populating_mapped_members_for_compatibility()
        {
            const int id = 11;
            const string storedName = "  grace hopper  ";
            const int storedRevision = 9;

            var factoryCalls = 0;
            var mapper = new BsonMapper();
            mapper.Entity<ConstructorOwnedRecord>().Ctor(document =>
            {
                factoryCalls++;
                return ConstructorOwnedRecord.Restore(document);
            });

            using var db = new LiteDatabase(":memory:", mapper);
            db.GetCollection("records").Insert(new BsonDocument
            {
                ["_id"] = id,
                ["Name"] = storedName,
                ["Revision"] = storedRevision
            });

            var raw = db.GetCollection("records").FindById(id);
            raw["_id"].AsInt32.Should().Be(id);
            raw["Name"].AsString.Should().Be(storedName);
            raw["Revision"].AsInt32.Should().Be(storedRevision);

            var actual = db.GetCollection<ConstructorOwnedRecord>("records").FindById(id);

            using (new AssertionScope())
            {
                factoryCalls.Should().Be(1);
                actual.ConstructorCalls.Should().Be(1);
                actual.IdSetterCalls.Should().Be(1);
                actual.NameSetterCalls.Should().Be(1);
                actual.RevisionSetterCalls.Should().Be(1);
                actual.Id.Should().Be(id);
                actual.Name.Should().Be(storedName, "the existing API promises post-construction population");
                actual.Revision.Should().Be(storedRevision);
                actual.ValidationPassed.Should().BeFalse();
                actual.IntegrityStamp.Should().Be("11:GRACE HOPPER:109");
                actual.HasValidIntegrity.Should().BeFalse(
                    "the compatibility path deliberately replaces constructor-owned values with stored values");
            }
        }

        internal static string ConfigureConstructorOnly<T>(BsonMapper mapper, Func<BsonDocument, T> factory)
        {
            var builder = mapper.Entity<T>();
            var factoryType = typeof(Func<BsonDocument, T>);
            var methods = builder.GetType().GetMethods();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();

                if (!method.IsStatic &&
                    method.Name == "CtorOnly" &&
                    parameters.Length == 1 &&
                    parameters[0].ParameterType == factoryType)
                {
                    method.Invoke(builder, new object[] { factory });
                    return "CtorOnly(factory)";
                }
            }

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();

                if (!method.IsStatic &&
                    method.Name == "Ctor" &&
                    parameters.Length == 2 &&
                    parameters[0].ParameterType == factoryType &&
                    parameters[1].ParameterType == typeof(bool))
                {
                    method.Invoke(builder, new object[] { factory, false });
                    return "Ctor(factory, populateMembers: false)";
                }
            }

            return null;
        }

        public sealed class ConstructorOwnedRecord
        {
            private int _id;
            private string _name;
            private int _revision;
            private int _idSetterCalls;
            private int _nameSetterCalls;
            private int _revisionSetterCalls;

            private ConstructorOwnedRecord(int id, string name, int storedRevision)
            {
                ConstructorCalls = 1;
                _id = id;
                _name = NormalizeAndValidateName(name);
                _revision = checked(storedRevision + 100);
                IntegrityStamp = BuildIntegrityStamp(_id, _name, _revision);
            }

            public static ConstructorOwnedRecord Restore(BsonDocument document)
            {
                return new ConstructorOwnedRecord(
                    document["_id"].AsInt32,
                    document["Name"].AsString,
                    document["Revision"].AsInt32);
            }

            public int Id
            {
                get => _id;
                set
                {
                    _idSetterCalls++;
                    _id = value;
                }
            }

            public string Name
            {
                get => _name;
                set
                {
                    _nameSetterCalls++;
                    _name = value;
                }
            }

            public int Revision
            {
                get => _revision;
                set
                {
                    _revisionSetterCalls++;
                    _revision = value;
                }
            }

            public int ConstructorCalls { get; }
            public int IdSetterCalls => _idSetterCalls;
            public int NameSetterCalls => _nameSetterCalls;
            public int RevisionSetterCalls => _revisionSetterCalls;
            public bool ValidationPassed =>
                _name.Length > 0 &&
                _name == _name.Trim().ToUpperInvariant() &&
                _revision >= 100;
            public string IntegrityStamp { get; }
            public bool HasValidIntegrity => IntegrityStamp == BuildIntegrityStamp(_id, _name, _revision);

            private static string NormalizeAndValidateName(string name)
            {
                var normalized = name.Trim().ToUpperInvariant();

                if (normalized.Length == 0)
                {
                    throw new ArgumentException("A record name is required.", nameof(name));
                }

                return normalized;
            }

            private static string BuildIntegrityStamp(int id, string name, int revision)
            {
                return $"{id}:{name}:{revision}";
            }
        }
    }
}
