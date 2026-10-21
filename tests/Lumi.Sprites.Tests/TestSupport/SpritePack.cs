using System.Text.Json.Nodes;
using Lumi.Sprites.Assets;

namespace Lumi.Sprites.Tests.TestSupport;

/// <summary>暫存資料夾裡的一個角色包：預設是合法的最小角色（只有 idle），測試用 <see cref="Manifest"/> 改出各種壞法。</summary>
internal sealed class SpritePack : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-sprites-" + Guid.NewGuid().ToString("N"));

    public SpritePack(string folderName = "pack", bool withIdle = true)
    {
        FolderName = folderName;
        Directory = Path.Combine(_root, folderName);
        System.IO.Directory.CreateDirectory(Directory);
        Manifest = ValidManifest();
        if (withIdle)
            WriteSheet("idle.png", 20, 20, 4, 2);
    }

    public string Root => _root;

    public string FolderName { get; }

    public string Directory { get; }

    public JsonObject Manifest { get; }

    public static JsonObject ValidManifest() => JsonNode.Parse("""
        {
          "schemaVersion": 1, "id": "test.pack", "name": "測試", "author": "我", "version": "1.0.0", "kind": "frames",
          "box": { "width": 80, "height": 80 }, "effectMargin": 0, "hitShape": "ellipse",
          "bubbleAnchor": { "x": 0.5, "y": 0.1 },
          "animations": { "idle": { "source": "idle.png", "frames": 4, "columns": 2, "fps": 8, "loop": true } }
        }
        """)!.AsObject();

    public JsonObject Animations => Manifest["animations"]!.AsObject();

    public void WriteSheet(string relative, int cellWidth, int cellHeight, int frames, int columns) =>
        WriteBytes(relative, TestPng.Sheet(cellWidth, cellHeight, frames, columns));

    public void WriteBytes(string relative, byte[] bytes)
    {
        var path = Path.Combine(Directory, relative);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>加一個動畫（寫好圖、改 manifest）。</summary>
    public void AddAnimation(string key, int frames = 2, int columns = 2, int cell = 20, double fps = 8, bool loop = true, string? next = null, string? hold = null)
    {
        var file = key.Replace('.', '_') + ".png";
        WriteSheet(file, cell, cell, frames, columns);
        var animation = new JsonObject { ["source"] = file, ["frames"] = frames, ["columns"] = columns, ["fps"] = fps, ["loop"] = loop };
        if (next is not null)
            animation["next"] = next;
        if (hold is not null)
            animation["hold"] = hold;
        Animations[key] = animation;
    }

    public void SaveManifest() => File.WriteAllText(Path.Combine(Directory, AssetSpriteLoader.ManifestFileName), Manifest.ToJsonString());

    public SpriteLoadResult Load(IReadOnlySet<string>? reserved = null)
    {
        SaveManifest();
        return AssetSpriteLoader.Load(Directory, reserved);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root))
            System.IO.Directory.Delete(_root, recursive: true);
    }
}
