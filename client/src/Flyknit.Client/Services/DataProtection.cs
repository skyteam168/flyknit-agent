using System;
using System.IO;
using System.Runtime.InteropServices;

using System.Text;

namespace Flyknit.Client.Services;

/// <summary>
/// 本地数据的静态保护。
///
/// 威胁是具体的：车间电脑的本地管理员密码往往全厂通用，谁都能登上去，把
/// %APPDATA%\Flyknit\data\history.db 拷走，用任何 SQLite 工具读完所有对话。
///
/// 对策分两层：
///   目录加密（EFS）—— 数据目录标记为加密后，库文件和它的 WAL、journal 一起受保护，
///                      只有当前 Windows 账号解得开，拷到别的机器就是一堆密文。
///   DPAPI      —— 设置文件里的设备令牌、代理密码这些单值，单独包一层。
///
/// 为什么不是 SQLCipher：它要换掉 SQLite 的原生 bundle，整库格式也要迁移，写错一次
/// 就是聊天记录全毁。EFS 对「文件被拷走」这个真实威胁的防护强度相当，但不动数据格式，
/// 失败了也只是没加密而已，不会丢数据。真要防到「连本机管理员跑用户身份也读不到」，
/// 那是另一个量级的需求，到时候再上 SQLCipher，存储层已经留好了口子。
/// </summary>
public static class DataProtection
{
    private const int FILE_ATTRIBUTE_ENCRYPTED = 0x4000;

    /// <summary>这台机器上加密是否可用。不可用时功能照常，只是数据不加密。</summary>
    public static bool Available { get; private set; } = true;

    /// <summary>上次失败的原因，用于在设置里如实告诉用户「本机没能加密」。</summary>
    public static string? Unavailable { get; private set; }

    /// <summary>
    /// 把目录标记为加密。之后在里面新建的文件自动继承，所以库、WAL、journal 都盖得到。
    /// 已经存在的文件不会自动加密，要单独处理，见 <see cref="ProtectExisting"/>。
    /// </summary>
    public static bool ProtectDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Fail("只有 Windows 支持此加密方式");
        }
        try
        {
            Directory.CreateDirectory(path);
            if (IsEncrypted(path))
            {
                return true;
            }
            if (EncryptFileW(path))
            {
                Log.Info($"数据目录已加密：{path}");
                return true;
            }
            return Fail(DescribeError(Marshal.GetLastWin32Error()));
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// 加密目录里已有的文件。升级时老用户的库是明文的，目录属性不会追溯过去。
    /// 逐个处理，单个失败不影响其余——半加密也比完全不加密好。
    /// </summary>
    public static int ProtectExisting(string directory, params string[] searchPatterns)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return 0;
        }
        var done = 0;
        var patterns = searchPatterns.Length > 0 ? searchPatterns : new[] { "*" };
        foreach (var pattern in patterns)
        {
            foreach (var file in SafeEnumerate(directory, pattern))
            {
                try
                {
                    if (IsEncrypted(file))
                    {
                        continue;
                    }
                    // File.Encrypt 内部就是 EncryptFile，失败会抛，不会悄悄留明文
                    File.Encrypt(file);
                    done++;
                }
                catch (Exception ex)
                {
                    // 文件正被占用、在不支持的卷上……记下来就好，不要打断启动
                    Log.Warn($"加密 {Path.GetFileName(file)} 失败：{ex.Message}");
                }
            }
        }
        if (done > 0)
        {
            Log.Info($"已加密 {done} 个历史文件");
        }
        return done;
    }

    private static System.Collections.Generic.IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public static bool IsEncrypted(string path)
    {
        try
        {
            return ((int)File.GetAttributes(path) & FILE_ATTRIBUTE_ENCRYPTED) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Fail(string reason)
    {
        Available = false;
        Unavailable = reason;
        Log.Warn($"本机无法加密数据目录：{reason}");
        return false;
    }

    private static string DescribeError(int code) => code switch
    {
        // 这三个是实际会遇到的：家庭版没有 EFS，FAT32 U 盘不支持，组策略关掉了
        50 => "该磁盘或系统版本不支持文件加密（Windows 家庭版没有此功能）",
        6 => "数据目录句柄无效",
        5 => "没有权限加密该目录",
        _ => $"系统错误 {code}",
    };

    [DllImport("advapi32.dll", EntryPoint = "EncryptFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EncryptFileW(string filename);

    // ---------- 单值保护（DPAPI） ----------
    // 实现在 Core（Flyknit.Core.Security.LocalSecret）：命令行 flyknit.exe 读同一个 settings.json，两边共用一份

    /// <summary>
    /// 把一个字符串保护起来。绑在当前 Windows 账号上，换个账号拿到密文也解不开。
    /// 加密不可用时原样返回——功能不能因为加不了密就坏掉。
    /// </summary>
    public static string Protect(string? plain) => Flyknit.Core.Security.LocalSecret.Protect(plain, m => Log.Warn(m));

    /// <summary>
    /// 还原。不带标记的按明文处理——老版本升上来时设置文件里就是明文，
    /// 直接读出来用，下次保存时自然会被包上。解不开时当没有，让用户重新注册一次。
    /// </summary>
    public static string Unprotect(string? stored) => Flyknit.Core.Security.LocalSecret.Unprotect(stored, m => Log.Warn(m));

    public static bool IsProtected(string? value) => Flyknit.Core.Security.LocalSecret.IsProtected(value);
}
