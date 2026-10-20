using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Core.Tools;

public sealed partial class ToolRegistry : IToolRegistry
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();

    private readonly Dictionary<string, ITool> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// 名稱格式不對、名稱重複、Schema 產生失敗都在這裡丟例外：寧可啟動就失敗，
    /// 不要等模型呼叫到才發現。
    /// </summary>
    public ToolRegistry(IEnumerable<ITool> tools)
    {
        foreach (var tool in tools)
        {
            var name = tool.Descriptor.Name;
            if (!ValidName().IsMatch(name))
                throw new InvalidOperationException($"工具名稱「{name}」不合法：只能使用英數、底線與連字號，最長 64 個字元。");
            if (_byName.ContainsKey(name))
                throw new InvalidOperationException($"工具名稱「{name}」重複註冊。");
            if (string.IsNullOrWhiteSpace(tool.Descriptor.Description))
                throw new InvalidOperationException($"工具「{name}」沒有寫 Description：模型靠它判斷何時呼叫。");
            if (tool.Descriptor.Timeout <= TimeSpan.Zero)
                throw new InvalidOperationException($"工具「{name}」沒有設定逾時。");

            _ = tool.InputSchema;   // 先產生一次，Schema 有問題就在這裡失敗
            _byName[name] = tool;
        }
    }

    public IReadOnlyList<ITool> GetAll() =>
        _byName.Values.OrderBy(t => t.Descriptor.Name, StringComparer.Ordinal).ToList();

    public ITool? Find(string name) => _byName.GetValueOrDefault(name);
}

public static class ToolServiceCollectionExtensions
{
    /// <summary>註冊一個工具：<c>services.AddLumiTool&lt;MyTool&gt;()</c>（agent.md 第 8 節）。</summary>
    public static IServiceCollection AddLumiTool<TTool>(this IServiceCollection services) where TTool : class, ITool =>
        services.AddSingleton<ITool, TTool>();
}
