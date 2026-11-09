#if NET8_0_OR_GREATER
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using LiteDB.Client.Shared;
using LiteDB.Tests;
using Xunit;

namespace LiteDB.Internals
{
    public class SharedRequestAdmission_Tests : IDisposable
    {
        private readonly string _directory = Path.Combine(SharedMappedDirectory.Root, "litedb-request-admission-" + Guid.NewGuid().ToString("N"));
        private string Filename => Path.Combine(_directory, "test.db");
        private bool _passed;
        public SharedRequestAdmission_Tests() => Directory.CreateDirectory(_directory);

        [Fact]
        public async Task Concurrent_processes_open_use_dispose_default_Shared_preserve_all_commits()
        {
            using (var seed = new LiteDatabase(new ConnectionString { Filename = Filename, Connection = ConnectionType.Shared }))
                seed.GetCollection("sentinel").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "untouched" });
            var workers = Enumerable.Range(0, 4).Select(id => new MvccProcess("request-open-loop", Filename, null, id.ToString())).ToArray();
            try
            {
                await Task.WhenAll(workers.Select(worker => worker.Expect("ready")));
                foreach (var worker in workers) worker.Send("continue");
                await Task.WhenAll(workers.Select(async worker =>
                {
                    (await worker.ReadLine(TimeSpan.FromSeconds(100))).Should().Be("done");
                    await worker.Finish();
                }));
            }
            finally { foreach (var worker in workers) worker.Dispose(); }
            // Independent cold reopen verifies every acknowledged write and unrelated data.
            using (var db = new LiteDatabase(Filename))
            {
                db.GetCollection("requests").Count().Should().Be(160);
                for (var worker = 0; worker < 4; worker++)
                    for (var iteration = 0; iteration < 40; iteration++)
                        db.GetCollection("requests").FindById(worker * 1000 + iteration)["value"].AsString.Should().Be("worker:" + worker);
                db.GetCollection("sentinel").FindById(1)["value"].AsString.Should().Be("untouched");
            }
            _passed = true;
        }

        [Fact]
        public async Task Shared_Dms_waits_for_real_process_lock_release()
        {
            using var owner = new MvccProcess("request-dms-hold", Filename, null);
            await owner.Expect("ready");
            using var contender = new SharedRangeLocks(Filename);
            var attempt = Task.Run(() => contender.ShareDms());
            await Task.Delay(150);
            attempt.IsCompleted.Should().BeFalse("admission must wait while another process owns exclusive DMS");
            owner.Send("continue");
            await owner.Finish();
            await attempt.WaitAsync(TimeSpan.FromSeconds(5));
            using var probe = new SharedRangeLocks(Filename);
            probe.TryExclusiveDms().Should().BeFalse("successful admission must retain its shared lock");
            _passed = true;
        }

        [Fact]
        public async Task Dms_timeout_refuses_admission_and_remains_retryable()
        {
            using var owner = new MvccProcess("request-dms-hold", Filename, null);
            await owner.Expect("ready");
            using var contender = new SharedRangeLocks(Filename);
            Action blocked = () => contender.ShareDms(TimeSpan.FromMilliseconds(100));
            blocked.Should().Throw<TimeoutException>();
            contender.TryExclusiveDms().Should().BeFalse("timeout must not acquire or release the peer lock");
            owner.Send("continue");
            await owner.Finish();
            contender.ShareDms(TimeSpan.FromSeconds(5));
            using var probe = new SharedRangeLocks(Filename);
            probe.TryExclusiveDms().Should().BeFalse();
            _passed = true;
        }

        public void Dispose()
        {
            if (_passed) Directory.Delete(_directory, true);
            else Console.Error.WriteLine("Preserved admission evidence: " + _directory);
        }
    }
}
#endif
