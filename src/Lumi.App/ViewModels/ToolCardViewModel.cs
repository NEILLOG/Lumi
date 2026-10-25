using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools;

namespace Lumi.App.ViewModels;

public enum ToolCardState { Running, AwaitingApproval, Succeeded, Failed, Denied }

/// <summary>
/// 一次工具呼叫的卡片（ui-spec 第 4、5 節）。標題的資料都來自事件或存檔的結果區塊（動作＝工具的顯示名稱、
/// 對象＝<see cref="ToolPreview.Target"/>、摘要＝<see cref="ToolResult.Summary"/>），畫面不猜。
/// </summary>
public partial class ToolCardViewModel : ObservableObject
{
    /// <summary>結果超過這麼多行就只顯示前段。</summary>
    public const int PreviewLines = 6;

    private readonly IToolApprovalResponder _responder;
    private readonly IClipboardService _clipboard;
    private readonly TimeProvider _time;

    public ToolCardViewModel(
        string callId, string toolName, string displayName, ToolPermission? permission, string? target, string argumentsJson,
        IToolApprovalResponder responder, IClipboardService clipboard, TimeProvider time)
    {
        CallId = callId;
        ToolName = toolName;
        _displayName = displayName;
        Permission = permission;
        _target = target;
        ArgumentsText = PrettyJson(argumentsJson);
        if (toolName == ShowDetailsTool.Name)
            (DetailsTitle, DetailsMarkdown) = ParseDetails(argumentsJson);
        _responder = responder;
        _clipboard = clipboard;
        _time = time;
    }

    public string CallId { get; }

    public string ToolName { get; }

    /// <summary>動作名稱。從歷史重建時先用工具名稱，套用結果區塊（存了顯示名稱）時更新。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(DetailsHeader))]
    private string _displayName;

    /// <summary>從歷史重建時不知道（沒存）；只有等待確認時需要。</summary>
    public ToolPermission? Permission { get; }

    public string ArgumentsText { get; }

    // ---- show_details（小精靈的完整內容）：畫成展開的內容卡，不是收合的工具卡 ----

    /// <summary>這張卡片是 <c>show_details</c>：內容在工具呼叫的輸入裡，重開程式也還在。</summary>
    public bool IsDetails => ToolName == ShowDetailsTool.Name;

    public string DetailsTitle { get; } = "";

    public string DetailsMarkdown { get; } = "";

    /// <summary>成功時才畫成內容卡；執行中、失敗（例如內容太長）、被拒絕仍用一般卡片，看得到參數與錯誤。</summary>
    public bool ShowDetailsCard => IsDetails && IsSucceeded && DetailsMarkdown.Length > 0;

    public bool ShowRegularCard => !ShowDetailsCard;

    /// <summary>內容卡的標題：「整理完整內容 · 標題」（字數另外顯示在右側）。</summary>
    public string DetailsHeader => HasTarget ? $"{DisplayName} · {Target}" : DisplayName;

    public string DetailsCharacters => string.Format(CultureInfo.CurrentCulture, Strings.Details_Characters, DetailsMarkdown.Length);

