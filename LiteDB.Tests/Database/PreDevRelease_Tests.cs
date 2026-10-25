using System;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Database
{
#if LITEDB_PREDEV
    [CollectionDefinition(nameof(PreDevReleaseCollection), DisableParallelization = true)]
    public sealed class PreDevReleaseCollection
    {
    }

    [Collection(nameof(PreDevReleaseCollection))]
#endif
    public class PreDevRelease_Tests
    {
        [Fact]
        public void Build_exposes_the_expected_predev_contract()
        {
#if LITEDB_PREDEV
            LiteDBPragmas.ResetPreDevRiskAcknowledgementForTesting();

            try
            {
                AssertGuardedConstructors();

                LiteDBPragmas.I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS();

                using (var file = new TempFile())
                {
                    using (var db = new LiteDatabase(file.Filename))
                    {
                        db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                    }

                    using (var reopened = new LiteDatabase(file.Filename))
                    {
                        reopened.GetCollection("docs").Count().Should().Be(1);
                    }
                }
            }
            finally
            {
                LiteDBPragmas.I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS();
            }
#else
            typeof(LiteDatabase).Assembly.GetType("LiteDB.LiteDBPragmas").Should().BeNull();

            using (var db = new LiteDatabase(":memory:"))
            {
                db.GetCollection("docs").Insert(new BsonDocument { ["_id"] = 1 });
                db.GetCollection("docs").Count().Should().Be(1);
            }
#endif
        }

#if LITEDB_PREDEV
        private static void AssertGuardedConstructors()
        {
            using (var file = new TempFile())
            {
                AssertPreDevWarning(() => new LiteDatabase(file.Filename));
                File.Exists(file.Filename).Should().BeFalse();
            }

            AssertPreDevWarning(() => new LiteDatabase(new ConnectionString(":memory:")));
            AssertPreDevWarning(() => new LiteRepository(":memory:"));

            using (var stream = new MemoryStream())
            {
                AssertPreDevWarning(() => new LiteDatabase(stream));
                stream.Length.Should().Be(0);
            }

            using (var engine = new LiteEngine(new EngineSettings { DataStream = new MemoryStream() }))
            {
                AssertPreDevWarning(() => new LiteDatabase(engine, disposeOnClose: false));
            }
        }

        private static void AssertPreDevWarning(Func<IDisposable> open)
        {
            Action action = () =>
            {
                using (open())
                {
                }
            };

            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*prerelease development build*Broken databases and data loss are expected*" +
                    "on your own if it breaks*DO NOT RUN IN PRODUCTION*" +
                    "LiteDBPragmas.I_AM_AWARE_MY_DATABASE_BREAKS_WHEN_I_USE_THIS()*");
        }
#endif
    }
}
