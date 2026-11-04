using System.IO;
using System.Windows;

namespace ClipProbe;

/// <summary>
/// 剪贴板探针：验证主控端「自动接收远程文件 → 放进本机剪贴板」这一步真的能被粘贴。
/// 做法与线上完全一致（Clipboard.SetData(DataFormats.FileDrop, string[]) + 重试），
/// 然后读回来核对：剪贴板里确实有文件、路径一致、文件存在。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 剪贴板文件探针 ===");

        var dir = Path.Combine(Path.GetTempPath(), "clip-probe");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "probe-file.txt");
        File.WriteAllText(file, "clip probe " + DateTime.Now.ToString("HH:mm:ss"));
        Console.WriteLine("  测试文件：" + file);

        string backup = "";
        try { if (Clipboard.ContainsText()) backup = Clipboard.GetText(); } catch { }
        int fails = 0;

        bool wrote = false;
        for (int i = 0; i < 6 && !wrote; i++)
        {
            try
            {
                Clipboard.SetData(DataFormats.FileDrop, new[] { file });
                wrote = Clipboard.ContainsFileDropList();
            }
            catch (Exception ex)
            {
                Console.WriteLine("    第 " + (i + 1) + " 次写入失败：" + ex.Message);
                Thread.Sleep(150);
            }
        }
        Console.WriteLine((wrote ? "  [PASS] " : "  [FAIL] ") + "把文件写进剪贴板（FileDrop 格式）");
        if (!wrote) fails++;

        if (wrote)
        {
            try
            {
                var list = Clipboard.GetFileDropList();
                string? got = list.Count > 0 ? list[0] : null;
                bool samePath = string.Equals(got, file, StringComparison.OrdinalIgnoreCase);
                bool exists = got != null && File.Exists(got);
                Console.WriteLine((samePath && exists ? "  [PASS] " : "  [FAIL] ") +
                                  "读回剪贴板里的文件：" + got +
                                  "（路径一致=" + samePath + "，文件存在=" + exists + "）");
                if (!(samePath && exists)) fails++;

                bool stillThere = Clipboard.ContainsFileDropList();
                Console.WriteLine((stillThere ? "  [PASS] " : "  [FAIL] ") + "文件格式仍在剪贴板里（可被 Ctrl+V 取用）");
                if (!stillThere) fails++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("  [FAIL] 读回剪贴板失败：" + ex.Message);
                fails++;
            }
        }

        try
        {
            if (!string.IsNullOrEmpty(backup)) Clipboard.SetText(backup);
            else Clipboard.Clear();
            Console.WriteLine("  已还原原有剪贴板文本");
        }
        catch { }

        Console.WriteLine(fails == 0
            ? "  ==> 通过：文件能被放进剪贴板并粘出来"
            : "  ==> 有 " + fails + " 项失败");
        Environment.Exit(fails == 0 ? 0 : 1);
        return fails == 0 ? 0 : 1;
    }
}
