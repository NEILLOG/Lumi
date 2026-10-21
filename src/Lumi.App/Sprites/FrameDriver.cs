using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.App.Sprites;

/// <summary>
/// 動畫時鐘，統一驅動角色的 <c>Advance</c>（角色自己不開計時器，agent.md 第 13 節）。
/// 目標 ≥ 50fps 時跟隨螢幕刷新（RequestAnimationFrame）；較低的幀率（待機 30、休眠 12）改用 DispatcherTimer，
/// 讓程式真正睡著以省電。來自 M0 的 POC。
/// </summary>
public sealed class FrameDriver
{
    private const int MinFps = 1;
    private const int MaxFps = 120;
    private const int RefreshFpsThreshold = 50;

    private readonly TopLevel _top;
    private readonly Action<TimeSpan> _tick;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private TimeSpan _last;
    private bool _running;
    private bool _frameRequested;
    private int _targetFps = 60;

    public FrameDriver(TopLevel top, Action<TimeSpan> tick)
    {
        _top = top;
        _tick = tick;
        _timer.Tick += (_, _) => Step();
    }

    public int Frames { get; private set; }

    public int TargetFps
    {
        get => _targetFps;
        set
        {
            value = Math.Clamp(value, MinFps, MaxFps);
            if (value == _targetFps)
                return;
            _targetFps = value;
            if (_running)
                Schedule();
        }
    }

    private bool UseRefreshRate => _targetFps >= RefreshFpsThreshold;

    public void Start()
    {
        _running = true;
        _last = _clock.Elapsed;
        Schedule();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
    }

    private void Schedule()
    {
        if (UseRefreshRate)
        {
            _timer.Stop();
            RequestFrame();
        }
        else
        {
            _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / _targetFps);
            if (!_timer.IsEnabled)
                _timer.Start();
        }
    }

    private void RequestFrame()
    {
        if (_frameRequested)
            return;
        _frameRequested = true;
        _top.RequestAnimationFrame(_ =>
        {
            _frameRequested = false;
            if (!_running || !UseRefreshRate)
                return;
            Step();
            RequestFrame();
        });
    }

    private void Step()
    {
        var now = _clock.Elapsed;
        var dt = now - _last;
        _last = now;
        _tick(dt);
        Frames++;
    }
}
