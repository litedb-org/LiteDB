using System;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Engine;
using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class Upgrade_Tests
    {
        [Fact]
        public void Migrage_From_V4()
        {
            // v5 upgrades only from v4!
            using(var tempFile = new TempFile("../../../Resources/v4.db"))
            {
                using (var db = new LiteDatabase($"filename={tempFile};upgrade=true"))
                {
                    // convert and open database
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }

                using (var db = new LiteDatabase($"filename={tempFile};upgrade=true"))
                {
                    // database already converted
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }
            }
        }

        [Fact]
        public void Migrage_From_V4_No_FileExtension()
        {
            // v5 upgrades only from v4!
            using (var tempFile = new TempFile("../../../Resources/v4.db"))
            {
                using (var db = new LiteDatabase($"filename={tempFile};upgrade=true"))
                {
                    // convert and open database
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }

                using (var db = new LiteDatabase($"filename={tempFile};upgrade=true"))
                {
                    // database already converted
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }
            }
        }

        [Fact]
        public void Stream_Constructor_Preserves_Public_Signature()
        {
            typeof(LiteDatabase).GetConstructor(new[]
            {
                typeof(Stream),
                typeof(BsonMapper),
                typeof(Stream)
            }).Should().NotBeNull();
        }

        [Fact]
        public void Upgrade_Plain_Stream_To_Separate_Destination()
        {
            var original = File.ReadAllBytes("../../../Resources/v4.db");

            using (var source = new FaultingMemoryStream(original))
            using (var destination = new FaultingMemoryStream(new byte[] { 1, 2, 3 }))
            {
                source.Position = 17;

                LiteEngine.Upgrade(source, destination).Should().BeTrue();

                source.ToArray().Should().Equal(original);
                source.Position.Should().Be(17);
                source.IsDisposed.Should().BeFalse();
                destination.Position.Should().Be(0);
                destination.IsDisposed.Should().BeFalse();

                using (var db = new LiteDatabase(destination))
                {
                    db.GetCollection("col1").Count().Should().Be(3);
                }

                destination.IsDisposed.Should().BeFalse();
            }
        }

        [Fact]
        public void Upgrade_Encrypted_Stream_To_Separate_Destination()
        {
            var original = File.ReadAllBytes("../../../Resources/Issue_2494_EncryptedV4.db");

            using (var source = new FaultingMemoryStream(original))
            using (var destination = new FaultingMemoryStream())
            {
                source.Position = 11;

                LiteEngine.Upgrade(source, destination, "pass123").Should().BeTrue();

                source.ToArray().Should().Equal(original);
                source.Position.Should().Be(11);
                source.IsDisposed.Should().BeFalse();
                destination.Position.Should().Be(0);

                var settings = new EngineSettings
                {
                    DataStream = destination,
                    Password = "pass123"
                };

                using (var db = new LiteDatabase(new LiteEngine(settings)))
                {
                    db.GetCollectionNames().Should().Contain("PlayerDto");
                }

                destination.IsDisposed.Should().BeFalse();
            }
        }

        [Fact]
        public void Upgrade_Current_Stream_Leaves_Destination_Unchanged()
        {
            var legacy = File.ReadAllBytes("../../../Resources/v4.db");

            using (var source = new FaultingMemoryStream(legacy))
            using (var current = new FaultingMemoryStream())
            using (var destination = new FaultingMemoryStream(new byte[] { 4, 5, 6 }))
            {
                LiteEngine.Upgrade(source, current).Should().BeTrue();

                var currentBytes = current.ToArray();
                current.Position = 23;
                destination.Position = 2;

                LiteEngine.Upgrade(current, destination).Should().BeFalse();

                current.ToArray().Should().Equal(currentBytes);
                current.Position.Should().Be(23);
                destination.ToArray().Should().Equal(4, 5, 6);
                destination.Position.Should().Be(2);
                current.IsDisposed.Should().BeFalse();
                destination.IsDisposed.Should().BeFalse();
            }
        }

        [Fact]
        public void Upgrade_Conversion_Failure_Preserves_Source_And_Destination()
        {
            var original = File.ReadAllBytes("../../../Resources/v4.db");

            using (var source = new FaultingMemoryStream(original, readsBeforeThrow: 1))
            using (var destination = new FaultingMemoryStream(new byte[] { 7, 8, 9 }))
            {
                source.Position = 31;
                destination.Position = 1;

                Action upgrade = () => LiteEngine.Upgrade(source, destination);

                upgrade.Should().Throw<IOException>();
                source.ToArray().Should().Equal(original);
                source.Position.Should().Be(31);
                destination.ToArray().Should().Equal(7, 8, 9);
                destination.Position.Should().Be(1);
                source.IsDisposed.Should().BeFalse();
                destination.IsDisposed.Should().BeFalse();
            }
        }

        [Fact]
        public void Upgrade_Copy_Failure_Preserves_Source()
        {
            var original = File.ReadAllBytes("../../../Resources/v4.db");

            using (var source = new FaultingMemoryStream(original))
            using (var destination = new FaultingMemoryStream(throwOnWrite: true))
            {
                source.Position = 37;

                Action upgrade = () => LiteEngine.Upgrade(source, destination);

                upgrade.Should().Throw<IOException>();
                source.ToArray().Should().Equal(original);
                source.Position.Should().Be(37);
                source.IsDisposed.Should().BeFalse();
                destination.IsDisposed.Should().BeFalse();
            }
        }

        [Fact]
        public void Upgrade_Flush_Failure_Preserves_Source()
        {
            var original = File.ReadAllBytes("../../../Resources/v4.db");

            using (var source = new FaultingMemoryStream(original))
            using (var destination = new FaultingMemoryStream(throwOnFlush: true))
            {
                source.Position = 41;

                Action upgrade = () => LiteEngine.Upgrade(source, destination);

                upgrade.Should().Throw<IOException>();
                source.ToArray().Should().Equal(original);
                source.Position.Should().Be(41);
                source.IsDisposed.Should().BeFalse();
                destination.IsDisposed.Should().BeFalse();
            }
        }

        [Fact]
        public void Upgrade_Rejects_Unsupported_Stream_Capabilities()
        {
            var original = File.ReadAllBytes("../../../Resources/v4.db");

            using (var unreadable = new FaultingMemoryStream(original, canRead: false))
            using (var unseekableSource = new FaultingMemoryStream(original, canSeek: false))
            using (var unwritable = new FaultingMemoryStream(canWrite: false))
            using (var unseekableDestination = new FaultingMemoryStream(canSeek: false))
            using (var validSource = new FaultingMemoryStream(original))
            using (var validDestination = new FaultingMemoryStream())
            {
                Action unreadableUpgrade = () => LiteEngine.Upgrade(unreadable, validDestination);
                Action unseekableSourceUpgrade = () => LiteEngine.Upgrade(unseekableSource, validDestination);
                Action unwritableUpgrade = () => LiteEngine.Upgrade(validSource, unwritable);
                Action unseekableDestinationUpgrade = () => LiteEngine.Upgrade(validSource, unseekableDestination);
                Action sameStreamUpgrade = () => LiteEngine.Upgrade(validSource, validSource);

                unreadableUpgrade.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("source");
                unseekableSourceUpgrade.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("source");
                unwritableUpgrade.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("destination");
                unseekableDestinationUpgrade.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("destination");
                sameStreamUpgrade.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("destination");
            }
        }

    }
}
