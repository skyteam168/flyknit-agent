using System.Text.Json;

namespace Flyknit.Agent.Tasks;

/// <summary>重启电脑（restart）：带倒计时，并给员工弹一条提示说明原因。</summary>
public static class Restarter
{
    public static async Task<RunResult> RunAsync(JsonElement p, CancellationToken ct)
    {
        var minutes = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("delay_minutes", out var m) && m.TryGetInt32(out var v)
            ? Math.Clamp(v, 1, 60)
            : 5;
        var message = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String
            ? msg.GetString() ?? ""
            : "IT 将重启这台电脑以完成维护，请保存好正在编辑的文件。";

        var seconds = minutes * 60;
        // shutdown 自带倒计时弹窗，这里再额外推一条我们自己的通知，口径一致
        Notice.Post("电脑即将重启", $"{message}（{minutes} 分钟后重启）");

        var args = $"/r /t {seconds} /c \"{message.Replace("\"", "'")}\" /d p:4:1";
        var r = await ProcessRunner.RunAsync("shutdown.exe", args, ct, TimeSpan.FromMinutes(1));
        return r.ExitCode == 0
            ? RunResult.Ok($"已安排 {minutes} 分钟后重启。", r.ExitCode)
            : RunResult.Fail($"安排重启失败，退出码 {r.ExitCode}：{r.Combined}", r.ExitCode);
    }
}
