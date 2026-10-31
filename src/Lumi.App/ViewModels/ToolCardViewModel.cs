using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools;

namespace Lumi.App.ViewModels;

public enum ToolCardState { Running, AwaitingApproval, Succeeded, Failed, Denied }

/// <summary>工具卡片的一張圖片（22F）：完整路徑、檔名，以及存在與否（不存在時畫面顯示「檔案已移動或刪除」）。</summary>
public sealed record ToolCardImage(string FilePath, string FileName, bool Exists);

/// <summary>
/// 一次工具呼叫的卡片（ui-spec 第 4、5 節）。標題的資料都來自事件或存檔的結果區塊（動作＝工具的顯示名稱、
/// 對象＝<see cref="ToolPreview.Target"/>、摘要＝<see cref="ToolResult.Summary"/>），畫面不猜。
/// </summary>
public partial class ToolCardViewModel : ObservableObject
{
    /// <summary>結果超過這麼多行就只顯示前段。</summary>
    public const int PreviewLines = 6;

    /// <summary>建立排程的工具名稱（M24，<c>create_scheduled_prompt</c>）：卡片看到它就換成排程確認區。</summary>
    private const string CreateScheduleToolName = "create_scheduled_prompt";

    /// <summary>排程紀錄保留的次數（與 <c>CreateScheduledPromptTool</c> 給模型的說明一致）。</summary>
    private const int ScheduleRecordLimit = 30;

    private readonly IToolApprovalResponder _responder;
    private readonly IClipboardService _clipboard;
    private readonly IConfirmService? _confirm;
    private readonly string _argumentsJson;
    private readonly TimeProvider _time;

    /// <summary>
    /// 這張卡在排程紀錄檢視裡（M24）：確認區的「本次對話都允許」換成「以後都允許（加入排程授權）」。
    /// 由建立卡片的人在建立時設定。
    /// </summary>
    public bool IsScheduledRun { get; init; }

    /// <summary>
    /// 排程紀錄裡的確認屬於排除類（記憶、上傳檔案、等你登入、管理排程、沒出現過的網站，M24 卡點 5）：
    /// 隱藏「以後都允許」，說明改成排除類的文字。只對 <see cref="IsScheduledRun"/> 的卡片有意義。
    /// </summary>
    public bool ScheduleExcluded { get; set; }

