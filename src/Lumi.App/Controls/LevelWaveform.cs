using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Lumi.App.Controls;

/// <summary>
/// 收音波形（ui-spec 6.3）：固定 <see cref="BarCount"/> 根長條，每次 <see cref="Level"/> 變更就往左推一根，最新的在最右。
/// 顏色只用 <c>LumiAccentBrush</c>（token）。不自己開計時器：畫面跟著 Level 屬性的變更重繪。
/// 這是純繪製元件：不可聚焦、不吃滑鼠。
/// </summary>
public sealed class LevelWaveform : Control
{
    public const int BarCount = 32;

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelWaveform, double>(nameof(Level));

    private readonly double[] _samples = new double[BarCount];

    static LevelWaveform()
    {
        LevelProperty.Changed.AddClassHandler<LevelWaveform>((waveform, e) => waveform.Push(e.GetNewValue<double>()));
    }

    /// <summary>目前的音量 0..1；每次變更推入一根新的長條。</summary>
    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>長條的高度（0..1），由舊到新；長度固定為 <see cref="BarCount"/>。</summary>
    public IReadOnlyList<double> Samples => _samples;

    private void Push(double level)
    {
        Array.Copy(_samples, 1, _samples, 0, BarCount - 1);
        _samples[BarCount - 1] = double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var brush = this.TryFindResource("LumiAccentBrush", ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        var slot = width / BarCount;
        var barWidth = Math.Max(2, slot * 0.6);
        var radius = barWidth / 2;

        for (var i = 0; i < BarCount; i++)
        {
            var barHeight = Math.Max(2, _samples[i] * height);
            var x = i * slot + (slot - barWidth) / 2;
            var y = (height - barHeight) / 2;
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, y, barWidth, barHeight), new CornerRadius(radius)));
        }
    }
}
