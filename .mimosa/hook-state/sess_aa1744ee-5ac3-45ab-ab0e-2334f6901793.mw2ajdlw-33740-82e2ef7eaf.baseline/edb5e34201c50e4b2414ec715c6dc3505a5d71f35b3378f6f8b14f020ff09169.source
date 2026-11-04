using System.Windows.Documents;
using System.Text;

namespace LicenseKeygen;

/// <summary>
/// Markdown → WPF FlowDocument 渲染器（「使用说明」标签页用它显示内置帮助文档）。
/// 按行状态机解析，不用正则处理整篇：帮助文档里代码块/表格/中文硬折行混在一起，
/// 标记一旦不配对，回溯式写法很容易失配甚至爆栈；逐行判断对畸形输入更好兜。
/// 配色按上位机的浅色主题定（Viewer 工程里另有一份深色版）。
/// </summary>
public static class MarkdownRenderer
{
    // ── 颜色。全部 Freeze：文档只读，冻结后不再产生变更通知开销，也能跨线程共用 ──
    private static readonly System.Windows.Media.Brush BodyFg = Frozen(0x1F, 0x1F, 0x1F);
    private static readonly System.Windows.Media.Brush H1Fg = Frozen(0x0B, 0x5F, 0xA5);
    private static readonly System.Windows.Media.Brush H2Fg = Frozen(0x0F, 0x6C, 0xBD);
    private static readonly System.Windows.Media.Brush H3Fg = Frozen(0x14, 0x63, 0x9E);
    private static readonly System.Windows.Media.Brush CodeFg = Frozen(0x12, 0x3B, 0x5C);
    private static readonly System.Windows.Media.Brush CodeBg = Frozen(0xEE, 0xF1, 0xF5);
    private static readonly System.Windows.Media.Brush LinkFg = Frozen(0x0F, 0x6C, 0xBD);
    private static readonly System.Windows.Media.Brush QuoteBar = Frozen(0x9C, 0xC3, 0xE5);
    private static readonly System.Windows.Media.Brush QuoteFg = Frozen(0x4A, 0x55, 0x68);
    private static readonly System.Windows.Media.Brush RuleLine = Frozen(0xD5, 0xDD, 0xE5);
    private static readonly System.Windows.Media.Brush HeadRuleLine = Frozen(0xC8, 0xDF, 0xF2);
    private static readonly System.Windows.Media.Brush TableHeadBg = Frozen(0xE3, 0xF0, 0xFA);
    private static readonly System.Windows.Media.Brush TableLine = Frozen(0xC6, 0xD4, 0xE2);

    // 只写 Windows 自带字体，不写 fallback 列表：WPF 缺字时会自动回落，写了反而让 FontFamily.Source 变成一长串
    private static readonly System.Windows.Media.FontFamily BodyFont = new("Microsoft YaHei UI");
    private static readonly System.Windows.Media.FontFamily MonoFont = new("Consolas");

    private const double BodySize = 13;
    private const double CodeSize = 12;

    // 正文段落不写死 FontSize：让它继承文档的 13，调用方把 doc.FontSize 改大改小就能整体缩放（说明窗口的 A-/A+）
    private const double BodyLineHeight = 21;

    // ── 间距：集中放这里，调排版不用到处翻 ──
    private static readonly System.Windows.Thickness DocPadding = new(18, 14, 22, 18);
    private static readonly System.Windows.Thickness ParaMargin = new(0, 0, 0, 9);
    private static readonly System.Windows.Thickness ItemMargin = new(0, 1, 0, 2);
    private static readonly System.Windows.Thickness H1Margin = new(0, 15, 0, 9);
    private static readonly System.Windows.Thickness H2Margin = new(0, 14, 0, 7);
    private static readonly System.Windows.Thickness H3Margin = new(0, 12, 0, 6);
    private static readonly System.Windows.Thickness H4Margin = new(0, 10, 0, 5);
    private static readonly System.Windows.Thickness RuleMargin = new(0, 12, 0, 12);
    private static readonly System.Windows.Thickness QuoteMargin = new(0, 6, 0, 10);
    private static readonly System.Windows.Thickness CodeMargin = new(0, 6, 0, 11);
    // 列表左边距：WPF 把项目符号画在文字列左侧（悬挂），不留边距的话有序列表的 "10." 会贴到页面最左边
    private static readonly System.Windows.Thickness ListMargin = new(14, 3, 0, 9);
    private static readonly System.Windows.Thickness SubListMargin = new(22, 2, 0, 2);
    private static readonly System.Windows.Thickness TableMargin = new(0, 6, 0, 12);
    private static readonly System.Windows.Thickness CellPadding = new(8, 5, 8, 5);
    private static readonly System.Windows.Thickness CodePadding = new(12, 9, 12, 9);
    private static readonly System.Windows.Thickness QuotePadding = new(12, 2, 0, 2);
    private static readonly System.Windows.Thickness NoPadding = new(0);

