using System.Text;

namespace Flyknit.Agent;

/// <summary>
/// 服务跑在 SYSTEM 账号下，没有界面也没有控制台，所有线索都落到一个日志文件里。
/// 文件在 %ProgramData%\Flyknit\agent.log，超过 2 MB 就滚动一次，只留上一份。
/// </summary>
public static class AgentLog
{
    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(AgentPaths.Root, "agent.log");
    private const long MaxBytes = 2 * 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) =>
        Write("WARN", ex is null ? message : $"{message}：{ex.Message}");

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AgentPaths.Root);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = Path + ".1";
                    File.Delete(old);
                    File.Move(Path, old);
                }
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 日志写不进去也不能让服务崩，直接忽略
            }
        }
    }
}
