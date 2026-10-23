using Lumi.App.Tests.TestSupport;
using Lumi.App.Views;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Views;

/// <summary>M9 9D：寫檔節流（假計時）。連續變動只寫一次、關閉時一定寫最後的值。</summary>
public class WindowMemoryRecorderTests
{
    private sealed class Rig
    {
        public ManualTimeProvider Time { get; } = new();
        public List<WindowSettings> Saved { get; } = [];
        public WindowMemoryRecorder Recorder { get; }

        public Rig(WindowSettings? initial = null, Func<WindowSettings, Task>? save = null)
        {
            Recorder = new WindowMemoryRecorder(Time, save ?? (v => { Saved.Add(v); return Task.CompletedTask; }),
                NullLogger.Instance, initial);
        }
    }

    private static WindowSettings At(int x) => new(x, 100, 1040, 760, false, 1.0);

    [Fact]
    public void BurstOfChanges_WritesOnlyOnce_WithTheLatestValue()
    {
        var rig = new Rig();

        rig.Recorder.Changed(At(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Recorder.Changed(At(2));
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Recorder.Changed(At(3));
        Assert.Empty(rig.Saved);                                     // 還在節流時間內

        rig.Time.Advance(TimeSpan.FromMilliseconds(300));            // 第一次變動後 500 ms

        Assert.Equal([At(3)], rig.Saved);
    }

    [Fact]
    public void ChangesOverTime_WriteOncePerThrottleWindow()
    {
        var rig = new Rig();

        for (var i = 0; i < 10; i++)
        {
            rig.Recorder.Changed(At(i));
            rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        }
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));

        // 拖曳 1 秒內最多寫 2～3 次，不是 10 次
        Assert.True(rig.Saved.Count is >= 1 and <= 3, $"寫了 {rig.Saved.Count} 次");
        Assert.Equal(At(9), rig.Saved[^1]);
    }

    [Fact]
    public void SameValueAgain_IsNotWrittenAgain()
    {
        var rig = new Rig();

        rig.Recorder.Changed(At(5));
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));
        rig.Recorder.Changed(At(5));
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Single(rig.Saved);
    }

    [Fact]
    public void ValueEqualToTheInitialStoredValue_IsNotWritten()
    {
        var rig = new Rig(initial: At(5));

        rig.Recorder.Changed(At(5));
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Empty(rig.Saved);
    }

    [Fact]
    public async Task Flush_WritesTheFinalValue_Immediately_AndCancelsThePendingTimer()
    {
        var rig = new Rig();
        rig.Recorder.Changed(At(1));

        await rig.Recorder.FlushAsync(At(9));

        Assert.Equal([At(9)], rig.Saved);
        rig.Time.Advance(TimeSpan.FromSeconds(2));                   // 計時器已取消，不會再寫一次
        Assert.Single(rig.Saved);
    }

    [Fact]
    public async Task Flush_WithoutFinalValue_WritesWhatIsPending()
    {
        var rig = new Rig();
        rig.Recorder.Changed(At(4));

        await rig.Recorder.FlushAsync();

        Assert.Equal([At(4)], rig.Saved);
    }

    [Fact]
    public async Task Flush_AfterClose_WritesEvenIfTheThrottleWouldStillBeWaiting()
    {
        var rig = new Rig();
        rig.Recorder.Changed(At(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(100));
        rig.Recorder.Changed(At(2));

        await rig.Recorder.FlushAsync(At(2));                        // 關閉：最後的值一定寫入（不等 500 ms）

        Assert.Equal([At(2)], rig.Saved);
    }

    [Fact]
    public async Task SaveFailure_IsSwallowed_AndTheNextChangeStillWrites()
    {
        var calls = 0;
        var rig = new Rig(save: _ =>
        {
            calls++;
            if (calls == 1)
                throw new IOException("磁碟滿了");
            return Task.CompletedTask;
        });

        rig.Recorder.Changed(At(1));
        rig.Time.Advance(TimeSpan.FromMilliseconds(500));            // 第一次寫入失敗，不丟例外
        await rig.Recorder.FlushAsync(At(2));

        Assert.Equal(2, calls);
    }
}
