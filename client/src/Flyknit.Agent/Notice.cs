using System.Text.Json;

namespace Flyknit.Agent;

/// <summary>
/// 给员工的桌面提示。代理以 SYSTEM 运行，弹不出用户桌面的通知，于是把通知写成一个
/// 文件扔进 %ProgramData%\Flyknit\notices；员工客户端监听这个目录，读到就弹右下角提示
/// （“IT 正在为你的电脑执行：xxx”），弹完删掉文件。
/// </summary>
public static class Notice
{
    public static void Post(string title, string body)
    {
        try
        {
            Directory.CreateDirectory(AgentPaths.Notices);
            var payload = JsonSerializer.Serialize(new
            {
                title,
                body,
                created_at = DateTimeOffset.Now.ToString("o"),
            });
            // 先写临时文件再改名，避免客户端读到写了一半的内容
            var name = $"{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            var tmp = Path.Combine(AgentPaths.Notices, name + ".tmp");
            var final = Path.Combine(AgentPaths.Notices, name + ".json");
            File.WriteAllText(tmp, payload);
            File.Move(tmp, final);
            CleanupStale();
        }
        catch (Exception ex)
        {
            AgentLog.Warn("写通知文件失败", ex);
        }
    }

    /// <summary>
    /// 没人登录时写的通知没人接，会一直堆着。超过 1 天的旧通知清掉，免得员工下次登录
    /// 被一堆过期提示淹没。
    /// </summary>
    private static void CleanupStale()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AgentPaths.Notices, "*.json"))
            {
                if (DateTime.Now - File.GetCreationTime(file) > TimeSpan.FromDays(1))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // 清理失败无所谓
        }
    }
}
