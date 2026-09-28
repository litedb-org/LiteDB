using LiteDB.Engine;
using LiteDB.Vector;

namespace LiteDB.Fuzz.Targets;

internal sealed class IntegrityFuzzer : IFuzzTarget
{
    public string Name => "integrity";
    public string Description => "Mutation tests proving the raw database integrity oracle rejects structural corruption.";

    public Task RunAsync(FuzzContext context)
    {
        context.Next();
        var baseline = context.RegisterFile(Path.Combine(context.DirectoryPath, "integrity.db"));
        using (var db = new LiteDatabase(new ConnectionString { Filename = baseline, TransactionPageLimit = 4 }))
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("group", "group");
            for (var id = 1; id <= 30; id++)
                rows.Insert(new BsonDocument { ["_id"] = id, ["group"] = id * 10, ["payload"] = new byte[12000] });
            rows.DeleteMany("_id <= 15");
            var vectors = db.GetCollection("vectors");
            vectors.EnsureIndex("embedding_idx", BsonExpression.Create("$.embedding"), new VectorIndexOptions(3));
            for (var id = 1; id <= 2; id++)
                vectors.Insert(new BsonDocument { ["_id"] = id, ["embedding"] = new BsonValue(new[] { (float)id, id + .5f, -id }) });
            db.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, baseline);

        var pages = ReadPages(baseline);
        var live = pages.First(pair => pair.Key != 0 && pair.Value is DataPage or IndexPage).Key;
        var empty = pages.First(pair => pair.Value.PageType == PageType.Empty).Key;
        var indexNodes = pages.Values.OfType<IndexPage>().SelectMany(page => page.GetIndexNodes()).ToArray();
        var primary = indexNodes.Where(node => node.Slot == 0 && !node.DataBlock.IsEmpty).Take(2).ToArray();
        var secondary = indexNodes.First(node => node.Slot != 0 && !node.DataBlock.IsEmpty && node.Key.IsInt32);
        var dataBlocks = pages.Values.OfType<DataPage>().SelectMany(page => page.GetUsedIndexs().Select(page.GetBlock)).ToArray();
        var multiBlock = dataBlocks.First(block => !block.Extend && !block.NextBlock.IsEmpty);
        var vectorNodes = pages.Values.OfType<VectorIndexPage>().SelectMany(page => page.GetNodes()).ToDictionary(node => node.Position);
        var vectorSource = vectorNodes.Values.First(node => Enumerable.Range(0, node.LevelCount).Any(level => node.GetNeighbors(level).Count != 0));
        var vectorLevel = Enumerable.Range(0, vectorSource.LevelCount).First(level => vectorSource.GetNeighbors(level).Count != 0);
        var vectorTarget = vectorNodes[vectorSource.GetNeighbors(vectorLevel)[0]];
        var backlinkIndex = vectorTarget.GetNeighbors(vectorLevel).ToList().IndexOf(vectorSource.Position);
        var detected = 0;
        detected += Mutant("page-id", stream => WriteUInt32(stream, Offset(live, BasePage.P_PAGE_ID), live + 1));
        detected += Mutant("accounting", stream => WriteUInt16(stream, Offset(live, BasePage.P_USED_BYTES),
            checked((ushort)(pages[live].UsedBytes + 1))));
        detected += Mutant("empty-cycle", stream => WriteUInt32(stream, Offset(empty, BasePage.P_NEXT_PAGE_ID), empty));
        detected += Mutant("orphan-empty", stream => WriteUInt32(stream, Offset(0, HeaderPage.P_FREE_EMPTY_PAGE_ID), pages[empty].NextPageID));
        detected += Mutant("dangling-index-link", stream => WritePageAddress(stream,
            SegmentOffset(secondary) + 12 + PageAddress.SIZE, new PageAddress(uint.MaxValue - 1, 1)));
        detected += Mutant("wrong-secondary-key", stream => WriteInt32(stream,
            SegmentOffset(secondary) + 12 + secondary.Levels * PageAddress.SIZE * 2 + 1, secondary.Key.AsInt32 + 1));
        detected += Mutant("primary-wrong-document", stream => WritePageAddress(stream,
            SegmentOffset(primary[0]) + 2, primary[1].DataBlock));
        detected += Mutant("cyclic-data-continuation", stream => WritePageAddress(stream,
            SegmentOffset(multiBlock) + DataBlock.P_NEXT_BLOCK, multiBlock.Position));
        detected += Mutant("free-live-page", stream => WriteUInt32(stream,
            Offset(0, HeaderPage.P_FREE_EMPTY_PAGE_ID), primary[0].DataBlock.PageID));
        detected += Mutant("broken-vector-backlink", stream => WritePageAddress(stream,
            SegmentOffset(vectorTarget) + 6 + vectorLevel * (1 + VectorIndexNode.MaxNeighborsPerLevel * PageAddress.SIZE) +
            1 + backlinkIndex * PageAddress.SIZE, PageAddress.Empty));
        context.Check(detected == 10, "The integrity oracle accepted a known structural or semantic mutant.");