    // 递归上限：引用块套引用块、行内标记套行内标记时，防止畸形输入把栈吃穿
    private const int MaxBlockDepth = 6;
    private const int MaxInlineDepth = 6;

    /// <summary>把 Markdown 文本渲染成 FlowDocument（供 FlowDocumentScrollViewer 显示）。</summary>
    public static System.Windows.Documents.FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = BodyFont,
            FontSize = BodySize,
            Foreground = BodyFg,
            // 背景留透明：底色交给宿主（TabItem / FlowDocumentScrollViewer）定，这里不抢
            Background = System.Windows.Media.Brushes.Transparent,
            PagePadding = DocPadding,
        };

        if (string.IsNullOrWhiteSpace(markdown)) return doc;   // 空文档/纯空白：给个空文档就行，别抛

        ParseBlocks(Lines(markdown), doc.Blocks, 0);
        return doc;
    }

    /// <summary>统一换行符：只 Split('\n') 的话，老 Mac 的 \r 会以控制字符留在行尾</summary>
    private static string[] Lines(string markdown)
        => markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ────────────────────────────────────────────── 块级解析

    /// <summary>块级主循环：认出一块就交给对应的处理函数，处理函数返回下一行的下标</summary>
    private static void ParseBlocks(string[] lines, BlockCollection target, int depth)
    {
        int i = 0;
        while (i < lines.Length)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0) { i++; continue; }

            if (IsFence(trimmed)) { i = FencedCode(lines, i, target); continue; }
            if (IsRule(trimmed)) { target.Add(Rule()); i++; continue; }   // 表格分隔行带 |，不会走到这里被当成横线
            if (TryHeading(trimmed, out int level, out string title)) { target.Add(Heading(title, level)); i++; continue; }
            if (trimmed[0] == '>') { i = Quote(lines, i, target, depth); continue; }
            if (IsTableStart(lines, i)) { i = Table(lines, i, target); continue; }
            if (IsListItem(lines[i], out _, out _, out _, out _)) { i = List(lines, i, target); continue; }
            i = Paragraph(lines, i, target);
        }
    }

    /// <summary>
    /// 这一行是不是某个块的开始（段落靠它判断该不该收尾）。
    /// 单独抽出来是为了让"段落什么时候结束"和主循环用同一套判断，两边不会说不到一块去。
    /// </summary>
    private static bool StartsBlock(string[] lines, int i)
    {
        string trimmed = lines[i].Trim();
        if (trimmed.Length == 0) return true;
        if (IsFence(trimmed) || IsRule(trimmed) || trimmed[0] == '>') return true;
        if (TryHeading(trimmed, out _, out _)) return true;
        if (IsListItem(lines[i], out _, out _, out _, out _)) return true;
        return IsTableStart(lines, i);
    }

    /// <summary>普通段落：一直吃到下一个块开始/空行，多行按软换行拼起来</summary>
    private static int Paragraph(string[] lines, int start, BlockCollection target)
    {
        var para = new Paragraph { Margin = ParaMargin, LineHeight = BodyLineHeight };
        var joiner = new Joiner(para);
        int i = start;
        while (i < lines.Length && !StartsBlock(lines, i)) { joiner.Add(lines[i].Trim(), HardBreak(lines[i])); i++; }
        if (i == start) { joiner.Add(lines[start].Trim(), false); i++; }   // 保险：一行都没吃进去也往前挪，绝不原地打转
        target.Add(para);
        return i;
    }

    /// <summary>分隔线：一条浅灰横线（空段落 + 只留下边框，比塞一个 Table 轻）</summary>
    private static Paragraph Rule()
    {
        var p = new Paragraph
        {
            Margin = RuleMargin,
            BorderBrush = RuleLine,
            BorderThickness = new System.Windows.Thickness(0, 1, 0, 0),
            LineHeight = 1,        // 段落本身没有文字，给个极小的行高，边框才有依附的高度
        };
        return p;
    }

    /// <summary>整行只有 3 个以上的 -、*、_（可以有空格）：是分隔线，不是列表也不是表格</summary>
    private static bool IsRule(string trimmed)
    {
        char c = trimmed[0];
        if (c != '-' && c != '*' && c != '_') return false;
        int count = 0;
        foreach (char ch in trimmed)
        {
            if (ch == c) { count++; continue; }
            if (ch == ' ' || ch == '\t') continue;
            return false;          // 混进了别的字符：比如 ***加粗***、- 列表项
        }
        return count >= 3;
    }

    // ── 标题 ──

    /// <summary>
    /// 识别 # 开头的一行。要求 # 后面跟空白（"#标签" 不算标题），
    /// 结尾的闭合 #（"## 标题 ##"）只在前面有空白时才算修饰，免得把 "### C#" 的 # 削掉。
    /// </summary>
    private static bool TryHeading(string trimmed, out int level, out string title)
    {
        level = 0;
        title = "";
        int n = 0;
        while (n < trimmed.Length && trimmed[n] == '#') n++;
        if (n == 0 || n > 6) return false;
        if (n < trimmed.Length && trimmed[n] != ' ' && trimmed[n] != '\t') return false;

        string rest = trimmed[n..].Trim();
        int e = rest.Length;
        while (e > 0 && rest[e - 1] == '#') e--;
        if (e < rest.Length && (e == 0 || rest[e - 1] == ' ' || rest[e - 1] == '\t')) rest = rest[..e].TrimEnd();
        if (rest.Length == 0) return false;      // 光有 "#"：当普通文本，别产生一个空标题块

        level = Math.Min(n, 4);                  // #### 以下不再变小，都按 4 级渲染
        title = rest;
        return true;
    }

    private static Paragraph Heading(string title, int level)
    {
        var p = new Paragraph
        {
            FontWeight = System.Windows.FontWeights.Bold,
            FontSize = level switch { 1 => 20d, 2 => 16d, 3 => 14d, _ => BodySize },
            Foreground = level switch { 1 => H1Fg, 2 => H2Fg, 3 => H3Fg, _ => BodyFg },
            LineHeight = level switch { 1 => 30d, 2 => 25d, 3 => 22d, _ => BodyLineHeight },
            Margin = level switch { 1 => H1Margin, 2 => H2Margin, 3 => H3Margin, _ => H4Margin },
        };
        if (level == 1)
        {
            // 一级标题下面一条浅色分隔线：让文档看出"分节"的结构
            p.BorderBrush = HeadRuleLine;
            p.BorderThickness = new System.Windows.Thickness(0, 0, 0, 1);
            p.Padding = new System.Windows.Thickness(0, 0, 0, 6);
        }
        Inline(title, p.Inlines, 0);   // 标题里也可以有 **粗体** / `代码`
        return p;
    }

    // ── 围栏代码块 ──

    private static bool IsFence(string trimmed)
        => trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);

    /// <summary>
    /// 围栏代码块。找不到收尾围栏（文档被截断）也照样渲染：
    /// 宁可多显示一段代码，也不能把后面的内容整段吞掉。
    /// </summary>
    private static int FencedCode(string[] lines, int start, BlockCollection target)
    {
        string fence = lines[start].Trim()[..3];   // 语言名（```powershell）只是装饰，不参与渲染
        var code = new StringBuilder();
        int i = start + 1;
        while (i < lines.Length)
        {
            if (lines[i].TrimStart().StartsWith(fence, StringComparison.Ordinal)) { i++; break; }
            code.Append(lines[i]).Append('\n');
            i++;
        }
        target.Add(CodeBlock(code.ToString().TrimEnd('\n')));
        return i;
    }

    private static Paragraph CodeBlock(string code)
    {
        var p = new Paragraph
        {
            FontFamily = MonoFont,
            FontSize = CodeSize,
            Foreground = CodeFg,
            Background = CodeBg,
            LineHeight = 19,
            Padding = CodePadding,
            Margin = CodeMargin,
        };
        // 逐行 Run + LineBreak，不把整段塞进一个 Run：空行也能占满一行高度，缩进和换行原样保留
        var codeLines = code.Split('\n');
        for (int k = 0; k < codeLines.Length; k++)
        {
            if (k > 0) p.Inlines.Add(new LineBreak());
            if (codeLines[k].Length > 0) p.Inlines.Add(new Run(codeLines[k]));
        }
        return p;
    }

    // ── 引用块 ──

    /// <summary>引用块：左侧竖条 + 灰字。灰字设在 Section 上让子块继承，内部的粗体/代码各自覆盖</summary>
    private static int Quote(string[] lines, int start, BlockCollection target, int depth)
    {
        var inner = new List<string>();
        int i = start;
        while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
        {
            string body = lines[i].TrimStart()[1..];
            if (body.StartsWith(' ')) body = body[1..];   // 去掉 "> " 的多余空格，">   缩进" 的缩进保留
            inner.Add(body);
            i++;
        }

        var section = new Section
        {
            Foreground = QuoteFg,
            BorderBrush = QuoteBar,
            BorderThickness = new System.Windows.Thickness(3, 0, 0, 0),
            Padding = QuotePadding,
            Margin = QuoteMargin,
        };
        if (depth < MaxBlockDepth) ParseBlocks(inner.ToArray(), section.Blocks, depth + 1);
        else section.Blocks.Add(new Paragraph(new Run(string.Join(" ", inner).Trim())) { Margin = ParaMargin });
        target.Add(section);
        return i;
    }

    // ── 表格 ──

    /// <summary>当前行带 |、下一行是 |---|---| 分隔行 → 表格从这里开始</summary>
    private static bool IsTableStart(string[] lines, int i)
        => i + 1 < lines.Length
           && lines[i].IndexOf('|') >= 0
           && IsTableSeparator(lines[i + 1]);

    /// <summary>分隔行：只允许 |、-、: 和空白，且至少一个 -（纯 --- 是分隔线，不算）</summary>
    private static bool IsTableSeparator(string line)
    {
        string t = line.Trim();
        if (t.IndexOf('|') < 0) return false;
        int dashes = 0;
        foreach (char c in t)
        {
            if (c == '-') { dashes++; continue; }
            if (c == '|' || c == ':' || c == ' ' || c == '\t') continue;
            return false;
        }
        return dashes > 0;
    }

    private static int Table(string[] lines, int start, BlockCollection target)
    {
        var rows = new List<string[]>();
        int i = start;
        while (i < lines.Length)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t.IndexOf('|') < 0) break;      // 遇到第一个不带 | 的行，表格结束
            if (!IsTableSeparator(t)) rows.Add(SplitRow(t));      // 分隔行本身不是数据
            i++;
        }
        if (rows.Count == 0) return i;

        // 列数取所有行的最大值：后面行多出来的单元格宁可加一列，也不能丢内容
        int cols = 1;
        foreach (var r in rows) cols = Math.Max(cols, r.Length);

        // 外框只画右/下、每个单元格画左/上：1px 细线不会两两叠成 2px
        var table = new Table
        {
            CellSpacing = 0,
            Margin = TableMargin,
            BorderBrush = TableLine,
            BorderThickness = new System.Windows.Thickness(0, 0, 1, 1),
        };
        // 列宽用 Star：列宽跟着窗口宽度走（列宽自适应），不会因为某列文字长就撑破排版
        for (int c = 0; c < cols; c++)
            table.Columns.Add(new TableColumn { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var cell = new TableCell
                {
                    Padding = CellPadding,
                    BorderBrush = TableLine,
                    BorderThickness = new System.Windows.Thickness(1, 1, 0, 0),
                };
                var para = new Paragraph { Margin = NoPadding, LineHeight = 20 };
                if (r == 0)
                {
                    cell.Background = TableHeadBg;
                    para.FontWeight = System.Windows.FontWeights.Bold;
                }
                Inline(c < rows[r].Length ? rows[r][c] : "", para.Inlines, 0);
                cell.Blocks.Add(para);
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        target.Add(table);
        return i;
    }

    /// <summary>拆单元格：去掉首尾的 |，中间的按 | 切（单元格里的 \| 转义不支持，文档里没有这种写法）</summary>
    private static string[] SplitRow(string trimmed)
    {
        string t = trimmed;
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|')) t = t[..^1];
        var parts = t.Split('|');
        for (int k = 0; k < parts.Length; k++) parts[k] = parts[k].Trim();
        return parts;
    }

    // ── 列表 ──

    /// <summary>
    /// 列表。项目符号、序号、悬挂缩进（多行文字对齐到文字列而不是顶到符号下面）都交给 WPF 的
    /// List/ListItem 自己做，手写 "• " 前缀做不到悬挂缩进。
    /// 缩进 ≥2 空格的项挂到当前一级项的 Blocks 里当子列表 —— 只支持这一层嵌套，更深的压到这一层。
    /// </summary>
    private static int List(string[] lines, int start, BlockCollection target)
    {
        var list = new List
        {
            MarkerStyle = System.Windows.TextMarkerStyle.Disc,
            MarkerOffset = 8,          // 符号与文字之间的距离
            Padding = NoPadding,
            Margin = ListMargin,
        };
        target.Add(list);

        bool ordered = false;
        bool styleKnown = false;
        ListItem? topItem = null;      // 最近的一级项：缩进项要挂到它里面
        List? sub = null;              // 一级项里的子列表（连续缩进项共用一个）
        Paragraph? current = null;     // 续行往这个段落里塞
        Joiner? joiner = null;
        int i = start;

        while (i < lines.Length)
        {
            string raw = lines[i];

            if (raw.Trim().Length == 0)
            {
                // 空行后面还有同级项 → 是"松散列表"，继续；否则本列表到此结束
                int j = i;
                while (j < lines.Length && lines[j].Trim().Length == 0) j++;
                if (j < lines.Length && IsListItem(lines[j], out _, out _, out int nextIndent, out _) && nextIndent < 2) { i = j; continue; }
                break;
            }

            if (IsListItem(raw, out bool itemOrdered, out int number, out int indent, out string text))
            {
                if (indent >= 2 && topItem != null)
                {
                    if (sub == null)
                    {
                        sub = new List
                        {
                            MarkerStyle = itemOrdered ? System.Windows.TextMarkerStyle.Decimal : System.Windows.TextMarkerStyle.Disc,
                            MarkerOffset = 8,
                            Padding = NoPadding,
                            Margin = SubListMargin,
                        };
                        topItem.Blocks.Add(sub);
                    }
                    var subPara = NewItemParagraph();
                    Inline(text, subPara.Inlines, 0);
                    sub.ListItems.Add(new ListItem(subPara));
                    current = subPara;
                    joiner = new Joiner(subPara);
                    i++;
                    continue;
                }

                if (styleKnown && itemOrdered != ordered) break;   // 中途换标记符：交给外层另起一个列表
                if (!styleKnown)
                {
                    styleKnown = true;
                    ordered = itemOrdered;
                    list.MarkerStyle = ordered ? System.Windows.TextMarkerStyle.Decimal : System.Windows.TextMarkerStyle.Disc;
                    if (ordered && number > 1) list.StartIndex = number;   // 尊重原文起始序号（"3. 4. 5."）
                }
                var para = NewItemParagraph();
                Inline(text, para.Inlines, 0);
                topItem = new ListItem(para);
                list.ListItems.Add(topItem);
                sub = null;
                current = para;
                joiner = new Joiner(para);
                i++;
                continue;
            }

            // 不是项目行：带缩进的当上一项的续行（中文文档里换行只是排版，不是新段落）；顶格的普通行结束列表
            if (current != null && joiner != null && IndentOf(raw) > 0)
            {
                joiner.Add(raw.Trim(), HardBreak(raw));
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>
    /// 认列表项："- "、"* "、"+"、"1. "、"2) "。
    /// 标记后面必须有空白，否则 "**粗体**" 开头的一行会被当成列表项；数字超过 9 位也不认（"2026.09 发布" 这类）。
    /// </summary>
    private static bool IsListItem(string line, out bool ordered, out int number, out int indent, out string text)
    {
        ordered = false;
        number = 0;
        text = "";
        indent = IndentOf(line);
        int k = 0;
        while (k < line.Length && (line[k] == ' ' || line[k] == '\t')) k++;
        if (k >= line.Length) return false;

        char c = line[k];
        if (c == '-' || c == '*' || c == '+')
        {
            if (k + 1 >= line.Length || (line[k + 1] != ' ' && line[k + 1] != '\t')) return false;
            text = line[(k + 1)..].Trim();
            return true;
        }

        if (!char.IsDigit(c)) return false;
        int d = k;
        while (d < line.Length && char.IsDigit(line[d])) d++;
        if (d - k > 9) return false;
        if (d >= line.Length || (line[d] != '.' && line[d] != ')')) return false;
        if (d + 1 >= line.Length || (line[d + 1] != ' ' && line[d + 1] != '\t')) return false;
        ordered = true;
        int.TryParse(line[k..d], out number);
        text = line[(d + 1)..].Trim();
        return true;
    }

    private static Paragraph NewItemParagraph() => new() { Margin = ItemMargin, LineHeight = BodyLineHeight };

    /// <summary>行首空白宽度（Tab 记 4 格）</summary>
    private static int IndentOf(string line)
    {
        int n = 0;
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == ' ') n++;
            else if (line[k] == '\t') n += 4;
            else break;
        }
        return n;
    }

    /// <summary>
    /// 把多行拼成一个段落。行尾两个以上空格 = Markdown 硬换行 → LineBreak；
    /// 其余软换行在中文之间不补空格，否则中文被硬折行的文档每行末尾都会多出一个英文空格。
    /// </summary>
    private sealed class Joiner
    {
        private readonly Paragraph _paragraph;
        private string _previous = "";
        private bool _hardBreak;

        public Joiner(Paragraph paragraph) => _paragraph = paragraph;

        public void Add(string text, bool hardBreak)
        {
            if (text.Length == 0) return;
            if (_previous.Length > 0)
            {
                if (_hardBreak) _paragraph.Inlines.Add(new LineBreak());
                else if (NeedSpace(_previous, text)) _paragraph.Inlines.Add(new Run(" "));
            }
            Inline(text, _paragraph.Inlines, 0);
            _previous = text;
            _hardBreak = hardBreak;
        }
    }

    private static bool NeedSpace(string previous, string next)
        => !(IsWide(previous[^1]) && IsWide(next[0]));

    /// <summary>行尾两个以上空格 = 硬换行</summary>
    private static bool HardBreak(string rawLine)
    {
        int end = rawLine.Length;
        while (end > 0 && (rawLine[end - 1] == ' ' || rawLine[end - 1] == '\t')) end--;
        return rawLine.Length - end >= 2;
    }

    /// <summary>
    /// 是否是中日韩文字/全角标点（判断软换行要不要补空格）。
    /// 只看"宽字符"就够用：中文折行的断点两边基本都是宽字符，补空格反而在句子中间多出一道缝。
    /// </summary>
    private static bool IsWide(char c)
        => (c >= 0x1100 && c <= 0x115F)     // 韩文字母
        || (c >= 0x2010 && c <= 0x2027)     // 破折号、引号、省略号
        || (c >= 0x2E80 && c <= 0xA4CF)     // 中日韩部首/假名/汉字
        || (c >= 0xAC00 && c <= 0xD7A3)     // 韩文音节
        || (c >= 0xF900 && c <= 0xFAFF)     // 兼容汉字
        || (c >= 0xFE30 && c <= 0xFE4F)     // 组合标点
        || (c >= 0xFF00 && c <= 0xFF60)     // 全角字符
        || (c >= 0xFFE0 && c <= 0xFFE6);    // 全角符号

    // ── 行内 ──

    /// <summary>
    /// 解析一行里的行内标记，追加到 target。段落、粗体、链接内部都调它，所以标记天然可嵌套。
    /// 线性扫描：找不到收尾标记就把标记字符当普通文本输出，不回退重扫、不抛异常。
    /// </summary>
    private static void Inline(string text, InlineCollection target, int depth)
    {
        if (text.Length == 0) return;
        if (depth > MaxInlineDepth) { target.Add(new Run(text)); return; }

        var plain = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // 反斜杠转义：只对 Markdown 元字符生效，像 C:\Users 这样的路径原样保留
            if (c == '\\' && i + 1 < text.Length && IsEscapable(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1) { Flush(plain, target); target.Add(CodeRun(text[(i + 1)..end])); i = end + 1; continue; }
            }
            else if (c == '*')
            {
                if (i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                    if (end > i + 2)
                    {
                        Flush(plain, target);
                        var bold = new Bold();
                        Inline(text[(i + 2)..end], bold.Inlines, depth + 1);
                        target.Add(bold);
                        i = end + 2;
                        continue;
                    }
                }
                else
                {
                    int end = ItalicEnd(text, i + 1);
                    if (end > 0)
                    {
                        Flush(plain, target);
                        var italic = new Italic();
                        Inline(text[(i + 1)..end], italic.Inlines, depth + 1);
                        target.Add(italic);
                        i = end + 1;
                        continue;
                    }
                }
            }
            else if (c == '[' || c == '!')
            {
                int at = c == '!' ? i + 1 : i;
                if (TryLink(text, at, out string label, out string url, out int end))
                {
                    if (c == '!')
                    {
                        // 图片不支持（FlowDocument 里插图要另开资源管线）：整段按原文显示，比只留个 "!" 清楚
                        plain.Append(text[i..end]);
                    }
                    else
                    {
                        Flush(plain, target);
                        target.Add(Link(label, url, depth));
                    }
                    i = end;
                    continue;
                }
            }

            plain.Append(c);
            i++;
        }
        Flush(plain, target);
    }

    /// <summary>
    /// 找 *斜体* 的收尾星号：必须是不属于 ** 的单个 *。
    /// 内容首尾贴空白就不认（"a * b * c" 里那对星号多半是乘号或通配符，不是斜体）。
    /// </summary>
    private static int ItalicEnd(string text, int start)
    {
        for (int k = start; k < text.Length; k++)
        {
            if (text[k] != '*') continue;
            if (k > start && text[k - 1] == '*') continue;                  // 属于 ** 的后半
            if (k + 1 < text.Length && text[k + 1] == '*') continue;        // 属于 ** 的前半
            if (k == start) return -1;                                       // "**" 之外的相邻星号不当收尾
            if (char.IsWhiteSpace(text[start]) || char.IsWhiteSpace(text[k - 1])) return -1;
            return k;
        }
        return -1;
    }

    /// <summary>[文字](网址)：任一步不配就返回 false，调用方按普通字符处理</summary>
    private static bool TryLink(string text, int start, out string label, out string url, out int end)
    {
        label = "";
        url = "";
        end = start;
        if (start >= text.Length || text[start] != '[') return false;
        int close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;
        int paren = text.IndexOf(')', close + 2);
        if (paren < 0) return false;

        string target = text[(close + 2)..paren].Trim();
        if (target.Length == 0) return false;
        foreach (char ch in target)
            if (char.IsWhiteSpace(ch)) return false;      // 网址里带空格的多半不是链接，别做成点不开的假链接

        label = text[(start + 1)..close];
        url = target;
        end = paren + 1;
        return true;
    }

    /// <summary>链接：蓝色下划线，点击用系统默认程序（浏览器）打开</summary>
    private static Hyperlink Link(string label, string url, int depth)
    {
        var link = new Hyperlink
        {
            Foreground = LinkFg,
            TextDecorations = System.Windows.TextDecorations.Underline,
            Cursor = System.Windows.Input.Cursors.Hand,     // 悬停变手型
            ToolTip = url,                                  // 说明文档里的链接多半要复制出去，先把地址亮出来
        };
        Inline(label, link.Inlines, depth + 1);
        link.Click += (_, _) => OpenUrl(url);
        return link;
    }

    private static Run CodeRun(string text) => new(text)
    {
        FontFamily = MonoFont,
        FontSize = CodeSize,
        Foreground = CodeFg,
        Background = CodeBg,
    };

    private static void Flush(StringBuilder plain, InlineCollection target)
    {
        if (plain.Length == 0) return;
        target.Add(new Run(plain.ToString()));
        plain.Clear();
    }

    /// <summary>反斜杠能转义的字符：Markdown 元字符 + 几个常见标点。字母数字不转义，Windows 路径才不会被吃掉</summary>
    private static bool IsEscapable(char c)
        => c is '*' or '_' or '`' or '[' or ']' or '(' or ')' or '#' or '+' or '-' or '.' or '!'
             or '|' or '~' or '>' or '<' or '{' or '}' or '"' or '\'' or ',' or '/' or ':' or '\\';

    private static void OpenUrl(string url)
    {
        // 交给 shell 打开：说明文档里的链接可能带中文或查询串，自己解析容易出错。
        // 失败也不往上抛——点击事件里抛异常会把整个窗口带崩，链接点不开不该是致命错误。
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    /// <summary>冻结画刷：只读文档反复渲染时省掉变更通知，也能安全地跨线程复用</summary>
    private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
