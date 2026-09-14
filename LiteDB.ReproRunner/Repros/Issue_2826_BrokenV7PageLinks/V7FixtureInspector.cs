using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

internal static class V7FixtureInspector
{
    public const int PageSize = 4096;
    public const int NextPageIdOffset = 9;
    public const string HealthyId = "healthy";
    public const string DamagedId = "damaged";
    public const string HealthyPayload = "healthy-document-survives";
    public const int LargePayloadLength = 20_000;

    private const int PageTypeOffset = 4;
    private const int PreviousPageIdOffset = 5;
    private const int ItemCountOffset = 13;
    private const int PageHeaderSize = 25;
    private const string ExpectedSha256 = "6A6597DC6A3C9882F90BFD6C2CF85A7E86559DCD4C588611376F3F25B1F9D348";

    public static V7FixtureLayout InspectPristine(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Require(bytes.Length == 40_960, $"fixture length is {bytes.Length}, expected 40960");
        Require(Hash(bytes) == ExpectedSha256, "fixture SHA-256 does not match the v4.1.4 source");
        var pageCount = bytes.Length / PageSize;

        AssertPage(bytes, 0, 1);
        Require(Encoding.UTF8.GetString(bytes, 25, 27) == "** This is a LiteDB file **",
            "fixture header signature is not LiteDB v7");
        Require(bytes[52] == 7, "fixture datafile version is not 7");
        Require(ReadUInt32(bytes, 59) == checked((uint)pageCount - 1),
            "fixture header has the wrong last page ID");
        Require(bytes.AsSpan(65, 36).IndexOfAnyExcept((byte)0) < 0, "fixture is unexpectedly encrypted");

        var header = new RawCursor(bytes, 101, PageSize - 101);
        Require(header.ReadByte() == 1, "fixture must contain exactly one collection");
        Require(header.ReadSizedString() == "documents", "fixture collection name changed");
        var collectionPageId = header.ReadUInt32();
        Require(collectionPageId == 2, "fixture collection page changed");

        var indexPageId = ReadCollection(bytes, collectionPageId);
        var references = ReadIndexReferences(bytes, indexPageId);
        var blocks = ReadDataBlocks(bytes, references);
        var inline = blocks.Single(x => x.Index == 0);
        var extended = blocks.Single(x => x.Index == 1);

        Require(inline.ExtendPageId == uint.MaxValue && inline.Data.Length == 79,
            "control document is not the expected inline block");
        Require(extended.ExtendPageId == 5 && extended.Data.Length == 0,
            "large document does not start on expected extend page 5");
        VerifyDocument(RawBsonStringDocument.Read(inline.Data), HealthyId, "control", HealthyPayload);

        var (extendData, extendPages) = ReadExtendChain(bytes, extended.ExtendPageId);
        Require(extendData.Length == 20_055, "large raw BSON document has the wrong length");
        Require(extendPages.SequenceEqual(new uint[] { 5, 6, 7, 8, 9 }),
            "large document does not occupy the expected five-page extend chain");
        VerifyDocument(RawBsonStringDocument.Read(extendData), DamagedId, "extended", null);

        Require(bytes.AsSpan(PageSize, PageSize).IndexOfAnyExcept((byte)0) < 0,
            "reserved physical page 1 is not empty");
        return new V7FixtureLayout(extended.ExtendPageId, extendPages[1], pageCount, ExpectedSha256);
    }

    public static uint ReadNextPageId(byte[] bytes, uint pageId) =>
        ReadUInt32(bytes, checked((int)pageId * PageSize + NextPageIdOffset));

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static uint ReadCollection(byte[] bytes, uint pageId)
    {
        AssertPage(bytes, pageId, 2);
        var cursor = PageCursor(bytes, pageId);
        Require(cursor.ReadSizedString() == "documents", "collection-page name changed");
        cursor.Skip(12);
        uint headPageId = uint.MaxValue;
        var populatedIndexes = 0;

        for (var slot = 0; slot < 16; slot++)
        {
            var field = cursor.ReadSizedString();
            var unique = cursor.ReadByte() != 0;
            var head = cursor.ReadUInt32();
            cursor.Skip(12);
            if (field.Length == 0)
            {
                continue;
            }

            populatedIndexes++;
            Require(field == "_id=$._id" && unique, "fixture _id index definition changed");
            headPageId = head;
        }

        Require(populatedIndexes == 1 && headPageId == 3, "fixture must have one _id index on page 3");
        return headPageId;
    }

