using System.Text.Json;
using WinUse.Core.Capabilities;
using WinUse.Core.Dispatch;
using WinUse.Core.Protocol;
using WinUse.Core.Tools;

namespace WinUse.Transport.Cli;

/// <summary>
/// CLI 传输（06 §1）：单次命令 → JSON-RPC 调度 → stdout 输出。
/// 由 Host 在一次性命令模式下调用。
/// </summary>
public sealed class CliRunner
{
    private readonly CapabilityDispatcher _dispatcher;
    private readonly CapabilityRegistry _registry;

    public CliRunner(CapabilityDispatcher dispatcher, CapabilityRegistry registry)
    {
        _dispatcher = dispatcher;
        _registry = registry;
    }

    public async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            await stdout.WriteLineAsync(Usage);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "invoke" => await InvokeAsync(args[1..], stdout),
                "tools" => Tools(args[1..], stdout),
                "health" => Health(stdout),
                _ => Unknown(args[0], stderr),
            };
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"错误: {ex.Message}");
            return 2;
        }
    }

    private async Task<int> InvokeAsync(string[] args, TextWriter stdout)
    {
        RpcRequest request;
        if (args.Length > 0 && args[0] == "--stdin")
        {
            var raw = Console.In.ReadToEnd();
            request = JsonSerializer.Deserialize<RpcRequest>(raw)
                      ?? throw new InvalidOperationException("stdin 内容不是合法 JSON-RPC 请求");
        }
        else
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("invoke 需要 method，例如: winuse invoke display.list");
            }
            var method = args[0];
            var parameters = BuildParameters(args[1..]);
            request = new RpcRequest
            {
                Id = JsonSerializer.SerializeToElement(1),
                Method = method,
                Params = JsonSerializer.SerializeToElement(parameters),
            };
        }

        var response = await _dispatcher.DispatchAsync(request);
        await stdout.WriteLineAsync(JsonSerializer.Serialize(response, CliJsonOptions));
        return response?.Error is null ? 0 : 1;
    }

    private int Tools(string[] args, TextWriter stdout)
    {
        if (args.Length == 0)
        {
            throw new ArgumentException("tools 需要格式: openai | anthropic | mcp");
        }
        var format = args[0].ToLowerInvariant();
        var tiers = ExtractOption(args[1..], "--tiers");
        var granted = ParseTiers(tiers);
        var visible = _registry.ListVisible(granted);

        object payload = format switch
        {
            "openai" => ToolExporter.OpenAI(visible),
            "anthropic" => ToolExporter.Anthropic(visible),
            "mcp" => ToolExporter.Mcp(visible),
            _ => throw new ArgumentException($"未知工具格式: {format}"),
        };
        stdout.WriteLine(JsonSerializer.Serialize(payload, CliJsonOptions));
        return 0;
    }

    private int Health(TextWriter stdout)
    {
        stdout.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            version = "0.1.0",
            implemented = _registry.Query().Count(d => _registry.IsImplemented(d.Id)),
        }, CliJsonOptions));
        return 0;
    }

    private static int Unknown(string command, TextWriter stderr)
    {
        stderr.WriteLine($"未知命令: {command}");
        stderr.Write(Usage);
        return 2;
    }

    /// <summary>
    /// 解析 key=value 参数：值优先按 JSON 解析（数字/布尔/数组/对象/带引号字符串），
    /// 失败则作为原始字符串。
    /// </summary>
    private static Dictionary<string, object?> BuildParameters(IReadOnlyList<string> args)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var eq = arg.IndexOf('=');
            if (eq <= 0)
            {
                throw new ArgumentException($"参数必须为 key=value 形式: {arg}");
            }
            var key = arg[..eq];
            var rawValue = arg[(eq + 1)..];
            result[key] = ParseValue(rawValue);
        }
        return result;
    }

    private static object? ParseValue(string rawValue)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawValue);
            return doc.RootElement.Deserialize<JsonElement>();
        }
        catch (JsonException)
        {
            return rawValue; // 未加引号的普通字符串
        }
    }

    private static string? ExtractOption(IEnumerable<string> args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return arg[(name.Length + 1)..];
            }
        }
        return null;
    }

    private static HashSet<Tier> ParseTiers(string? text)
    {
        var granted = new HashSet<Tier> { Tier.Core };
        if (text is null)
        {
            return granted;
        }
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<Tier>(raw, ignoreCase: true, out var t) && t != Tier.Future)
            {
                granted.Add(t);
            }
        }
        return granted;
    }

    internal static readonly JsonSerializerOptions CliJsonOptions = new()
    {
        WriteIndented = false,
    };

    private const string Usage = """
        WinUse CLI

        用法:
          winuse invoke <method> [key=value ...]   调用一个能力（值按 JSON 解析，否则为字符串）
          winuse invoke --stdin                    从 stdin 读取完整 JSON-RPC 请求
          winuse tools <openai|anthropic|mcp> [--tiers=core,extended,advanced]
                                                   导出工具定义
          winuse health                            健康检查
          winuse help                              显示本帮助

        示例:
          winuse invoke display.list
          winuse invoke mouse.move x=100 y=200
        """;
}
