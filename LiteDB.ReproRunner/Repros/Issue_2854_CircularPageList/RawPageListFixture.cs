using System.Buffers.Binary;
using System.Security.Cryptography;

internal static class RawPageListFixture
{
    private const int PageSize = 8192;
    private const int PageIdOffset = 0;
    private const int PageTypeOffset = 4;
    private const int PreviousPageIdOffset = 5;
    private const int NextPageIdOffset = 9;
    private const int PageListSlotOffset = 13;
    private const int CollectionIdOffset = 19;
    private const int CollectionFreeListOffset = 32;
    private const int RecoveryMarkerOffset = 191;
    private const byte CollectionPageType = 2;
    private const byte DataPageType = 4;
    private const int FreeListSlots = 5;

    public static HealthyLayout InspectHealthy(string path)
    {
        var bytes = ReadAndValidateFile(path);
        Require(bytes[RecoveryMarkerOffset] == 0,
            "healthy fixture already has LiteDB's recovery marker set");
        var collectionPageId = FindOnlyCollectionPage(bytes);
        var reachable = new HashSet<uint>();
        var chains = new List<IReadOnlyList<uint>>();

        for (var slot = 0; slot < FreeListSlots; slot++)
        {
            var chain = ReadAcyclicChain(bytes, collectionPageId, slot, reachable);
            if (chain.Count > 0)
            {
                chains.Add(chain);
            }
        }

        var allDataPages = PageIds(bytes)
            .Where(id => ReadByte(bytes, id, PageTypeOffset) == DataPageType)
            .Where(id => ReadUInt32(bytes, id, CollectionIdOffset) == collectionPageId)
            .ToHashSet();

        Require(allDataPages.Count >= 2, "fixture did not create at least two target data pages");
        Require(allDataPages.SetEquals(reachable),
            "the collection page's five free lists do not account for every target data page");

        var selected = chains.OrderByDescending(x => x.Count).FirstOrDefault();
        Require(selected != null && selected.Count >= 2,
            "fixture did not create a non-degenerate data-page chain");

        var slotIndex = ReadByte(bytes, selected![0], PageListSlotOffset);
        return new HealthyLayout(
            collectionPageId,
            slotIndex,
            selected.ToArray(),
            allDataPages.OrderBy(x => x).ToArray());
    }

    public static void CreateCycle(string source, string destination, HealthyLayout expected)
    {
        File.Copy(source, destination, true);
        var bytes = ReadAndValidateFile(destination);
        var head = expected.Chain[0];
        var tail = expected.Chain[^1];

        Require(ReadUInt32(bytes, head, PreviousPageIdOffset) == uint.MaxValue,
            "selected healthy chain does not start at its head");
        Require(ReadUInt32(bytes, tail, NextPageIdOffset) == uint.MaxValue,
            "selected healthy chain does not end at its tail");

        WriteUInt32(bytes, tail, NextPageIdOffset, head);
        WriteUInt32(bytes, head, PreviousPageIdOffset, tail);
        File.WriteAllBytes(destination, bytes);

        AssertOnlyCycleEdgesChanged(source, destination, head, tail);
        AssertExactCycle(destination, expected);
    }

    public static void AssertExactCycle(string path, HealthyLayout expected)
    {
        var bytes = ReadAndValidateFile(path);
        Require(FindOnlyCollectionPage(bytes) == expected.CollectionPageId,
            "collection page identity changed");
        Require(ReadFreeListHead(bytes, expected.CollectionPageId, expected.Slot) == expected.Chain[0],
            "the corrupted chain is no longer reachable from its collection free-list head");

        for (var index = 0; index < expected.Chain.Count; index++)
        {
            var pageId = expected.Chain[index];
            var previous = expected.Chain[(index + expected.Chain.Count - 1) % expected.Chain.Count];
            var next = expected.Chain[(index + 1) % expected.Chain.Count];
            AssertDataPage(bytes, pageId, expected.CollectionPageId, expected.Slot);
            Require(ReadUInt32(bytes, pageId, PreviousPageIdOffset) == previous,
                $"page {pageId} has the wrong previous edge");
            Require(ReadUInt32(bytes, pageId, NextPageIdOffset) == next,
                $"page {pageId} has the wrong next edge");
        }

        var visited = new HashSet<uint>();
        var current = ReadFreeListHead(bytes, expected.CollectionPageId, expected.Slot);
        while (visited.Add(current))
        {
            Require(visited.Count <= expected.Chain.Count,
                "raw cycle proof escaped the selected chain");
            current = ReadUInt32(bytes, current, NextPageIdOffset);
        }

        Require(current == expected.Chain[0] && visited.SetEquals(expected.Chain),
            "raw traversal did not return to the reachable head through exactly the selected pages");
    }

