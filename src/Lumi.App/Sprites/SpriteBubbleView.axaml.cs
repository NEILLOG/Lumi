using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Sprites;

public partial class SpriteBubbleView : UserControl
{
    /// <summary>氣泡陰影（<c>LumiSpriteShadow</c>：y 位移 8、模糊 24）往上超出的距離；氣泡上緣至少要離視窗頂端這麼遠。</summary>
    public const double ShadowTop = 16;

    private const double ShadowSide = 24;
    private const double ShadowBottom = 32;

    public SpriteBubbleView()
    {
        InitializeComponent();
        // 捲動區會切掉超出它的內容（含陰影）：向外擴一圈、內容再縮回來，氣泡的位置與大小不變
        BubbleScroll.Margin = new Thickness(-ShadowSide, -ShadowTop, -ShadowSide, -ShadowBottom);
        Root.Margin = new Thickness(ShadowSide, ShadowTop, ShadowSide, ShadowBottom);
        // 內容變動時停在最下面：最新的回答與按鈕在下方，貼著角色
        Root.SizeChanged += (_, _) => BubbleScroll.ScrollToEnd();
    }
}
