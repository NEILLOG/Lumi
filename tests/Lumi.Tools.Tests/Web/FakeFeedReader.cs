using Lumi.Core.Web;

namespace Lumi.Tools.Tests.Web;

/// <summary>回傳預先設定的 feed；Result 是 null 表示「不是 feed」。</summary>
internal sealed class FakeFeedReader : IFeedReader
{
    public Feed? Result { get; set; }

    public List<byte[]> Calls { get; } = [];

    public Feed? Parse(byte[] body)
    {
        Calls.Add(body);
        return Result;
    }
}
