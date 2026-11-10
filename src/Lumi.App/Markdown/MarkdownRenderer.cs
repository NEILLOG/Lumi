using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Lumi.App.Controls;
using Lumi.App.Resources;

namespace Lumi.App.Markdown;

/// <summary>
/// 區塊模型 → Avalonia 控制項。所有文字都經過 <see cref="EmojiText"/>，顏色與字型只透過樣式類別
/// （<c>Themes/Markdown.axaml</c>）引用主題 token。
/// </summary>
public sealed class MarkdownRenderer
{
    /// <summary><c>LineBreak</c> 在 <c>TextLayout</c> 的文字位置裡占幾個字元（用 Headless 測試確認）。</summary>
    internal const int LineBreakLength = 1;

    private readonly MarkdownServices _services;

    public MarkdownRenderer(MarkdownServices services)
    {
        _services = services;
    }

    public Control Render(MdBlock block) => Render(block, depth: 0, new CopyContext(block, "", ""));

    /// <summary>
    /// 選取複製成 Markdown 用（<see cref="MarkdownCopy"/>）：所屬的最上層區塊、第一行與之後每一行前面要加的符號。
    /// </summary>
    private sealed record CopyContext(MdBlock Root, string First, string Rest)
    {
        public CopyContext Following => this with { First = Rest };
    }

    private Control Render(MdBlock block, int depth, CopyContext copy) => block switch
    {
        MdParagraph p => CreateText(p.Inlines, copy),
        MdHeading h => CreateText(h.Inlines, copy with { First = copy.First + new string('#', h.Level) + " " }, $"md-h{h.Level}"),
        MdCodeBlock c when IsMermaid(c) => CreateMermaidBlock(c, copy),
        MdCodeBlock c => CreateCodeBlock(c, copy),
        MdQuote q => CreateQuote(q, depth, copy),
        MdList l => CreateList(l, depth, copy),
        MdTable t => CreateTable(t, copy),
        MdRule => new Border { Classes = { "md-rule" } },
        _ => new Border(),
    };

