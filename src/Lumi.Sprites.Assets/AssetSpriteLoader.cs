using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Assets;

/// <summary>
/// 讀一個角色資料夾（<c>manifest.json</c> 加上 sprite sheet 的 PNG），驗證後交出 <see cref="AssetSpriteDefinition"/>。
/// 素材型角色不執行任何程式碼，但檔案來自外部，所以路徑、大小、圖片標頭、語氣都要檢查；
/// 壞掉的角色回報原因、不會丟例外，也不影響其他角色。規則見 <c>plan/08-asset-sprites.md</c> 的驗證表。
/// </summary>
public static partial class AssetSpriteLoader
{
    public const string ManifestFileName = "manifest.json";
    public const string PreviewFileName = "preview.png";

    /// <summary>內建角色保留的 id 前綴；素材型角色不能用。</summary>
    public const string ReservedIdPrefix = "lumi.";

    /// <summary>動畫的 key：十個狀態（小寫）加三個互動動畫。</summary>
    public static IReadOnlyDictionary<string, SpriteState> StateKeys { get; } =
        Enum.GetValues<SpriteState>().ToDictionary(s => s.ToString().ToLowerInvariant());

    public static IReadOnlyList<string> InteractionKeys { get; } = ["poke.press", "poke.release", "drag"];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static SpriteLoadResult Load(string directory, IReadOnlySet<string>? reservedIds = null)
    {
        var loader = new Loader(directory, reservedIds ?? new HashSet<string>());
        AssetSpriteDefinition? definition = null;
        try
        {
            definition = loader.Run();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loader.Fatal(null, $"讀取資料夾失敗（{ex.Message}）");
        }
        return new SpriteLoadResult(loader.HasFatal ? null : definition, loader.Issues);
    }

    private sealed class Loader(string directory, IReadOnlySet<string> reservedIds)
    {
        private readonly string _folder = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        private readonly string _baseDirectory = Path.GetFullPath(directory);
        private long _decodedBytes;
        private readonly HashSet<string> _countedSheets = new(StringComparer.Ordinal);

        public List<SpriteLoadIssue> Issues { get; } = [];

        /// <summary>整個角色無法載入。動畫層級的問題（非 idle）只會讓那個動畫被丟掉，不算在內。</summary>
        public bool HasFatal => _characterFailed;

        private bool _characterFailed;

        public void Warn(string? field, string message) => Issues.Add(new SpriteLoadIssue(_folder, field, message, false));

        public void Fatal(string? field, string message)
        {
            _characterFailed = true;
            Issues.Add(new SpriteLoadIssue(_folder, field, message, true));
        }

        /// <summary>這個動畫不能用：idle 讓整個角色失敗，其他動畫只是被丟掉（角色照常載入，對應的狀態會走退化）。</summary>
        private void DropAnimation(string key, string message)
        {
            if (key == "idle")
                Fatal("animations.idle", $"idle 動畫無法使用，所以整個角色無法載入：{message}");
            else
                Issues.Add(new SpriteLoadIssue(_folder, $"animations.{key}", $"{message}，這個動畫不會使用", false));
        }

