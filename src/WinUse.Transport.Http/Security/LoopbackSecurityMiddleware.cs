using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace WinUse.Transport.Http.Security;

/// <summary>HTTP 安全选项（06 §13）。</summary>
public sealed class WinUseHttpSecurityOptions
{
    /// <summary>期望的 Bearer token；为空则拒绝所有需鉴权请求。</summary>
    public string Token { get; set; } = "";

    /// <summary>允许的 Host 头（小写，含端口）。默认仅回环。</summary>
    public HashSet<string> AllowedHosts { get; } = new(StringComparer.Ordinal);

    /// <summary>无需鉴权的路径（精确匹配）。</summary>
    public HashSet<string> AnonymousPaths { get; } = new(StringComparer.Ordinal)
    {
        "/v1/health",
    };
}

/// <summary>
/// 回环安全中间件（06 §13）：
/// DNS rebinding（Host 校验）、Origin 拒绝、CSRF（Content-Type + 自定义头）、Bearer token。
/// </summary>
public sealed class LoopbackSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<WinUseHttpSecurityOptions> _options;

    public LoopbackSecurityMiddleware(
        RequestDelegate next,
        IOptionsMonitor<WinUseHttpSecurityOptions> options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var o = _options.CurrentValue;

        // 1) 拒绝任何 Origin（浏览器网页不得能调用）。
        if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
            !string.IsNullOrWhiteSpace(origin))
        {
            await Forbidden(context, "origin_rejected");
            return;
        }

        // 2) Host 头必须命中白名单（DNS rebinding 防护）。
        var host = context.Request.Host.Host.ToLowerInvariant();
        var hostWithPort = context.Request.Host.Port is { } port
            ? $"{host}:{port}"
            : host;
        var allowed = o.AllowedHosts.Count == 0
            ? IsLoopbackHost(host)
            : o.AllowedHosts.Contains(host) || o.AllowedHosts.Contains(hostWithPort);
        if (!allowed)
        {
            await Forbidden(context, "host_not_allowed");
            return;
        }

        // 3) 匿名路径放行（健康检查）。
        if (o.AnonymousPaths.Contains(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // 4) 写请求必须为 application/json（非浏览器简单请求）。
        if (HttpMethods.IsPost(context.Request.Method))
        {
            var contentType = context.Request.ContentType?.Split(';')[0].Trim()
                .ToLowerInvariant();
            if (contentType != "application/json")
            {
                await Forbidden(context, "content_type_must_be_json");
                return;
            }
        }

        // 5) Bearer token，或自定义头 X-WinUse-Token。
        var presented = ExtractBearer(context.Request.Headers)
                        ?? context.Request.Headers["X-WinUse-Token"].FirstOrDefault();
        if (presented is null || !FixedTimeEquals(presented, o.Token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "unauthorized",
                message = "缺少或无效的 Bearer token",
            });
            return;
        }

        await _next(context);
    }

    private static string? ExtractBearer(IHeaderDictionary headers)
    {
        var auth = headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }
        return null;
    }

    private static bool FixedTimeEquals(string presented, string expected)
    {
        if (expected.Length == 0)
        {
            return false;
        }
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "[::1]" or "::1";

    private static async Task Forbidden(HttpContext context, string reason)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "forbidden", reason });
    }
}
