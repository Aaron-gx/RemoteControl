using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Viewer;

/// <summary>
/// 极简 Markdown → WPF FlowDocument 渲染（够用即可，不引第三方库）。
/// 支持：标题(#~####)、段落、无序/有序列表、表格、围栏代码块、引用块、水平线，
/// 行内：**粗体**、`代码`、[文字](链接)。
/// 说明窗口用它把 docs\使用说明.md 渲染成正常排版（之前是纯文本框，直接显示 ## 和 | 符号）。
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
    private static readonly Brush FgMuted = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0xA8, 0xE8));
    private static readonly Brush CodeBg = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
    private const string Mono = "Consolas, Microsoft YaHei UI";

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = Fg,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            PagePadding = new Thickness(2, 0, 8, 0),
            LineHeight = 21,
        };

        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];

            // 空行
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // 围栏代码块
            if (line.TrimStart().StartsWith("```"))
            {
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) { code.AppendLine(lines[i]); i++; }
                i++;   // 跳过结束的 ```
                doc.Blocks.Add(CodeBlock(code.ToString().TrimEnd('\n')));
                continue;
            }

            // 标题
            if (line.StartsWith("#"))
            {
                int level = 0;
                while (level < line.Length && line[level] == '#') level++;
                string text = line[level..].Trim();
                if (text.Length > 0)
                {
                    doc.Blocks.Add(new Paragraph(new Run(text))
                    {
                        FontSize = level switch { 1 => 21, 2 => 17, 3 => 14.5, _ => 13 },
                        FontWeight = FontWeights.SemiBold,
                        Foreground = level <= 2 ? Brushes.White : Fg,
                        Margin = new Thickness(0, level == 1 ? 2 : 14, 0, level == 1 ? 10 : 6),
                    });
                    if (level == 1)
                    {
                        doc.Blocks.Add(new Paragraph
                        {
                            BorderBrush = Line,
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            Margin = new Thickness(0, 0, 0, 10),
                        });
                    }
                    i++;
                    continue;
                }
            }

            // 水平线
            if (line.Trim() is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = Line,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 10, 0, 10),
                });
                i++;
                continue;
            }

            // 表格（连续以 | 开头的行）
            if (line.TrimStart().StartsWith("|"))
            {
                var rows = new List<string[]>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|"))
                {
                    var cells = SplitRow(lines[i]);
                    // 跳过 |---|---| 分隔行
                    if (!cells.All(c => c.Trim().Trim('-', ':').Length == 0))
                        rows.Add(cells);
                    i++;
                }
                if (rows.Count > 0) doc.Blocks.Add(Table(rows));
                continue;
            }

            // 引用块
            if (line.TrimStart().StartsWith(">"))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">"))
                {
                    quote.AppendLine(lines[i].TrimStart()[1..].Trim());
                    i++;
                }
                doc.Blocks.Add(new Paragraph(Inline(quote.ToString().TrimEnd()))
                {
                    Foreground = FgMuted,
                    BorderBrush = Line,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Margin = new Thickness(0, 4, 0, 8),
                });
                continue;
            }

            // 列表（无序 / 有序）
            if (IsBullet(line, out _) || IsOrdered(line, out _))
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(18, 2, 0, 8) };
                while (i < lines.Length && (IsBullet(lines[i], out _) || IsOrdered(lines[i], out _)))
                {
                    string itemText = IsBullet(lines[i], out var b) ? b : (IsOrdered(lines[i], out var o) ? o : "");
                    var p = new Paragraph(Inline(itemText)) { Margin = new Thickness(0, 1, 0, 1) };
                    list.ListItems.Add(new ListItem(p));
                    if (list.MarkerStyle == TextMarkerStyle.Disc && IsOrdered(lines[i], out _))
                        list.MarkerStyle = TextMarkerStyle.Decimal;
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // 普通段落（连续非空行合并）
            var para = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].StartsWith("#") && !lines[i].TrimStart().StartsWith("|")
                   && !lines[i].TrimStart().StartsWith(">") && !lines[i].TrimStart().StartsWith("```")
                   && !IsBullet(lines[i], out _) && !IsOrdered(lines[i], out _))
            {
                if (para.Length > 0) para.Append(' ');
                para.Append(lines[i].Trim());
                i++;
            }
            doc.Blocks.Add(new Paragraph(Inline(para.ToString())) { Margin = new Thickness(0, 3, 0, 3) });
        }
        return doc;
    }

    private static bool IsBullet(string line, out string text)
    {
        var t = line.TrimStart();
        if (t.StartsWith("- ") || t.StartsWith("* ") || t.StartsWith("· "))
        {
            text = t[2..].Trim();
            return true;
        }
        text = "";
        return false;
    }

    private static bool IsOrdered(string line, out string text)
    {
        var t = line.TrimStart();
        int k = 0;
        while (k < t.Length && char.IsDigit(t[k])) k++;
        if (k > 0 && k + 1 < t.Length && (t[k] == '.' || t[k] == ')') && t[k + 1] == ' ')
        {
            text = t[(k + 2)..].Trim();
            return true;
        }
        text = "";
        return false;
    }

    private static string[] SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|")) t = t[1..];
        if (t.EndsWith("|")) t = t[..^1];
        return t.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static Table Table(List<string[]> rows)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
        int cols = rows.Max(r => r.Length);
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        for (int r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                string text = c < rows[r].Length ? rows[r][c] : "";
                var para = new Paragraph(Inline(text)) { Margin = new Thickness(0) };
                if (r == 0) para.FontWeight = FontWeights.SemiBold;
                var cell = new TableCell(para) { Padding = new Thickness(8, 5, 8, 5) };
                if (r == 0) cell.Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
                else if (r % 2 == 0) cell.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x24));
                cell.BorderBrush = Line;
                cell.BorderThickness = new Thickness(0, 0, 0, 1);
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static Paragraph CodeBlock(string code)
        => new(new Run(code))
        {
            FontFamily = new FontFamily(Mono),
            FontSize = 12,
            Background = CodeBg,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 10),
        };

    /// <summary>行内元素：**粗体**、`代码`、[文字](链接)、其余纯文本</summary>
    private static Inline Inline(string text)
    {
        var span = new Span();
        int i = 0;
        while (i < text.Length)
        {
            // **粗体**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    span.Inlines.Add(new Bold(new Run(text[(i + 2)..end])));
                    i = end + 2;
                    continue;
                }
            }
            // `代码`
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > 0)
                {
                    span.Inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = new FontFamily(Mono),
                        FontSize = 12,
                        Background = CodeBg,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xE6, 0xC8)),
                    });
                    i = end + 1;
                    continue;
                }
            }
            // [文字](链接)
            if (text[i] == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > 0 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    int paren = text.IndexOf(')', close + 2);
                    if (paren > 0)
                    {
                        string labelText = text[(i + 1)..close];
                        string url = text[(close + 2)..paren];
                        var link = new Hyperlink(new Run(labelText))
                        {
                            Foreground = Accent,
                            Cursor = System.Windows.Input.Cursors.Hand,
                            ToolTip = url,
                        };
                        link.Click += (_, _) => OpenUrl(url);
                        span.Inlines.Add(link);
                        i = paren + 1;
                        continue;
                    }
                }
            }
            // 普通文本
            int next = i + 1;
            while (next < text.Length && text[next] != '*' && text[next] != '`' && text[next] != '[') next++;
            span.Inlines.Add(new Run(text[i..next]));
            i = next;
        }
        return span;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
