#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Tests;
using Xunit;
using static LiteDB.Internals.SharedOneFilePublicationFixture;

namespace LiteDB.Internals
{
    [Collection("SharedOneFilePublication")]
    public class SharedOneFileMaintenancePathReview_Tests
    {
        [OneFileTheory]
        [InlineData(null, false)]
        [InlineData("secret", false)]
        [InlineData(null, true)]
        [InlineData("secret", true)]
        public async Task Recovery_must_accept_the_same_database_through_a_normalized_path(string password, bool relative)
        {
            using var files = new SharedOneFilePublicationFixture(password);
            var settings = files.Settings;
            settings.Filename = relative ? Path.GetRelativePath(Environment.CurrentDirectory, files.Filename) :
                Path.Combine(Path.GetDirectoryName(files.Filename), ".", Path.GetFileName(files.Filename));
            // Positive control: this spelling is supported before an interrupted rebuild.
            using (var engine = new SharedEngine(settings))
            using (var db = new LiteDatabase(engine, disposeOnClose: false)) Verify(db, 0);

            using (var install = new MvccProcess("one-file-maintenance-cut", files.Filename, password,
                "install:after-temp-install"))
            {
                await install.Expect("ready");
                await install.Kill();
            }

            Exception failure = null;
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var error = Record.Exception(() =>
                {
                    using var engine = new SharedEngine(settings);
                    using var db = new LiteDatabase(engine, disposeOnClose: false);
                    Verify(db, 11);
                });
                failure = failure ?? error;
            }
            // Establish that the exact guarded state was recoverable, independently
            // of the rejected spelling; this must not be a damaged-data refusal.
            SharedOneFileMaintenanceCrash_Tests.Recover(files);
            failure.Should().BeNull("recovery must canonicalize supported input before comparing guard paths");
        }
    }
}
#endif
