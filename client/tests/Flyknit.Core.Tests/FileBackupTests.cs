using System;
using System.IO;
using System.Linq;
using System.Threading;
using Flyknit.Core.Tools;
using Xunit;

namespace Flyknit.Core.Tests;

public class FileBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flyknit-backup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work;
    private readonly string _store;

    public FileBackupTests()
    {
        _work = Path.Combine(_root, "work");
        _store = Path.Combine(_root, "backups");
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { /* 清不掉算了 */ }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void KeepsTheContentThatIsAboutToBeOverwritten()
    {
        var file = Write("report.txt", "九月的原始数据");
        var backup = new FileBackup(_store);

        var saved = backup.Capture(file);
        File.WriteAllText(file, "被覆盖了");

        Assert.NotNull(saved);
        Assert.Equal("九月的原始数据", File.ReadAllText(saved!));
        Assert.Equal("被覆盖了", File.ReadAllText(file));
    }

    [Fact]
    public void NewFilesHaveNothingToKeep()
    {
        var backup = new FileBackup(_store);

        Assert.Null(backup.Capture(Path.Combine(_work, "does-not-exist.txt")));
        Assert.Null(backup.Capture(Write("empty.txt", "")));
    }

    [Fact]
    public void SameNameInDifferentFoldersDoesNotCollide()
    {
        // 到处都有 README.md，只按文件名存会互相覆盖
        var a = Path.Combine(_work, "a"); Directory.CreateDirectory(a);
        var b = Path.Combine(_work, "b"); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "README.md"), "A 的内容");
        File.WriteAllText(Path.Combine(b, "README.md"), "B 的内容");
        var backup = new FileBackup(_store);

        var sa = backup.Capture(Path.Combine(a, "README.md"));
        var sb = backup.Capture(Path.Combine(b, "README.md"));

        Assert.NotNull(sa);
        Assert.NotNull(sb);
        Assert.NotEqual(sa, sb);
        Assert.Equal("A 的内容", File.ReadAllText(sa!));
        Assert.Equal("B 的内容", File.ReadAllText(sb!));
    }

    [Fact]
    public void VersionsOfFindsEveryCopyNewestFirst()
    {
        var file = Write("notes.txt", "第一版");
        var backup = new FileBackup(_store);

        backup.Capture(file);
        File.WriteAllText(file, "第二版");
        Thread.Sleep(1100); // 备份名按秒取时间戳
        backup.Capture(file);

        var versions = backup.VersionsOf(file);
        Assert.Equal(2, versions.Count);
        Assert.Equal("第二版", File.ReadAllText(versions[0]));
        Assert.Equal("第一版", File.ReadAllText(versions[1]));
        // 别的文件的备份不该混进来
        Assert.Empty(backup.VersionsOf(Path.Combine(_work, "other.txt")));
    }

    [Fact]
    public void HugeFilesAreSkippedRatherThanCopied()
    {
        var file = Path.Combine(_work, "big.bin");
        using (var fs = File.Create(file))
        {
            fs.SetLength(FileBackup.MaxFileBytes + 1);
        }
        var warnings = new System.Collections.Generic.List<string>();
        var backup = new FileBackup(_store, warn: warnings.Add);

        Assert.Null(backup.Capture(file));
        Assert.Contains(warnings, w => w.Contains("跳过备份"));
    }

    [Fact]
    public void OldestCopiesGoWhenTheQuotaIsReached()
    {
        var backup = new FileBackup(_store, maxBytes: 3000);
        var payload = new string('x', 1000);

        for (var i = 0; i < 8; i++)
        {
            var f = Write($"f{i}.txt", payload);
            backup.Capture(f);
        }

        Assert.True(backup.UsedBytes() <= 3000, $"占用 {backup.UsedBytes()} 超过上限");
        Assert.True(backup.UsedBytes() > 0, "不该把备份全删光");
    }

    [Fact]
    public void EmptyDayFoldersAreCleanedUp()
    {
        var backup = new FileBackup(_store, maxBytes: 500);
        backup.Capture(Write("a.txt", new string('x', 2000)));
        backup.Trim();

        if (Directory.Exists(_store))
        {
            Assert.Empty(Directory.EnumerateDirectories(_store)
                .Where(d => !Directory.EnumerateFileSystemEntries(d).Any()));
        }
    }

    [Fact]
    public void FailureToBackUpNeverThrows()
    {
        // 备份目录指到一个「父级是文件」的位置，建不出来，Capture 要安静地返回 null
        var blocker = Write("nope.txt", "我是文件不是目录");
        var backup = new FileBackup(Path.Combine(blocker, "sub"));
        var file = Write("x.txt", "内容");

        var saved = backup.Capture(file);
        Assert.Null(saved);
        Assert.Equal("内容", File.ReadAllText(file)); // 原文件没被动过
    }

    [Fact]
    public void UsedBytesIsZeroBeforeAnythingIsKept()
    {
        Assert.Equal(0, new FileBackup(_store).UsedBytes());
    }
}
