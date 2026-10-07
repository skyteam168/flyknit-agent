using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Flyknit.Updater;

/// <summary>
/// 把新版本换上去。
///
/// 为什么要单独一个程序：Windows 上正在运行的 exe 和 dll 是锁住的，程序没法覆盖自己。
/// 所以主程序把文件备好、启动这个小工具、然后自己退出，由它来做替换。
///
/// 它自己也不能待在被替换的目录里，否则替换到一半把自己删了。主程序会先把它拷到临时
/// 目录再启动。
///
/// 一条贯穿全文件的规矩：**宁可留在旧版本，也不能留下一个装了一半的目录。**
/// 所以先把旧目录整个改名留着，拷贝失败就原样改回去。
/// </summary>
internal static class Program
{
    private static string _log = "";

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        _log = options.LogFile;

        if (options.Source.Length == 0 || options.Target.Length == 0)
        {
            Say("参数不全，什么也没做");
            return 2;
        }

        Say($"准备把 {options.Source} 换到 {options.Target}");
        if (!WaitForExit(options.Pid, TimeSpan.FromSeconds(60)))
        {
            Say($"等了 60 秒，进程 {options.Pid} 还在，放弃这次更新");
            return 3;
        }

        var backup = options.Target.TrimEnd(Path.DirectorySeparatorChar) + ".old";
        var result = Swap.Replace(options.Source, options.Target, backup);
        if (!result.Ok)
        {
            Say(result.Message);
            Say(result.RolledBack ? "已回滚到更新前的版本" : "安装目录没有被动过");
            Relaunch(options);   // 换不上去也要把旧版本拉起来，不能让人以为程序没了
            return 4;
        }

        TryDelete(backup);
        TryDeleteFile(options.Zip);
        TryDelete(options.Source);
        Say("更新完成");
        Relaunch(options);
        return 0;
    }




    /// <summary>等主程序退出。等不到就不动手——文件锁着，动手只会换出一个半成品。</summary>
    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0)
        {
            return true;
        }
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;  // 已经退了
        }
        using (process)
        {
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
    }

    private static void Relaunch(Options options)
    {
        if (options.Silent || options.Exe.Length == 0 || !File.Exists(options.Exe))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(options.Exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(options.Exe) ?? "",
            });
        }
        catch (Exception ex)
        {
            Say($"重新启动失败：{ex.Message}");
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (dir.Length > 0 && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Say($"清理 {dir} 失败：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (file.Length > 0)
            {
                File.Delete(file);
            }
        }
        catch (Exception)
        {
            // 清理失败不影响更新本身
        }
    }

    private static void Say(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        if (_log.Length == 0)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_log)!);
            File.AppendAllText(_log, line + Environment.NewLine);
        }
        catch (Exception)
        {
            // 日志写不了也要继续更新
        }
    }

    private sealed class Options
    {
        public int Pid;
        public string Source = "";
        public string Target = "";
        public string Exe = "";
        public string Zip = "";
        public string LogFile = "";
        public bool Silent;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var next = i + 1 < args.Length ? args[i + 1] : "";
                switch (args[i])
                {
                    case "--pid": o.Pid = int.TryParse(next, out var p) ? p : 0; i++; break;
                    case "--source": o.Source = next; i++; break;
                    case "--target": o.Target = next; i++; break;
                    case "--exe": o.Exe = next; i++; break;
                    case "--zip": o.Zip = next; i++; break;
                    case "--log": o.LogFile = next; i++; break;
                    case "--silent": o.Silent = true; break;
                }
            }
            return o;
        }
    }
}
