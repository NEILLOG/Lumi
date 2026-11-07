namespace Lumi.Infrastructure.Skills;

public sealed class SkillStoreOptions
{
    /// <summary>技能資料夾；null 表示 <see cref="LumiPaths.DataDirectory"/> 下的 <c>skills</c>（M29）。測試用暫存路徑。</summary>
    public string? Directory { get; set; }

    public string ResolveDirectory() => Directory ?? Path.Combine(LumiPaths.DataDirectory, "skills");
}
