using LiteDB;
using LiteDB.ReproRunner.Shared.Messaging;

namespace Issue_2163_MixedAccess;

internal static class OwnerScenario
{
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CollisionObservationWindow = TimeSpan.FromSeconds(3);

    public static Verdict Run(ReproHostClient host, ScenarioPaths paths)
    {
        InitializeBaseline(paths);
        var baseline = ScenarioData.ReadReceipts(paths.Receipts).Single();
        var ownerReady = default(OwnerReady)!;
        var mutation = default(OwnerMutationOutcome)!;
        ContenderOutcome? earlyOutcome = null;
        LiteDatabase? owner = null;

        try
        {
            owner = ScenarioData.Open(paths.Database, ConnectionType.Direct);
            if (!owner.BeginTrans())
            {
                throw new InvalidOperationException("The Direct owner could not begin its transaction.");
            }

            var visible = ScenarioData.Collection(owner).FindAll().ToArray();
            if (visible.Length != 1 || !baseline.Matches(visible[0]))
            {
                throw new InvalidDataException("The Direct owner did not read the exact baseline row.");
            }

            ownerReady = new OwnerReady(Environment.ProcessId, TransactionOpen: true, visible.Length);
            Coordination.WriteJson(paths.OwnerReady, ownerReady);
            Coordination.WaitFor(paths.ContenderAttempting, PeerTimeout, paths.ContenderFatal);

            if (Coordination.TryWaitFor(
                    paths.ContenderOutcome,
                    CollisionObservationWindow,
                    paths.ContenderFatal))
            {
                earlyOutcome = Coordination.ReadJson<ContenderOutcome>(paths.ContenderOutcome);
            }

            var rowsBeforeMutation = ScenarioData.Collection(owner).FindAll().Count();
            host.SendLog(
                $"Direct owner kept transaction open: contenderCompleted={earlyOutcome is not null}, " +
                $"visibleRowsBeforeMutation={rowsBeforeMutation}.");
            mutation = MutateOwner(owner, paths, rowsBeforeMutation);

            Coordination.WriteMarker(paths.ReleaseStarted);
        }
        finally
        {
            if (!File.Exists(paths.ReleaseStarted))
            {
                Coordination.WriteMarker(paths.ReleaseStarted);
            }

            try
            {
                owner?.Dispose();
            }
            finally
            {
                Coordination.WriteMarker(paths.OwnerReleased);
            }
        }

        Coordination.WaitFor(paths.ContenderOutcome, PeerTimeout, paths.ContenderFatal);
        Coordination.WaitFor(paths.PostControlOutcome, PeerTimeout, paths.ContenderFatal);
        var contender = Coordination.ReadJson<ContenderOutcome>(paths.ContenderOutcome);
        var postControl = Coordination.ReadJson<PostControlOutcome>(paths.PostControlOutcome);

        VerifyProtocol(ownerReady, contender, mutation, postControl, paths);

        var receipts = ScenarioData.ReadReceipts(paths.Receipts);
        var snapshots = DatabaseOracle.ReopenTwice(paths.Database, receipts);
        host.SendLog($"First post-run reopen: {snapshots.First.Describe()}.");
        host.SendLog($"Second post-checkpoint reopen: {snapshots.Second.Describe()}.");

        var verdict = Evaluate(contender, mutation, snapshots.First, snapshots.Second);
        paths.TryDeleteDatabaseAlias();
        Coordination.WriteJson(paths.Verdict, verdict);
        return verdict;
    }

    private static void InitializeBaseline(ScenarioPaths paths)
    {
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.Receipts);
        paths.CreateDatabaseAlias();

        if (File.Exists(paths.Database))
        {
            throw new InvalidOperationException("The supposedly fresh run directory already contains the database.");
        }

        var receipt = ScenarioData.CreateReceipt(
            ScenarioData.BaselineId,
            "direct-baseline",
            Environment.ProcessId);

        using (var database = ScenarioData.Open(paths.Database, ConnectionType.Direct))
        {
            var collection = ScenarioData.Collection(database);
            if (!collection.EnsureIndex("token", unique: true))
            {
                throw new InvalidOperationException("The baseline token index was not created.");
            }

            ScenarioData.Insert(collection, receipt);
            database.Checkpoint();
        }

