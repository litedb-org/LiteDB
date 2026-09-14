using System.Buffers.Binary;

internal static class V7FixtureMutator
{
    public static void CreateSelfLoop(string source, string destination, V7FixtureLayout layout)
    {
        Create(source, destination, layout, layout.FirstExtendPageId);
    }

    public static void CreatePastEof(string source, string destination, V7FixtureLayout layout)
    {
        Create(source, destination, layout, checked((uint)layout.PageCount + 1));
    }

    private static void Create(
        string source,
        string destination,
        V7FixtureLayout layout,
        uint nextPageId)
    {
        var original = File.ReadAllBytes(source);
        Require(V7FixtureInspector.Hash(original) == layout.SourceSha256, "source fixture changed before mutation");
        Require(V7FixtureInspector.ReadNextPageId(original, layout.FirstExtendPageId) == layout.OriginalNextPageId,
            "first extend page no longer points to the second extend page");

        var mutated = (byte[])original.Clone();
        var fieldOffset = checked((int)layout.FirstExtendPageId * V7FixtureInspector.PageSize +
            V7FixtureInspector.NextPageIdOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(mutated.AsSpan(fieldOffset, sizeof(uint)), nextPageId);
        File.WriteAllBytes(destination, mutated);

        var persisted = File.ReadAllBytes(destination);
        Require(persisted.Length == original.Length, "mutation changed the fixture length");
        Require(V7FixtureInspector.ReadNextPageId(persisted, layout.FirstExtendPageId) == nextPageId,
            "mutated NextPageID was not persisted");

        var changedBytes = 0;
        for (var index = 0; index < original.Length; index++)
        {
            if (original[index] == persisted[index])
            {
                continue;
            }

            changedBytes++;
            Require(index >= fieldOffset && index < fieldOffset + sizeof(uint),
                $"mutation unexpectedly changed byte {index}");
        }

        Require(changedBytes > 0, "mutation did not change the NextPageID field");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
