using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.OrbPoc.Diagnostics;

/// <summary>
/// 動畫時鐘。目標 ≥ 50fps 時跟隨螢幕刷新（RequestAnimationFrame）；
/// 較低幀率（待機 30、休眠 12）改用 DispatcherTimer，讓程式真正睡著以省電。
/// </summary>
public sealed class FrameDriver
{
    readonly TopLevel _top;
    readonly Action<double> _tick;
    readonly Stopwatch _sw = Stopwatch.StartNew();
    readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    double _last;
    bool _running, _rafPending;
    int _targetFps = 60;

    public int Frames { get; private set; }

    public FrameDriver(TopLevel top, Action<double> tick)
    {
        _top = top;
        _tick = tick;
        _timer.Tick += (_, _) => Step();
    }

    public int TargetFps
    {
        get => _targetFps;
        set
        {
            value = Math.Clamp(value, 1, 120);
            if (value == _targetFps) return;
            _targetFps = value;
            if (_running) Schedule();
        }
    }

    bool UseRaf => _targetFps >= 50;

    public void Start()
    {
        _running = true;
        _last = _sw.Elapsed.TotalSeconds;
        Schedule();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    void Schedule()
    {
        if (UseRaf)
        {
            _timer.Stop();
            RequestRaf();
        }
        else
        {
            _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
            if (!_timer.IsEnabled) _timer.Start();
        }
    }

    void RequestRaf()
    {
        if (_rafPending) return;
        _rafPending = true;
        _top.RequestAnimationFrame(_ =>
        {
            _rafPending = false;
            if (!_running || !UseRaf) return;
            Step();
            RequestRaf();
        });
    }

    void Step()
    {
        double now = _sw.Elapsed.TotalSeconds;
        double dt = now - _last;
        _last = now;
        _tick(dt);
        Frames++;
    }
}
