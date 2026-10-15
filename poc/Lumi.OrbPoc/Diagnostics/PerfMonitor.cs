using System.Diagnostics;

namespace Lumi.OrbPoc.Diagnostics;

/// <summary>每秒取樣一次：實際 FPS、行程 CPU（單核 %）、30 秒平均、記憶體。</summary>
public sealed class PerfMonitor
{
    readonly Process _proc = Process.GetCurrentProcess();
    readonly Queue<double> _window = new();
    TimeSpan _lastCpu;
    DateTime _lastWall = DateTime.UtcNow;
    int _lastFrames;

    public double Fps { get; private set; }
    /// <summary>以單一核心為 100% 計算。</summary>
    public double CpuOneCore { get; private set; }
    public double CpuAvg30 { get; private set; }
    public double WorkingSetMb { get; private set; }

    public PerfMonitor() => _lastCpu = _proc.TotalProcessorTime;

    public void Sample(int frames)
    {
        _proc.Refresh();
        var now = DateTime.UtcNow;
        double wall = (now - _lastWall).TotalSeconds;
        if (wall <= 0) return;

        var cpu = _proc.TotalProcessorTime;
        CpuOneCore = (cpu - _lastCpu).TotalSeconds / wall * 100;
        Fps = (frames - _lastFrames) / wall;
        WorkingSetMb = _proc.WorkingSet64 / 1024.0 / 1024.0;

        _window.Enqueue(CpuOneCore);
        while (_window.Count > 30) _window.Dequeue();
        CpuAvg30 = _window.Average();

        _lastCpu = cpu;
        _lastWall = now;
        _lastFrames = frames;
    }

    public void ResetAverage() => _window.Clear();
}
