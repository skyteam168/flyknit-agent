namespace Flyknit.Core.Setup;

/// <summary>
/// 员工端是怎么装的。后台据此标出「免安装版」：这类电脑没有运维代理，不会自动升级，IT 也管不了。
/// 只用于展示和提示，不影响任何权限。
/// </summary>
public static class InstallKind
{
    /// <summary>安装程序装的，并且运维代理负责升级（程序目录里有 flyknit.managed）。</summary>
    public const string Managed = "managed";

    /// <summary>装在 Program Files 里，但没有运维代理（老安装包、或者包里没带代理）。</summary>
    public const string Setup = "setup";

    /// <summary>zip 免安装版：解压到哪儿就从哪儿运行。</summary>
    public const string Portable = "portable";

    public static string Detect(string installDir, string programFiles, bool managedMarker)
    {
        if (managedMarker)
        {
            return Managed;
        }
        return IsUnder(installDir, programFiles) ? Setup : Portable;
    }

    private static bool IsUnder(string dir, string root)
    {
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }
        var d = Normalize(dir);
        var r = Normalize(root);
        return d.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
