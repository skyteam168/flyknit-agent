using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Flyknit.Core.Gateway;
using Flyknit.Core.Security;
using Flyknit.Core.Skills;

namespace Flyknit.Client.Services;

/// <summary>
/// 技能的安装、卸载与启停。
/// 安装来源：本地 zip、文件夹（含共享盘）、公司技能库（服务端下载）。
/// 安装后目录监听会自动重新注册，不需要重启程序。
/// </summary>
public sealed class SkillService
{
    private readonly SkillCatalog _catalog;
    private readonly AppSettings _settings;
    private readonly FlyknitServerClient _server;
    private readonly Func<CommandPolicy> _policy;

    public SkillService(SkillCatalog catalog, AppSettings settings, FlyknitServerClient server, Func<CommandPolicy> policy)
    {
        _catalog = catalog;
        _settings = settings;
        _server = server;
        _policy = policy;
    }

    /// <summary>用户安装的技能放在 skills 根目录下，企业的在 org\，复盘沉淀的在 learned\。</summary>
    public static string UserRoot => AppPaths.Skills;

    public IReadOnlyList<SkillInfo> List() => _catalog.Skills;

    /// <summary>先检查不安装，界面据此显示技能包内容与风险。</summary>
    public SkillInspection Inspect(string path)
    {
        var staging = Path.Combine(AppPaths.Temp, "skill-inspect", Guid.NewGuid().ToString("N"));
        try
        {
            if (Directory.Exists(path))
            {
                return SkillPackage.Inspect(path, _policy(), Exists);
            }
            Directory.CreateDirectory(staging);
            System.IO.Compression.ZipFile.ExtractToDirectory(path, staging, overwriteFiles: true);
            var root = SkillPackage.FindSkillRoot(staging);
            return root is null
                ? new SkillInspection { Error = "压缩包里没有找到 SKILL.md，这不是一个技能包" }
                : SkillPackage.Inspect(root, _policy(), Exists);
        }
        catch (Exception ex)
        {
            return new SkillInspection { Error = ex.Message };
        }
        finally
        {
            SkillPackage.TryDelete(staging);
        }
    }

    public bool Exists(string name) => _catalog.FindAny(name) is not null;

    /// <summary>从本地 zip 或文件夹安装。</summary>
    public SkillInstallResult InstallFrom(string path, string origin = "")
    {
        var result = SkillPackage.Install(path, UserRoot, _policy(), origin.Length > 0 ? origin : Path.GetFileName(path));
        if (result.Ok)
        {
            Enable(result.Inspection.Name, true);
            Refresh();
            Log.Info($"已安装技能 {result.Inspection.Name}（来源 {origin}）");
        }
        else
        {
            Log.Warn($"安装技能失败（{path}）：{result.Message}");
        }
        return result;
    }

    /// <summary>一个压缩包里包含多个技能时全部安装（社区仓库常见）。</summary>
    public List<SkillInstallResult> InstallAllFrom(string zipPath, string origin = "")
    {
        var staging = Path.Combine(AppPaths.Temp, "skill-batch", Guid.NewGuid().ToString("N"));
        var results = new List<SkillInstallResult>();
        try
        {
            Directory.CreateDirectory(staging);
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            var roots = SkillPackage.FindAllSkillRoots(staging);
            if (roots.Count == 0)
            {
                results.Add(new SkillInstallResult(false, "压缩包里没有找到 SKILL.md，这不是一个技能包", new SkillInspection { Error = "没有 SKILL.md" }));
                return results;
            }
            foreach (var root in roots)
            {
                results.Add(InstallFrom(root, origin.Length > 0 ? origin : Path.GetFileName(zipPath)));
            }
        }
        catch (Exception ex)
        {
            results.Add(new SkillInstallResult(false, $"安装失败：{ex.Message}", new SkillInspection { Error = ex.Message }));
        }
        finally
        {
            SkillPackage.TryDelete(staging);
        }
        return results;
    }

    /// <summary>从公司技能库安装（服务端下载 zip 后本地安装）。</summary>
    public async Task<SkillInstallResult> InstallFromLibraryAsync(string name, CancellationToken ct)
    {
        var temp = Path.Combine(AppPaths.Temp, "skill-download");
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, $"{SkillPackage.Sanitize(name)}-{Guid.NewGuid():N}.zip");
        try
        {
            await _server.DownloadSkillAsync(name, zip, ct);
            return InstallFrom(zip, "公司技能库");
        }
        catch (Exception ex)
        {
            Log.Warn($"从公司技能库安装 {name} 失败", ex);
            return new SkillInstallResult(false, $"下载失败：{ex.Message}", new SkillInspection { Error = ex.Message });
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }

