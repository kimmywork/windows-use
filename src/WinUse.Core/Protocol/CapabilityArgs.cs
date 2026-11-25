using System.Text.Json;

namespace WinUse.Core.Protocol;

/// <summary>
/// 参数访问器：包装 params 的 <see cref="JsonElement"/>，提供类型化读取。
/// 所有读取均为容错的；必填参数由能力显式 Require* 并在缺失时返回 INVALID_PARAMS。
/// </summary>
public readonly struct CapabilityArgs
{
    public JsonElement Element { get; }

    public CapabilityArgs(JsonElement element)
    {
        Element = element;
    }

    public static CapabilityArgs FromObject(object? value)
    {
        if (value is null)
        {
            return default;
        }
        return new CapabilityArgs(JsonSerializer.SerializeToElement(value));
    }

    public bool IsValid => Element.ValueKind == JsonValueKind.Object;

    public bool Has(string name) =>
        IsValid && Element.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null;

    public JsonElement? Raw(string name) =>
        IsValid && Element.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p : null;

    public string? GetString(string name) =>
        Raw(name) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public int? GetInt32(string name)
    {
        var e = Raw(name);
        if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var v))
        {
            return v;
        }
        return null;
    }

    public long? GetInt64(string name)
    {
        var e = Raw(name);
        if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v))
        {
            return v;
        }
        return null;
    }

    public double? GetDouble(string name)
    {
        var e = Raw(name);
        if (e is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var v))
        {
            return v;
        }
        return null;
    }

    public bool? GetBool(string name)
    {
        var e = Raw(name);
        if (e is { ValueKind: JsonValueKind.True })
        {
            return true;
        }
        if (e is { ValueKind: JsonValueKind.False })
        {
            return false;
        }
        return null;
    }

    public T? GetObject<T>(string name, JsonSerializerOptions? options = null)
    {
        var e = Raw(name);
        return e is null ? default : e.Value.Deserialize<T>(options);
    }

    public string RequireString(string name) =>
        GetString(name) ?? throw new MissingParameterException(name);

    public int RequireInt32(string name) =>
        GetInt32(name) ?? throw new MissingParameterException(name);
}

/// <summary>必填参数缺失。由分发层转换为 INVALID_PARAMS。</summary>
public sealed class MissingParameterException : Exception
{
    public string ParameterName { get; }

    public MissingParameterException(string parameterName)
        : base($"缺少必填参数: {parameterName}")
    {
        ParameterName = parameterName;
    }
}
