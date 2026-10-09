using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Flyknit.Client.Services;

/// <summary>
/// 校验本机 Windows 账号的密码（没加域的电脑登录用）。只问 Windows“这个密码对不对”，
/// 拿到的登录令牌立刻关掉，密码不存、不发给服务端。
/// </summary>
public static class WindowsLogon
{
    public enum Result
    {
        Ok,
        WrongPassword,
        /// <summary>其他原因（账号被停用、策略不允许等），带 Windows 的错误说明。</summary>
        Failed,
    }

    private const int Logon32LogonInteractive = 2;
    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    private const int ErrorLogonFailure = 1326;
    private const int ErrorAccountRestriction = 1327;
    private const int ErrorLogonTypeNotGranted = 1385;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <param name="domain">本机账号传 "."；域账号传域名；UPN（user@corp.com）传 null。</param>
    public static (Result Result, string Message) Verify(string user, string? domain, string password)
    {
        var (ok, error) = TryLogon(user, domain, password, Logon32LogonInteractive);
        if (!ok && error == ErrorLogonTypeNotGranted)
        {
            // 有的电脑策略不许“交互式登录”这种方式（比如共享机），换成网络登录再验一次，意思一样：密码对不对
            (ok, error) = TryLogon(user, domain, password, Logon32LogonNetwork);
        }
        if (ok)
        {
            return (Result.Ok, "");
        }
        return error switch
        {
            ErrorLogonFailure => (Result.WrongPassword, ""),
            // 没设密码的账号：Windows 默认不许空密码这样登录。密码框也空着，说明这个账号确实没密码——就是他本人的电脑
            ErrorAccountRestriction when password.Length == 0 => (Result.Ok, ""),
            _ => (Result.Failed, new Win32Exception(error).Message),
        };
    }

    private static (bool Ok, int Error) TryLogon(string user, string? domain, string password, int type)
    {
        if (LogonUser(user, domain!, password, type, Logon32ProviderDefault, out var token))
        {
            CloseHandle(token);
            return (true, 0);
        }
        return (false, Marshal.GetLastWin32Error());
    }
}
