using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Flyknit.Core.Chat;
using Flyknit.Core.Storage;

namespace Flyknit.Client.Bridge;

// 与 client/web/src/types.ts 保持一致（JSON 使用 camelCase）

public sealed record ConversationDto(
    string Id,
    string Title,
    string TitleSource,
    string Mode,
    bool Pinned,
    string TranslateFrom,
    string TranslateTo,
    string CreatedAt,
    string UpdatedAt,
    string? DeletedAt,
    int MessageCount)
{
    public static ConversationDto From(Conversation c) => new(
        c.Id,
        c.Title,
        c.TitleSource,
        ConversationStore.ModeToText(c.Mode),
        c.Pinned,
        c.TranslateFrom,
        c.TranslateTo,
        c.CreatedAt.ToString("O"),
        c.UpdatedAt.ToString("O"),
        c.DeletedAt?.ToString("O"),
        c.MessageCount);
}

public sealed class AttachmentDto
{
    public string FileName { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    public long Size { get; set; }

    public static AttachmentDto From(Attachment a) => new() { FileName = a.FileName, LocalPath = a.LocalPath, Mime = a.Mime, Size = a.Size };

    public Attachment ToModel() => new() { FileName = FileName, LocalPath = LocalPath, Mime = Mime, Size = Size };

    public static AttachmentDto FromPath(string path)
    {
        var info = new FileInfo(path);
        return new AttachmentDto
        {
            FileName = info.Name,
            LocalPath = info.FullName,
            Mime = MimeTypes.Get(info.Extension),
            Size = info.Exists ? info.Length : 0,
        };
    }
}

public sealed record ToolCallDto(string Id, string Name, string Arguments);

public sealed record MessageDto(
    string Id,
    string Role,
    string Content,
    string? Reasoning,
    List<AttachmentDto>? Attachments,
    List<ToolCallDto>? ToolCalls,
    string? ToolCallId,
    string? ToolName,
    string CreatedAt)
{
    public static MessageDto From(ChatMessage m) => new(
        m.Id,
        m.Role.ToString().ToLowerInvariant(),
        m.Content,
        m.Reasoning,
        m.Attachments.Count > 0 ? m.Attachments.Select(AttachmentDto.From).ToList() : null,
        m.ToolCalls.Count > 0 ? m.ToolCalls.Select(c => new ToolCallDto(c.Id, c.Name, c.ArgumentsJson)).ToList() : null,
        m.ToolCallId,
        m.ToolName,
        m.CreatedAt.ToString("O"));
}

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".md"] = "text/markdown",
        [".json"] = "application/json",
        [".pdf"] = "application/pdf",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".xls"] = "application/vnd.ms-excel",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip",
    };

    public static string Get(string extension) => Map.TryGetValue(extension, out var m) ? m : "application/octet-stream";
}

/// <summary>从工具参数中取出给用户看的关键内容（命令、路径、名称）。</summary>
public static class ToolDetail
{
    public static string From(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            foreach (var key in new[] { "command", "path", "name", "fact", "pattern" })
            {
                if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString() ?? "";
                    return s.Length > 600 ? s[..600] + "…" : s;
                }
            }
        }
        catch (JsonException)
        {
        }
        return "";
    }
}
