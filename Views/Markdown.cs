using System.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Jicun.Desktop.Views;

/// <summary>段落对齐。目前只用到「居中」这一个开关。</summary>
internal enum MarkdownAlign { Left, Center }


/// <summary>任务列表的勾选状态。</summary>
internal enum MarkdownTask { None, Open, Done }
/// <summary>行内样式，可以叠加（比如加粗里套代码）。</summary>
[Flags]
internal enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Highlight = 8,
    Strike = 16,
}

/// <summary>一段带样式的文字。<see cref="Link"/> 非空时是个链接。</summary>
internal sealed class MarkdownSpan
{
    public string Text { get; init; } = "";
    public MarkdownStyle Style { get; init; }
    public string? Link { get; init; }
}

/// <summary>
/// 一个块：段落（可带对齐 / 标题级别 / 列表符号 / 缩进 / 任务勾选 / 引用），
/// 或者分隔线、围栏代码块、表格 —— 三个特殊块各自一个渲染分支。
/// </summary>
internal sealed class MarkdownBlock
{
    public MarkdownAlign Align { get; set; }
    public int Heading { get; set; }              // 0 = 正文，1..6 = #..######
    public bool Bullet { get; set; }
    public string Number { get; set; } = "";      // 有序列表的序号文本，空 = 不是有序项
    public MarkdownTask Task { get; set; }        // - [ ] / - [x]
    public int Indent { get; set; }               // 列表缩进级别（2 空格一级，封顶 4）
    public bool Quote { get; set; }               // > 引用
    public bool Rule { get; set; }
    public bool Code { get; set; }                // 围栏代码块
    public string CodeText { get; set; } = "";    // 代码块原文（保留换行，里面不再解析标记）
    public List<string[]> Table { get; } = new(); // 表格：第一行是表头
    public List<MarkdownSpan> Spans { get; } = new();
}

/// <summary>
/// 更新说明用的迷你 Markdown。**不引第三方库**（这个工程本来就没有第三方依赖）。
///
/// 支持的写法（对齐 GitHub 上 release notes 常见的那些）：
/// <list type="bullet">
///   <item><c># 标题</c> ~ <c>###### 标题</c>（字号递减；收尾的 <c>#</c> 不算文字）</item>
///   <item><c>**加粗**</c> / <c>__加粗__</c>、<c>*斜体*</c> / <c>_斜体_</c>、<c>`代码`</c></item>
///   <item><c>~~删除线~~</c>、<c>==高亮==</c></item>
///   <item><c>[文字](链接)</c>（只放行 http / https，可带 "标题"）、裸链接自动成链接</item>
///   <item><c>- 无序</c> / <c>* 无序</c> / <c>+ 无序</c> / <c>1. 有序</c>；列表前导空格缩进一级（2 空格）</item>
///   <item><c>- [ ] 待办</c> / <c>- [x] 已办</c> 任务列表</item>
///   <item><c>&gt; 引用</c>（连续几行合成一段，左侧竖线）</item>
///   <item><c>```</c> / <c>~~~</c> 围栏代码块（等宽 + 底色，里面不再解析）</item>
///   <item><c>---</c> / <c>***</c> / <c>___</c> 分隔线</item>
///   <item>表格：<c>| 表头 |</c> 加 <c>|---|---|</c> 再加数据行</item>
///   <item>居中：<c>&lt;div align="center"&gt;…&lt;/div&gt;</c>、<c>&lt;center&gt;…&lt;/center&gt;</c>；<c>&lt;br&gt;</c> 换行</item>
/// </list>
///
/// 不做的：图片（要联网抓图）、HTML 实体与其它 HTML 标签、嵌套引用 / 列表里套引用。
///
/// 解析（<see cref="Parse"/>）是纯函数：命令行自检能离线验它，不用起界面。
/// 只有 <see cref="Build"/> 碰控件。
/// </summary>
internal static class Markdown
{
    private static readonly string[] CenterOpens =
    {
        "<div align=\"center\">", "<div align='center'>", "<div align=center>",
        "<p align=\"center\">", "<p align='center'>", "<p align=center>",
        "<center>",
    };

    private static readonly string[] CenterCloses = { "</div>", "</p>", "</center>" };

