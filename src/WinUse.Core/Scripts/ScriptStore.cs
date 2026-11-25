using System.Text.Json;
using WinUse.Core.Capabilities;

namespace WinUse.Core.Scripts;

/// <summary>脚本格式错误：校验失败必须显式报错，禁止静默写入/丢弃。</summary>
public sealed class ScriptFormatException : Exception
{
    public ScriptFormatException(string message) : base(message) { }
}

/// <summary>
/// 用户脚本存储：目录下每个脚本一个 &lt;name&gt;.json（缩进、UTF-8 无 BOM）。
/// 默认目录 %LOCALAPPDATA%\WinUse\scripts。
/// </summary>
public sealed class ScriptStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    public ScriptStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    /// <summary>
    /// 列出全部可解析脚本；无法解析的文件进入 <paramref name="loadErrors"/>，不静默跳过。
    /// </summary>
    public ScriptDocument[] List(out IReadOnlyList<(string File, string Error)> loadErrors)
    {
        var docs = new List<ScriptDocument>();
        var errors = new List<(string, string)>();

        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                docs.Add(LoadFromFile(file));
            }
            catch (Exception ex)
            {
                errors.Add((Path.GetFileName(file), ex.Message));
            }
        }

        loadErrors = errors;
        return docs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ScriptDocument Load(string name) => LoadFromFile(PathFor(name));

    private ScriptDocument LoadFromFile(string file)
    {
        ScriptFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ScriptFileDto>(File.ReadAllText(file), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ScriptFormatException($"JSON 解析失败: {ex.Message}");
        }

        if (dto is null)
        {
            throw new ScriptFormatException("文件内容为空");
        }

        var doc = new ScriptDocument(
            dto.Name ?? Path.GetFileNameWithoutExtension(file),
            dto.Title ?? "",
            dto.Description ?? "",
            dto.Steps is null ? Array.Empty<ScriptStep>() : dto.Steps.ToArray(),
            string.IsNullOrWhiteSpace(dto.OnError) ? "abort" : dto.OnError!);

        Validate(doc);
        return doc;
    }

    /// <summary>校验并保存（返回写入的完整路径）。</summary>
    public string Save(ScriptDocument doc)
    {
        Validate(doc);

        var dto = new ScriptFileDto
        {
            Name = doc.Name,
            Title = doc.Title,
            Description = doc.Description,
            OnError = doc.OnError,
            Steps = doc.Steps.ToList(),
        };

        var path = PathFor(doc.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, WriteOptions), new System.Text.UTF8Encoding(false));
        return path;
    }

    public bool Exists(string name) => File.Exists(PathFor(name));

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Rename(string oldName, string newName)
    {
        var doc = Load(oldName);
        var renamed = doc with { Name = newName };
        Save(renamed);
        Delete(oldName);
    }

    /// <summary>静态校验（不依赖运行时目录）。</summary>
    public static void Validate(ScriptDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Name))
        {
            throw new ScriptFormatException("脚本缺少 name");
        }
        if (doc.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ScriptFormatException($"脚本名含非法文件名字符: {doc.Name}");
        }
        if (!ScriptDocument.OnErrorPolicies.Contains(doc.OnError))
        {
            throw new ScriptFormatException($"on_error 仅支持 abort/continue/rollback，实际: {doc.OnError}");
        }
        if (doc.Steps.Count == 0)
        {
            throw new ScriptFormatException("脚本至少包含一个步骤");
        }
        for (var i = 0; i < doc.Steps.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(doc.Steps[i].Capability))
            {
                throw new ScriptFormatException($"第 {i + 1} 步缺少 capability");
            }
            if (doc.Steps[i].Params is { } p && p.ValueKind != JsonValueKind.Object && p.ValueKind != JsonValueKind.Null)
            {
                throw new ScriptFormatException($"第 {i + 1} 步的 params 必须是对象");
            }
        }
    }

    /// <summary>
    /// 结合能力目录做运行时校验：每步引用的能力必须存在。
    /// 未实现/未授权不在此拦截（运行时按 trace 体现）。
    /// </summary>
    public static void ValidateAgainstCatalog(ScriptDocument doc, CapabilityRegistry registry)
    {
        Validate(doc);
        for (var i = 0; i < doc.Steps.Count; i++)
        {
            if (!registry.IsKnown(doc.Steps[i].Capability))
            {
                throw new ScriptFormatException(
                    $"第 {i + 1} 步引用了不存在的能力: {doc.Steps[i].Capability}");
            }
        }
    }

    public string PathFor(string name) => Path.Combine(_directory, name + ".json");

    /// <summary>
    /// 解析编辑器文本为脚本文档（供外部 UI 使用，避免暴露内部 DTO）。
    /// name 以当前打开的文件名为准。
    /// </summary>
    public static bool TryParseText(string text, string currentName,
        out ScriptDocument? document, out string? error)
    {
        document = null;
        error = null;

        ScriptFileDto? dto;
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            dto = JsonSerializer.Deserialize<ScriptFileDto>(text, options);
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "内容为空";
            return false;
        }
        if (dto.Steps is null)
        {
            error = "缺少 steps 数组";
            return false;
        }

        var doc = new ScriptDocument(
            currentName,
            dto.Title ?? "",
            dto.Description ?? "",
            dto.Steps.ToArray(),
            string.IsNullOrWhiteSpace(dto.OnError) ? "abort" : dto.OnError!);

        try
        {
            Validate(doc);
        }
        catch (ScriptFormatException ex)
        {
            error = ex.Message;
            return false;
        }

        document = doc;
        return true;
    }

    /// <summary>新建脚本模板。</summary>
    public static ScriptDocument Template(string name)
    {
        var emptyParams = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
        return new ScriptDocument(
            name,
            "新脚本",
            "在此填写脚本说明",
            new[]
            {
                new ScriptStep("display.list", emptyParams),
            },
            "abort");
    }
}