    private StackPanel CreateBlocks(IEnumerable<MdBlock> blocks, int depth, CopyContext copy, double spacing = 10)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var block in blocks)
        {
            panel.Children.Add(Render(block, depth, copy));
            copy = copy.Following;
        }
        return panel;
    }

    // ---- 文字 ----

    private MarkdownTextBlock CreateText(IReadOnlyList<MdInline> inlines, CopyContext copy, string? cssClass = null,
        MarkdownCopyKind kind = MarkdownCopyKind.Text, int row = 0, bool headerRow = false)
    {
        var block = new MarkdownTextBlock { TextWrapping = TextWrapping.Wrap };
        if (cssClass is not null)
            block.Classes.Add(cssClass);

        var collection = new InlineCollection();
        var builder = new InlineBuilder(collection);
        builder.Add(inlines, InlineStyle.Plain);
        block.Inlines = collection;
        block.Links = builder.Links;
        MarkdownCopy.SetInfo(block, new MarkdownCopyInfo(copy.Root, copy.First, copy.Rest, kind, builder.Spans, Row: row, HeaderRow: headerRow));
        block.LinkClicked += uri => _ = _services.OpenLink(uri);
        return block;
    }

    /// <param name="Link">在可點的連結裡：連結的網址。</param>
    private sealed record InlineStyle(bool Bold, bool Italic, bool Strike, string? Link)
    {
        public static readonly InlineStyle Plain = new(false, false, false, null);
    }

    /// <summary>把行內元素加進 <see cref="InlineCollection"/>，同時算出每個連結的文字範圍與每段文字的格式（選取複製成 Markdown 用）。</summary>
    private sealed class InlineBuilder
    {
        private readonly InlineCollection _inlines;
        private int _offset;
        private readonly List<LinkRange> _links = [];
        private readonly List<MarkdownSpan> _spans = [];

        public InlineBuilder(InlineCollection inlines)
        {
            _inlines = inlines;
        }

        public IReadOnlyList<LinkRange> Links => _links;

        public IReadOnlyList<MarkdownSpan> Spans => _spans;

        public void Add(IEnumerable<MdInline> inlines, InlineStyle style)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case MdText t:
                        AddText(t.Text, style, isCode: false);
                        break;
                    case MdCode c:
                        AddText(c.Text, style, isCode: true);
                        break;
                    case MdStrong s:
                        Add(s.Children, style with { Bold = true });
                        break;
                    case MdEmphasis e:
                        Add(e.Children, style with { Italic = true });
                        break;
                    case MdStrike s:
                        Add(s.Children, style with { Strike = true });
                        break;
                    case MdLineBreak:
                        _inlines.Add(new LineBreak());
                        AddSpan("\n", style, isCode: false);
                        _offset += LineBreakLength;
                        break;
                    case MdLink link:
                        AddLink(link, style);
                        break;
                }
            }
        }

        private void AddLink(MdLink link, InlineStyle style)
        {
            // 不在白名單的協定：顯示成純文字，不可點
            if (!MarkdownLinks.IsAllowed(link.Url))
            {
                Add(link.Children, style);
                return;
            }

            var start = _offset;
            Add(link.Children, style with { Link = link.Url.Trim() });
            if (_offset > start)
                _links.Add(new LinkRange(start, _offset - start, new Uri(link.Url.Trim())));
        }

        private void AddText(string text, InlineStyle style, bool isCode)
        {
            var before = _inlines.Count;
            EmojiText.AddText(_inlines, text, run => Apply(run, style, isCode));
            for (var i = before; i < _inlines.Count; i++)
            {
                var piece = _inlines[i] switch
                {
                    Run run => run.Text ?? "",
                    LineBreak => "\n",   // 長度等於 LineBreakLength
                    _ => "",
                };
                AddSpan(piece, style, isCode);
                _offset += piece.Length;
            }
        }

        private void AddSpan(string text, InlineStyle style, bool isCode)
        {
            if (text.Length > 0)
                _spans.Add(new MarkdownSpan(_offset, text, style.Bold, style.Italic, style.Strike, isCode, style.Link));
        }

        private static void Apply(Run run, InlineStyle style, bool isCode)
        {
            if (style.Bold) run.FontWeight = FontWeight.Bold;
            if (style.Italic) run.FontStyle = FontStyle.Italic;
            if (style.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (isCode) run.Classes.Add("md-code");
            if (style.Link is not null) run.Classes.Add("md-link");
        }
    }

    // ---- 清單、引用 ----

    private Control CreateList(MdList list, int depth, CopyContext copy)
    {
        var panel = new StackPanel { Spacing = 4 };
        for (var i = 0; i < list.Items.Count; i++)
        {
            var marker = list.Ordered
                ? $"{list.Start + i}."
                : depth % 2 == 0 ? "•" : "◦";

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var markerText = new TextBlock { Text = marker, MinWidth = list.Ordered ? 26 : 16, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(markerText, 0);
            var copyMarker = list.Ordered ? $"{list.Start + i}. " : "- ";
            var itemCopy = copy with { First = (i == 0 ? copy.First : copy.Rest) + copyMarker, Rest = copy.Rest + new string(' ', copyMarker.Length) };
            var content = CreateBlocks(list.Items[i], depth + 1, itemCopy, spacing: 6);
            Grid.SetColumn(content, 1);
            row.Children.Add(markerText);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private Control CreateQuote(MdQuote quote, int depth, CopyContext copy) =>
        new Border { Classes = { "md-quote" }, Child = CreateBlocks(quote.Blocks, depth, copy with { First = copy.First + "> ", Rest = copy.Rest + "> " }, spacing: 6) };

    // ---- 程式碼區塊 ----

    /// <summary>
    /// 程式碼區塊：外框（<c>md-codeblock</c>）＝標題列（<c>md-codeheader</c>：左側語言標籤、右側「複製」鈕）＋水平捲動的程式碼區。
    /// 複製鈕在捲動區外面，不會隨程式碼捲動；複製後短暫變成打勾圖示加「已複製」。
    /// </summary>
    private Control CreateCodeBlock(MdCodeBlock block, CopyContext context)
    {
        var language = new TextBlock { Text = block.Language ?? "", VerticalAlignment = VerticalAlignment.Center, Classes = { "md-codelang" } };
        var copy = CreateCopyButton(block.Code);

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(language, 0);
        Grid.SetColumn(copy, 1);
        headerGrid.Children.Add(language);
        headerGrid.Children.Add(copy);
        var header = new Border { Classes = { "md-codeheader" }, Child = headerGrid };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(CreateCodeScroller(block, context));
        return new Border { Classes = { "md-codeblock" }, Child = stack };
    }

    /// <summary>語言標成 mermaid 的程式碼區塊畫成圖（見 <see cref="MermaidBlock"/>）。</summary>
    private static bool IsMermaid(MdCodeBlock block) => string.Equals(block.Language, "mermaid", StringComparison.OrdinalIgnoreCase);

    private Control CreateMermaidBlock(MdCodeBlock block, CopyContext copy) =>
        new MermaidBlock(block.Code, _services, CreateCodeScroller(block, copy), CreateCopyButton(block.Code));

    /// <summary>
    /// 水平捲動的程式碼區（不換行）。捲軸不自動隱藏（FX-02、FX-03）：自動隱藏時捲軸疊在內容上面，會蓋住最後一行，
    /// 細線狀態也很難點到；不自動隱藏時捲軸有自己的一列，只在內容比可視範圍寬時出現。
    /// </summary>
    private static ScrollViewer CreateCodeScroller(MdCodeBlock block, CopyContext copy)
    {
        var text = new EmojiTextBlock { Text = block.Code, TextWrapping = TextWrapping.NoWrap, Classes = { "md-codetext" } };
        MarkdownCopy.SetInfo(text, new MarkdownCopyInfo(copy.Root, copy.First, copy.Rest, MarkdownCopyKind.Code, [], block.Language));
        return new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
            Padding = new Thickness(16, 12, 16, 12),
        };
    }

    /// <summary>標題列右側的「複製」：複製後短暫變成打勾圖示加「已複製」。</summary>
    private Button CreateCopyButton(string code)
    {
        var icon = new LumiIcon { Size = 14 };
        icon.Bind(LumiIcon.DataProperty, new DynamicResourceExtension("LumiIconCopy"));
        var label = new TextBlock { Text = Strings.Workbench_Copy, VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button
        {
            Classes = { "md-copy" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { icon, label } },
        };
        ToolTip.SetTip(copy, Strings.Workbench_Copy);
        AutomationProperties.SetName(copy, Strings.Workbench_Copy);

        void ShowCopied(bool copied)
        {
            icon.Bind(LumiIcon.DataProperty, new DynamicResourceExtension(copied ? "LumiIconCheck" : "LumiIconCopy"));
            label.Text = copied ? Strings.Workbench_Copied : Strings.Workbench_Copy;
        }

        copy.Click += async (_, _) =>
        {
            await _services.CopyText(code);
            ShowCopied(true);
            await Task.Delay(1500);
            Dispatcher.UIThread.Post(() => ShowCopied(false));
        };
        return copy;
    }

    // ---- 表格 ----

    private Control CreateTable(MdTable table, CopyContext copy)
    {
        var columns = Math.Max(table.Alignments.Count, Math.Max(table.Header.Count, table.Rows.Select(r => r.Count).DefaultIfEmpty(0).Max()));
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var row = 0;
        void AddRow(IReadOnlyList<MdTableCell> cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var inlines = c < cells.Count ? cells[c].Inlines : [];
                var text = CreateText(inlines, row == 0 ? copy : copy.Following, kind: MarkdownCopyKind.TableCell, row: row, headerRow: header);
                text.MaxWidth = 360;
                text.TextAlignment = (c < table.Alignments.Count ? table.Alignments[c] : MdAlign.Left) switch
                {
                    MdAlign.Center => TextAlignment.Center,
                    MdAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                var cell = new Border { Classes = { "md-cell" }, Child = text };
                if (header)
                    cell.Classes.Add("md-thead");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            row++;
        }

        if (table.Header.Count > 0)
            AddRow(table.Header, header: true);
        foreach (var r in table.Rows)
            AddRow(r, header: false);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            AllowAutoHide = false,   // 寬表格的橫向捲軸固定顯示成可以點的寬度（FX-02）
            Content = new Border { Classes = { "md-table" }, Child = grid },
        };
    }
}