    /// <summary>裸链接尾部不该吞进去的标点。</summary>
    private static readonly char[] UrlTrailing =
    {
        '.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'', '。', '，', '、', '；', '：', '！', '？', '）', '】',
    };

    public static List<MarkdownBlock> Parse(string? text)
    {
        var blocks = new List<MarkdownBlock>();
        var align = MarkdownAlign.Left;
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var quote = new StringBuilder();

        // 连续的 > 行合成一段引用（一个块，渲染时才像一段引用）
        void FlushQuote()
        {
            if (quote.Length == 0) return;

            var quoted = new MarkdownBlock { Align = align, Quote = true };
            ScanInline(quote.ToString(), MarkdownStyle.None, quoted.Spans);
            if (quoted.Spans.Count > 0) blocks.Add(quoted);
            quote.Clear();
        }

        for (var n = 0; n < lines.Length; n++)
        {
            var raw = lines[n];
            var line = raw.Trim();

            // 围栏代码块：``` 或 ~~~（语言标识忽略），一直吃到同种围栏；没闭合就吃到末尾
            if (TryFence(line, out var fence))
            {
                FlushQuote();

                var code = new StringBuilder();
                var close = n + 1;
                for (; close < lines.Length; close++)
                {
                    if (lines[close].Trim().StartsWith(fence, StringComparison.Ordinal)) break;
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[close].TrimEnd());
                }

                blocks.Add(new MarkdownBlock { Code = true, CodeText = code.ToString() });
                n = close;
                continue;
            }

            if (line.Length == 0)
            {
                FlushQuote();
                continue;
            }

            // 表格：这一行有 | 且下一行是 |---| 那种分隔行
            if (line.Contains('|') && n + 1 < lines.Length && IsTableSeparator(lines[n + 1]))
            {
                FlushQuote();

                var table = new MarkdownBlock();
                var header = SplitRow(line);
                table.Table.Add(header);

                var last = n + 1;
                for (var r = n + 2; r < lines.Length; r++)
                {
                    var row = lines[r].Trim();
                    if (row.Length == 0 || !row.Contains('|')) break;

                    var cells = SplitRow(row);
                    var normalized = new string[header.Length];
                    for (var c = 0; c < normalized.Length; c++) normalized[c] = c < cells.Length ? cells[c] : "";
                    table.Table.Add(normalized);
                    last = r;
                }

                blocks.Add(table);
                n = last;
                continue;
            }

            // 居中块的开关；同一行开合的（<div align="center">字</div>）只居中这一行
            var open = CenterOpens.FirstOrDefault(tag => line.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
            if (open is not null)
            {
                FlushQuote();

                var rest = line[open.Length..];
                var closeAt = IndexOfAny(rest, CenterCloses);
                if (closeAt >= 0)
                {
                    AddLine(blocks, rest[..closeAt].Trim(), MarkdownAlign.Center);
                    continue;
                }

                align = MarkdownAlign.Center;
                AddLine(blocks, rest.Trim(), align);
                continue;
            }

            if (CenterCloses.Any(tag => line.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            {
                align = MarkdownAlign.Left;
                continue;
            }

            if (IsRule(line))
            {
                FlushQuote();
                blocks.Add(new MarkdownBlock { Rule = true });
                continue;
            }

            // 引用：整行合到当前那段引用里，空行或别的块收尾
            if (line[0] == '>')
            {
                var body = line[1..].TrimStart();
                if (quote.Length > 0) quote.Append('\n');
                quote.Append(body);
                continue;
            }

            FlushQuote();

            // 缩进只对列表项有意义（普通段落缩进忽略，免得把「4 空格＝代码块」那套搅进来）
            var indent = IndentLevel(raw);
            var heading = HeadingLevel(line, out var headingText);
            var bullet = false;
            var number = "";
            var task = MarkdownTask.None;
            var content = line;

            if (heading == 0)
            {
                if (TryBullet(line, out var bulletText))
                {
                    bullet = true;
                    content = bulletText;
                    task = TaskMark(ref content);
                }
                else if (TryNumbered(line, out var num, out var numText))
                {
                    number = num;
                    content = numText;
                }
            }

            // <br> 把一个逻辑行拆成多个块（GitHub 上也常这么写）
            foreach (var part in SplitBr(heading > 0 ? headingText : content))
            {
                if (part.Trim().Length == 0) continue;

                var block = new MarkdownBlock
                {
                    Align = align,
                    Heading = heading,
                    Bullet = bullet,
                    Number = number,
                    Task = task,
                    Indent = bullet || number.Length > 0 ? indent : 0,
                };
                ScanInline(part, MarkdownStyle.None, block.Spans);
                if (block.Spans.Count > 0) blocks.Add(block);
            }
        }

        FlushQuote();
        return blocks;
    }

    /// <summary>把说明变成可以直接塞进弹窗的一摞控件。只在 UI 线程上调。</summary>
    public static StackPanel Build(string? text)
    {
        var panel = new StackPanel { Spacing = 6 };
        var ruleBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.35 };
        var markBrush = new SolidColorBrush(Colors.Gold);
        var markTextBrush = new SolidColorBrush(Colors.Black);
        var quoteBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.6 };
        var quoteTextBrush = new SolidColorBrush(Colors.Gray);
        var codeBrush = new SolidColorBrush(Colors.Gray) { Opacity = 0.18 };

        foreach (var block in Parse(text))
        {
            if (block.Rule)
            {
                panel.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = ruleBrush });
                continue;
            }

