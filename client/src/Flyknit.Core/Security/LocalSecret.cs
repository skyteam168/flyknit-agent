using System.Runtime.InteropServices;
using System.Text;

namespace Flyknit.Core.Security;

/// <summary>
/// 本机单值保护（DPAPI，绑在当前 Windows 账号上）：设备令牌、代理密码落盘时包一层。
/// 员工端（WPF）和命令行（flyknit.exe）读的是同一个 settings.json，所以放在 Core 共用，两边不能各写一套。
/// 非 Windows：保护时原样返回、还原带标记的值时返回空（没有 DPAPI，解不开就当没有）。
/// </summary>
public static class LocalSecret
{
    public const string Marker = "dpapi:";

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>保护。加密不可用时原样返回（功能不能因为加不了密就坏掉），<paramref name="warn"/> 收到原因。</summary>
    public static string Protect(string? plain, Action<string>? warn = null)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain))
        {
            return plain ?? "";
        }
        if (!OperatingSystem.IsWindows())
        {
            return plain;
        }
        try
        {
            return Marker + Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(plain), protect: true));
        }
        catch (Exception ex)
        {
            warn?.Invoke($"保护敏感配置失败，将以明文保存：{ex.Message}");
            return plain;
        }
    }

    /// <summary>
    /// 还原。不带标记的按明文处理（老版本的设置文件里是明文）。
    /// 解不开（换了 Windows 账号、文件从别的机器拷来）返回空，让调用方当作没登录。
    /// </summary>
    public static string Unprotect(string? stored, Action<string>? warn = null)
    {
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored))
        {
            return stored ?? "";
        }
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }
        try
        {
            var bytes = Convert.FromBase64String(stored[Marker.Length..]);
            return Encoding.UTF8.GetString(Dpapi(bytes, protect: false));
        }
        catch (Exception ex)
        {
            warn?.Invoke($"无法还原敏感配置（可能来自其他 Windows 账号）：{ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// 直接调 crypt32 的 DPAPI。不用 System.Security.Cryptography.ProtectedData，
    /// 是因为那在 .NET 8 里是单独的 NuGet 包，为了两个方法多带一个依赖不值得。
    /// </summary>
    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            input.cbData = data.Length;
            input.pbData = handle.AddrOfPinnedObject();

            var ok = protect
                ? CryptProtectData(ref input, "Flyknit", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref output);
            if (!ok)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            if (handle.IsAllocated) { handle.Free(); }
            if (output.pbData != IntPtr.Zero) { LocalFree(output.pbData); }
        }
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
