using Agent.Common;
using Xunit;

/// <summary>文件夹复制（2026-10 新增）的打包/解压/命名清理逻辑</summary>
public class FolderTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rc-foldertest-" + Guid.NewGuid().ToString("N"));

    private string Dir(params string[] parts)
    {
        var p = Path.Combine(_root, Path.Combine(parts));
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void PackFolder_目录不存在_返回错误()
    {
        var zip = FolderTransfer.PackFolder(Path.Combine(_root, "no-such-dir"), out var err);
        Assert.Null(zip);
        Assert.Contains("不存在", err);
    }

    [Fact]
    public void PackFolder_打包后可解压还原内容()
    {
        var dir = Dir("我的资料");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello 文件夹");
        Dir("我的资料", "子目录");
        File.WriteAllText(Path.Combine(dir, "子目录", "b.bin"), new string('x', 1000));

        var zip = FolderTransfer.PackFolder(dir, out var err);
        Assert.NotNull(zip);
        Assert.Equal("", err);
        Assert.True(File.Exists(zip));

        // 解压到新目录：内容与结构一致（ExtractToUniqueDir 自带重名避让）
        var out1 = FolderTransfer.ExtractToUniqueDir(zip!, _root, "还原");
        var out2 = FolderTransfer.ExtractToUniqueDir(zip!, _root, "还原");
        Assert.NotNull(out1);
        Assert.NotNull(out2);
        Assert.NotEqual(out1, out2);                       // 重名自动加 (2)
        Assert.Equal("hello 文件夹", File.ReadAllText(Path.Combine(out1!, "a.txt")));
        Assert.Equal(1000, new FileInfo(Path.Combine(out1, "子目录", "b.bin")).Length);

        // 清理：PackFolder 产生的临时 zip 所在目录
        try { Directory.Delete(Path.GetDirectoryName(zip!)!, true); } catch { }
    }

    [Fact]
    public void PackFolder_空目录也能打包()
    {
        var dir = Dir("empty");
        var zip = FolderTransfer.PackFolder(dir, out var err);
        Assert.NotNull(zip);
        var dest = FolderTransfer.ExtractToUniqueDir(zip!, _root, "empty-out");
        Assert.NotNull(dest);
        Assert.Empty(Directory.GetFileSystemEntries(dest!));
        try { Directory.Delete(Path.GetDirectoryName(zip!)!, true); } catch { }
    }

    [Fact]
    public void SanitizeFolderName_非法字符与结尾点空格被清理()
    {
        Assert.Equal("a_b__c", FolderTransfer.SanitizeFolderName("a<b>:c"));   // < > : 三个都是非法字符
        Assert.Equal("名字", FolderTransfer.SanitizeFolderName("名字. "));
        var allBad = FolderTransfer.SanitizeFolderName(new string(Path.GetInvalidFileNameChars()));
        Assert.NotEqual("", allBad);                                   // 变成下划线，不会是空串
        Assert.All(allBad, c => Assert.False(Path.GetInvalidFileNameChars().Contains(c)));   // 但绝无非法字符
    }

    [Fact]
    public void 剪贴板DTO_文件夹字段往返()
    {
        var info = new ClipboardFileInfo
        {
            FileId = "f1", FileName = "资料.zip", FileSize = 123, IsFolder = true, FolderName = "资料",
        };
        var json = JsonCodec.Encode(info);
        var back = JsonCodec.Decode<ClipboardFileInfo>(json);
        Assert.NotNull(back);
        Assert.True(back!.IsFolder);
        Assert.Equal("资料", back.FolderName);

        var drop = new FileDropInfo { LocalPath = @"C:\x\资料", FileName = "资料", IsFolder = true };
        var back2 = JsonCodec.Decode<FileDropInfo>(JsonCodec.Encode(drop));
        Assert.NotNull(back2);
        Assert.True(back2!.IsFolder);
        // 旧版主控端/被控端收到的 JSON 里没有这些字段 → 反序列化成默认值，不炸
        var legacy = JsonCodec.Decode<ClipboardFileInfo>("{\"FileId\":\"a\",\"FileName\":\"b\",\"FileSize\":1}");
        Assert.NotNull(legacy);
        Assert.False(legacy!.IsFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
