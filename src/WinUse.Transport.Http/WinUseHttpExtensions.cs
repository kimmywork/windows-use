using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WinUse.Core.Capabilities;
using WinUse.Core.Dispatch;
using WinUse.Core.Protocol;
using WinUse.Core.Tools;
using WinUse.Transport.Http.Security;

namespace WinUse.Transport.Http;

/// <summary>WinUse HTTP 传输组装（06 §1）。</summary>
public static class WinUseHttpExtensions
{
    /// <summary>注册 HTTP 传输所需服务与安全选项。</summary>
    public static IServiceCollection AddWinUseHttp(
        this IServiceCollection services,
        Action<WinUseHttpSecurityOptions> configure)
    {
        services.Configure(configure);
        return services;
    }

    /// <summary>启用回环安全中间件（须在端点映射前调用）。</summary>
    public static IApplicationBuilder UseWinUseSecurity(this IApplicationBuilder app) =>
        app.UseMiddleware<LoopbackSecurityMiddleware>();

    /// <summary>映射 WinUse 全部 HTTP 端点。</summary>
    public static IEndpointRouteBuilder MapWinUseEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/invoke", InvokeAsync);
        endpoints.MapGet("/v1/tools/openai", ToolsOpenAiAsync);
        endpoints.MapGet("/v1/tools/anthropic", ToolsAnthropicAsync);
        endpoints.MapGet("/v1/tools/mcp", ToolsMcpAsync);
        endpoints.MapGet("/v1/health", HealthAsync);
        return endpoints;
    }

    private static async Task<IResult> InvokeAsync(
        HttpContext http,
        CapabilityDispatcher dispatcher,
        CapabilityRegistry registry,
        CancellationToken cancellationToken)
    {
        RpcRequest? request;
        try
        {
            // 显式拒绝 JSON-RPC 批量（v1 协议仅支持单请求）。
            var raw = await new StreamReader(http.Request.Body).ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Results.Json(RpcResponse.Fail(null,
                    Error("invalid_request", "请求体为空")), statusCode: 400);
            }
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('['))
            {
                return Results.Json(RpcResponse.Fail(null,
                    Error("invalid_request", "v1 不支持 JSON-RPC 批量请求")), statusCode: 400);
            }
            request = System.Text.Json.JsonSerializer.Deserialize<RpcRequest>(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return Results.Json(RpcResponse.Fail(null,
                Error("parse_error", "请求体不是合法 JSON")), statusCode: 400);
        }

        var response = await dispatcher.DispatchAsync(request, cancellationToken);
        if (response is null)
        {
            return Results.Accepted(); // 通知：无 id，执行但不回响应
        }
        return Results.Json(response, statusCode: response.Error is null ? 200 : 200);
    }

    private static IResult ToolsOpenAiAsync(
        HttpContext http, CapabilityRegistry registry) =>
        Results.Json(ToolExporter.OpenAI(Visible(http, registry)));

    private static IResult ToolsAnthropicAsync(
        HttpContext http, CapabilityRegistry registry) =>
        Results.Json(ToolExporter.Anthropic(Visible(http, registry)));

    private static IResult ToolsMcpAsync(
        HttpContext http, CapabilityRegistry registry) =>
        Results.Json(ToolExporter.Mcp(Visible(http, registry)));

    /// <summary>按 ?tiers= 查询参数过滤；默认仅 core（06 §3.2）。</summary>
    private static CapabilityDescriptor[] Visible(HttpContext http, CapabilityRegistry registry)
    {
        var tiersText = http.Request.Query["tiers"].ToString();
        if (string.IsNullOrEmpty(tiersText))
        {
            return registry.Query(Tier.Core);
        }
        var granted = new HashSet<Tier> { Tier.Core };
        foreach (var raw in tiersText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<Tier>(raw, ignoreCase: true, out var t) && t != Tier.Future)
            {
                granted.Add(t);
            }
        }
        return registry.ListVisible(granted);
    }

    private static IResult HealthAsync(CapabilityRegistry registry)
    {
        var uptime = DateTime.UtcNow - ProcessStartTime;
        return Results.Json(new
        {
            ok = true,
            version = ThisVersion,
            uptime_s = Math.Round(uptime.TotalSeconds, 1),
            implemented = registry.Query().Count(d => registry.IsImplemented(d.Id)),
        });
    }

    private static readonly DateTime ProcessStartTime = DateTime.UtcNow;
    private const string ThisVersion = "0.1.0";

    private static RpcError Error(string type, string message) => new()
    {
        Code = type switch
        {
            "parse_error" => -32700,
            _ => -32600,
        },
        Type = type,
        Message = message,
    };

    private static int HttpErrorStatus(int code) => code;
}
