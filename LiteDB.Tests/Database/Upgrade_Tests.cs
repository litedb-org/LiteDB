using System;
using System.IO;
using System.Linq;
using LiteDB;
using LiteDB.Tests.Utils;
using FluentAssertions;
using Xunit;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;

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
                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, $"filename={tempFile};upgrade=true"))
                {
                    // convert and open database
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }

                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, $"filename={tempFile};upgrade=true"))
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
                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, $"filename={tempFile};upgrade=true"))
                {
                    // convert and open database
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }

                using (var db = DatabaseFactory.Create(TestDatabaseType.Disk, $"filename={tempFile};upgrade=true"))
                {
                    // database already converted
                    var col1 = db.GetCollection("col1");

                    col1.Count().Should().Be(3);
                }
            }
        }
    }
}