using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
        public void ConnectionString_ToString()
        {
            var empty = new ConnectionString();

            empty.ToString().Should().BeEmpty();

            var onlyfile = new ConnectionString
            {
                Filename = @"c:\only file\demo.db",
            };

            onlyfile.ToString().Should().Be(@"c:\only file\demo.db");

            // filename with =
            var fileWithSpecials = new ConnectionString
            {
                Filename = @"c:\only file\d""e=mo.db",
            };

            fileWithSpecials.ToString().Should().Be(@"Filename=""c:\only file\d\""e=mo.db""");

            // file with spaces with " and ;
            var full = new ConnectionString
            {
                Filename = @"c:\only;file""d""emo.db",
                Password = "john-doe ",
                ReadOnly = true,
                InitialSize = 10_485_760,
            };

            full.ToString().Should().Be(@"Filename=""c:\only;file\""d\""emo.db"";Password=""john-doe "";Initial Size=10485760;ReadOnly=True");

            // ToString/Parse round trips
            var parsed = new ConnectionString(full.ToString());
            parsed.Should().BeEquivalentTo(full);
        }
    }
}
