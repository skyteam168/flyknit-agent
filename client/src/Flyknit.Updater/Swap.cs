using System;
using System.IO;

namespace Flyknit.Updater;

/// <summary>
/// 把新版本的文件换到安装目录去。
///
/// 这是整个更新里唯一会毁掉员工电脑上程序的一步，所以单独拎出来、单独测。
/// 一条规矩：**宁可留在旧版本，也不能留下一个装了一半的目录。**
///
/// 做法是先把旧目录整个改名留着（改名是原子的），再往目标位置拷贝（拷贝不是原子的）。
/// 拷到一半失败就把改名那步倒回去，员工看到的还是原来那个能用的程序。
/// </summary>
public static class Swap
{
    public sealed record Result(bool Ok, string Message, bool RolledBack = false);

    /// <summary>
    /// 用 source 替换 target，旧的先挪到 backup。
    /// 成功时 backup 由调用方删除——留到最后再删，中间任何一步出事都还能救回来。
    /// </summary>
    /// <param name="copy">
    /// 拷贝这一步，默认就是 CopyTree。留这个口子是为了能测「拷到一半失败」之后的回滚——
    /// 那条路径正是这段代码存在的理由，不该只靠读代码确认它是对的。
    /// </param>
    public static Result Replace(string source, string target, string backup,
                                 Action<string, string>? copy = null)
    {
        if (!Directory.Exists(source))
        {
            return new Result(false, $"新版本的文件不在：{source}");
        }
        if (!HasPayload(source))
        {
            // 空目录或者缺主程序的目录换上去，等于把程序删了
            return new Result(false, $"{source} 里没有 FlyknitBuddy.exe，不敢换");
        }

        try
        {
            Delete(backup);
        }
        catch (Exception ex)
        {
            return new Result(false, $"清理上次的备份失败：{ex.Message}");
        }

        var moved = false;
        try
        {
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                moved = true;
            }
            (copy ?? CopyTree)(source, target);
            return new Result(true, "");
        }
        catch (Exception ex)
        {
            if (!moved)
            {
                return new Result(false, $"替换失败：{ex.Message}");
            }
            try
            {
                Delete(target);
                Directory.Move(backup, target);
                return new Result(false, $"替换失败：{ex.Message}", RolledBack: true);
            }
            catch (Exception rollbackError)
            {
                // 走到这里旧版本还在 backup 里，人工能救，所以把路径写进消息
                return new Result(false,
                    $"替换失败：{ex.Message}；回滚也失败：{rollbackError.Message}。原来的程序在 {backup}");
            }
        }
    }

    /// <summary>这个目录看起来像不像一份能用的程序。</summary>
    public static bool HasPayload(string dir) => File.Exists(Path.Combine(dir, "FlyknitBuddy.exe"));

    public static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, dir[(source.Length + 1)..]));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, file[(source.Length + 1)..]), overwrite: true);
        }
    }

    public static void Delete(string dir)
    {
        if (dir.Length > 0 && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
