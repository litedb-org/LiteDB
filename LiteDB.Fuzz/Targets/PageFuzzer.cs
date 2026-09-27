using LiteDB.Engine;
using static LiteDB.Constants;

namespace LiteDB.Fuzz.Targets;

internal sealed class PageFuzzer : IFuzzTarget
{
    public string Name => "page";
    public string Description => "BasePage allocator model with payload, footer, accounting, overlap, and defrag invariants.";

    public Task RunAsync(FuzzContext context)
    {
        var buffer = new PageBuffer(new byte[PAGE_SIZE], 0, context.Seed) { ShareCounter = BUFFER_WRITABLE };
        var page = new BasePage(buffer, 1, PageType.Empty);
        var model = new Dictionary<byte, byte[]>();
        var inserts = 0;
        var updates = 0;
        var deletes = 0;
        var defrags = 0;
        try
        {
            while (context.Next())
            {
                var operation = context.Random.Next(4);
                if ((operation == 0 || model.Count == 0) && page.FreeBytes > BasePage.SLOT_SIZE + 1)
                {
                    var length = context.Random.Next(1, Math.Min(700, page.FreeBytes - BasePage.SLOT_SIZE) + 1);
                    var payload = Bytes(context.Random, length);
                    var slice = page.Insert((ushort)length, out var index);
                    Write(slice, payload);
                    model[index] = payload;
                    inserts++;
                    context.Trace("insert", new { index, length });
                }
                else if (operation == 1 && model.Count != 0)
                {
                    var index = model.Keys.ElementAt(context.Random.Next(model.Count));
                    var maximum = Math.Min(900, page.FreeBytes + model[index].Length);
                    if (maximum > 0)
                    {
                        var payload = Bytes(context.Random, context.Random.Next(1, maximum + 1));
                        var slice = page.Update(index, (ushort)payload.Length);
                        Write(slice, payload);
                        model[index] = payload;
                        updates++;
                        context.Trace("update", new { index, length = payload.Length });
                    }
                }
                else if (operation == 2 && model.Count != 0)
                {
                    var index = model.Keys.ElementAt(context.Random.Next(model.Count));
                    page.Delete(index);
                    model.Remove(index);
                    deletes++;
                    context.Trace("delete", index);
                }
                else if (page.FragmentedBytes > 0)
                {
                    page.Defrag();
                    defrags++;
                    context.Trace("defrag");
                }
                Validate(context, page, model);
                if (context.Steps % 17 == 0)
                {
                    page.UpdateBuffer();
                    page = new BasePage(buffer);
                    Validate(context, page, model);
                }
            }
            if (context.Steps >= 200)
                context.Check(inserts > 0 && updates > 0 && deletes > 0 && defrags > 0,
                    "Page campaign missed an allocator mutation path.");
            context.Metrics["operations"] = new { inserts, updates, deletes, defrags };
            context.Metrics["survivingSlots"] = model.Count;
            context.Metrics["usedBytes"] = page.UsedBytes;
            context.Metrics["freeBytes"] = page.FreeBytes;
        }
        finally { buffer.ShareCounter = 0; }
        return Task.CompletedTask;
    }

    private static void Validate(FuzzContext context, BasePage page, Dictionary<byte, byte[]> model)
    {
        context.Check(page.ItemsCount == model.Count, "ItemsCount disagreed with reference slots.");
        context.Check(page.UsedBytes == model.Values.Sum(value => value.Length), "UsedBytes disagreed with payload lengths.");
        var expectedHighest = model.Count == 0 ? byte.MaxValue : model.Keys.Max();
        context.Check(page.HighestIndex == expectedHighest, "HighestIndex disagreed with occupied slots.");
        context.Check(page.FooterSize == (model.Count == 0 ? 0 : (expectedHighest + 1) * BasePage.SLOT_SIZE),
            "FooterSize disagreed with HighestIndex.");
        context.Check(page.FreeBytes == PAGE_SIZE - PAGE_HEADER_SIZE - page.UsedBytes - page.FooterSize,
            "FreeBytes accounting mismatch.");
        context.Check(page.NextFreePosition >= PAGE_HEADER_SIZE && page.NextFreePosition + page.FooterSize <= PAGE_SIZE,
            "Content and footer boundaries overlap.");
        context.Check(page.FragmentedBytes <= page.FreeBytes, "FragmentedBytes exceeded FreeBytes.");

        var ranges = new List<(int Start, int End, byte Index)>();
        foreach (var pair in model)
        {
            var slice = page.Get(pair.Key);
            var actual = Read(slice);
            context.Check(actual.SequenceEqual(pair.Value), $"Payload changed for slot {pair.Key}.");
            var position = page.Buffer.ReadUInt16(BasePage.CalcPositionAddr(pair.Key));
            var length = page.Buffer.ReadUInt16(BasePage.CalcLengthAddr(pair.Key));
            context.Check(length == pair.Value.Length, $"Footer length mismatch for slot {pair.Key}.");
            context.Check(position >= PAGE_HEADER_SIZE && position + length <= PAGE_SIZE - page.FooterSize,
                $"Slot {pair.Key} overlaps header/footer.");
            ranges.Add((position, position + length, pair.Key));
        }
        var ordered = ranges.OrderBy(range => range.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++)
            context.Check(ordered[i - 1].End <= ordered[i].Start,
                $"Slots {ordered[i - 1].Index} and {ordered[i].Index} overlap.");
    }

    private static byte[] Bytes(Random random, int length)
    {
        var value = new byte[length];
        random.NextBytes(value);
        return value;
    }

    private static void Write(BufferSlice slice, byte[] value)
    {
        for (var i = 0; i < value.Length; i++) slice[i] = value[i];
    }

    private static byte[] Read(BufferSlice slice)
    {
        var value = new byte[slice.Count];
        for (var i = 0; i < value.Length; i++) value[i] = slice[i];
        return value;
    }
}
