using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Flyknit.Core.Chat;
using Flyknit.Core.Library;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Services;

/// <summary>
/// 资料库在客户端这一侧的事：什么时候收录文件、缩略图、把文件交给网页显示、复制到剪贴板、另存为。
/// 索引和整理逻辑在 <see cref="LibraryStore"/>。
/// </summary>
public sealed class LibraryService
{
    /// <summary>网页里用这个地址取资料库文件：https://files.flyknit.local/{id}，加 ?thumb=1 取缩略图。</summary>
    public const string Host = "files.flyknit.local";

    public const int ThumbSize = 480;

    public LibraryStore Store { get; }

    /// <summary>资料库内容变了（收录了新文件），界面打开着资料库时据此刷新。</summary>
    public event Action? Changed;

    public LibraryService(string databasePath)
    {
        Store = new LibraryStore(databasePath, Path.Combine(AppPaths.Library, "files"));
    }

    /// <summary>
    /// 发送消息前处理附件：临时目录里的（粘贴的截图）先存进资料库，附件改指向资料库里的那份，
    /// 免得临时目录被清理后历史消息里的图片打不开；其余的上传在后台复制一份进资料库，原路径不变
    /// （AI 要改的是用户原来的文件）。
    /// </summary>
    public List<Attachment> IngestUploads(IReadOnlyList<Attachment> attachments, string conversationId)
    {
        var result = new List<Attachment>();
        var rest = new List<Attachment>();
        foreach (var a in attachments)
        {
            if (IsTemp(a.LocalPath))
            {
                try
                {
                    if (Store.Add(a.LocalPath, "paste", copy: true, conversationId, a.FileName) is { } item)
                    {
                        result.Add(new Attachment { FileName = a.FileName, LocalPath = item.Path, Mime = a.Mime, Size = a.Size });
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"截图存入资料库失败：{a.LocalPath}", ex);
                }
            }
            else
            {
                rest.Add(a);
            }
            result.Add(a);
        }
        if (rest.Count > 0)
        {
            _ = Task.Run(() =>
            {
                foreach (var a in rest)
                {
                    TryAdd(a.LocalPath, "upload", copy: true, conversationId);
                }
                Changed?.Invoke();
            });
        }
        else if (result.Count > 0)
        {
            Changed?.Invoke();
        }
        return result;
    }

    /// <summary>AI 这一轮产出的文件：登记位置，不复制。</summary>
    public void IngestOutputs(IEnumerable<string> paths, string conversationId)
    {
        var list = paths.ToList();
        if (list.Count == 0)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            foreach (var path in list)
            {
                TryAdd(path, "output", copy: false, conversationId);
            }
            Changed?.Invoke();
        });
    }

    /// <summary>升级后第一次启动：把历史对话里的文件补登进来，顺带清理回收站里过期的。</summary>
    public void Initialize(ConversationStore conversations)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var refs = conversations.FileReferences()
                    .Select(r => (r.Path, r.Source == "upload" && IsTemp(r.Path) ? "paste" : r.Source, (string?)r.ConversationId, r.At));
                var added = Store.Backfill(refs, IsTemp);
                if (added > 0)
                {
                    Log.Info($"资料库：补登了 {added} 个历史文件");
                    Changed?.Invoke();
                }
                Store.PurgeExpired();
            }
            catch (Exception ex)
            {
                Log.Warn("资料库初始化失败", ex);
            }
        });
    }

    /// <summary>在资料库页面里直接上传（复制进来）。</summary>
    public List<LibraryItem> Upload(IEnumerable<string> paths, string? folderId)
    {
        var added = new List<LibraryItem>();
        foreach (var path in paths)
        {
            try
            {
                if (Store.Add(path, "library", copy: true, null, null, folderId) is { } item)
                {
                    if (folderId is not null && item.FolderId != folderId)
                    {
                        Store.Move(new[] { item.Id }, folderId); // 以前收录过同样内容的文件：移到当前文件夹
                    }
                    added.Add(item);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"上传到资料库失败：{path}", ex);
            }
        }
        return added;
    }

    /// <summary>网页取文件内容：原文件或缩略图。找不到返回 null。</summary>
    public (Stream Stream, string Mime)? Open(string id, bool thumb)
    {
        var item = Store.Get(id);
        if (item is null || !File.Exists(item.Path))
        {
            return null;
        }
        if (thumb && item.Kind == "image" && !item.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && Thumbnail(item) is { } t)
        {
            // 缩略图很小，读进内存再给：不占着文件，下次重新生成时不会写不进去
            return (new MemoryStream(File.ReadAllBytes(t)), "image/jpeg");
        }
        return (new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), item.Mime);
    }

    /// <summary>图片缩略图（长边 480 像素的 JPEG），网格里显示用，省得把十几 MB 的原图都塞进页面。</summary>
    private static string? Thumbnail(LibraryItem item)
    {
        var dir = Path.Combine(AppPaths.Library, "thumbs");
        var thumb = Path.Combine(dir, item.Id + ".jpg");
        var source = new FileInfo(item.Path);
        if (File.Exists(thumb) && File.GetLastWriteTimeUtc(thumb) >= source.LastWriteTimeUtc)
        {
            return thumb;
        }
        try
        {
            Directory.CreateDirectory(dir);
            using var input = source.OpenRead();
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var scale = Math.Min(1.0, (double)ThumbSize / Math.Max(frame.PixelWidth, frame.PixelHeight));
            BitmapSource bitmap = scale < 1 ? new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale)) : frame;
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(thumb);
            encoder.Save(output);
            return thumb;
        }
        catch (Exception ex)
        {
            Log.Warn($"生成缩略图失败：{item.Path}", ex);
            return null;
        }
    }

    /// <summary>
    /// 分享：把文件放到剪贴板（和在资源管理器里按 Ctrl+C 一样），可以直接粘贴到微信、邮件、聊天软件里发出去。
    /// </summary>
    public int CopyToClipboard(IEnumerable<string> ids)
    {
        var files = new StringCollection();
        foreach (var item in ids.Select(Store.Get).OfType<LibraryItem>().Where(i => File.Exists(i.Path)))
        {
            files.Add(item.Path);
        }
        if (files.Count == 0)
        {
            return 0;
        }
        Application.Current.Dispatcher.Invoke(() => Clipboard.SetFileDropList(files));
        return files.Count;
    }

    /// <summary>另存为：一个文件弹保存对话框，多个文件选一个文件夹。返回保存了几个。</summary>
    public int Download(IReadOnlyList<string> ids, Func<string, string?> pickSaveFile, Func<string?> pickFolder)
    {
        var items = ids.Select(Store.Get).OfType<LibraryItem>().Where(i => File.Exists(i.Path)).ToList();
        if (items.Count == 0)
        {
            return 0;
        }
        if (items.Count == 1)
        {
            var target = pickSaveFile(items[0].Name);
            if (target is null)
            {
                return 0;
            }
            File.Copy(items[0].Path, target, overwrite: true);
            return 1;
        }
        var folder = pickFolder();
        if (folder is null)
        {
            return 0;
        }
        foreach (var item in items)
        {
            File.Copy(item.Path, UniquePath(Path.Combine(folder, item.Name)), overwrite: false);
        }
        return items.Count;
    }

    private void TryAdd(string path, string source, bool copy, string conversationId)
    {
        try
        {
            Store.Add(path, source, copy, conversationId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"收录到资料库失败：{path}", ex);
        }
    }

    private static bool IsTemp(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var temp = Path.GetFullPath(AppPaths.Temp).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