            // 围栏代码块：等宽 + 底色，内容原样（里面不解析标记）
            if (block.Code)
            {
                panel.Children.Add(new Border
                {
                    Background = codeBrush,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 8, 10, 8),
                    Child = new TextBlock
                    {
                        Text = block.CodeText.Length > 0 ? block.CodeText : " ",
                        FontFamily = CodeFont(),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                });
                continue;
            }

            if (block.Table.Count > 0)
            {
                panel.Children.Add(BuildTable(block.Table, ruleBrush));
                continue;
            }

            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            if (block.Align == MarkdownAlign.Center) line.TextAlignment = TextAlignment.Center;
            if (block.Heading > 0)
            {
                line.FontWeight = FontWeights.SemiBold;
                line.FontSize = HeadingSize(block.Heading);
            }

            var prefix = Prefix(block);
            if (prefix.Length > 0) line.Inlines.Add(new Run { Text = prefix });

            foreach (var span in block.Spans) line.Inlines.Add(ToInline(span));

            // WinUI 3 里 InlineUIContainer 一用就抛（Run 又没有 Background），
            // 所以「高亮」走平台自带的 TextHighlighters：按字符区间刷底色，和别的行内样式能共存。
            var ranges = HighlightRanges(block);
            if (ranges.Count > 0)
            {
                // 底色 + 字色都显式给：TextHighlighter 不理笔刷的 Opacity（实测刷出来就是纯色），
                // 而深色主题里白字压在金色上根本看不清，所以高亮处的字固定用黑的。
                var highlighter = new TextHighlighter { Background = markBrush, Foreground = markTextBrush };
                foreach (var range in ranges)
                    highlighter.Ranges.Add(new TextRange { StartIndex = range.Start, Length = range.Length });
                line.TextHighlighters.Add(highlighter);
            }

