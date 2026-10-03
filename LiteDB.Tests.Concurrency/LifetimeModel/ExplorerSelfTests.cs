using System.Collections.Generic;
using Xunit;

namespace LiteDB.Tests.Concurrency.LifetimeModel
{
    /// <summary>
    /// Controls for the explorer itself, independent of LiteDB: an untimed lock-order
    /// inversion must be reported as a liveness violation, the same inversion with bounded
    /// waits must pass (one side times out and is refused), and the safety ledger must fire.
    /// </summary>
    public class ExplorerSelfTests
    {
        private sealed class LockOrderModel : LifetimeModel
        {
            private readonly bool _bounded;
            private readonly int?[] _owners = new int?[2];

            public LockOrderModel(bool bounded) => _bounded = bounded;

            public override void Build()
            {
                this.Host.Spawn("A", t => this.TakeBoth(t, 0, 1));
                this.Host.Spawn("B", t => this.TakeBoth(t, 1, 0));
            }

            private IEnumerable<Step> TakeBoth(ModelThread thread, int first, int second)
            {
                var op = this.Ledger.Begin(thread, OpKind.Fresh, $"lock {first} then {second}");
                foreach (var index in new[] { first, second })
                {
                    yield return Step.At($"self-test: acquire {index}");
                    var lockIndex = index;
                    yield return _bounded
                        ? Step.TimedWait($"self-test: wait {index}", 1, () => _owners[lockIndex] == null)
                        : Step.Wait($"self-test: wait {index}", () => _owners[lockIndex] == null);
                    if (thread.TimedOut)
                    {
                        if (_owners[first] == thread.Id) _owners[first] = null;
                        this.Reject(op, "timeout", $"self-test: wait {index}");
                        yield break;
                    }
                    _owners[lockIndex] = thread.Id;
                    if (index == first) this.Ledger.Admitted(op, "self-test: first lock", "self-test");
                }
                yield return Step.At("self-test: release");
                _owners[first] = _owners[second] = null;
                this.Complete(op, "self-test: release");
            }
        }

        private sealed class FenceModel : LifetimeModel
        {
            public override void Build()
            {
                this.Host.Spawn("closer", this.Close);
                this.Host.Spawn("worker", this.Work);
            }

            private IEnumerable<Step> Close(ModelThread thread)
            {
                var op = this.Ledger.Begin(thread, OpKind.Close, "close");
                yield return Step.At("self-test: close");
                this.Ledger.FenceAcquired("self-test", "self-test: close");
                this.Complete(op, "self-test: close");
            }

            private IEnumerable<Step> Work(ModelThread thread)
            {
                var op = this.Ledger.Begin(thread, OpKind.Fresh, "unfenced work");
                yield return Step.At("self-test: admit without checking the fence");
                this.Ledger.Admitted(op, "self-test: admit", "self-test");
                this.Complete(op, "self-test: done");
            }
        }

        private static ModelRunOptions Options() => new ModelRunOptions { Iterations = 300, Seed = 7 };

        [Fact]
        public void Unbounded_lock_order_inversion_is_a_liveness_violation()
        {
            var result = ModelRunner.Run("selftest-lockorder", () => new LockOrderModel(bounded: false), Options());
            Assert.True(result.BugFound, result.Summary);
            Assert.Contains("Liveness violated", result.Bug);
            Assert.Contains("self-test: wait", result.Bug);
        }

        [Fact]
        public void Bounded_lock_order_inversion_ends_by_timeout()
        {
            var result = ModelRunner.Run("selftest-bounded", () => new LockOrderModel(bounded: true), Options());
            Assert.False(result.BugFound, result.Summary + "\n" + result.Bug);
        }

        [Fact]
        public void Unfenced_admission_after_close_is_a_safety_violation()
        {
            var result = ModelRunner.Run("selftest-fence", () => new FenceModel(), Options());
            Assert.True(result.BugFound, result.Summary);
            Assert.Contains("no fresh admission after close acquired", result.Bug);
        }
    }
}
