using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinUse.Core.Dispatch;
using WinUse.Core.Protocol;

namespace WinUse.Core.Scripts;

/// <summary>单步执行轨迹（对齐 07 §20.2 trace）。</summary>
public sealed record ScriptStepTrace(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("error_message")] string? ErrorMessage,
    [property: JsonPropertyName("result")] object? Result);

/// <summary>失败位置。</summary>
public sealed record ScriptFailure(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("capability")] string Capability,
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message);

/// <summary>脚本运行结果。</summary>
public sealed record ScriptRunResult(
    [property: JsonPropertyName("script")] string Script,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("steps_total")] int StepsTotal,
    [property: JsonPropertyName("steps_completed")] int StepsCompleted,
    [property: JsonPropertyName("trace")] IReadOnlyList<ScriptStepTrace> Trace,
    [property: JsonPropertyName("failed_at")] ScriptFailure? FailedAt,
    [property: JsonPropertyName("note")] string? Note)
{
    public static ScriptRunResult Empty(string script) =>
        new(script, true, 0, 0, Array.Empty<ScriptStepTrace>(), null, null);
}

/// <summary>
/// 脚本执行器：逐步经 <see cref="CapabilityDispatcher"/> 派发，
/// 每步都走完整的档位/风险/确认/审计链路（07 §20.2：R3+ 仍需确认门）。
/// </summary>
public sealed class ScriptRunner
{
    private readonly CapabilityDispatcher _dispatcher;

    public ScriptRunner(CapabilityDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<ScriptRunResult> RunAsync(ScriptDocument script, CancellationToken cancellationToken = default)
    {
        var trace = new List<ScriptStepTrace>();
        var continueOnError = script.OnError.Equals("continue", StringComparison.OrdinalIgnoreCase);
        string? note = null;

        if (script.OnError.Equals("rollback", StringComparison.OrdinalIgnoreCase))
        {
            // 事务回滚属 v1.5（07 §20.4）；v1 按中止处理并显式说明。
            note = "on_error=rollback 需要事务支持（v1.5），本次按 abort 处理";
        }

        for (var i = 0; i < script.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var step = script.Steps[i];
            var request = new RpcRequest
            {
                Id = JsonSerializer.SerializeToElement(i + 1),
                Method = step.Capability,
                Params = step.Params is { } p && p.ValueKind == JsonValueKind.Object
                    ? p
                    : JsonSerializer.SerializeToElement(new Dictionary<string, object?>()),
                Meta = new RequestMeta { Client = "script-runner" },
            };

            var sw = Stopwatch.StartNew();
            var response = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (response is null)
            {
                // 通知不返回响应：脚本步骤必须有 id，正常不会走到。
                trace.Add(new ScriptStepTrace(i + 1, step.Capability, false,
                    sw.Elapsed.TotalMilliseconds, "INTERNAL_ERROR", "步骤未返回响应", null));
            }
            else if (response.Error is { } error)
            {
                trace.Add(new ScriptStepTrace(i + 1, step.Capability, false,
                    sw.Elapsed.TotalMilliseconds, error.Type, error.Message, null));

                if (!continueOnError)
                {
                    var failure = new ScriptFailure(i + 1, step.Capability, error.Type, error.Message);
                    return new ScriptRunResult(script.Name, false, script.Steps.Count, i, trace, failure, note);
                }
            }
            else
            {
                trace.Add(new ScriptStepTrace(i + 1, step.Capability, true,
                    sw.Elapsed.TotalMilliseconds, null, null, response.Result));
            }
        }

        var failed = trace.Any(t => !t.Ok);
        return new ScriptRunResult(
            script.Name,
            !failed,
            script.Steps.Count,
            trace.Count(t => t.Ok),
            trace,
            null,
            note);
    }
}
