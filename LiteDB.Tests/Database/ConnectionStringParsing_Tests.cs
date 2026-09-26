using System;

using FluentAssertions;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Database
{
    public class ConnectionStringParsing_Tests
    {
        [Fact]
        public void Quoted_values_preserve_semicolons_quotes_and_spaces()
        {
            var parsed = new ConnectionString(@"filename=demo;password=""ab\""|    c;785; """);
            parsed.Filename.Should().Be("demo");
            parsed.Password.Should().Be("ab\"|    c;785; ");
            new ConnectionString(@"filename=demo;password=ab\""123").Password.Should().Be("ab\"123");
        }

        [Fact]
        public void Current_options_accept_case_and_repeated_whitespace()
        {
            var parsed = new ConnectionString("filename=demo; initial   size=2 MB; MEMORY   PROFILE=LowMemory; cache   size=64 MB; transaction   pages=32; readONLY=true; upgrade=true");
            parsed.InitialSize.Should().Be(2 * 1024 * 1024);
            parsed.MemoryProfile.Should().Be(MemoryProfile.LowMemory);
            parsed.CacheSize.Should().Be(64 * 1024 * 1024);
            parsed.TransactionPageLimit.Should().Be(32);
            parsed.ReadOnly.Should().BeTrue();
            parsed.Upgrade.Should().BeTrue();
        }

        [Fact]
        public void Windows_paths_and_single_quoted_values_keep_their_contents()
        {
            var parsed = new ConnectionString(@"filename=""\\server\share\data.db"";password=' a;b '");
            parsed.Filename.Should().Be(@"\\server\share\data.db");
            parsed.Password.Should().Be(" a;b ");
            new ConnectionString(@"filename=C:\folder\").Filename.Should().Be(@"C:\folder\");
        }

        [Theory]
        [InlineData("filename=demo;password=ab;123")]
        [InlineData("filename=demo;password=\"unterminated")]
        [InlineData("filename=demo;password=\"value\"junk")]
        [InlineData("filename=demo;=value")]
        [InlineData("filename=demo;;password=value")]
        public void Malformed_options_throw_instead_of_being_partially_accepted(string text)
        {
            Action parse = () => new ConnectionString(text);
            parse.Should().Throw<FormatException>();
        }

        [Fact]
        public void Custom_options_are_preserved_without_parser_registration()
        {
            var parsed = new ConnectionString("tenant=acme;region=west;filename=demo");

            parsed["tenant"].Should().Be("acme");
            parsed["region"].Should().Be("west");
            parsed.Filename.Should().Be("demo");
        }

        [Fact]
        public void Duplicate_options_use_the_last_value()
        {
            var parsed = new ConnectionString("filename=first;password=old;filename=last;password='new;value'");

            parsed.Filename.Should().Be("last");
            parsed.Password.Should().Be("new;value");
        }

        [Fact]
        public void Settings_only_strings_remain_valid_for_current_dev()
        {
            var parsed = new ConnectionString("memory profile=LowMemory;cache size=1 MB");
            parsed.Filename.Should().BeEmpty();
            parsed.MemoryProfile.Should().Be(MemoryProfile.LowMemory);
            parsed.CacheSize.Should().Be(1024 * 1024);
        }
    }
}
