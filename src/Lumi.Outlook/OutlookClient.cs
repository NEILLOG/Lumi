using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Lumi.Core.Outlook;
using Microsoft.Extensions.Logging;
using Interop = Microsoft.Office.Interop.Outlook;

namespace Lumi.Outlook;

/// <summary>
/// 使用者已登入的 classic Outlook（COM）。所有 COM 呼叫都經過 <see cref="StaWorker"/>，一次只做一個。
/// 不會啟動 Outlook：沒有在執行就回「請先開啟 Outlook」。
/// 信件與行程的代號只存在記憶體（<see cref="HandleTable"/>），重開程式後失效。
/// 這個檔案只放狀態、連線與共用的輔助；信件與行事曆的實作在 OutlookClient.Mail.cs、OutlookClient.Calendar.cs。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class OutlookClient : IOutlookClient, IDisposable
{
    private const string ProgId = "Outlook.Application";
    private const string HandleExpired = OutlookErrors.HandleExpired;
    private const string NoOwnAddress = "讀不到你自己的信箱地址，沒有寄出。";
    private const string CreateFailed = "Outlook 無法建立這個項目。";

    // COM 的 HResult：Outlook 程序已經結束（RPC 伺服器不可用、連線中斷）
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcDisconnected = unchecked((int)0x80010108);

    private const long ProgIdCacheMs = 60_000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<OutlookClient> _logger;
    private readonly StaWorker _worker = new(useSta: true);
    private readonly HandleTable _handles = new();

    // 以下由 _statusGate 保護（狀態查詢可能在任何執行緒）
    private readonly object _statusGate = new();
    private (bool Installed, long CheckedAt)? _progIdCache;
    private bool _disposed;

    // 以下只在 STA 執行緒上存取
    private Interop.Application? _app;
    private Interop.NameSpace? _ns;

    private volatile string? _ownAddress;

    public OutlookClient(ILogger<OutlookClient> logger)
    {
        _logger = logger;
    }

    public string? CachedOwnAddress => _ownAddress;

    /// <summary>不連線、不卡住：只看平台、ProgID（快取 1 分鐘）與 Outlook 有沒有在執行。</summary>
    public OutlookStatus GetStatus()
    {
        if (!OperatingSystem.IsWindows())
            return new OutlookStatus(OutlookState.NotSupported, NullOutlookClient.Message);
        if (!IsProgIdInstalled())
            return new OutlookStatus(OutlookState.NotInstalled, OutlookToolText.NotInstalled);
        if (!IsOutlookRunning())
            return new OutlookStatus(OutlookState.NotRunning, OutlookToolText.NotRunning);
        return new OutlookStatus(OutlookState.Available, null);
    }

    public void Dispose()
    {
        lock (_statusGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // 先在 STA 執行緒釋放連線的 COM 物件，完成後再關閉 worker
        _ = _worker.RunAsync(() => { ReleaseCached(); return true; }, TimeSpan.FromSeconds(5), CancellationToken.None)
            .ContinueWith(_ => _worker.Dispose(), TaskScheduler.Default);
    }

    private bool IsProgIdInstalled()
    {
        lock (_statusGate)
        {
            var now = Environment.TickCount64;
            if (_progIdCache is { } cached && now - cached.CheckedAt < ProgIdCacheMs)
                return cached.Installed;

            var installed = Type.GetTypeFromProgID(ProgId, throwOnError: false) is not null;
            _progIdCache = (installed, now);
            return installed;
        }
    }

    private static bool IsOutlookRunning()
    {
        var processes = Process.GetProcessesByName("OUTLOOK");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// 在 STA 執行緒上執行一次 COM 呼叫：連線、建立 <see cref="ComScope"/>、COM 錯誤轉成白話訊息。
    /// log 只記方法名稱與 HResult。
    /// </summary>
    private T Invoke<T>(string method, Func<OutlookCall, T> body)
    {
        using var scope = new ComScope();
        try
        {
            return body(Connect(scope));
        }
        catch (COMException ex)
        {
            if (ex.HResult is RpcServerUnavailable or RpcDisconnected)
                ReleaseCached();   // 下次呼叫重新連線

            _logger.LogWarning("Outlook 呼叫失敗：{Method}，HResult {HResult}", method, ex.HResult.ToString("X8", CultureInfo.InvariantCulture));
            throw OutlookErrors.FromHResult(ex.HResult, ex.Message);
        }
        catch (Exception ex) when (OutlookErrors.IsComponentLoadFailure(ex))
        {
            _logger.LogError(ex, "Outlook 元件載入失敗：{Method}", method);
            throw new OutlookException(OutlookErrors.ComponentLoadFailed);
        }
    }

    private Task<T> CallAsync<T>(string method, TimeSpan timeout, CancellationToken ct, Func<OutlookCall, T> body) =>
        _worker.RunAsync(() => Invoke(method, body), timeout, ct);

    /// <summary>連到執行中的 Outlook（只在 STA 執行緒上呼叫）。沒有在執行就丟錯誤，絕不啟動 Outlook。</summary>
    private OutlookCall Connect(ComScope scope)
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: false) ?? throw new OutlookException(OutlookToolText.NotInstalled);
        if (!IsOutlookRunning())
            throw new OutlookException(OutlookToolText.NotRunning);

        _app ??= (Interop.Application)Activator.CreateInstance(type)!;
        _ns ??= _app.GetNamespace("MAPI");

        var call = new OutlookCall(_app, _ns, scope);
        if (_ownAddress is null)
        {
            // 第一次成功連線時讀一次；讀不到不影響這次呼叫
            try
            {
                _ownAddress = ReadOwnAddress(call);
            }
            catch (COMException)
            {
            }
        }
        return call;
    }

    /// <summary>丟掉快取的 COM 物件（只在 STA 執行緒上呼叫）。</summary>
    private void ReleaseCached()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_ns is not null)
                Marshal.ReleaseComObject(_ns);
            if (_app is not null)
                Marshal.ReleaseComObject(_app);
        }
        _ns = null;
        _app = null;
    }

    /// <summary>一次 COM 呼叫可以用的東西。只在 STA 執行緒上使用。</summary>
    private sealed class OutlookCall(Interop.Application app, Interop.NameSpace nameSpace, ComScope scope)
    {
        public Interop.Application App { get; } = app;

        public Interop.NameSpace NameSpace { get; } = nameSpace;

        public ComScope Scope { get; } = scope;
    }

    /// <summary>
    /// 自己帳號的 SMTP 地址。是電子郵件地址才回傳，否則回 null。
    /// </summary>
    private static string? ReadOwnAddress(OutlookCall call)
    {
        var user = call.Scope.Track(call.NameSpace.CurrentUser);
        var entry = call.Scope.Track(user?.AddressEntry);
        var address = entry is null ? null : AddressOf(call, entry);
        return OutlookAddresses.IsEmailAddress(address) ? address : null;
    }

    /// <summary>Exchange 帳號用 SMTP 地址；其他用 Address。</summary>
    private static string? AddressOf(OutlookCall call, Interop.AddressEntry entry)
    {
        if (string.Equals(entry.Type, "EX", StringComparison.OrdinalIgnoreCase))
            return call.Scope.Track(entry.GetExchangeUser())?.PrimarySmtpAddress;
        return entry.Address;
    }

    /// <summary>「名稱 &lt;地址&gt;」；拿不到地址時只回名稱。</summary>
    private static string RecipientText(OutlookCall call, Interop.Recipient recipient)
    {
        var name = recipient.Name ?? "";
        var entry = call.Scope.Track(recipient.AddressEntry);
        var address = entry is null ? null : AddressOf(call, entry);
        if (string.IsNullOrEmpty(address))
            return name;
        if (name.Length == 0 || string.Equals(name, address, StringComparison.OrdinalIgnoreCase))
            return address;
        return $"{name} <{address}>";
    }

    /// <summary>加入收件人並設定類型（olTo／olCC／olBCC 或會議的 olRequired／olOptional）。</summary>
    private static void AddRecipients(
        OutlookCall call, Interop.Recipients recipients, IEnumerable<string> texts, int type, List<(string Text, Interop.Recipient Recipient)> added)
    {
        foreach (var text in texts)
        {
            var recipient = call.Scope.Track(recipients.Add(text));
            recipient.Type = type;
            added.Add((text, recipient));
        }
    }

    /// <summary>ResolveAll 失敗時丟錯誤（呼叫端負責丟掉這個項目）。訊息列出第一個解析不了的名稱。</summary>
    private static void EnsureResolved(Interop.Recipients recipients, List<(string Text, Interop.Recipient Recipient)> added)
    {
        if (recipients.ResolveAll())
            return;

        var missing = added.Where(a => !a.Recipient.Resolved).Select(a => a.Text).FirstOrDefault() ?? "";
        throw new OutlookException($"找不到收件人：{missing}");
    }

    private static void AddAttachments(OutlookCall call, Interop.MailItem item, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var attachments = call.Scope.Track(item.Attachments);
        foreach (var path in paths)
            call.Scope.Track(attachments.Add(path, Type.Missing, Type.Missing, Type.Missing));
    }

    /// <summary>建立的草稿或寄件還沒保存、沒有寄出：丟掉。</summary>
    private static void Discard(Interop.MailItem item)
    {
        try
        {
            item.Close(Interop.OlInspectorClose.olDiscard);
        }
        catch (COMException)
        {
            // 項目已經不在 Outlook 裡，不需要處理
        }
    }

    private static void Discard(Interop.AppointmentItem appointment)
    {
        try
        {
            appointment.Close(Interop.OlInspectorClose.olDiscard);
        }
        catch (COMException)
        {
            // 項目已經不在 Outlook 裡，不需要處理
        }
    }

    /// <summary>用代號取得 COM 物件（TryGet 失敗丟「已經失效」）。</summary>
    private object GetItemByHandle(OutlookCall call, string conversationId, string id, out string storeId)
    {
        if (!_handles.TryGet(conversationId, id, out var handle))
            throw new OutlookException(HandleExpired);

        storeId = handle.StoreId;
        return call.Scope.Track(call.NameSpace.GetItemFromID(handle.EntryId, handle.StoreId));
    }
}
