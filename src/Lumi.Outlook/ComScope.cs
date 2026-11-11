using System.Runtime.InteropServices;

namespace Lumi.Outlook;

/// <summary>
/// 一次 COM 呼叫裡取得的物件都交給這裡收集，呼叫結束時（Dispose）逆序釋放。
/// 只在 STA 執行緒上使用，不是執行緒安全的。
/// </summary>
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _objects = [];
    private bool _disposed;

    /// <summary>收集這個物件並原樣回傳；null 直接回傳，不收集。</summary>
    public T Track<T>(T obj) where T : class?
    {
        if (obj is not null)
            _objects.Add(obj);
        return obj;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // 後取得的先釋放（和 COM 物件之間的參照關係一致）
        for (var i = _objects.Count - 1; i >= 0; i--)
        {
            var obj = _objects[i];
            if (OperatingSystem.IsWindows() && Marshal.IsComObject(obj))
                Marshal.ReleaseComObject(obj);
        }
        _objects.Clear();
    }
}
