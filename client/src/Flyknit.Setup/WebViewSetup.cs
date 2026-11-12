using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace Flyknit.Setup;

/// <summary>
/// 安装 / 升级时顺带把 WebView2 运行时装好（安装程序和运维代理共用这份源文件，都是管理员 / SYSTEM 身份）。
///
/// 只用发布包里 IT 放进去的离线安装包（redist\MicrosoftEdgeWebView2RuntimeInstallerX64.exe），
/// 不从员工电脑联网下载（工厂很多电脑不通外网），也不接受服务端或员工给的路径。
/// 运行前校验微软的数字签名；任何一步失败都只记一笔，不挡住员工端的安装 / 升级。
/// </summary>
public static class WebViewSetup
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>本机已装的 WebView2 运行时版本（全机安装或当前用户安装里较新的那个）；没装返回空串。</summary>
    public static string InstalledVersion()
    {
        var found = new List<string>();
        var sub = $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{RuntimeCheck.RuntimeClientId}";
        // 全机安装在 32 位视图（WOW6432Node）下
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, sub, found);
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, sub, found);
        Read(RegistryHive.CurrentUser, RegistryView.Default, sub, found);
        return found.OrderByDescending(v => v, Comparer<string>.Create(RuntimeCheck.CompareVersions)).FirstOrDefault() ?? "";
    }

    private static void Read(RegistryHive hive, RegistryView view, string sub, List<string> found)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(sub);
            if (key?.GetValue("pv") is string pv && RuntimeCheck.Classify(pv) != WebViewState.Missing)
            {
                found.Add(pv);
            }
        }
        catch (Exception)
        {
            // 读不了当作没有
        }
    }

    /// <summary>
    /// 包里带了离线安装包、而本机没装或太旧时，静默安装。返回装完后的版本（没动就是原来的版本）。
    /// </summary>
    public static string EnsureRuntime(string packageRoot, Action<string> say)
    {
        var installer = Path.Combine(packageRoot, RuntimeCheck.RedistDir, RuntimeCheck.OfflineInstaller);
        var present = File.Exists(installer);
        var before = InstalledVersion();
        var state = RuntimeCheck.Classify(before);
        if (!RuntimeCheck.ShouldInstallRuntime(before, present))
        {
            if (state != WebViewState.Ok)
            {
                say(state == WebViewState.Missing
                    ? "这台电脑没有 WebView2 运行时，安装包里也没带离线安装包：员工端会打不开，请另外安装 WebView2 Runtime"
                    : $"这台电脑的 WebView2 运行时较旧（{before}），安装包里没带离线安装包：员工端能用，建议更新到 {RuntimeCheck.RecommendedWebView} 以上");
            }
            return before;
        }
        if (!Authenticode.IsSignedByMicrosoft(installer))
        {
            say("安装包里的 WebView2 离线安装包没有通过微软签名校验，已跳过（请从微软官网重新下载放进 client\\redist）");
            return before;
        }
        say(state == WebViewState.Missing ? "正在安装 WebView2 运行时（需要几分钟）…" : $"正在更新 WebView2 运行时（当前 {before}，需要几分钟）…");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(installer, "/silent /install") { UseShellExecute = false, CreateNoWindow = true })!;
            if (!p.WaitForExit(InstallTimeout))
            {
                say("WebView2 运行时安装超时，已跳过（不影响员工端安装）");
                return InstalledVersion();
            }
            var after = InstalledVersion();
            say(RuntimeCheck.Classify(after) == WebViewState.Ok
                ? $"WebView2 运行时已就绪：{after}"
                : $"WebView2 运行时安装没有成功（退出码 {p.ExitCode}，当前版本 {(after.Length > 0 ? after : "无")}）");
            return after;
        }
        catch (Exception ex)
        {
            say($"WebView2 运行时安装失败（不影响员工端安装）：{ex.Message}");
            return InstalledVersion();
        }
    }

    /// <summary>离线安装包有 170 MB 左右，装完就从要放进 Program Files 的目录里拿掉。</summary>
    public static void RemoveRedist(string packageRoot)
    {
        var dir = Path.Combine(packageRoot, RuntimeCheck.RedistDir);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception)
        {
            // 删不掉只是多占点空间
        }
    }
}

/// <summary>Authenticode 签名校验：签名有效（WinVerifyTrust）且签名者是微软。</summary>
internal static class Authenticode
{
    public static bool IsSignedByMicrosoft(string path)
    {
        if (!VerifyTrust(path))
        {
            return false;
        }
        try
        {
#pragma warning disable SYSLIB0057 // 只是取签名证书看签名者，不用于加载
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    private const uint UiNone = 2, RevokeNone = 0, ChoiceFile = 1, CacheOnlyUrlRetrieval = 0x1000;

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);

    private static bool VerifyTrust(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var filePtr = IntPtr.Zero;
        var dataPtr = IntPtr.Zero;
        try
        {
            var file = new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), FilePath = pathPtr };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);
            // 工厂电脑多数不通外网：不查吊销列表，只用本机缓存，免得卡在联网上
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                FileInfo = filePtr,
                ProvFlags = CacheOnlyUrlRetrieval,
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>());
            Marshal.StructureToPtr(data, dataPtr, false);
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, dataPtr) == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
