using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Resources;

namespace Lumi.App.Controls;

/// <summary>
/// 可綁定、可選取的文字：<see cref="Text"/> 變更時經過 <see cref="EmojiText.Apply"/>，
/// 讓訊息列表直接用 binding，不用 code-behind。所有顯示對話內容的地方都應使用它。
/// 右鍵選單的「複製」：外層有跨段落選取（<see cref="ITextSelectionScope"/>）就複製那段，否則有選取就複製選取的文字，沒有就複製整段。
/// 放在按鈕裡的文字要設 <c>IsHitTestVisible="False"</c>，讓點擊交給按鈕。
/// </summary>
public class EmojiTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    private bool _applying;

    public EmojiTextBlock()
    {
        // 沒有背景的 TextBlock 不會被滑鼠命中測試，選取與點連結都收不到按下事件，所以給透明背景
        Background = Brushes.Transparent;
        // 點一下就能選取與 Ctrl+C，但不放進 Tab 順序（每則訊息都停一下會很吵）
        IsTabStop = false;

        var copy = new MenuItem { Header = Strings.Workbench_Copy };
        copy.Click += (_, _) => CopyTextOrSelection();
        ContextMenu = new ContextMenu { Items = { copy } };
    }

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

    /// <summary>整段純文字：用 <see cref="Text"/>；Markdown 段落是用 <see cref="Inlines"/> 組的，要從行內元素拼回來。</summary>
    private string PlainText()
    {
        if (!string.IsNullOrEmpty(Text) || Inlines is null)
            return Text ?? "";
        return string.Concat(Inlines.Select(i => i switch
        {
            Run run => run.Text,
            LineBreak => "\n",
            _ => "",
        }));
    }

    private async void CopyTextOrSelection()
    {
        var scoped = this.FindAncestorOfType<ITextSelectionScope>()?.SelectedText;
        var text = !string.IsNullOrEmpty(scoped) ? scoped
            : string.IsNullOrEmpty(SelectedText) ? PlainText() : SelectedText;
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // 剪貼簿被其他程式佔用時放棄這次複製；async void 的例外不能讓程式結束
        }
    }
}
