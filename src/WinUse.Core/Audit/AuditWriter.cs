using System.Text.Json;
using WinUse.Core.Protocol;

namespace WinUse.Core.Audit;

/// <summary>
/// 结构化审计写入器：按天滚动 JSONL（06 §12）。
/// 写入串行、同步落盘；审计失败不得阻断业务，但必须抛出可观测异常由 Host 处理。
/// </summary>
public sealed class AuditWriter
{
    private readonly string _directory;
    private readonly object _lock = new();

    public AuditWriter(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public void Write(AuditEntry entry)
    {
        var file = Path.Combine(_directory, $"audit-{entry.Timestamp:yyyyMMdd}.jsonl");
        var line = JsonSerializer.Serialize(entry);
        lock (_lock)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    public static string? RequestIdToString(JsonElement? id)
    {
        if (id is not { } e)
        {
            return null;
        }
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => e.GetRawText(),
        };
    }

    /// <summary>读取审计日志（按时间升序），供 audit.query 使用。</summary>
    public List<AuditEntry> Read(DateOnly? day = null, int limit = 1000)
    {
        var result = new List<AuditEntry>();
        string[] files;
        lock (_lock)
        {
            files = Directory.GetFiles(_directory, "audit-*.jsonl");
        }
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var entry = JsonSerializer.Deserialize<AuditEntry>(line);
                if (entry is null)
                {
                    continue;
                }
                if (day is { } d && DateOnly.FromDateTime(entry.Timestamp.DateTime) != d)
                {
                    continue;
                }
                result.Add(entry);
            }
        }
        return result.Count > limit ? result.Skip(result.Count - limit).ToList() : result;
    }
}