    /// <summary>从直链下载技能包（GitHub release、社区站的下载链接）。客户端能直接上网时才可用。</summary>
    public async Task<SkillInstallResult> InstallFromUrlAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return new SkillInstallResult(false, "请填写 http(s) 开头的下载链接", new SkillInspection { Error = "链接无效" });
        }
        var temp = Path.Combine(AppPaths.Temp, "skill-download");
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, $"url-{Guid.NewGuid():N}.zip");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            using var resp = await http.GetAsync(GitHubZipUrl(uri), HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new SkillInstallResult(false, $"下载失败：HTTP {(int)resp.StatusCode}", new SkillInspection { Error = "下载失败" });
            }
            await using (var stream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var file = File.Create(zip))
            {
                await stream.CopyToAsync(file, ct);
            }
            return InstallFrom(zip, url.Trim());
        }
        catch (Exception ex)
        {
            return new SkillInstallResult(false, $"下载失败：{ex.Message}", new SkillInspection { Error = ex.Message });
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }

    /// <summary>GitHub 仓库页面链接转成 zip 下载链接。</summary>
    public static string GitHubZipUrl(Uri uri)
    {
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2)
        {
            return uri.ToString();
        }
        var branch = parts.Length >= 4 && (parts[2] == "tree" || parts[2] == "blob") ? parts[3] : "HEAD";
        return $"https://codeload.github.com/{parts[0]}/{parts[1]}/zip/{branch}";
    }

    /// <summary>卸载（企业下发和必装的技能不允许卸载）。</summary>
    public (bool Ok, string Message) Uninstall(string name)
    {
        var skill = _catalog.FindAny(name);
        if (skill is null)
        {
            return (false, "技能不存在");
        }
        if (skill.IsOrganization)
        {
            return (false, "企业下发的技能不能卸载");
        }
        try
        {
            Directory.Delete(skill.Directory, recursive: true);
            _settings.DisabledSkills.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            Refresh();
            Log.Info($"已卸载技能 {name}");
            return (true, $"已卸载 {name}");
        }
        catch (Exception ex)
        {
            Log.Warn($"卸载技能 {name} 失败", ex);
            return (false, $"卸载失败：{ex.Message}");
        }
    }

    /// <summary>启用或停用。必装的技能不允许停用。</summary>
    public (bool Ok, string Message) Enable(string name, bool enabled)
    {
        var skill = _catalog.FindAny(name);
        if (skill is not null && skill.Required && !enabled)
        {
            return (false, "这是企业要求安装的技能，不能停用");
        }
        _settings.DisabledSkills.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            _settings.DisabledSkills.Add(name);
        }
        _settings.Save();
        Refresh();
        return (true, "");
    }

    /// <summary>重新扫描并把启停状态应用上去。</summary>
    public void Refresh() =>
        _catalog.SetState(
            new HashSet<string>(_settings.DisabledSkills, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(_settings.RequiredSkills, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 同步公司技能库：下载管理员标记为「必装」的技能（新装或版本变了才下载），
    /// 启动时和定时刷新配置时各执行一次。
    /// </summary>
    public async Task<int> SyncRequiredAsync(CancellationToken ct)
    {
        List<ServerSkill> library;
        try
        {
            library = await _server.GetSkillsAsync(ct);
        }
        catch (Exception ex)
        {
            Log.Warn("获取公司技能库失败", ex);
            return 0;
        }

        _settings.RequiredSkills = library.Where(s => s.Required).Select(s => s.Name).ToList();
        _settings.Save();

        var installed = 0;
        foreach (var item in library.Where(s => s.Required))
        {
            var current = _catalog.FindAny(item.Name);
            if (current is not null && (item.Version.Length == 0 || current.Version == item.Version))
            {
                continue;
            }
            var temp = Path.Combine(AppPaths.Temp, "skill-download");
            Directory.CreateDirectory(temp);
            var zip = Path.Combine(temp, $"{SkillPackage.Sanitize(item.Name)}-{Guid.NewGuid():N}.zip");
            try
            {
                await _server.DownloadSkillAsync(item.Name, zip, ct);
                // 企业必装的技能装到 org 目录（只读，用户不能卸载）
                var result = SkillPackage.Install(zip, AppPaths.OrgSkills, _policy(), "公司技能库");
                if (result.Ok)
                {
                    installed++;
                    Log.Info($"已从公司技能库安装必装技能 {item.Name}");
                }
                else
                {
                    Log.Warn($"安装必装技能 {item.Name} 失败：{result.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"下载必装技能 {item.Name} 失败", ex);
            }
            finally
            {
                try { File.Delete(zip); } catch (IOException) { }
            }
        }
        Refresh();
        return installed;
    }
}