        public AssetSpriteDefinition? Run()
        {
            if (!Directory.Exists(_baseDirectory))
            {
                Fatal(null, "找不到這個資料夾");
                return null;
            }

            using var document = ReadManifest();
            if (document is null)
                return null;
            var root = document.RootElement;

            // ---- 識別資料 ----
            if (!TryGetInt(root, "schemaVersion", out var schema) || schema != SpriteManifest.CurrentSchemaVersion)
            {
                Fatal("schemaVersion", $"不支援的 schemaVersion（需要 {SpriteManifest.CurrentSchemaVersion}）");
                return null;
            }
            if (TryGetString(root, "kind") is { } kind && !string.Equals(kind, "frames", StringComparison.Ordinal))
            {
                Fatal("kind", $"目前只支援 \"frames\"（sprite sheet），不支援 \"{Shorten(kind)}\"");
                return null;
            }

            var id = TryGetString(root, "id");
            if (id is null || !IdPattern().IsMatch(id))
                Fatal("id", "id 只能用小寫英數與 . _ -，長度 3～64，並以英數開頭");
            else if (id.StartsWith(ReservedIdPrefix, StringComparison.Ordinal))
                Fatal("id", $"id 不能以 \"{ReservedIdPrefix}\" 開頭（保留給內建角色）");
            else if (reservedIds.Contains(id))
                Fatal("id", "這個 id 已經被其他角色使用");

            var name = RequiredText(root, "name");
            var author = RequiredText(root, "author");
            var version = RequiredText(root, "version");

            // ---- 尺寸與外觀 ----
            var box = ReadBox(root);
            var margin = ReadEffectMargin(root);
            var hitShape = ReadEnum(root, "hitShape", SpriteHitShape.Ellipse, new Dictionary<string, SpriteHitShape>
            {
                ["ellipse"] = SpriteHitShape.Ellipse, ["rect"] = SpriteHitShape.Rect, ["alpha"] = SpriteHitShape.Alpha,
            });
            var rendering = ReadEnum(root, "rendering", SpriteRendering.Smooth, new Dictionary<string, SpriteRendering>
            {
                ["smooth"] = SpriteRendering.Smooth, ["pixelated"] = SpriteRendering.Pixelated,
            });
            var anchor = ReadAnchor(root);
            var voice = ReadVoice(root);

            // ---- 動畫 ----
            var animations = ReadAnimations(root);
            if (_characterFailed || id is null || name is null || author is null || version is null || box is null || animations is null)
                return null;

            var cell = animations["idle"].CellPixels;
            var pixelGrid = ReadPixelGrid(root, cell);
            CheckAspect(box.Value, margin, cell);

            var supported = animations.Keys.Where(StateKeys.ContainsKey).Select(k => StateKeys[k]).ToHashSet();
            var fallback = ReadFallback(root);

            var manifest = new SpriteManifest(
                SpriteManifest.CurrentSchemaVersion, id, name, author, version, box.Value, margin, hitShape, anchor, voice, supported, fallback)
            {
                Rendering = rendering,
                PixelGrid = pixelGrid,
            };
            return new AssetSpriteDefinition(manifest, _baseDirectory, _folder, animations, cell, ReadPreview(), [.. Issues]);
        }

        // ---- manifest.json ----

        private JsonDocument? ReadManifest()
        {
            var path = Path.Combine(_baseDirectory, ManifestFileName);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                Fatal(null, $"找不到 {ManifestFileName}");
                return null;
            }
            if (IsLink(info))
            {
                Fatal(null, $"{ManifestFileName} 不能是捷徑或符號連結");
                return null;
            }
            if (info.Length > AssetSpriteLimits.ManifestMaxBytes)
            {
                Fatal(null, $"{ManifestFileName} 超過 {AssetSpriteLimits.ManifestMaxBytes / 1024} KB");
                return null;
            }

            try
            {
                var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document;
                document.Dispose();
                Fatal(null, $"{ManifestFileName} 的最外層必須是一個物件 {{ }}");
            }
            catch (JsonException ex)
            {
                Fatal(null, $"{ManifestFileName} 不是合法的 JSON（{ex.Message}）");
            }
            return null;
        }

