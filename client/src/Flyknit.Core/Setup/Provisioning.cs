using System.Text.Json;

namespace Flyknit.Core.Setup;

/// <summary>
/// IT 从后台下载的员工端安装包里带的开通信息：服务器地址和一张安装凭证（flyknit.provision.json，放在 FlyknitBuddy.exe 旁边）。
/// 有它，员工第一次打开只需要点「登录」，不用再问 IT 服务器地址和注册密钥。
/// </summary>
public sealed record Provision(string ServerUrl, string Ticket, string Label);

public static class Provisioning
{
    public const string FileName = "flyknit.provision.json";

    /// <summary>按顺序在这几个目录里找开通文件，用第一个有效的。都没有返回 null（退回手动填写服务器地址）。</summary>
    public static Provision? Load(params string[] directories) =>
        directories.Select(LoadFrom).FirstOrDefault(p => p is not null);

    /// <summary>
    /// 登录成功后把开通文件留一份到用户数据目录：自动升级会整个换掉程序目录，
    /// 以后设备令牌失效要重新登录时，还能直接点「登录」而不是退回手动填服务器。
    /// </summary>
    public static void Keep(string programDirectory, string dataDirectory)
    {
        var source = Path.Combine(programDirectory, FileName);
        if (File.Exists(source) && LoadFrom(programDirectory) is not null)
        {
            Directory.CreateDirectory(dataDirectory);
            File.Copy(source, Path.Combine(dataDirectory, FileName), overwrite: true);
        }
    }

    private static Provision? LoadFrom(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            var url = Str("server_url").TrimEnd('/');
            var ticket = Str("ticket");
            if (ticket.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return null;
            }
            return new Provision(url, ticket, Str("label"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>员工怎么证明自己是谁。</summary>
public enum LoginKind
{
    /// <summary>域账号（含 Azure AD）：Windows 开机时已经由域控验证过，点一下「登录」就行。</summary>
    Domain,

    /// <summary>本机账号（含微软账号登录的本机用户）：要输入这个账号的 Windows 密码，由本机校验。</summary>
    Local,
}

/// <summary>当前登录 Windows 的账号。</summary>
public sealed record WindowsAccount(string Domain, string User, string MachineName)
{
    /// <summary>账号所属的不是本机（域名和计算机名不同）就是域账号。</summary>
    public LoginKind Kind =>
        Domain.Length > 0 && !Domain.Equals(MachineName, StringComparison.OrdinalIgnoreCase) ? LoginKind.Domain : LoginKind.Local;

    /// <summary>DOMAIN\user 的写法，记到服务端的设备台账里。</summary>
    public string Display => Domain.Length > 0 ? $"{Domain}\\{User}" : User;

    public static WindowsAccount Current() => new(Environment.UserDomainName, Environment.UserName, Environment.MachineName);
}