        VerifyPreallocated(context);
        VerifyEncrypted(context);
        var legacyFiles = VerifyLegacyCorpus(context);
        context.Metrics["mutantsDetected"] = detected;
        context.Metrics["preallocatedLayouts"] = 1;
        context.Metrics["encryptedLayouts"] = 1;
        context.Metrics["legacyFiles"] = legacyFiles;
        return Task.CompletedTask;

        int Mutant(string name, Action<FileStream> mutate)
        {
            var file = context.RegisterFile(Path.Combine(context.DirectoryPath, $"mutant-{name}.db"));
            File.Copy(baseline, file, true);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) mutate(stream);
            try
            {
                DatabaseIntegrityVerifier.Verify(context, file);
                return 0;
            }
            catch (FuzzFailureException error)
            {
                context.Trace("integrity-mutant", new { name, detected = true, error = error.Message });
                return 1;
            }
        }
    }

    private static Dictionary<uint, BasePage> ReadPages(string file)
    {
        using var stream = File.OpenRead(file);
        var result = new Dictionary<uint, BasePage>();
        for (uint id = 0; id < stream.Length / Constants.PAGE_SIZE; id++)
        {
            var bytes = new byte[Constants.PAGE_SIZE];
            stream.ReadExactly(bytes);
            var buffer = new PageBuffer(bytes, 0, checked((int)id + 1));
            var basic = BasePage.ReadPage<BasePage>(buffer);
            result[id] = basic.PageType switch
            {
                PageType.Header => BasePage.ReadPage<HeaderPage>(buffer),
                PageType.Collection => BasePage.ReadPage<CollectionPage>(buffer),
                PageType.Data => BasePage.ReadPage<DataPage>(buffer),
                PageType.Index => BasePage.ReadPage<IndexPage>(buffer),
                PageType.VectorIndex => BasePage.ReadPage<VectorIndexPage>(buffer),
                _ => basic
            };
        }
        return result;
    }

    private static long Offset(uint pageID, int field) => (long)pageID * Constants.PAGE_SIZE + field;

    private static long SegmentOffset(IndexNode node) =>
        Offset(node.Position.PageID, node.Page.Get(node.Position.Index).Offset);

    private static long SegmentOffset(DataBlock block) =>
        Offset(block.Position.PageID, block.Buffer.Offset - DataBlock.P_BUFFER);

    private static long SegmentOffset(VectorIndexNode node) =>
        Offset(node.Position.PageID, node.Page.Get(node.Position.Index).Offset);

    private static void WriteUInt32(FileStream stream, long offset, uint value)
    {
        stream.Position = offset;
        stream.Write(BitConverter.GetBytes(value));
        stream.Flush(true);
    }

    private static void WriteUInt16(FileStream stream, long offset, ushort value)
    {
        stream.Position = offset;
        stream.Write(BitConverter.GetBytes(value));
        stream.Flush(true);
    }

    private static void WriteInt32(FileStream stream, long offset, int value)
    {
        stream.Position = offset;
        stream.Write(BitConverter.GetBytes(value));
        stream.Flush(true);
    }

    private static void WritePageAddress(FileStream stream, long offset, PageAddress value)
    {
        stream.Position = offset;
        stream.Write(BitConverter.GetBytes(value.PageID));
        stream.WriteByte(value.Index);
        stream.Flush(true);
    }

    private static void VerifyPreallocated(FuzzContext context)
    {
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "preallocated.db"));
        using (var db = new LiteDatabase(new ConnectionString { Filename = file, InitialSize = 32L * Constants.PAGE_SIZE }))
        {
            db.GetCollection("rows").Insert(new BsonDocument { ["_id"] = 1, ["value"] = "preallocated" });
            db.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, file);
    }

    private static void VerifyEncrypted(FuzzContext context)
    {
        const string password = "integrity-secret";
        var file = context.RegisterFile(Path.Combine(context.DirectoryPath, "encrypted.db"));
        using (var db = new LiteDatabase(new ConnectionString { Filename = file, Password = password }))
        {
            var rows = db.GetCollection("rows");
            rows.EnsureIndex("value", "value");
            rows.Insert(new BsonDocument { ["_id"] = 1, ["value"] = "encrypted" });
            db.Checkpoint();
        }
        DatabaseIntegrityVerifier.Verify(context, file, password);
    }

    private static int VerifyLegacyCorpus(FuzzContext context)
    {
        var files = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Corpus", "Legacy", "Issue2255_Before2753.db")
        };
        context.Check(files.All(File.Exists), "The declared legacy fuzz corpus is incomplete.");
        foreach (var source in files)
        {
            var target = context.RegisterFile(Path.Combine(context.DirectoryPath, "legacy-" + Path.GetFileName(source)));
            File.Copy(source, target, true);
            using (var db = new LiteDatabase(target))
            {
                var documents = db.GetCollectionNames().Sum(name => db.GetCollection(name).Count());
                context.Check(documents > 0, $"Legacy corpus file {Path.GetFileName(source)} opened without documents.");
                db.Rebuild();
                db.Checkpoint();
            }
            DatabaseIntegrityVerifier.Verify(context, target);
        }
        return files.Length;
    }
}
