using System.Security.Cryptography;
using System.Text;

namespace Flyknit.Core.Documents;

/// <summary>
/// 文档画布里的文档：读出来给界面编辑、员工点保存时写回去。
/// 员工在画布里改的同时 AI 可能也在用 edit_file 改同一个文件：保存时带上打开时的版本号，
/// 文件在这期间被改过就不覆盖，把当前内容交回界面让员工决定（不悄悄丢掉任何一方的改动）。
/// </summary>
public static class DocumentCanvas
{
    /// <summary>画布只编辑 Markdown：所见即所得的编辑结果按 Markdown 存回去，别的格式会被改坏。</summary>
    public static bool IsDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown";

    /// <summary>画布里最多打开这么大的文档（再大就不是「一篇文稿」了，编辑器也会卡）。</summary>
    public const int MaxChars = 1_000_000;

    public static DocumentSnapshot Load(string path)
    {
        if (!File.Exists(path))
        {
            return new DocumentSnapshot(path, "", "", Exists: false, TooLarge: false);
        }
        if (new FileInfo(path).Length > MaxChars * 4L)
        {
            return new DocumentSnapshot(path, "", "", Exists: true, TooLarge: true);
        }
        var text = Read(path);
        return text.Length > MaxChars
            ? new DocumentSnapshot(path, "", Version(text), Exists: true, TooLarge: true)
            : new DocumentSnapshot(path, text, Version(text), Exists: true, TooLarge: false);
    }

    /// <summary>
    /// 写回员工改过的内容。<paramref name="baseVersion"/> 是打开（或上次保存）时的版本：文件在这之后变过就返回冲突，不写。
    /// 先写临时文件再替换，写到一半断电也不会留下半截文档。
    /// </summary>
    public static SaveResult Save(string path, string text, string baseVersion)
    {
        if (text.Length > MaxChars)
        {
            return new SaveResult(false, false, baseVersion, "", "文档太长，保存不了");
        }
        if (File.Exists(path))
        {
            var current = Read(path);
            var currentVersion = Version(current);
            if (currentVersion != baseVersion)
            {
                return currentVersion == Version(text)
                    ? new SaveResult(true, false, currentVersion, "", "") // 改成的样子和文件里一样，不算冲突
                    : new SaveResult(false, true, currentVersion, current, "文档在你编辑期间被改过");
            }
        }
        else if (baseVersion.Length > 0)
        {
            return new SaveResult(false, true, "", "", "文档已经被删除或移走");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".flyknit-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return new SaveResult(true, false, Version(text), "", "");
    }

    /// <summary>内容的指纹：只看内容，不看修改时间（同步盘、杀毒软件碰一下文件不算改过）。</summary>
    public static string Version(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static string Read(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

public sealed record DocumentSnapshot(string Path, string Text, string Version, bool Exists, bool TooLarge);

/// <summary>保存结果。Conflict 时 CurrentText / Version 是文件现在的内容和版本。</summary>
public sealed record SaveResult(bool Ok, bool Conflict, string Version, string CurrentText, string Message);