    private static (string Title, string Markdown) ParseDetails(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            string Read(string name) => doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim()
                : "";
            return (Read("title"), Read("markdown"));
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(HasTarget), nameof(DetailsHeader))]
    private string? _target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsAwaiting), nameof(IsSucceeded), nameof(IsFailed), nameof(IsDenied),
        nameof(IsForcedOpen), nameof(IsOpen), nameof(CanToggle), nameof(StatusText), nameof(Title),
        nameof(ShowDetailsCard), nameof(ShowRegularCard))]
    private ToolCardState _state = ToolCardState.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Title))]
    private string? _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultDisplay), nameof(HasMoreResult), nameof(HasResult))]
    private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(HasDuration))]
    private long _durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalNote), nameof(HasApprovalNote))]
    private ToolApproval _approval = ToolApproval.NotRequired;

    /// <summary>等待確認時的請求內容（預覽、權限等級）；之後清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDetail), nameof(HasPreviewDetail), nameof(ShowAllowForConversation), nameof(IsDestructive), nameof(PermissionNote),
        nameof(Warnings), nameof(HasWarnings))]
    private ToolApprovalRequest? _request;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultDisplay), nameof(ShowFullLabel))]
    private bool _resultShowsAll;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyLabel))]
    private bool _isCopied;

    public bool HasTarget => !string.IsNullOrEmpty(Target);

    public bool IsRunning => State == ToolCardState.Running;

    public bool IsAwaiting => State == ToolCardState.AwaitingApproval;

    public bool IsSucceeded => State == ToolCardState.Succeeded;

    public bool IsFailed => State == ToolCardState.Failed;

    public bool IsDenied => State == ToolCardState.Denied;

    /// <summary>執行中與等待允許的卡片強制展開、不能收合（ui-spec 第 4 節）。</summary>
    public bool IsForcedOpen => State is ToolCardState.Running or ToolCardState.AwaitingApproval;

    public bool IsOpen => IsForcedOpen || IsExpanded;

    public bool CanToggle => !IsForcedOpen;

    /// <summary>標題最後一段：結果摘要，或目前的狀態說明。</summary>
    public string StatusText => State switch
    {
        ToolCardState.Running => Strings.Tool_Running,
        ToolCardState.AwaitingApproval => Strings.Tool_Awaiting,
        ToolCardState.Denied => Strings.Tool_Denied,
        ToolCardState.Failed => string.IsNullOrWhiteSpace(Summary) ? Strings.Tool_Failed : Summary,
        _ => string.IsNullOrWhiteSpace(Summary) ? Strings.Tool_Done : Summary,
    };

    /// <summary>動作 · 對象 · 結果摘要。</summary>
    public string Title => HasTarget ? $"{DisplayName} · {Target} · {StatusText}" : $"{DisplayName} · {StatusText}";

    public bool HasDuration => DurationMs > 0 && State is ToolCardState.Succeeded or ToolCardState.Failed;

    public string DurationText => DurationMs < 1000
        ? string.Format(CultureInfo.CurrentCulture, Strings.Tool_DurationMs, DurationMs)
        : string.Format(CultureInfo.CurrentCulture, Strings.Tool_DurationSeconds, (DurationMs / 1000.0).ToString("0.#", CultureInfo.CurrentCulture));

    public bool HasApprovalNote => ApprovalNote.Length > 0;

    public string ApprovalNote => Approval switch
    {
        ToolApproval.AllowedOnce => Strings.Tool_ApprovedOnce,
        ToolApproval.AllowedForConversation => Strings.Tool_ApprovedConversation,
        ToolApproval.AllowedAlways => Strings.Tool_ApprovedAlways,
        ToolApproval.Denied => Strings.Tool_Denied,
        _ => "",
    };

    public bool HasResult => ResultText.Length > 0;

    private string[] ResultLines => ResultText.Split('\n');

    public bool HasMoreResult => ResultLines.Length > PreviewLines;

    public string ResultDisplay => ResultShowsAll || !HasMoreResult
        ? ResultText
        : string.Join('\n', ResultLines.Take(PreviewLines)) + "\n…";

    public string ShowFullLabel => ResultShowsAll ? Strings.Tool_ShowLess : Strings.Tool_ShowFull;

    public string CopyLabel => IsCopied ? Strings.Tool_Copied : Strings.Tool_Copy;

    // ---- 確認區 ----

    public bool HasPreviewDetail => !string.IsNullOrWhiteSpace(PreviewDetail);

    public string? PreviewDetail => Request?.Preview.Detail;

    public bool IsDestructive => Request?.Permission == ToolPermission.Destructive;

    /// <summary>確認區的警告（M17：沒有在對話中出現過的網站、搜尋引擎網址）；每則一列。</summary>
    public IReadOnlyList<string> Warnings => Request?.Warnings ?? [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>「本次對話都允許」只有 Write、Network 有；Destructive 每次都問（agent.md 第 8 節）。
    /// 請求說不提供（<see cref="ToolApprovalRequest.AllowRemember"/> 為 false，M17 搜尋引擎網址）時也不提供。</summary>
    public bool ShowAllowForConversation => Request is { AllowRemember: true } request
        && request.Permission is ToolPermission.Write or ToolPermission.Network;

    public string PermissionNote => Request?.Permission switch
    {
        ToolPermission.Write => Strings.Tool_NeedsWrite,
        ToolPermission.Network => Strings.Tool_NeedsNetwork,
        ToolPermission.Destructive => Strings.Tool_NeedsDestructive,
        _ => "",
    };

    // ---- 狀態轉換 ----

    public void SetAwaiting(ToolApprovalRequest request)
    {
        Request = request;
        State = ToolCardState.AwaitingApproval;
    }

    /// <summary>套用最終結果（來自 <see cref="ToolCallCompleted"/> 或存檔的結果區塊）。</summary>
    public void Complete(ToolResultBlock result)
    {
        if (!string.IsNullOrEmpty(result.DisplayName))
            DisplayName = result.DisplayName;
        Target ??= result.Target;
        Summary = result.Summary;
        ResultText = result.Content;
        DurationMs = result.DurationMs;
        Approval = result.Approval;
        Request = null;
        State = result.Approval == ToolApproval.Denied ? ToolCardState.Denied
            : result.IsError ? ToolCardState.Failed
            : ToolCardState.Succeeded;
    }

    /// <summary>從歷史重建時，呼叫沒有留下結果（程式在中途結束）。</summary>
    public void MarkNoResult()
    {
        Summary = Strings.Tool_NoResult;
        State = ToolCardState.Failed;
    }

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleFullOutput() => ResultShowsAll = !ResultShowsAll;

    [RelayCommand]
    private void AllowOnce() => Decide(ApprovalDecision.AllowOnce);

    [RelayCommand]
    private void AllowForConversation() => Decide(ApprovalDecision.AllowForConversation);

    [RelayCommand]
    private void Deny() => Decide(ApprovalDecision.Deny);

    private void Decide(ApprovalDecision decision)
    {
        if (!IsAwaiting)
            return;
        ApplyDecision(decision);
        _responder.Resolve(CallId, decision);
    }

    /// <summary>別處（例如小精靈的氣泡）回答了這張卡片的確認：只更新畫面，不再通知對話迴圈。</summary>
    public void ApplyResolved(ApprovalDecision decision)
    {
        if (IsAwaiting)
            ApplyDecision(decision);
    }

    private void ApplyDecision(ApprovalDecision decision)
    {
        if (decision == ApprovalDecision.Deny)
        {
            Approval = ToolApproval.Denied;
            Request = null;
            State = ToolCardState.Denied;
            return;
        }

        Approval = decision == ApprovalDecision.AllowForConversation && Request?.Permission != ToolPermission.Destructive
            ? ToolApproval.AllowedForConversation
            : ToolApproval.AllowedOnce;
        Request = null;
        State = ToolCardState.Running;
    }

    [RelayCommand]
    private async Task CopyResultAsync()
    {
        // 內容卡複製的是整份 Markdown，不是工具回給模型的那句確認
        var text = ShowDetailsCard ? DetailsMarkdown : ResultText;
        if (text.Length == 0)
            return;
        await _clipboard.SetTextAsync(text);
        IsCopied = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), _time);
        }
        finally
        {
            IsCopied = false;
        }
    }

    partial void OnStateChanged(ToolCardState value) => ToggleCommand.NotifyCanExecuteChanged();

    private static string PrettyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不要變成 \uXXXX
            });
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