        private string? RequiredText(JsonElement root, string field)
        {
            var value = TryGetString(root, field)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                Fatal(field, $"缺少 {field}");
                return null;
            }
            if (value.Length <= AssetSpriteLimits.NameMaxLength)
                return value;
            Warn(field, $"{field} 超過 {AssetSpriteLimits.NameMaxLength} 字，已截斷");
            return value[..AssetSpriteLimits.NameMaxLength];
        }

        private Size? ReadBox(JsonElement root)
        {
            if (root.TryGetProperty("box", out var box) && box.ValueKind == JsonValueKind.Object
                && TryGetDouble(box, "width", out var width) && TryGetDouble(box, "height", out var height)
                && width > 0 && height > 0 && width <= AssetSpriteLimits.PngMaxDimension && height <= AssetSpriteLimits.PngMaxDimension)
                return new Size(width, height);   // 超出 SpriteLimits 的長寬比與最長邊由殼的 SpriteLimits.Fit 自動縮放並警告

            Fatal("box", "box 必須是 { \"width\": 正數, \"height\": 正數 }（「中」檔位的角色框大小，單位 DIP）");
            return null;
        }

        private double ReadEffectMargin(JsonElement root)
        {
            if (!root.TryGetProperty("effectMargin", out _))
                return 0;
            if (!TryGetDouble(root, "effectMargin", out var margin))
            {
                Warn("effectMargin", "effectMargin 必須是數字，已改用 0");
                return 0;
            }
            if (margin is >= 0 and <= SpriteLimits.EffectMarginMax)
                return margin;
            var clamped = Math.Clamp(margin, 0, SpriteLimits.EffectMarginMax);
            Warn("effectMargin", $"effectMargin {margin.ToString(CultureInfo.InvariantCulture)} 超出 0～{SpriteLimits.EffectMarginMax.ToString(CultureInfo.InvariantCulture)}，已限制為 {clamped.ToString(CultureInfo.InvariantCulture)}");
            return clamped;
        }

        private T ReadEnum<T>(JsonElement root, string field, T fallback, Dictionary<string, T> names) where T : struct
        {
            if (!root.TryGetProperty(field, out _))
                return fallback;
            if (TryGetString(root, field) is { } text && names.TryGetValue(text.ToLowerInvariant(), out var value))
                return value;
            Warn(field, $"不認得的 {field}（可用：{string.Join("、", names.Keys)}），已改用 {names.First(p => EqualityComparer<T>.Default.Equals(p.Value, fallback)).Key}");
            return fallback;
        }

        private Point ReadAnchor(JsonElement root)
        {
            var anchor = new Point(0.5, 0);
            if (!root.TryGetProperty("bubbleAnchor", out var value))
                return anchor;
            if (value.ValueKind != JsonValueKind.Object || !TryGetDouble(value, "x", out var x) || !TryGetDouble(value, "y", out var y))
            {
                Warn("bubbleAnchor", "bubbleAnchor 必須是 { \"x\": 0～1, \"y\": 0～1 }，已改用頭頂中央");
                return anchor;
            }
            if (x is < 0 or > 1 || y is < 0 or > 1)
                Warn("bubbleAnchor", "bubbleAnchor 的 x、y 必須在 0～1 之間，已限制");
            return new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }

        private string? ReadVoice(JsonElement root)
        {
            if (!root.TryGetProperty("voice", out var element) || element.ValueKind is JsonValueKind.Null)
                return null;
            if (element.ValueKind != JsonValueKind.String)
            {
                Warn("voice", "語氣被忽略：voice 必須是文字");
                return null;
            }

            var check = SpriteVoiceValidator.Check(element.GetString());
            if (check.IsRejected)
                Warn("voice", check.Rejection!);
            return check.Voice;
        }

        private int? ReadPixelGrid(JsonElement root, Size cell)
        {
            if (!root.TryGetProperty("pixelGrid", out _))
                return null;
            if (!TryGetInt(root, "pixelGrid", out var grid) || grid <= 0)
            {
                Warn("pixelGrid", "pixelGrid 必須是正整數，已忽略（會用一般縮放）");
                return null;
            }
            // pixelGrid 是格子「寬度方向」的邏輯像素數；邏輯像素是正方形，所以高度方向也要剛好是整數個
            if (cell.Width % grid != 0 || cell.Height * grid % cell.Width != 0)
            {
                Warn("pixelGrid", $"pixelGrid {grid} 不能整除格子大小 {cell.Width:0}×{cell.Height:0}（邏輯像素要是正方形），已忽略（會用一般縮放）");
                return null;
            }
            return grid;
        }

        private void CheckAspect(Size box, double margin, Size cell)
        {
            // 每一格涵蓋的是 Outer（角色框加上特效外擴）；長寬比對不上畫的時候會等比例縮放、底部置中
            var outerAspect = box.Width * (1 + 2 * margin) / (box.Height * (1 + 2 * margin));
            var cellAspect = cell.Width / cell.Height;
            if (Math.Abs(cellAspect / outerAspect - 1) > AssetSpriteLimits.AspectTolerance)
                Warn("box", $"格子的長寬比 {cellAspect:0.##} 和角色框的長寬比 {outerAspect:0.##} 不一致，會等比例縮放並底部置中");
        }

        private Dictionary<SpriteState, SpriteState> ReadFallback(JsonElement root)
        {
            var result = new Dictionary<SpriteState, SpriteState>();
            if (!root.TryGetProperty("fallback", out var element))
                return result;
            if (element.ValueKind != JsonValueKind.Object)
            {
                Warn("fallback", "fallback 必須是 { \"狀態\": \"狀態\" }，已忽略");
                return result;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (StateKeys.TryGetValue(property.Name.ToLowerInvariant(), out var from)
                    && property.Value.ValueKind == JsonValueKind.String
                    && StateKeys.TryGetValue(property.Value.GetString()!.ToLowerInvariant(), out var to))
                    result[from] = to;
                else
                    Warn($"fallback.{property.Name}", "fallback 的 key 與值都必須是狀態名稱，這一項已忽略");
            }
            return result;
        }

        // ---- 動畫 ----

        private Dictionary<string, AssetAnimation>? ReadAnimations(JsonElement root)
        {
            if (!root.TryGetProperty("animations", out var element) || element.ValueKind != JsonValueKind.Object)
            {
                Fatal("animations", "缺少 animations（至少要有 idle）");
                return null;
            }
            if (!element.TryGetProperty("idle", out var idleElement))
            {
                Fatal("animations.idle", "缺少 idle 動畫（每個角色都必須支援待機）");
                return null;
            }

            var animations = new Dictionary<string, AssetAnimation>(StringComparer.Ordinal);
            var idle = ReadAnimation("idle", idleElement, null);
            if (idle is null)
                return null;   // 原因已經記錄為 Fatal
            animations["idle"] = idle;
            var cell = idle.CellPixels;

            foreach (var property in element.EnumerateObject())
            {
                var key = property.Name;
                if (key == "idle")
                    continue;
                if (!StateKeys.ContainsKey(key) && !InteractionKeys.Contains(key))
                {
                    Warn($"animations.{key}", $"不認得的動畫 \"{Shorten(key)}\"（可用：十個狀態與 {string.Join("、", InteractionKeys)}），已忽略");
                    continue;
                }
                if (ReadAnimation(key, property.Value, cell) is { } animation)
                    animations[key] = animation;
            }

            FixLinks(animations);
            return animations;
        }

        /// <summary><paramref name="reference"/> 是 idle 的格子大小；讀 idle 本身時是 null。回 null 表示這個動畫不能用（原因已記錄）。</summary>
        private AssetAnimation? ReadAnimation(string key, JsonElement element, Size? reference)
        {
            var field = $"animations.{key}";
            if (element.ValueKind != JsonValueKind.Object)
                return Drop(key, "必須是一個物件");

            if (!TryGetInt(element, "frames", out var frames) || frames < 1 || frames > AssetSpriteLimits.FramesMax)
                return Drop(key, $"frames 必須是 1～{AssetSpriteLimits.FramesMax} 的整數");
            if (!TryGetInt(element, "columns", out var columns) || columns < 1 || columns > frames)
                return Drop(key, "columns 必須是 1～frames 的整數（每列幾格）");
            if (!TryGetDouble(element, "fps", out var fps) || fps < 1 || fps > AssetSpriteLimits.FpsMax)
                return Drop(key, $"fps 必須是 1～{AssetSpriteLimits.FpsMax:0} 的數字");

            var loop = false;
            if (element.TryGetProperty("loop", out var loopElement))
            {
                if (loopElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    loop = loopElement.GetBoolean();
                else
                    Warn($"{field}.loop", "loop 必須是 true 或 false，已當作 false");
            }

            var holdLast = false;
            if (element.TryGetProperty("hold", out _))
            {
                if (TryGetString(element, "hold") == "last")
                    holdLast = true;
                else
                    Warn($"{field}.hold", "hold 只能是 \"last\"，已忽略");
            }
            var next = TryGetString(element, "next");

            var source = TryGetString(element, "source");
            if (string.IsNullOrWhiteSpace(source))
                return Drop(key, "缺少 source（sprite sheet 的檔名）");
            var (fullPath, reason) = ResolveInside(source);
            if (fullPath is null)
                return Drop(key, reason!);

            var (width, height, error) = PngHeader.Read(fullPath);
            if (error is not null)
                return Drop(key, $"{source}：{error}");
            if (width is < 1 or > AssetSpriteLimits.PngMaxDimension || height is < 1 or > AssetSpriteLimits.PngMaxDimension)
                return Drop(key, $"{source} 的大小 {width}×{height} 超出 1～{AssetSpriteLimits.PngMaxDimension} 的範圍");

            var animation = new AssetAnimation(key, source, fullPath, frames, columns, fps, loop, next, holdLast, new Size(width, height));
            if (width % columns != 0 || height % animation.Rows != 0)
                return Drop(key, $"{source} 的大小 {width}×{height} 不是 {columns} 欄 × {animation.Rows} 列的整數倍（每格大小要相同）");
            if (reference is { } cell && (animation.CellPixels.Width != cell.Width || animation.CellPixels.Height != cell.Height))
                return Drop(key, $"格子大小 {animation.CellPixels.Width:0}×{animation.CellPixels.Height:0} 和 idle 的 {cell.Width:0}×{cell.Height:0} 不同");

            // 解碼後的總量（同一張圖被多個動畫引用只算一次）
            if (_countedSheets.Add(fullPath))
            {
                _decodedBytes += width * height * 4;
                if (_decodedBytes > AssetSpriteLimits.DecodedMaxBytes)
                {
                    Fatal(field, $"所有圖片解碼後超過 {AssetSpriteLimits.DecodedMaxBytes / 1024 / 1024} MB，請縮小格子或減少張數");
                    return null;
                }
            }
            return animation;

            AssetAnimation? Drop(string animationKey, string message)
            {
                DropAnimation(animationKey, message);
                return null;
            }
        }

        /// <summary><c>next</c>／<c>loop</c> 的關係：循環的動畫不會結束所以 next 沒有意義；next 要指向存在的動畫。</summary>
        private void FixLinks(Dictionary<string, AssetAnimation> animations)
        {
            foreach (var animation in animations.Values.ToList())
            {
                if (animation.Next is null)
                    continue;
                if (animation.Loop)
                {
                    Warn($"animations.{animation.Key}.next", "loop 為 true 的動畫不會結束，next 已忽略");
                    animations[animation.Key] = animation with { Next = null };
                }
                else if (!animations.ContainsKey(animation.Next))
                {
                    Warn($"animations.{animation.Key}.next", $"next 指向不存在的動畫 \"{Shorten(animation.Next)}\"，已忽略");
                    animations[animation.Key] = animation with { Next = null };
                }
            }
        }

        private string? ReadPreview()
        {
            var path = Path.Combine(_baseDirectory, PreviewFileName);
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            if (IsLink(info))
            {
                Warn(PreviewFileName, "preview.png 不能是捷徑或符號連結，沒有縮圖");
                return null;
            }
            if (info.Length > AssetSpriteLimits.PreviewMaxBytes)
            {
                Warn(PreviewFileName, "preview.png 超過 1 MB，沒有縮圖");
                return null;
            }

            var (width, height, error) = PngHeader.Read(path);
            if (error is not null || width is < 1 or > AssetSpriteLimits.PreviewMaxDimension || height is < 1 or > AssetSpriteLimits.PreviewMaxDimension)
            {
                Warn(PreviewFileName, error ?? $"preview.png 的大小 {width}×{height} 超出 1～{AssetSpriteLimits.PreviewMaxDimension}，沒有縮圖");
                return null;
            }
            return path;
        }

        // ---- 路徑與檔案 ----

        /// <summary>
        /// 相對路徑必須落在角色資料夾內：不能是絕對路徑、不能有 <c>..</c>、不能經過捷徑或符號連結，
        /// 必須是存在的 <c>.png</c>、不超過 8 MB。
        /// </summary>
        private (string? FullPath, string? Reason) ResolveInside(string relative)
        {
            var normalized = relative.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains(':') || Path.IsPathRooted(relative))
                return (null, "source 不能是絕對路徑");

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(s => s == ".."))
                return (null, "source 不能含有 ..（只能指向角色資料夾內的檔案）");
            if (!normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return (null, "source 必須是 .png 檔");

            var candidate = Path.GetFullPath(Path.Combine(_baseDirectory, string.Join(Path.DirectorySeparatorChar, segments)));
            var prefix = _baseDirectory.EndsWith(Path.DirectorySeparatorChar) ? _baseDirectory : _baseDirectory + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
                return (null, "source 指向角色資料夾以外的位置");

            // 逐層檢查：中間的資料夾與檔案本身都不能是捷徑或符號連結
            var current = _baseDirectory;
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                FileSystemInfo info = i == segments.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
                if (!info.Exists)
                    return (null, $"找不到檔案 {relative}");
                if (IsLink(info))
                    return (null, "source 不能經過捷徑或符號連結");
            }

            if (new FileInfo(candidate).Length > AssetSpriteLimits.PngMaxBytes)
                return (null, $"{relative} 超過 {AssetSpriteLimits.PngMaxBytes / 1024 / 1024} MB");
            return (candidate, null);
        }

        private static bool IsLink(FileSystemInfo info) =>
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

        // ---- JSON 小工具 ----

        private static string? TryGetString(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool TryGetInt(JsonElement parent, string name, out int value)
        {
            value = 0;
            return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
        }

        private static bool TryGetDouble(JsonElement parent, string name, out double value)
        {
            value = 0;
            return parent.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
                   && element.TryGetDouble(out value) && double.IsFinite(value);
        }

        private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "…";
    }
}
