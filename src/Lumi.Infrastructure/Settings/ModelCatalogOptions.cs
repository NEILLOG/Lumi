namespace Lumi.Infrastructure.Settings;

public sealed class ModelCatalogOptions
{
    /// <summary>設定檔路徑；null 表示 <see cref="LumiPaths.DataDirectory"/> 下的 <c>settings.json</c>。測試用暫存路徑。</summary>
    public string? SettingsPath { get; set; }

    public string ResolveSettingsPath() => SettingsPath ?? Path.Combine(LumiPaths.DataDirectory, "settings.json");
}