            FrameworkElement element = line;
            if (block.Quote)
            {
                line.Foreground = quoteTextBrush;
                element = new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = quoteBrush,
                    Padding = new Thickness(10, 0, 0, 0),
                    Child = line,
                };
            }

            if (block.Indent > 0) element.Margin = new Thickness(block.Indent * 16, 0, 0, 0);
            panel.Children.Add(element);
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "（本次发布没有写更新说明）", TextWrapping = TextWrapping.Wrap });

        return panel;
    }

    /// <summary>列表 / 任务符号。渲染和 <see cref="HighlightRanges"/> 的下标都要按它对齐。</summary>
    private static string Prefix(MarkdownBlock block)
    {
        if (block.Task == MarkdownTask.Done) return "☑ ";
        if (block.Task == MarkdownTask.Open) return "☐ ";
        if (block.Bullet) return "• ";
        if (block.Number.Length > 0) return block.Number + " ";
        return "";
    }

    /// <summary>
    /// 这一行里要高亮的字符区间（下标相对整行纯文本，列表符号也算进去）。
    /// 抽成纯函数是为了让自检能离线验这段下标算术；真底色由 TextHighlighters 刷。
    /// </summary>
    public static List<(int Start, int Length)> HighlightRanges(MarkdownBlock block)
    {
        var ranges = new List<(int, int)>();
        var at = Prefix(block).Length;

        foreach (var span in block.Spans)
        {
            if (span.Style.HasFlag(MarkdownStyle.Highlight)) ranges.Add((at, span.Text.Length));
            at += span.Text.Length;
        }
        return ranges;
    }

    /// <summary>表格：列等分，外框一圈线、格与格之间两条内线。</summary>
    private static Border BuildTable(List<string[]> rows, Brush lineBrush)
    {
        var columns = rows[0].Length;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var cell = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                if (r == 0) cell.FontWeight = FontWeights.SemiBold;   // 第一行是表头

                // 格子里的 **加粗** / [链接] 也认
                var holder = new MarkdownBlock();
                ScanInline(rows[r][c], MarkdownStyle.None, holder.Spans);
                foreach (var span in holder.Spans) cell.Inlines.Add(ToInline(span));

                var box = new Border
                {
                    // 只画左、上两条内线，外框交给外面那层 Border —— 合起来就是一整张表
                    BorderThickness = new Thickness(c == 0 ? 0 : 1, r == 0 ? 0 : 1, 0, 0),
                    BorderBrush = lineBrush,
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = cell,
                };
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                grid.Children.Add(box);
            }
        }

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = lineBrush,
            CornerRadius = new CornerRadius(4),
            Child = grid,
        };
    }

    // ---- 解析 ----

    private static void AddLine(List<MarkdownBlock> blocks, string content, MarkdownAlign align)
    {
        if (content.Length == 0) return;

        var block = new MarkdownBlock { Align = align };
        ScanInline(content, MarkdownStyle.None, block.Spans);
        if (block.Spans.Count > 0) blocks.Add(block);
    }

    private static int IndexOfAny(string text, string[] needles)
    {
        var best = -1;
        foreach (var needle in needles)
        {
            var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
    }

    /// <summary>行内扫描：找成对的标记，里面递归（所以 <c>**加粗里的 `代码`**</c> 也对）。</summary>
    private static void ScanInline(string text, MarkdownStyle inherited, List<MarkdownSpan> into)
    {
        var plain = new StringBuilder();
        var i = 0;

        void Flush()
        {
            if (plain.Length == 0) return;
            into.Add(new MarkdownSpan { Text = plain.ToString(), Style = inherited });
            plain.Clear();
        }

        while (i < text.Length)
        {
            if (text[i] == '[' && TryLink(text, ref i, inherited, into, Flush)) continue;
            if (TryBareUrl(text, ref i, inherited, into, Flush)) continue;

            // 先试两字符的标记，再试单字符的（否则 ** 会被当成两次 *）
            if (TryDelimited(text, ref i, "**", MarkdownStyle.Bold, inherited, into, Flush)) continue;
            if (TryDelimited(text, ref i, "__", MarkdownStyle.Bold, inherited, into, Flush, underscore: true)) continue;
            if (TryDelimited(text, ref i, "~~", MarkdownStyle.Strike, inherited, into, Flush)) continue;
            if (TryDelimited(text, ref i, "==", MarkdownStyle.Highlight, inherited, into, Flush)) continue;
            if (TryDelimited(text, ref i, "`", MarkdownStyle.Code, inherited, into, Flush)) continue;
            if (TryDelimited(text, ref i, "*", MarkdownStyle.Italic, inherited, into, Flush)) continue;
            if (TryDelimited(text, ref i, "_", MarkdownStyle.Italic, inherited, into, Flush, underscore: true)) continue;

            plain.Append(text[i]);
            i++;
        }

        Flush();
    }

    /// <summary>裸链接（<c>https://…</c>）也认；尾巴上的标点留给后面的普通文字。</summary>
    private static bool TryBareUrl(
        string text, ref int i, MarkdownStyle inherited, List<MarkdownSpan> into, Action flush)
    {
        if (text[i] is not ('h' or 'H')) return false;

        var rest = text.AsSpan(i);
        if (!rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = i;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"')) end++;

        var url = text[i..end].TrimEnd(UrlTrailing);
        if (url.Length <= 8) return false;   // 只有协议头，不算链接

        flush();
        into.Add(new MarkdownSpan { Text = url, Style = inherited, Link = url });
        i += url.Length;
        return true;
    }

    /// <summary>任务列表：<c>[ ] 待办</c> / <c>[x] 已办</c>；认出来就把标记从正文里去掉。</summary>
    private static MarkdownTask TaskMark(ref string content)
    {
        var text = content;
        if (text.Length < 4) return MarkdownTask.None;
        if (text[0] != '[' || text[2] != ']' || text[3] != ' ') return MarkdownTask.None;

        var mark = char.ToLowerInvariant(text[1]);
        if (mark is not (' ' or 'x')) return MarkdownTask.None;

        content = text[4..].Trim();
        return mark == 'x' ? MarkdownTask.Done : MarkdownTask.Open;
    }

    private static bool TryDelimited(
        string text, ref int i, string marker, MarkdownStyle style,
        MarkdownStyle inherited, List<MarkdownSpan> into, Action flush, bool underscore = false)
    {
        if (!text.AsSpan(i).StartsWith(marker, StringComparison.Ordinal)) return false;

        var open = i + marker.Length;
        var close = text.IndexOf(marker, open, StringComparison.Ordinal);
        if (close < 0 || close == open) return false;   // 没闭合 / 里面是空的 → 当普通字符
        if (underscore && !UnderscoreOk(text, i, marker.Length, open, close)) return false;

        flush();
        ScanInline(text[open..close], inherited | style, into);
        i = close + marker.Length;
        return true;
    }

    /// <summary>
    /// 下划线标记的边界判定：GitHub 里 <c>_斜_</c> 生效，而 <c>file_name_x</c> 不生效。
    /// 规则：开标记左边是行首 / 空白 / 标点，右边不是空白；闭标记左边不是空白，右边是行尾 / 空白 / 标点。
    /// </summary>
    private static bool UnderscoreOk(string text, int i, int markerLength, int open, int close)
    {
        if (i > 0 && !IsBreak(text[i - 1])) return false;
        if (open < text.Length && char.IsWhiteSpace(text[open])) return false;
        if (close > 0 && char.IsWhiteSpace(text[close - 1])) return false;

        var after = close + markerLength;
        return after >= text.Length || IsBreak(text[after]);
    }

    private static bool IsBreak(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool TryLink(
        string text, ref int i, MarkdownStyle inherited, List<MarkdownSpan> into, Action flush)
    {
        if (text[i] != '[') return false;

        var mid = text.IndexOf("](", i, StringComparison.Ordinal);
        if (mid < 0) return false;
        var end = text.IndexOf(')', mid + 2);
        if (end < 0) return false;

        var label = text[(i + 1)..mid];
        var target = text[(mid + 2)..end].Trim();
        if (label.Length == 0 || target.Length == 0) return false;

        // [文字](地址 "标题")：标题不要，只取地址那一段
        var space = target.IndexOfAny(new[] { ' ', '\t' });
        var url = space > 0 ? target[..space] : target;

        // 只放行 http(s)：说明文字是从网上来的，别让它把 file:// 之类的塞给系统去打开
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        flush();
        into.Add(new MarkdownSpan { Text = label, Style = inherited, Link = url });
        i = end + 1;
        return true;
    }

    private static int HeadingLevel(string line, out string content)
    {
        content = line;

        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level is 0 or > 6) return 0;
        if (level >= line.Length || line[level] != ' ') return 0;   // "#没有空格" 不算标题

        content = TrimClosingHashes(line[(level + 1)..].Trim());
        return content.Length > 0 ? level : 0;
    }

    /// <summary>ATX 标题收尾的井号（<c># 标题 #</c>）不算文字。</summary>
    private static string TrimClosingHashes(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] == '#') end--;
        if (end == text.Length) return text;

        var stripped = text[..end].TrimEnd();
        return stripped.Length > 0 ? stripped : text;   // 整行都是井号就别乱切
    }

    private static bool TryBullet(string line, out string content)
    {
        content = line;
        if (line.Length < 3) return false;
        if (line[0] is not ('-' or '*' or '+')) return false;
        if (line[1] != ' ') return false;

        content = line[2..].Trim();
        return content.Length > 0;
    }

    private static bool TryNumbered(string line, out string number, out string content)
    {
        number = "";
        content = line;

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;
        if (digits == 0 || digits >= line.Length) return false;
        if (line[digits] is not ('.' or '、' or ')')) return false;
        if (digits + 1 < line.Length && line[digits + 1] != ' ') return false;

        number = line[..digits] + ".";
        content = line[(digits + 1)..].Trim();
        return content.Length > 0;
    }

    private static bool IsRule(string line)
    {
        if (line.Length < 3) return false;
        return line.All(c => c == '-') || line.All(c => c == '*') || line.All(c => c == '_');
    }

    /// <summary>围栏代码块的开头：<c>```</c> 或 <c>~~~</c>（至少三个）。</summary>
    private static bool TryFence(string line, out string fence)
    {
        fence = "";
        if (line.Length < 3) return false;

        var c = line[0];
        if (c is not ('`' or '~')) return false;

        var length = 0;
        while (length < line.Length && line[length] == c) length++;
        if (length < 3) return false;

        fence = new string(c, length);
        return true;
    }

    /// <summary>表格的分隔行：只有 | - : 和空白，且带横线。</summary>
    private static bool IsTableSeparator(string line)
    {
        var text = line.Trim();
        if (text.Length < 3) return false;
        if (text[0] != '|' && text[^1] != '|') return false;
        if (!text.Contains('-')) return false;

        foreach (var c in text)
            if (c is not ('|' or '-' or ':' or ' ' or '\t')) return false;

        return true;
    }

    /// <summary>表格的一行 → 各格文字（去掉首尾竖线、逐格 Trim）。</summary>
    private static string[] SplitRow(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];
        return text.Split('|').Select(cell => cell.Trim()).ToArray();
    }

    /// <summary>把 <c>&lt;br&gt;</c> / <c>&lt;br/&gt;</c> / <c>&lt;br /&gt;</c> 当换行，拆成多个块。</summary>
    private static List<string> SplitBr(string text)
    {
        var parts = new List<string>();
        var start = 0;
        var at = 0;

        while (true)
        {
            var lt = text.IndexOf("<br", at, StringComparison.OrdinalIgnoreCase);
            if (lt < 0) break;

            var gt = text.IndexOf('>', lt);
            if (gt < 0) break;

            var between = text[(lt + 3)..gt].Trim().TrimEnd('/').Trim();   // "" / "/" / " /"
            if (between.Length > 0) { at = lt + 3; continue; }             // 别的标签（<brrr> 之类）不碰

            parts.Add(text[start..lt]);
            start = gt + 1;
            at = start;
        }

        parts.Add(text[start..]);
        return parts;
    }

    /// <summary>列表缩进：2 空格一级（Tab 算 2 空格），最多 4 级。</summary>
    private static int IndentLevel(string raw)
    {
        var spaces = 0;
        foreach (var c in raw)
        {
            if (c == ' ') spaces++;
            else if (c == '\t') spaces += 2;
            else break;
        }

        return Math.Min(spaces / 2, 4);
    }

    // ---- 渲染 ----

    private static double HeadingSize(int level) => level switch
    {
        1 => 20,
        2 => 17,
        3 => 15,
        _ => 14,
    };

    private static Inline ToInline(MarkdownSpan span)
    {
        if (span.Link is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var hyperlink = new Hyperlink { NavigateUri = uri };
            hyperlink.Inlines.Add(new Run { Text = span.Text, FontWeight = Weight(span.Style) });
            return hyperlink;
        }

        var run = new Run { Text = span.Text, FontWeight = Weight(span.Style) };
        if (span.Style.HasFlag(MarkdownStyle.Italic)) run.FontStyle = FontStyle.Italic;
        if (span.Style.HasFlag(MarkdownStyle.Code)) run.FontFamily = CodeFont();
        if (span.Style.HasFlag(MarkdownStyle.Strike)) run.TextDecorations = TextDecorations.Strikethrough;
        return run;
    }

    private static FontWeight Weight(MarkdownStyle style) =>
        style.HasFlag(MarkdownStyle.Bold) ? FontWeights.SemiBold : FontWeights.Normal;

    private static FontFamily CodeFont() => new("Consolas");
}
