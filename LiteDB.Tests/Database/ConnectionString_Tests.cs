using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FluentAssertions;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class ConnectionString_Tests
    {
        [Fact]
        public void ConnectionString_Parser()
        {
            // only filename
            var onlyfile = new ConnectionString(@"demo.db");

            onlyfile.Filename.Should().Be(@"demo.db");

            // file with spaces without "
            var normal = new ConnectionString(@"filename=c:\only file\demo.db");

            normal.Filename.Should().Be(@"c:\only file\demo.db");

            // filename with timeout

            // file with spaces with " and ;
            var full = new ConnectionString(
                @"filename=""c:\only;file\""d\""e=mo.db""; 
                  password =   ""john-doe "" ;
                  initial size = 10 MB ;
                  readONLY =  TRUE;");

            full.Filename.Should().Be(@"c:\only;file""d""e=mo.db");
            full.Password.Should().Be("john-doe ");
            full.ReadOnly.Should().BeTrue();
            full.InitialSize.Should().Be(10 * 1024 * 1024);

        }

        [Fact]
        public void ConnectionString_Very_Long()
        {
            var cn = new ConnectionString(@"Filename=C:\Users\yup\AppData\Roaming\corex\storecore.file;Password='1495c305c5312dd1a9a18d9502daa0369216763ca7a6f537ddbe290241cf8aad1ca326313adec74bb98d1955747347cf0e3f087899d8bb2e0aa002ff825e1c0f25eaa79e5dfbf1c0e2daf6746a3a3f140244b764204c20c0ccede3521eaf8537ae32d4b13a04f1c387f56a8d6fa095bc53451c1892a46b8182afd94559cd7377aebc8d4a2b4883c637a359e6e67e1d8c2d789721351ebb000409329b2e875d21278b7c76724c68729e53dac50168564b8c3432018212a111c952e593829b42c296458cc0020174aaef9ca6b5661ca965004404c2bbb256bc41a8aa5c5349c615e40328a3263c45e5f96e61048149e98aa8b6f2afb59d73379e1dce5429752d8d'");

            cn.Filename.Length.Should().Be(49);
            cn.Password.Length.Should().Be(512);

        }

        [Fact]
        public void ConnectionString_ToString_Redacts_Password()
        {
            var empty = new ConnectionString();

            empty.ToString().Should().BeEmpty();
            empty.ToStringWithPassword().Should().BeEmpty();

            var onlyfile = new ConnectionString
            {
                Filename = @"c:\only file\demo.db",
            };

            onlyfile.ToString().Should().Be(@"c:\only file\demo.db");
            onlyfile.ToStringWithPassword().Should().Be(@"c:\only file\demo.db");

            var protectedConnection = new ConnectionString
            {
                Filename = "demo.db",
                Password = "secret",
            };

            protectedConnection.ToString().Should().Be("""Filename="demo.db";Password=********""");
            protectedConnection.ToString().Should().NotContain("secret");
            protectedConnection.ToStringWithPassword().Should().Be("Filename=\"demo.db\";Password=\"secret\"");
        }

        [Fact]
        public void ConnectionString_ToStringWithPassword_Serializes_All_Settings()
        {
            var full = new ConnectionString
            {
                Filename = """c:\only;file"d"emo.db""",
                Connection = ConnectionType.Shared,
                Password = "john-doe\\ ",
                InitialSize = 10_485_760,
                ReadOnly = true,
                Collation = new Collation("ca-ES-VALENCIA/IgnoreCase"),
                Upgrade = true,
                AutoRebuild = true,
                MemoryProfile = MemoryProfile.Throughput,
                CacheSize = 64L * 1024 * 1024,
                TransactionPageLimit = 32,
            };

            var serialized = full.ToStringWithPassword();

            // culture name casing differs between ICU (ca-ES-VALENCIA) and NLS on .NET Framework
            // (ca-ES-valencia); the serialized form must match whatever Collation.ToString() emits
            var collation = full.Collation.ToString();

            serialized.Should().Be(
                $"""Filename="c:\only;file\"d\"emo.db";Connection=Shared;Password="john-doe\ ";InitialSize=10485760;ReadOnly=True;Collation={collation};Upgrade=True;Auto-Rebuild=True;Memory Profile=Throughput;Cache Size=67108864;Transaction Pages=32""");
            full.ToString().Should().Be(
                $"""Filename="c:\only;file\"d\"emo.db";Connection=Shared;Password=********;InitialSize=10485760;ReadOnly=True;Collation={collation};Upgrade=True;Auto-Rebuild=True;Memory Profile=Throughput;Cache Size=67108864;Transaction Pages=32""");

            var parsed = new ConnectionString(serialized);

            parsed.Filename.Should().Be(full.Filename);
            parsed.Connection.Should().Be(full.Connection);
            parsed.Password.Should().Be(full.Password);
            parsed.InitialSize.Should().Be(full.InitialSize);
            parsed.ReadOnly.Should().Be(full.ReadOnly);
            parsed.Collation.LCID.Should().Be(full.Collation.LCID);
            parsed.Collation.SortOptions.Should().Be(full.Collation.SortOptions);
            parsed.Upgrade.Should().Be(full.Upgrade);
            parsed.AutoRebuild.Should().Be(full.AutoRebuild);
            parsed.MemoryProfile.Should().Be(full.MemoryProfile);
            parsed.CacheSize.Should().Be(full.CacheSize);
            parsed.TransactionPageLimit.Should().Be(full.TransactionPageLimit);
        }

        [Theory]
        [InlineData(" db")]
        [InlineData("db ")]
        [InlineData(" db ")]
        [InlineData(@"db\")]
        public void ConnectionString_ToStringWithPassword_Preserves_Filename_Edge_Cases(string filename)
        {
            var original = new ConnectionString
            {
                Filename = filename,
                ReadOnly = true,
            };

            var parsed = new ConnectionString(original.ToStringWithPassword());

            parsed.Filename.Should().Be(filename);
        }

        [Fact]
        public void ConnectionString_ToStringWithPassword_Preserves_Backslashes_And_Quotes()
        {
            var original = new ConnectionString
            {
                Filename = "demo.db",
                Password = "secret\\\"tail\\",
            };

            var parsed = new ConnectionString(original.ToStringWithPassword());

            parsed.Password.Should().Be(original.Password);
        }

        [Fact]
        public void ConnectionString_ToStringWithPassword_Preserves_Explicit_Empty_Password()
        {
            var original = new ConnectionString
            {
                Filename = "demo.db",
                Password = string.Empty,
            };

            var serialized = original.ToStringWithPassword();

            serialized.Should().EndWith("Password=\"\"");
            new ConnectionString(serialized).Password.Should().BeEmpty();
            new ConnectionString("filename=demo.db;password=").Password.Should().BeNull();
        }

        [Fact]
        public void ConnectionString_ToStringWithPassword_Quotes_Ambiguous_Filename()
        {
            var fileWithSpecials = new ConnectionString
            {
                Filename = @"c:\only file\d""e=mo.db",
            };

            fileWithSpecials.ToStringWithPassword().Should().Be(@"Filename=""c:\only file\d\""e=mo.db""");
        }

        [Fact]
        public void ConnectionString_Rejects_Missing_Separator_After_Quoted_Value()
        {
            Action parse = () => new ConnectionString("Filename=\"demo.db\"ReadOnly=true");

            parse.Should().Throw<FormatException>()
                .WithMessage("*Expected ';' after a quoted connection value*");
        }

        [Fact]
        public void ConnectionString_Parses_Memory_Limits()
        {
            var connection = new ConnectionString("filename=test.db;cache size=64MB;transaction pages=32");

            connection.CacheSize.Should().Be(64L * 1024 * 1024);
            connection.TransactionPageLimit.Should().Be(32);
        }

        [Fact]
        public void ConnectionString_Requires_Units_For_Small_Cache_Sizes()
        {
            Action parse = () => new ConnectionString("filename=test.db;cache size=512");

            parse.Should().Throw<LiteException>()
                .WithMessage("*below 1 MB*include a size unit*");
        }

        [Fact]
        public void ConnectionString_Accepts_Explicit_Kilobytes()
        {
            var connection = new ConnectionString("filename=test.db;cache size=512KB");

            connection.CacheSize.Should().Be(512L * 1024);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("-1")]
        [InlineData("1XB")]
        [InlineData("999999999999999999999999TB")]
        [InlineData("1.5MB")]
        [InlineData("9223372036854775808")]
        [InlineData("9223372036854775807TB")]
        [InlineData("''")]
        [InlineData("")]
        public void ConnectionString_Rejects_Invalid_Cache_Sizes(string value)
        {
            Action parse = () => new ConnectionString("filename=:memory:;cache size=" + value);
            parse.Should().Throw<LiteException>();
        }

        [Theory]
        [InlineData("0", 0L)]
        [InlineData("0MB", 0L)]
        [InlineData("512bytes", 512L)]
        [InlineData("1048576", 1048576L)]
        [InlineData("64 mb", 67108864L)]
        public void ConnectionString_Accepts_Default_And_Explicit_Byte_Units(string value, long expected)
        {
            new ConnectionString("filename=:memory:;cache size=" + value).CacheSize.Should().Be(expected);
        }
    }
}
