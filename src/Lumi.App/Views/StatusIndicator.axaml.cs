using Avalonia;
using Avalonia.Controls;
using Lumi.App.Services;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 連線狀態：狀態點＋文字（連線正常／連線失敗／尚未確認），不能只靠顏色。
/// <see cref="IsBadge"/> 為 true 時是 Soft 底的徽章（<c>Border.badge</c>），用在詳細區標頭；否則是清單卡片右側的點加 Caption。
/// </summary>
public partial class StatusIndicator : UserControl
{
    public static readonly StyledProperty<ConnectionStatus> StatusProperty =
        AvaloniaProperty.Register<StatusIndicator, ConnectionStatus>(nameof(Status));

    public static readonly StyledProperty<bool> IsBadgeProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(IsBadge));

    public static readonly DirectProperty<StatusIndicator, string> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<StatusIndicator, string>(nameof(StatusText), o => o.StatusText);

    private string _statusText = ConnectionStatusText.Of(ConnectionStatus.Unknown);

    public StatusIndicator()
    {
        InitializeComponent();
        Apply();
    }

    public ConnectionStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public bool IsBadge
    {
        get => GetValue(IsBadgeProperty);
        set => SetValue(IsBadgeProperty, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty || change.Property == IsBadgeProperty)
            Apply();
    }

    private void Apply()
    {
        StatusText = ConnectionStatusText.Of(Status);
        Pill.Classes.Set("ok", Status == ConnectionStatus.Ok);
        Pill.Classes.Set("failed", Status == ConnectionStatus.Failed);
        Pill.Classes.Set("badge", IsBadge);
    }
}