        ScenarioData.WriteReceipt(paths.Receipts, receipt);
    }

    private static OwnerMutationOutcome MutateOwner(
        LiteDatabase owner,
        ScenarioPaths paths,
        int visibleRows)
    {
        var receipt = ScenarioData.CreateReceipt(
            ScenarioData.DirectAfterSharedId,
            "direct-after-shared",
            Environment.ProcessId);

        try
        {
            ScenarioData.Insert(ScenarioData.Collection(owner), receipt);
            if (!owner.Commit())
            {
                throw new InvalidOperationException("The Direct owner returned false from Commit.");
            }
        }
        catch (Exception ex)
        {
            TryRollback(owner);
            return CollisionClassifier.IsExpectedOwnershipFailure(ex)
                ? new OwnerMutationOutcome(
                    CommitAcknowledged: false,
                    CheckpointAcknowledged: false,
                    RejectedBeforeCommit: true,
                    VisibleRowsBeforeMutation: visibleRows,
                    ExpectedCollision: ExceptionDetails.Capture(ex),
                    UnexpectedFailure: null)
                : new OwnerMutationOutcome(
                    CommitAcknowledged: false,
                    CheckpointAcknowledged: false,
                    RejectedBeforeCommit: false,
                    VisibleRowsBeforeMutation: visibleRows,
                    ExpectedCollision: null,
                    UnexpectedFailure: ExceptionDetails.Capture(ex));
        }

        ScenarioData.WriteReceipt(paths.Receipts, receipt);

        try
        {
            owner.Checkpoint();
            return new OwnerMutationOutcome(
                CommitAcknowledged: true,
                CheckpointAcknowledged: true,
                RejectedBeforeCommit: false,
                VisibleRowsBeforeMutation: visibleRows,
                ExpectedCollision: null,
                UnexpectedFailure: null);
        }
        catch (Exception ex)
        {
            return CollisionClassifier.IsExpectedOwnershipFailure(ex)
                ? new OwnerMutationOutcome(
                    CommitAcknowledged: true,
                    CheckpointAcknowledged: false,
                    RejectedBeforeCommit: false,
                    VisibleRowsBeforeMutation: visibleRows,
                    ExpectedCollision: ExceptionDetails.Capture(ex),
                    UnexpectedFailure: null)
                : new OwnerMutationOutcome(
                    CommitAcknowledged: true,
                    CheckpointAcknowledged: false,
                    RejectedBeforeCommit: false,
                    VisibleRowsBeforeMutation: visibleRows,
                    ExpectedCollision: null,
                    UnexpectedFailure: ExceptionDetails.Capture(ex));
        }
    }

    private static void TryRollback(LiteDatabase owner)
    {
        try
        {
            owner.Rollback();
        }
        catch
        {
            // The original mutation exception is the useful classification input.
        }
    }

    private static void VerifyProtocol(
        OwnerReady owner,
        ContenderOutcome contender,
        OwnerMutationOutcome mutation,
        PostControlOutcome postControl,
        ScenarioPaths paths)
    {
        if (!owner.TransactionOpen || owner.VisibleRows != 1 || contender.ProcessId == owner.ProcessId)
        {
            throw new InvalidOperationException("The two-process ownership precondition was not established.");
        }

        if (contender.Kind is not ("success" or "collision") ||
            (contender.Kind == "collision" && (!contender.CompletedBeforeRelease || contender.Exception is null)) ||
            (contender.Kind == "success" && contender.Exception is not null))
        {
            throw new InvalidOperationException(
                $"The mixed-mode attempt had an unexpected outcome: {contender.Kind}, " +
                $"{contender.Exception?.Type}: {contender.Exception?.Message}");
        }

        if (mutation.UnexpectedFailure is not null)
        {
            throw new InvalidOperationException(
                $"The Direct owner failed unexpectedly: {mutation.UnexpectedFailure.Type}: " +
                mutation.UnexpectedFailure.Message);
        }

        if (!postControl.Succeeded)
        {
            throw new InvalidOperationException(
                $"The non-conflicting Shared control failed: {postControl.Exception?.Type}: " +
                postControl.Exception?.Message);
        }

        var actualIds = ScenarioData.ReadReceipts(paths.Receipts).Select(receipt => receipt.Id).OrderBy(id => id);
        var expectedIds = new List<int> { ScenarioData.BaselineId, ScenarioData.SharedAfterReleaseId };
        if (contender.Kind == "success") expectedIds.Add(ScenarioData.SharedDuringOwnerId);
        if (mutation.CommitAcknowledged) expectedIds.Add(ScenarioData.DirectAfterSharedId);

        if (!actualIds.SequenceEqual(expectedIds.OrderBy(id => id)))
        {
            throw new InvalidDataException("External receipts disagree with the acknowledged operations.");
        }
    }

    private static Verdict Evaluate(
        ContenderOutcome contender,
        OwnerMutationOutcome mutation,
        DatabaseSnapshot first,
        DatabaseSnapshot second)
    {
        if (first.IsOnlyMissing(ScenarioData.SharedDuringOwnerId) &&
            second.IsOnlyMissing(ScenarioData.SharedDuringOwnerId) &&
            contender.Kind == "success" &&
            contender.CompletedBeforeRelease &&
            mutation.CommitAcknowledged &&
            mutation.CheckpointAcknowledged &&
            !mutation.CollisionRejected &&
            mutation.VisibleRowsBeforeMutation == 1)
        {
            return new Verdict(
                0,
                "BUG_2163_CONFIRMED",
                "Shared acknowledged row 202 before Direct ownership ended; Direct remained stale, " +
                "acknowledged row 303, and two fresh reopens contained every receipt except row 202.");
        }

        if (!first.IsClean || !second.IsClean)
        {
            throw new InvalidDataException(
                $"Post-run state was neither clean nor the exact issue-2163 loss: " +
                $"first=({first.Describe()}), second=({second.Describe()}).");
        }

        if (contender.Kind == "collision")
        {
            return new Verdict(10, "NO_BUG_2163_SHARED_REJECTED", "Shared failed fast without mutating the owned file.");
        }

        if (mutation.CollisionRejected)
        {
            return new Verdict(10, "NO_BUG_2163_DIRECT_REJECTED", "Direct rejected its stale mutation and all acknowledged rows survived.");
        }

        if (!contender.CompletedBeforeRelease)
        {
            return new Verdict(10, "NO_BUG_2163_SERIALIZED", "Shared completed after Direct released ownership and all rows survived.");
        }

        return new Verdict(10, "NO_BUG_2163_PRESERVED", "Both operations completed during overlap and every acknowledged row survived.");
    }
}
