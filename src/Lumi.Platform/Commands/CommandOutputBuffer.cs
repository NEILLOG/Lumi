using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Platform.Commands;

/// <summary>
/// 指令輸出的緩衝（M31 設計 4）。stdout 與 stderr 依收到的順序寫進同一個緩衝。
/// 控制碼清除、<c>\r</c> 處理與截斷都在 <see cref="Build"/> 時做，因為 chunk 可能切在控制碼中間。
/// 原始輸出只留開頭 <see cref="RawHeadChars"/> 字與最後約 <see cref="RawTailChars"/> 字，中間的直接丟掉，
/// 所以指令輸出再多（例如印出一個大檔案）也不會讓 Lumi 的記憶體一直長大。
/// </summary>
internal sealed class CommandOutputBuffer
{
    /// <summary>超過這個字數才截斷。</summary>
    private const int MaxChars = 30_000;

    /// <summary>截斷時保留的開頭字數。</summary>
    internal const int HeadChars = 10_000;

    /// <summary>截斷時保留的結尾字數。</summary>
    internal const int TailChars = 20_000;

    /// <summary>原始輸出保留的開頭字數（清掉控制碼後仍要夠 <see cref="HeadChars"/>，所以留得比較多）。</summary>
    internal const int RawHeadChars = 40_000;

    /// <summary>原始輸出保留的結尾字數；超過兩倍時才從前面丟掉，避免每次 Append 都搬移。</summary>
    internal const int RawTailChars = 80_000;

    /// <summary>進度列最多幾字。</summary>
    private const int LatestLineMaxChars = 200;

    /// <summary>找最新一行時只看結尾這一段，避免每次都整份處理（進度回報每 500 毫秒最多一次）。</summary>
    private const int LatestLineWindowChars = 4_000;

    private static readonly Regex CsiPattern = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.CultureInvariant);
    private static readonly Regex OscPattern = new(@"\x1B\][^\x07]*\x07", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();
    private long _dropped;

    public void Append(string chunk)
    {
        lock (_gate)
        {
            var room = RawHeadChars - _head.Length;
            if (room > 0)
            {
                var take = Math.Min(room, chunk.Length);
                _head.Append(chunk, 0, take);
                chunk = chunk[take..];
            }
            if (chunk.Length == 0)
                return;

            _tail.Append(chunk);
            if (_tail.Length > RawTailChars * 2)
            {
                var remove = _tail.Length - RawTailChars;
                _tail.Remove(0, remove);
                _dropped += remove;
            }
        }
    }

    /// <summary>最新一行非空白的內容（已清掉控制碼、取進度列的最後一段），最多 200 字；沒有內容時是空字串。</summary>
    public string LatestLine
    {
        get
        {
            string tail;
            lock (_gate)
            {
                var source = _tail.Length > 0 ? _tail : _head;
                var start = Math.Max(0, source.Length - LatestLineWindowChars);
                tail = source.ToString(start, source.Length - start);
            }

            var lines = Clean(tail).Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (line.Length > 0)
                    return line.Length > LatestLineMaxChars ? line[..LatestLineMaxChars] : line;
            }
            return string.Empty;
        }
    }

    /// <summary>回傳清理過、必要時截斷的輸出。</summary>
    public (string Text, bool Truncated) Build()
    {
        string head, tail;
        long dropped;
        lock (_gate)
        {
            head = _head.ToString();
            tail = _tail.ToString();
            dropped = _dropped;
        }

        if (dropped == 0)
        {
            var whole = Clean(head + tail);
            if (whole.Length <= MaxChars)
                return (whole, false);
            return (Truncate(whole[..HeadChars], whole[^TailChars..], whole.Length - HeadChars - TailChars), true);
        }

        // 中間已經丟掉一段：開頭與結尾各自清理；省略的字數用清理前的長度估計
        var cleanedHead = Clean(head);
        var cleanedTail = Clean(tail);
        var shownHead = cleanedHead.Length > HeadChars ? cleanedHead[..HeadChars] : cleanedHead;
        var shownTail = cleanedTail.Length > TailChars ? cleanedTail[^TailChars..] : cleanedTail;
        var omittedApprox = head.Length + dropped + tail.Length - shownHead.Length - shownTail.Length;
        return (Truncate(shownHead, shownTail, omittedApprox), true);
    }

    private static string Truncate(string head, string tail, long omitted)
    {
        var marker = $"…（中間省略 {omitted.ToString("N0", CultureInfo.InvariantCulture)} 字）…";
        return head + "\n" + marker + "\n" + tail;
    }

    /// <summary>拿掉 ANSI 控制碼，<c>\r\n</c> 換成 <c>\n</c>，一行裡有 <c>\r</c> 時只留最後一個 <c>\r</c> 之後的內容。</summary>
    private static string Clean(string text)
    {
        var stripped = OscPattern.Replace(CsiPattern.Replace(text, string.Empty), string.Empty);
        var lines = stripped.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var cr = lines[i].LastIndexOf('\r');
            if (cr >= 0)
                lines[i] = lines[i][(cr + 1)..];
        }
        return string.Join('\n', lines);
    }
}
