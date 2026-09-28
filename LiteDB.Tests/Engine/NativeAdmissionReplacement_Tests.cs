using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Engine;
using Xunit;

namespace LiteDB.Tests.Engine
{
    public class NativeAdmissionReplacement_Tests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Local_connections_follow_replacement_even_when_the_first_admission_was_read_only(bool readOnly)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using (var first = new LiteDatabase(new ConnectionString
                { Filename = file, ReadOnly = readOnly, Connection = ConnectionType.Shared }))
            using (var second = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
            {
                first.GetCollection("rows").Count().Should().Be(1);
                second.Rebuild();
                second.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 43 });
                first.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(43);
                second.GetCollection("rows").Update(new BsonDocument { ["_id"] = 1, ["value"] = 42 });
                second.Rebuild(); // No accumulating retired identities or handles.
                first.GetCollection("rows").FindById(1)["value"].AsInt32.Should().Be(42);
                Action direct = () => { using var db = new LiteDatabase(file); };
                direct.Should().Throw<DatabaseAdmissionException>();
                second.Dispose();
                direct.Should().Throw<DatabaseAdmissionException>();
            }
            NativeAdmission_Tests.Verify(file);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Failed_downgrade_retains_exclusion_and_faults_cached_admission(bool installationFails)
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using (var db = new LiteDatabase(new ConnectionString { Filename = file, Connection = ConnectionType.Shared }))
            {
                var reached = false;
                RebuildService.SimulateInstallFailure = stage =>
                {
                    if (stage != "after-temp-install") return;
                    SharedCoordinationFile.CreationStage = (path, operation) =>
                    {
                        if (operation != "mode-shared-lock") return;
                        reached = true;
                        throw new IOException("injected native downgrade failure");
                    };
                    if (installationFails) throw new IOException("primary installation failure");
                };
                try
                {
                    Action rebuild = () => db.Rebuild(new RebuildOptions { Password = "replacement-password" });
                    var error = rebuild.Should().Throw<IOException>().WithMessage(installationFails
                        ? "primary installation failure" : "injected native downgrade failure").Which;
                    error.Data[RebuildService.LiveStateDataKey].Should().Be(installationFails
                        ? RebuildService.LiveStateOriginal : RebuildService.LiveStateReplacement);
                    if (installationFails)
                        error.Data[RebuildService.RollbackErrorsDataKey].Should().BeOfType<AggregateException>();
                    reached.Should().BeTrue();
                }
                finally
                {
                    SharedCoordinationFile.CreationStage = null;
                    RebuildService.SimulateInstallFailure = null;
                }
                Action write = () => db.GetCollection("rows").DeleteAll();
                write.Should().Throw<DatabaseAdmissionException>().WithMessage("*conversion failed*");
                Action direct = () => { using var engine = new LiteEngine(file); };
                direct.Should().Throw<DatabaseAdmissionException>();
            }
            NativeAdmission_Tests.Verify(file, installationFails ? null : "replacement-password");
        }

        [Fact]
        public async Task An_opener_that_checked_before_replacement_cannot_create_in_the_rename_gap()
        {
            using var file = new TempFile();
            NativeAdmission_Tests.Seed(file);
            using var checkedMarker = new ManualResetEventSlim();
            using var allowOpen = new ManualResetEventSlim();
            using var attempted = new ManualResetEventSlim();
            using var owner = new LiteDatabase(file);
            var opening = Task.Run(() =>
            {
                SharedCoordinationFile.CreationStage = (path, stage) =>
                {
                    if (stage != "mode-before-path-lock") return;
                    checkedMarker.Set();
                    allowOpen.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                };
                try
                {
                    Action open = () => { using var engine = new LiteEngine(file); };
                    open.Should().Throw<IOException>();
                }
                finally { SharedCoordinationFile.CreationStage = null; attempted.Set(); }
            });
            checkedMarker.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var gap = false;
            RebuildService.SimulateInstallFailure = stage =>
            {
                if (stage != "after-source-backup") return;
                gap = true;
                allowOpen.Set();
                // The opener times out waiting for the scoped path mutex. It
                // must never create an empty live file while installation pauses.
                attempted.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
                File.Exists(file).Should().BeFalse();
            };
            try { owner.Rebuild(); }
            finally { RebuildService.SimulateInstallFailure = null; allowOpen.Set(); }
            await opening;
            gap.Should().BeTrue();
            owner.Dispose();
            NativeAdmission_Tests.Verify(file);
        }
    }
}