    private static IReadOnlyList<IndexReference> ReadIndexReferences(byte[] bytes, uint pageId)
    {
        AssertPage(bytes, pageId, 3);
        var cursor = PageCursor(bytes, pageId);
        var itemCount = ReadUInt16(bytes, checked((int)pageId * PageSize + ItemCountOffset));
        Require(itemCount == 4, "fixture index page must contain two sentinels and two document nodes");
        var references = new List<IndexReference>();

        for (var item = 0; item < itemCount; item++)
        {
            _ = cursor.ReadUInt16();
            var levels = cursor.ReadByte();
            Require(levels is >= 1 and <= 32, "fixture index node has an invalid level count");
            cursor.Skip(13);
            var keyLength = cursor.ReadUInt16();
            _ = cursor.ReadByte();
            var key = Encoding.UTF8.GetString(cursor.ReadBytes(keyLength));
            var dataPageId = cursor.ReadUInt32();
            var dataIndex = cursor.ReadUInt16();
            cursor.Skip(12 + (levels - 1) * 12);

            if (dataPageId != uint.MaxValue)
            {
                references.Add(new IndexReference(key, dataPageId, dataIndex));
            }
        }

        Require(references.Count == 2, "fixture _id index must reference exactly two documents");
        Require(references.Any(x => x == new IndexReference(HealthyId, 4, 0)),
            "healthy index node does not reference data block 4:0");
        Require(references.Any(x => x == new IndexReference(DamagedId, 4, 1)),
            "damaged index node does not reference data block 4:1");
        return references;
    }

    private static IReadOnlyList<DataBlock> ReadDataBlocks(
        byte[] bytes,
        IReadOnlyList<IndexReference> references)
    {
        Require(references.All(x => x.PageId == 4), "fixture index references more than one data page");
        AssertPage(bytes, 4, 4);
        var cursor = PageCursor(bytes, 4);
        var count = ReadUInt16(bytes, 4 * PageSize + ItemCountOffset);
        Require(count == 2, "fixture data page must contain exactly two blocks");
        var blocks = new List<DataBlock>();
        for (var item = 0; item < count; item++)
        {
            var index = cursor.ReadUInt16();
            var extendPageId = cursor.ReadUInt32();
            var length = cursor.ReadUInt16();
            blocks.Add(new DataBlock(index, extendPageId, cursor.ReadBytes(length)));
        }

        Require(blocks.Select(x => x.Index).OrderBy(x => x).SequenceEqual(new ushort[] { 0, 1 }),
            "fixture data-block indexes changed");
        return blocks;
    }

    private static (byte[] Data, IReadOnlyList<uint> Pages) ReadExtendChain(byte[] bytes, uint firstPageId)
    {
        var pageCount = bytes.Length / PageSize;
        var pages = new List<uint>();
        using var data = new MemoryStream(pageCount * (PageSize - PageHeaderSize));
        var previous = uint.MaxValue;
        var current = firstPageId;

        while (current != uint.MaxValue)
        {
            Require(current < (uint)pageCount && pages.Count < pageCount,
                "healthy extend chain escapes the file or cycles");
            Require(!pages.Contains(current), "healthy extend chain contains a repeated page");
            AssertPage(bytes, current, 5);
            var offset = checked((int)current * PageSize);
            Require(ReadUInt32(bytes, offset + PreviousPageIdOffset) == previous,
                $"extend page {current} has the wrong previous link");
            var length = ReadUInt16(bytes, offset + ItemCountOffset);
            Require(length > 0 && length <= PageSize - PageHeaderSize,
                $"extend page {current} has an invalid payload length");
            data.Write(bytes, offset + PageHeaderSize, length);
            pages.Add(current);
            previous = current;
            current = ReadUInt32(bytes, offset + NextPageIdOffset);
        }

        Require(pages.Take(4).All(id => ReadUInt16(bytes, (int)id * PageSize + ItemCountOffset) == 4071) &&
            ReadUInt16(bytes, (int)pages[^1] * PageSize + ItemCountOffset) == 3771,
            "extend-page payload sizes changed");
        return (data.ToArray(), pages);
    }

    private static void VerifyDocument(
        IReadOnlyDictionary<string, string> document,
        string id,
        string kind,
        string? payload)
    {
        Require(document.Count == 3 && document["_id"] == id && document["kind"] == kind,
            $"raw document {id} fields changed");
        var actualPayload = document["payload"];
        if (payload != null)
        {
            Require(actualPayload == payload, "healthy raw payload changed");
        }
        else
        {
            Require(actualPayload.Length == LargePayloadLength && actualPayload.All(x => x == 'x'),
                "large raw payload is not exactly 20,000 x characters");
        }
    }

    private static RawCursor PageCursor(byte[] bytes, uint pageId) =>
        new(bytes, checked((int)pageId * PageSize + PageHeaderSize), PageSize - PageHeaderSize);

    private static void AssertPage(byte[] bytes, uint pageId, byte pageType)
    {
        var offset = checked((int)pageId * PageSize);
        Require(offset >= 0 && offset <= bytes.Length - PageSize, $"page {pageId} is outside the fixture");
        Require(ReadUInt32(bytes, offset) == pageId, $"physical page {pageId} stores a different page ID");
        Require(bytes[offset + PageTypeOffset] == pageType, $"page {pageId} has the wrong page type");
    }

    private static ushort ReadUInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record IndexReference(string Key, uint PageId, ushort BlockIndex);
    private sealed record DataBlock(ushort Index, uint ExtendPageId, byte[] Data);
}

internal sealed record V7FixtureLayout(
    uint FirstExtendPageId,
    uint OriginalNextPageId,
    int PageCount,
    string SourceSha256);
