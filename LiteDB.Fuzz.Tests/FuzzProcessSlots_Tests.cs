using Xunit;

namespace LiteDB.Fuzz.Tests;

public sealed class FuzzProcessSlots_Tests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "litedb-fuzz-slots-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_full_slot_set_makes_the_next_run_wait_until_one_is_released()
    {
        var first = await FuzzProcessSlots.AcquireAsync(_directory, 2).WaitAsync(Bound);
        var second = await FuzzProcessSlots.AcquireAsync(_directory, 2).WaitAsync(Bound);
        var third = FuzzProcessSlots.AcquireAsync(_directory, 2);

        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.False(third.IsCompleted, "a third run started while both slots were held");

        second.Dispose();
        var held = await third.WaitAsync(Bound);
        held.Dispose();
        first.Dispose();
    }

    [Fact]
    public async Task Each_holder_gets_its_own_slot()
    {
        var holders = new List<IDisposable>();
        for (var index = 0; index < 3; index++) holders.Add(await FuzzProcessSlots.AcquireAsync(_directory, 3).WaitAsync(Bound));

        var waiting = FuzzProcessSlots.AcquireAsync(_directory, 3);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.False(waiting.IsCompleted, "more holders than slots");

        foreach (var holder in holders) holder.Dispose();
        (await waiting.WaitAsync(Bound)).Dispose();
    }

    [Fact]
    public async Task Without_settings_there_is_no_shared_limit()
    {
        Assert.Null(FuzzProcessSlots.FromEnvironment());
        Assert.Null(await FuzzProcessSlots.AcquireAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
