using LiteDB;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2163_MixedAccess;

internal static class ContenderScenario
{
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);

    public static Verdict Run(ReproHostClient host, ScenarioPaths paths)
    {
        if (!Coordination.TryWaitFor(paths.OwnerReady, PeerTimeout))
        {
            if (File.Exists(paths.Verdict)) return Coordination.ReadJson<Verdict>(paths.Verdict);
            throw new TimeoutException("The Direct owner never published its ready state.");
        }

        var owner = Coordination.ReadJson<OwnerReady>(paths.OwnerReady);
        if (!owner.TransactionOpen || owner.ProcessId == Environment.ProcessId)
        {
            throw new InvalidOperationException("The contender did not observe a distinct live Direct owner.");
        }

        Coordination.WriteMarker(paths.ContenderAttempting);
        var outcome = AttemptMixedWrite(paths);
        Coordination.WriteJson(paths.ContenderOutcome, outcome);
        host.SendLog(
            $"Mixed Shared attempt: kind={outcome.Kind}, " +
            $"insertAcknowledged={outcome.InsertAcknowledged}, " +
            $"checkpointAcknowledged={outcome.CheckpointAcknowledged}, " +
            $"freshReadBackVerified={outcome.FreshReadBackVerified}, " +
            $"completedBeforeRelease={outcome.CompletedBeforeRelease}, " +
            $"ownerReleasedObserved={outcome.OwnerReleasedObserved}, " +
            $"exception={outcome.Exception?.Type ?? "none"}.");

        Coordination.WaitFor(paths.OwnerReleased, PeerTimeout);
        var postControl = RunNonConflictingControl(paths);
        Coordination.WriteJson(paths.PostControlOutcome, postControl);
        Coordination.WaitFor(paths.Verdict, PeerTimeout);
        return Coordination.ReadJson<Verdict>(paths.Verdict);
    }

    private static ContenderOutcome AttemptMixedWrite(ScenarioPaths paths)
    {
        var receipt = ScenarioData.CreateReceipt(
            ScenarioData.SharedDuringOwnerId,
            "shared-during-direct",
            Environment.ProcessId);
        var insertAcknowledged = false;
        var checkpointAcknowledged = false;
        var freshReadBackVerified = false;

        try
        {
            using (var database = ScenarioData.Open(paths.Database, ConnectionType.Shared))
            {
                ScenarioData.Insert(ScenarioData.Collection(database), receipt);
                insertAcknowledged = true;
                ScenarioData.WriteReceipt(paths.Receipts, receipt);
                database.Checkpoint();
                checkpointAcknowledged = true;
            }

            using (var reopened = ScenarioData.Open(paths.Database, ConnectionType.Shared))
            {
                ScenarioData.VerifyExact(reopened, receipt);
                freshReadBackVerified = true;
            }

            return new ContenderOutcome(
                "success",
                insertAcknowledged,
                checkpointAcknowledged,
                freshReadBackVerified,
                CompletedBeforeRelease: !File.Exists(paths.ReleaseStarted),
                OwnerReleasedObserved: File.Exists(paths.OwnerReleased),
                Environment.ProcessId,
                Exception: null);
        }
        catch (Exception ex)
        {
            var beforeRelease = !File.Exists(paths.ReleaseStarted);
            var expectedCollision =
                beforeRelease &&
                !insertAcknowledged &&
                CollisionClassifier.IsExpectedOwnershipFailure(ex);
            return new ContenderOutcome(
                expectedCollision ? "collision" : "unexpected",
                insertAcknowledged,
                checkpointAcknowledged,
                freshReadBackVerified,
                CompletedBeforeRelease: beforeRelease,
                OwnerReleasedObserved: File.Exists(paths.OwnerReleased),
                Environment.ProcessId,
                ExceptionDetails.Capture(ex));
        }
    }

    private static PostControlOutcome RunNonConflictingControl(ScenarioPaths paths)
    {
        var receipt = ScenarioData.CreateReceipt(
            ScenarioData.SharedAfterReleaseId,
            "shared-after-release",
            Environment.ProcessId);
        var insertAcknowledged = false;
        var checkpointAcknowledged = false;
        var freshReadBackVerified = false;

        try
        {
            using (var database = ScenarioData.Open(paths.Database, ConnectionType.Shared))
            {
                ScenarioData.Insert(ScenarioData.Collection(database), receipt);
                insertAcknowledged = true;
                ScenarioData.WriteReceipt(paths.Receipts, receipt);
                database.Checkpoint();
                checkpointAcknowledged = true;
            }

            using (var reopened = ScenarioData.Open(paths.Database, ConnectionType.Shared))
            {
                ScenarioData.VerifyExact(reopened, receipt);
                freshReadBackVerified = true;
            }

            return new PostControlOutcome(
                insertAcknowledged,
                checkpointAcknowledged,
                freshReadBackVerified,
                Exception: null);
        }
        catch (Exception ex)
        {
            return new PostControlOutcome(
                insertAcknowledged,
                checkpointAcknowledged,
                freshReadBackVerified,
                ExceptionDetails.Capture(ex));
        }
    }
}
