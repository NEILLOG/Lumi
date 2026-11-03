namespace Lumi.App.Sprites;

/// <summary>工作台裡的角色站在哪裡（M27）。</summary>
public enum WorkbenchSpriteSlot { None, Welcome, Toolbar }

/// <param name="Fits">工具列右側放不放得下；放不下就隱藏（plan/27 D1）。</param>
/// <param name="BoxLeft">角色框的左緣（視窗座標）。</param>
/// <param name="CapsuleReserve">模型選單那一欄右邊要讓出的寬度；放不下時是 0。</param>
public sealed record WorkbenchSpritePlacement(bool Fits, double BoxLeft, double CapsuleReserve);

/// <summary>工作台角色的版位與尺寸規則（純函式，plan/27 第 8.3 節）。</summary>
public static class WorkbenchSpriteRules
{
    /// <summary>工具列位置的尺寸檔位。不加進 <c>SpriteLimits.Tiers</c>：設定頁的尺寸選項不變。</summary>
    public const double ToolbarTier = 40;

    /// <summary>歡迎區的尺寸檔位。</summary>
    public const double WelcomeTier = 60;

    /// <summary>角色框右緣和「小精靈模式」按鈕左緣的間距；角色框左緣和模型選單的間距也是這個值。</summary>
    public const double Gap = 8;

    /// <summary>模型選單至少留的寬度，放得下「Anthropic · claude-haiku-5-5」（plan/27 D2）。</summary>
    public const double MinCapsuleWidth = 280;

    /// <summary>探頭時，頭頂以下露出多少。</summary>
    public const double PeekShow = 24;

    /// <summary>閒置多久才縮回去。</summary>
    public static readonly TimeSpan PeekDelay = TimeSpan.FromSeconds(10);

    public static WorkbenchSpriteSlot ResolveSlot(bool enabled, bool showWelcome, bool loading, bool viewingRun, bool toolbarFits, WorkbenchSpriteSlot previous)
    {
        // 關閉或正在看排程紀錄：不顯示（plan/27 第 8.3 節，由上而下）
        if (!enabled || viewingRun)
            return WorkbenchSpriteSlot.None;

        // 載入訊息中維持原位置，避免閃動
        if (loading)
            return previous;

        if (showWelcome)
            return WorkbenchSpriteSlot.Welcome;

        if (toolbarFits)
            return WorkbenchSpriteSlot.Toolbar;

        return WorkbenchSpriteSlot.None;
    }

    /// <param name="modeButtonLeft">「小精靈模式」按鈕的左緣（視窗座標）。</param>
    /// <param name="boxWidth">角色框的寬度（不含光暈）。</param>
    /// <param name="capsuleColumnLeft">模型選單那一欄的左緣（視窗座標）。</param>
    /// <param name="capsuleColumnRight">模型選單那一欄的右緣（視窗座標）。</param>
    public static WorkbenchSpritePlacement PlaceOnToolbar(double modeButtonLeft, double boxWidth, double capsuleColumnLeft, double capsuleColumnRight)
    {
        var boxRight = modeButtonLeft - Gap;
        var boxLeft = boxRight - boxWidth;
        var fits = boxLeft - Gap - capsuleColumnLeft >= MinCapsuleWidth && boxRight <= capsuleColumnRight;
        var reserve = fits ? Math.Max(0, capsuleColumnRight - (boxLeft - Gap)) : 0;
        return new WorkbenchSpritePlacement(fits, boxLeft, reserve);
    }

    /// <summary>探頭的下沉量：<paramref name="outerHeight"/> 是 <c>SpriteOptions.Outer.Height</c>，<paramref name="headTop"/> 是 <c>ISpriteView.BubbleAnchor.Y</c>。不會是負值。</summary>
    public static double PeekDepth(double outerHeight, double headTop) =>
        Math.Max(0, outerHeight - (headTop + PeekShow));
}
