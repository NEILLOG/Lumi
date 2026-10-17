using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Controls;

/// <summary>
/// 可綁定的 TextBlock：<see cref="Text"/> 變更時經過 <see cref="EmojiText.Apply"/>，
/// 讓訊息列表直接用 binding，不用 code-behind。所有顯示對話內容的地方都應使用它。
/// </summary>
public class EmojiTextBlock : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    private bool _applying;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !_applying)
        {
            _applying = true;
            try { EmojiText.Apply(this, change.GetNewValue<string?>()); }
            finally { _applying = false; }
        }
    }
}