    /// <param name="confirm">
    /// 破壞性操作的「以後都允許」要先確認（M24 2.2）。沒有傳就不提供這個按鈕（安全的預設）。
    /// </param>
    public ToolCardViewModel(
        string callId, string toolName, string displayName, ToolPermission? permission, string? target, string argumentsJson,
        IToolApprovalResponder responder, IClipboardService clipboard, TimeProvider time, IConfirmService? confirm = null)
    {
        CallId = callId;
        ToolName = toolName;
        _displayName = displayName;
        Permission = permission;
        _target = target;
        ArgumentsText = PrettyJson(argumentsJson);
        _argumentsJson = argumentsJson;
        if (toolName == ShowDetailsTool.Name)
            (DetailsTitle, DetailsMarkdown) = ParseDetails(argumentsJson);
        _responder = responder;
        _clipboard = clipboard;
        _confirm = confirm;
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
        nameof(ShowDetailsCard), nameof(ShowRegularCard), nameof(ShowGenericConfirm), nameof(ShowScheduleConfirm),
        nameof(ShowAwaitingNote), nameof(AwaitingNote))]
    private ToolCardState _state = ToolCardState.Running;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Title))]
    private string? _summary;

    /// <summary>執行中的進度文字（M19 D12）：只給畫面顯示，不存檔；等待確認或完成時清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Title))]
    private string? _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultDisplay), nameof(HasMoreResult), nameof(HasResult))]
    private string _resultText = "";

    /// <summary>工具產生的圖片（22F）：只給畫面顯示，點了用系統的看圖程式開啟；檔案不在了就標成遺失。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImages))]
    private IReadOnlyList<ToolCardImage> _images = [];

    public bool HasImages => Images.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(HasDuration))]
    private long _durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApprovalNote), nameof(HasApprovalNote))]
    private ToolApproval _approval = ToolApproval.NotRequired;

    /// <summary>等待確認時的請求內容（預覽、權限等級）；之後清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDetail), nameof(HasPreviewDetail), nameof(ShowAllowForConversation), nameof(IsDestructive), nameof(PermissionNote),
        nameof(Warnings), nameof(HasWarnings), nameof(ShowGenericConfirm), nameof(ShowScheduleConfirm), nameof(ShowAwaitingNote),
        nameof(AwaitingNote), nameof(ScheduleName), nameof(ScheduleWhen), nameof(SchedulePrompt), nameof(ScheduleModel), nameof(ScheduleScope),
        nameof(WriteSuggested), nameof(NetworkSuggested))]
    private ToolApprovalRequest? _request;

    // ---- 建立排程的確認區（M24 4.4，草圖 ScheduleCreate）：授權勾選，預設照 Lumi 的建議 ----

    [ObservableProperty]
    private bool _scheduleWrite;

    [ObservableProperty]
    private bool _scheduleNetwork;

    /// <summary>破壞性操作：永遠不會因為建議而預先勾選（M24 2.2）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScheduleDestructiveNote))]
    private bool _scheduleDestructive;

    /// <summary>建立成功後的摘要（下次執行、實際授權）；只有建立排程的卡片成功時才有，解析不了就是 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasScheduleSummary))]
    private string? _scheduleSummary;

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
        ToolCardState.Running => string.IsNullOrWhiteSpace(Progress) ? Strings.Tool_Running : Progress,
        ToolCardState.AwaitingApproval => Strings.Tool_Awaiting,
        ToolCardState.Denied => Strings.Tool_Denied,
        ToolCardState.Failed => string.IsNullOrWhiteSpace(Summary) ? Strings.Tool_Failed : Summary,
        _ => string.IsNullOrWhiteSpace(Summary) ? Strings.Tool_Done : Summary,
    };

    /// <summary>動作 · 對象 · 結果摘要。建立排程的卡片用排程的標題（草圖 ScheduleCreate）。</summary>
    public string Title => ScheduleCardTitle ?? (HasTarget ? $"{DisplayName} · {Target} · {StatusText}" : $"{DisplayName} · {StatusText}");

    private string? ScheduleCardTitle
    {
        get
        {
            if (!IsCreateSchedule)
                return null;
            var name = string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Create_TitleFormat, Target ?? "");
            return State switch
            {
                ToolCardState.AwaitingApproval => $"{name} · {Strings.Schedule_Create_Waiting}",
                ToolCardState.Succeeded => $"{name} · {Strings.Schedule_Create_Done}",
                ToolCardState.Denied => name,
                _ => null,
            };
        }
    }

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
        ToolApproval.AllowedBySchedule => Strings.Schedule_ApprovedBySchedule,
        ToolApproval.Denied => IsCreateSchedule ? Strings.Schedule_Create_Denied : Strings.Tool_Denied,
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
    /// 請求說不提供（<see cref="ToolApprovalRequest.AllowRemember"/> 為 false，M17 搜尋引擎網址）時也不提供。
    /// 排程紀錄（<see cref="IsScheduledRun"/>）改成「以後都允許」：多了 Destructive（要先確認），排除類隱藏（M24 4.3）。</summary>
    public bool ShowAllowForConversation => Request is { AllowRemember: true } request && (IsScheduledRun
        ? !ScheduleExcluded && (request.Permission is ToolPermission.Write or ToolPermission.Network
            || (request.Permission == ToolPermission.Destructive && _confirm is not null))
        : request.Permission is ToolPermission.Write or ToolPermission.Network);

    /// <summary>排程紀錄的按鈕文字：「以後都允許（加入排程授權）」；一般卡片仍是「本次對話都允許」。回答的值一樣是 AllowForConversation。</summary>
    public string AllowConversationLabel => IsScheduledRun ? Strings.Schedule_AllowAlways : Strings.Tool_AllowConversation;

    /// <summary>排程紀錄的確認區上方說明（M24 4.3）：沒有授權的權限名稱，或排除類。</summary>
    public bool ShowAwaitingNote => IsScheduledRun && IsAwaiting;

    public string AwaitingNote => ScheduleExcluded
        ? Strings.Schedule_AwaitingNoteExcluded
        : string.Format(CultureInfo.CurrentCulture, Strings.Schedule_AwaitingNoteFormat, PermissionLabel);

    private string PermissionLabel => Request?.Permission switch
    {
        ToolPermission.Write => Strings.Schedule_Perm_Write,
        ToolPermission.Network => Strings.Schedule_Perm_Network,
        ToolPermission.Destructive => Strings.Schedule_Perm_Destructive,
        _ => Strings.Schedule_Perm_ReadOnly,
    };

    /// <summary>建立排程（<c>create_scheduled_prompt</c>）的卡片。</summary>
    public bool IsCreateSchedule => ToolName == CreateScheduleToolName;

    /// <summary>等待確認、而且請求帶著排程內容：換成排程確認區。</summary>
    public bool ShowScheduleConfirm => IsAwaiting && Request?.Schedule is not null;

    /// <summary>一般的確認區（沒有排程內容）。</summary>
    public bool ShowGenericConfirm => IsAwaiting && Request?.Schedule is null;

    public string ScheduleName => Request?.Schedule?.Name ?? "";

    /// <summary>時間，後面接下次執行。</summary>
    public string ScheduleWhen
    {
        get
        {
            var schedule = Request?.Schedule;
            if (schedule is null)
                return "";
            return string.IsNullOrEmpty(schedule.NextRunText)
                ? schedule.WhenText
                : $"{schedule.WhenText}（{string.Format(CultureInfo.CurrentCulture, Strings.Schedule_NextFormat, schedule.NextRunText)}）";
        }
    }

    public string SchedulePrompt => Request?.Schedule?.Prompt ?? "";

    public string ScheduleModel => Request?.Schedule?.ModelText ?? "";

    public string ScheduleRecords => string.Format(CultureInfo.CurrentCulture, Strings.Schedule_RecordsValueFormat, ScheduleRecordLimit);

    public string ScheduleScope => Request?.Schedule is { } schedule && schedule.ScopeNames.Count > 0
        ? string.Join("、", schedule.ScopeNames)
        : Strings.Schedule_ScopeNone;

    public bool WriteSuggested => Request?.Schedule?.SuggestedWrite == true;

    public bool NetworkSuggested => Request?.Schedule?.SuggestedNetwork == true;

    public string ScheduleDestructiveNote => ScheduleDestructive ? Strings.Schedule_Grant_DestructiveOnNote : Strings.Schedule_Grant_DestructiveNote;

    public bool HasScheduleSummary => ScheduleSummary is { Length: > 0 };

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
        Progress = null;
        Request = request;
        // 建立排程：預先勾選模型的建議（寫入、網路）；破壞性永遠不預先勾（M24 2.2）
        ScheduleWrite = request.Schedule?.SuggestedWrite == true;
        ScheduleNetwork = request.Schedule?.SuggestedNetwork == true;
        ScheduleDestructive = false;
        State = ToolCardState.AwaitingApproval;
    }

    /// <summary>執行中換成最新的進度文字（M19 D12）；只在執行中有效，等待確認與結束後不覆蓋。</summary>
    public void SetProgress(string text)
    {
        if (State == ToolCardState.Running)
            Progress = text;
    }

    /// <summary>套用最終結果（來自 <see cref="ToolCallCompleted"/> 或存檔的結果區塊）。</summary>
    public void Complete(ToolResultBlock result)
    {
        if (!string.IsNullOrEmpty(result.DisplayName))
            DisplayName = result.DisplayName;
        Target ??= result.Target;
        Progress = null;
        Summary = result.Summary;
        ResultText = result.Content;
        DurationMs = result.DurationMs;
        Approval = result.Approval;
        Images = (result.ImageFiles ?? []).Select(p => new ToolCardImage(p, System.IO.Path.GetFileName(p), File.Exists(p))).ToList();
        ScheduleSummary = IsCreateSchedule && result is { IsError: false, Approval: not ToolApproval.Denied } ? CreatedSummary(result.Content) : null;
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
    private async Task AllowForConversationAsync()
    {
        if (!IsAwaiting)
            return;

        // 排程紀錄裡的破壞性操作「以後都允許」要再確認一次（M24 2.2）；沒有確認服務就不允許
        if (IsScheduledRun && IsDestructive)
        {
            if (_confirm is null)
                return;
            var ok = await _confirm.ConfirmDestructiveAsync(
                string.Format(CultureInfo.CurrentCulture, Strings.Schedule_AllowAlwaysDestructiveFormat, DisplayName),
                Strings.Schedule_AllowAlwaysDestructiveConfirm);
            if (!ok)
                return;
        }

        Decide(ApprovalDecision.AllowForConversation);
    }

    [RelayCommand]
    private void Deny() => Decide(ApprovalDecision.Deny);

    /// <summary>建立排程：把使用者勾選的授權寫回參數，允許執行（M24 4.4）。</summary>
    [RelayCommand]
    private void CreateSchedule()
    {
        if (Request?.Schedule is null)
            return;
        Decide(new ApprovalAnswer(ApprovalDecision.AllowOnce, AmendedScheduleArguments()));
    }

    private void Decide(ApprovalAnswer answer)
    {
        if (!IsAwaiting)
            return;
        ApplyDecision(answer.Decision);
        _responder.Resolve(CallId, answer);
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

        // 排程紀錄的「以後都允許」由排程的權限分流處理，這一次在畫面上仍是「允許一次」
        Approval = decision == ApprovalDecision.AllowForConversation && Request?.Permission != ToolPermission.Destructive && !IsScheduledRun
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

    /// <summary>
    /// 建立排程的參數：以模型原本的參數為底，<c>suggested_permissions</c> 改成使用者勾的寫入、網路，
    /// <c>granted_permissions</c> 一定加上（沒勾任何也是空陣列）。存檔的 <c>tool_use</c> 不變。
    /// </summary>
    private string AmendedScheduleArguments()
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(_argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var suggested = new JsonArray();
        var granted = new JsonArray();
        if (ScheduleWrite)
        {
            suggested.Add("write");
            granted.Add("write");
        }
        if (ScheduleNetwork)
        {
            suggested.Add("network");
            granted.Add("network");
        }
        if (ScheduleDestructive)
            granted.Add("destructive");

        root["suggested_permissions"] = suggested;
        root[ScheduleToolFields.GrantedPermissions] = granted;
        return root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>
    /// 建立成功的結果（<c>CreateScheduledPromptTool</c> 的 <c>CreatedSchedule</c>，欄位是 snake_case）→
    /// 「下次執行：… · 執行時不再詢問：…」。解析失敗回 null，畫面只顯示結果摘要。
    /// </summary>
    private static string? CreatedSummary(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("granted", out var granted) || granted.ValueKind != JsonValueKind.Array)
                return null;

            var next = ReadString(root, "next_run_at_local") ?? ReadString(root, "next_run_at");
            if (next is null)
                return null;

            var names = granted.EnumerateArray()
                .Select(g => g.ValueKind == JsonValueKind.String ? GrantLabel(g.GetString()) : null)
                .OfType<string>()
                .ToList();
            var shown = names.Count > 0 ? string.Join(Strings.Schedule_GrantListSeparator, names) : Strings.Schedule_Perm_ReadOnly;
            return string.Format(CultureInfo.CurrentCulture, Strings.Schedule_Created_NextFormat, next, shown);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static string? GrantLabel(string? name) => name switch
    {
        "read" => Strings.Schedule_Perm_ReadOnly,
        "write" => Strings.Schedule_Perm_Write,
        "network" => Strings.Schedule_Perm_Network,
        "destructive" => Strings.Schedule_Perm_Destructive,
        _ => null,
    };

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
