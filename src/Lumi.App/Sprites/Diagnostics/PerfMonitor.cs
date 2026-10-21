using System.Diagnostics;

namespace Lumi.App.Sprites.Diagnostics;

/// <summary>每秒取樣一次：實際 FPS、行程 CPU（單核 %）、30 秒平均、記憶體。補上 M0 還沒量的待機與休眠耗能。</summary>
public sealed class PerfMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Queue<double> _window = new();
    private TimeSpan _lastCpu;
    private DateTime _lastWall = DateTime.UtcNow;
    private int _lastFrames;

    public PerfMonitor() => _lastCpu = _process.TotalProcessorTime;

    public double Fps { get; private set; }

    /// <summary>以單一核心為 100% 計算。</summary>
    public double CpuOneCore { get; private set; }

    public double CpuAverage30 { get; private set; }

    public double WorkingSetMb { get; private set; }

    public void Sample(int frames)
    {
        _process.Refresh();
        var now = DateTime.UtcNow;
        var wall = (now - _lastWall).TotalSeconds;
        if (wall <= 0)
            return;

        var cpu = _process.TotalProcessorTime;
        CpuOneCore = (cpu - _lastCpu).TotalSeconds / wall * 100;
        Fps = (frames - _lastFrames) / wall;
        WorkingSetMb = _process.WorkingSet64 / 1024.0 / 1024.0;

        _window.Enqueue(CpuOneCore);
        while (_window.Count > 30)
            _window.Dequeue();
        CpuAverage30 = _window.Average();

        _lastCpu = cpu;
        _lastWall = now;
        _lastFrames = frames;
    }

    public void ResetAverage() => _window.Clear();
}
