using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using LiteDB.Engine;
using Xunit;
using Xunit.Abstractions;

namespace LiteDB.Tests.Engine
{
    public class AllocationPowerLoss_Tests
    {
        private readonly ITestOutputHelper _output;
        public AllocationPowerLoss_Tests(ITestOutputHelper output) { _output = output; }

        [Theory]
        [InlineData(null, "hole")]
        [InlineData("secret", "hole")]
        [InlineData(null, "tail")]
        [InlineData("secret", "tail")]
        [InlineData(null, "free")]
        [InlineData("secret", "free")]
        public void Every_physical_publication_boundary_recovers_a_complete_graph(string password, string mode)
        {
            using var file = new TempFile();
            using (var seed = AllocationWorkload.Open(file.Filename, password)) AllocationWorkload.Seed(seed, mode);
            using var device = new IndexMigrationCrashDevice(File.ReadAllBytes(file.Filename), Array.Empty<byte>());
            using (var engine = new LiteEngine(new EngineSettings
            {
                DataStream = device.Data, LogStream = device.Log, Password = password,
                TransactionPageLimit = 1, CompactStorage = CompactStorageMode.Legacy
            }))
            using (var db = new LiteDatabase(engine, disposeOnClose: false))
            {
                device.Armed = false;
                // Initial WAL preamble is complete before the operation under test.
                db.CheckpointSize = 0;
                device.Data.FlushToDisk();
                device.Log.FlushToDisk();
                AllocationWorkload.Publish(db, mode, stage: stage =>
                {
                    if (stage == "allocated") { device.Stage = "commit"; device.Armed = true; }
                    if (stage == "committed") device.Stage = "acknowledged";
                });
                device.Armed = false;
            }
            _output.WriteLine("{0}/{1}: {2} unique physical images from {3} boundaries", mode, password == null ? "plain" : "encrypted", device.Images.Count, device.PhysicalBoundaries);
            device.Images.Count.Should().BeGreaterThan(5);
            foreach (var image in device.Images)
            {
                using var recoveredFile = new TempFile();
                File.WriteAllBytes(recoveredFile.Filename, image.Data);
                File.WriteAllBytes(FileHelper.GetLogFile(recoveredFile.Filename), image.Log);
                for (var retry = 0; retry < 2; retry++)
                {
                    using var recovered = AllocationWorkload.Open(recoveredFile.Filename, password);
                    var committed = recovered.GetCollection("b").Count() == 9;
                    if (image.Event.StartsWith("acknowledged:")) committed.Should().BeTrue(image.Event);
                    AllocationWorkload.Verify(recovered, committed: committed, headerPublished: recovered.UserVersion == 3082);
                    recovered.Checkpoint();
                }
                AllocationReachability.Verify(recoveredFile.Filename, password);
            }
        }
    }
}
