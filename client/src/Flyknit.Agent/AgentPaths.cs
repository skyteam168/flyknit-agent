using System.Security.AccessControl;
using System.Security.Principal;

namespace Flyknit.Agent;

/// <summary>运维代理用到的几个固定路径，都在 %ProgramData%\Flyknit 下。</summary>
public static class AgentPaths
{
    /// <summary>%ProgramData%\Flyknit —— 和员工客户端共用同一个根目录。</summary>
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Flyknit");

    /// <summary>配置文件：服务端地址、注册密钥、代理身份令牌。</summary>
    public static readonly string Config = Path.Combine(Root, "agent.json");

    /// <summary>通知投递目录：代理往里写文件，员工客户端监听后弹提示。</summary>
    public static readonly string Notices = Path.Combine(Root, "notices");

    /// <summary>安装包、脚本等临时工作目录。</summary>
    public static readonly string Work = Path.Combine(Root, "work");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Work);
        EnsureNoticesWritableByEveryone();
    }

    /// <summary>
    /// 通知目录要让任意登录用户都能读到（代理以 SYSTEM 写，员工以普通账号读），
    /// 所以单独给它放开读权限；配置文件则相反，要锁起来（见 <see cref="RestrictConfig"/>）。
    /// </summary>
    private static void EnsureNoticesWritableByEveryone()
    {
        Directory.CreateDirectory(Notices);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var dir = new DirectoryInfo(Notices);
            var acl = dir.GetAccessControl();
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(
                users,
                FileSystemRights.ReadAndExecute | FileSystemRights.Delete,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dir.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            AgentLog.Warn("设置通知目录权限失败", ex);
        }
    }

    /// <summary>
    /// 配置文件里有注册密钥和代理令牌，拿到就能冒充这台电脑领任务，必须锁死：
    /// 只有 SYSTEM 和 Administrators 能读写，普通员工账号一律看不到。
    /// </summary>
    public static void RestrictConfig()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Config))
        {
            return;
        }
        try
        {
            var file = new FileInfo(Config);
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (var sid in new[] { system, admins })
            {
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }
            acl.SetOwner(admins);
            file.SetAccessControl(acl);
        }
        catch (Exception ex)
        {
            AgentLog.Warn("锁定配置文件权限失败", ex);
        }
    }
}
