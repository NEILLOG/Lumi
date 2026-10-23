using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Views;

/// <summary>
/// 工作台視窗的位置與大小記憶（M9 9D）：開啟時還原、移動或縮放時節流寫檔、關閉時再寫一次。
/// <para>
/// 記的是「正常狀態」的位置與大小；最大化時只把 <see cref="WindowSettings.Maximized"/> 設成 true，位置與大小沿用最後一次正常狀態的值，
/// 不會把最大化的尺寸寫進去。最小化與隱藏（小精靈模式）時不記、不寫。
/// 還原前（<see cref="_armed"/> 為 false）發生的事件一律忽略，避免把預設置中的位置當成使用者的設定寫回去。
/// </para>
/// </summary>
internal sealed class WorkbenchWindowMemory
{
    /// <summary>關閉時等待寫檔的上限，超過就放棄等待（寫入仍會在背景完成）。</summary>
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly IModelCatalog _settings;
    private readonly ILogger? _logger;
    private readonly WindowMemoryRecorder _recorder;
    private WindowSettings? _normal;
    private bool _armed;

    public WorkbenchWindowMemory(Window window, IModelCatalog settings, TimeProvider time, ILogger? logger)
    {
        _window = window;
        _settings = settings;
        _logger = logger;
        _recorder = new WindowMemoryRecorder(time, value => settings.SetWorkbenchWindowAsync(value), logger, settings.Current.WorkbenchWindow?.Normalize());

        window.PositionChanged += (_, _) => OnGeometryChanged();
        window.Resized += (_, _) => OnGeometryChanged();
        // 大小也可能只在排版時才改變（Bounds），所以一起接；狀態變了也要記（最大化旗標、還原後的位置）
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Layoutable.BoundsProperty)
                OnGeometryChanged();
        };
    }

    /// <summary>視窗第一次開啟時呼叫：有可用的儲存值就還原，否則維持預設（置中）。</summary>
    public void RestoreOnOpen()
    {
        try
        {
            var screens = _window.Screens.All.Select(s => new WindowScreen(s.WorkingArea, s.Scaling)).ToList();
            var placement = WindowPlacementRules.Restore(_settings.Current.WorkbenchWindow, screens, _window.MinWidth, _window.MinHeight);
            if (placement is not null)
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Position = placement.Position;
                _window.Width = placement.Width;
                _window.Height = placement.Height;
                // 先記下「還原後的正常狀態」（用剛設定的值，不讀 ClientSize：排版前可能還是舊的大小），之後最大化時才有正確的位置與大小可寫
                _normal = new WindowSettings(placement.Position.X, placement.Position.Y, placement.Width, placement.Height, false, _window.RenderScaling);
                if (placement.Maximized)
                    _window.WindowState = WindowState.Maximized;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "還原工作台視窗位置失敗，使用預設位置");
        }
        finally
        {
            _armed = true;
        }
    }

    /// <summary>關閉視窗（<see cref="Window.Closing"/>）時呼叫：把最後的狀態寫進去，等待有上限，不讓關閉被拖慢太久。</summary>
    public void OnClosing()
    {
        if (!_armed)
            return;
        try
        {
            _recorder.FlushAsync(CurrentSnapshot()).Wait(FlushTimeout);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "關閉時儲存工作台視窗位置失敗");
        }
    }

    private void OnGeometryChanged()
    {
        if (!_armed || !_window.IsVisible)
            return;

        var snapshot = CurrentSnapshot();
        if (snapshot is not null)
            _recorder.Changed(snapshot);
    }

    /// <summary>目前應該記下的值：正常狀態＝實際的位置與大小；最大化＝上次的正常值加上旗標；最小化或沒有正常值＝null（不記）。</summary>
    private WindowSettings? CurrentSnapshot()
    {
        switch (_window.WindowState)
        {
            case WindowState.Normal:
                _normal = Capture(_window);
                return _normal;
            case WindowState.Maximized:
                return _normal is null ? null : _normal with { Maximized = true };
            default:
                return null;   // Minimized、FullScreen：不記
        }
    }

    private static WindowSettings Capture(Window window)
    {
        var position = window.Position;
        var size = window.ClientSize;
        return new WindowSettings(position.X, position.Y, size.Width, size.Height, false, window.RenderScaling);
    }
}
