using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Views;

/// <summary>
/// 研究本視窗的位置與大小記憶（M35 35G）。結構照 <see cref="WorkbenchWindowMemory"/>，只是存在 <c>app_meta</c>（<see cref="IConversationStore"/>）
/// 而且讀寫是非同步的：<see cref="RestoreAsync"/> 要在視窗顯示前呼叫；還原前發生的事件一律忽略。
/// 移動或縮放後節流寫檔（<see cref="WindowMemoryRecorder"/>），關閉時再寫一次（<see cref="OnClosing"/>）。
/// </summary>
internal sealed class ContextNoteWindowMemory : IDisposable
{
    /// <summary><c>app_meta</c> 的鍵。</summary>
    public const string MetaKey = "context_note_window";

    /// <summary>關閉時等待寫檔的上限，超過就放棄等待（寫入仍會在背景完成）。</summary>
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Window _window;
    private readonly IConversationStore _conversations;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private WindowMemoryRecorder? _recorder;
    private WindowSettings? _normal;
    private bool _armed;

    public ContextNoteWindowMemory(Window window, IConversationStore conversations, TimeProvider time, ILogger? logger = null)
    {
        _window = window;
        _conversations = conversations;
        _time = time;
        _logger = logger;

        window.PositionChanged += (_, _) => OnGeometryChanged();
        window.Resized += (_, _) => OnGeometryChanged();
        // 大小也可能只在排版時才改變（Bounds），所以一起接；狀態變了也要記（最大化旗標、還原後的位置）
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Layoutable.BoundsProperty)
                OnGeometryChanged();
        };
    }

    /// <summary>視窗顯示前呼叫：有可用的儲存值就還原，否則維持預設（螢幕中央）。</summary>
    public async Task RestoreAsync()
    {
        WindowSettings? saved = null;
        try
        {
            var text = await _conversations.GetMetaAsync(MetaKey);
            if (text is not null)
                saved = JsonSerializer.Deserialize<WindowSettings>(text)?.Normalize();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "讀取研究本視窗位置失敗，使用預設位置");
        }

        try
        {
            var screens = _window.Screens.All.Select(s => new WindowScreen(s.WorkingArea, s.Scaling)).ToList();
            var placement = WindowPlacementRules.Restore(saved, screens, _window.MinWidth, _window.MinHeight);
            if (placement is not null)
            {
                _window.WindowStartupLocation = WindowStartupLocation.Manual;
                _window.Position = placement.Position;
                _window.Width = placement.Width;
                _window.Height = placement.Height;
                _normal = new WindowSettings(placement.Position.X, placement.Position.Y, placement.Width, placement.Height, false, _window.RenderScaling);
                if (placement.Maximized)
                    _window.WindowState = WindowState.Maximized;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "還原研究本視窗位置失敗，使用預設位置");
        }
        finally
        {
            _recorder = new WindowMemoryRecorder(_time, SaveAsync, _logger, saved);
            _armed = true;
        }
    }

    /// <summary>視窗關閉（<see cref="Window.Closing"/>）時呼叫：把最後的狀態寫進去，等待有上限。</summary>
    public void OnClosing()
    {
        if (!_armed || _recorder is null)
            return;
        try
        {
            _recorder.FlushAsync(CurrentSnapshot()).Wait(FlushTimeout);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "關閉時儲存研究本視窗位置失敗");
        }
    }

    public void Dispose() => _recorder?.Dispose();

    private Task SaveAsync(WindowSettings value) =>
        _conversations.SetMetaAsync(MetaKey, JsonSerializer.Serialize(value));

    private void OnGeometryChanged()
    {
        if (!_armed || _recorder is null || !_window.IsVisible)
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
                return null;
        }
    }

    private static WindowSettings Capture(Window window)
    {
        var position = window.Position;
        var size = window.ClientSize;
        return new WindowSettings(position.X, position.Y, size.Width, size.Height, false, window.RenderScaling);
    }
}