    public static string Hash(string path)
    {
        return HashBytes(File.ReadAllBytes(path));
    }

    public static string StructuralHash(string path)
    {
        var bytes = ReadAndValidateFile(path);
        bytes[RecoveryMarkerOffset] = 0;
        return HashBytes(bytes);
    }

    public static void AssertRecoveryMarker(string path)
    {
        var bytes = ReadAndValidateFile(path);
        Require(bytes[RecoveryMarkerOffset] == 1,
            "error 999 did not set LiteDB's recovery marker");
    }

    private static IReadOnlyList<uint> ReadAcyclicChain(
        byte[] bytes,
        uint collectionPageId,
        int slot,
        HashSet<uint> globallyReachable)
    {
        var result = new List<uint>();
        var current = ReadFreeListHead(bytes, collectionPageId, slot);
        var previous = uint.MaxValue;

        while (current != uint.MaxValue)
        {
            Require(result.Count <= bytes.Length / PageSize,
                "healthy fixture already contains an unbounded page list");
            Require(globallyReachable.Add(current),
                $"healthy page {current} is reachable more than once");
            AssertDataPage(bytes, current, collectionPageId, slot);
            Require(ReadUInt32(bytes, current, PreviousPageIdOffset) == previous,
                $"healthy page {current} has a broken previous edge");
            result.Add(current);
            previous = current;
            current = ReadUInt32(bytes, current, NextPageIdOffset);
        }

        return result;
    }

    private static void AssertDataPage(byte[] bytes, uint pageId, uint collectionPageId, int slot)
    {
        Require(pageId < bytes.Length / PageSize, $"page {pageId} points outside the file");
        Require(ReadUInt32(bytes, pageId, PageIdOffset) == pageId,
            $"physical page {pageId} has a different stored ID");
        Require(ReadByte(bytes, pageId, PageTypeOffset) == DataPageType,
            $"page {pageId} is not a data page");
        Require(ReadUInt32(bytes, pageId, CollectionIdOffset) == collectionPageId,
            $"page {pageId} belongs to another collection");
        Require(ReadByte(bytes, pageId, PageListSlotOffset) == slot,
            $"page {pageId} does not belong to free-list slot {slot}");
    }

    private static byte[] ReadAndValidateFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Require(bytes.Length >= PageSize * 4 && bytes.Length % PageSize == 0,
            "fixture is not a page-aligned LiteDB data file");
        return bytes;
    }

    private static void AssertOnlyCycleEdgesChanged(
        string source,
        string destination,
        uint head,
        uint tail)
    {
        var original = ReadAndValidateFile(source);
        var corrupt = ReadAndValidateFile(destination);
        Require(original.Length == corrupt.Length, "cycle mutation changed the file length");

        var headPrevious = checked((int)head * PageSize + PreviousPageIdOffset);
        var tailNext = checked((int)tail * PageSize + NextPageIdOffset);
        var changes = 0;

        for (var index = 0; index < original.Length; index++)
        {
            if (original[index] == corrupt[index])
            {
                continue;
            }

            changes++;
            var inHeadPrevious = index >= headPrevious && index < headPrevious + sizeof(uint);
            var inTailNext = index >= tailNext && index < tailNext + sizeof(uint);
            Require(inHeadPrevious || inTailNext,
                $"cycle mutation unexpectedly changed byte {index}");
        }

        Require(changes > 0, "cycle mutation did not change any bytes");
    }

    private static string HashBytes(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }

    private static uint FindOnlyCollectionPage(byte[] bytes)
    {
        var pages = PageIds(bytes)
            .Where(id => ReadByte(bytes, id, PageTypeOffset) == CollectionPageType)
            .ToArray();
        Require(pages.Length == 1, $"expected one collection page but found {pages.Length}");
        return pages[0];
    }

    private static IEnumerable<uint> PageIds(byte[] bytes)
    {
        for (uint id = 0; id < bytes.Length / PageSize; id++)
        {
            yield return id;
        }
    }

    private static uint ReadFreeListHead(byte[] bytes, uint collectionPageId, int slot) =>
        ReadUInt32(bytes, collectionPageId, CollectionFreeListOffset + slot * sizeof(uint));

    private static byte ReadByte(byte[] bytes, uint pageId, int offset) =>
        bytes[checked((int)pageId * PageSize + offset)];

    private static uint ReadUInt32(byte[] bytes, uint pageId, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(checked((int)pageId * PageSize + offset), sizeof(uint)));

    private static void WriteUInt32(byte[] bytes, uint pageId, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(checked((int)pageId * PageSize + offset), sizeof(uint)), value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record HealthyLayout(
    uint CollectionPageId,
    int Slot,
    IReadOnlyList<uint> Chain,
    IReadOnlyList<uint> DataPageIds);
