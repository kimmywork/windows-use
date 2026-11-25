using WinUse.Core.Protocol;

namespace WinUse.Core.Errors;

/// <summary>错误码目录（06 §9.2）。一条错误的全部元数据集中在此，禁止散落硬编码。</summary>
public sealed record ErrorDefinition(
    string Type,
    int Code,
    string Recovery,
    string DefaultMessage,
    string Hint);

/// <summary>错误码目录查询与构造。</summary>
public static class ErrorCatalog
{
    // JSON-RPC 标准错误（06 §9.1）
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    // 业务错误按 06 §9.2 表顺序从 -32000 起
    private static readonly ErrorDefinition[] Business =
    [
        new("ELEMENT_NOT_FOUND", -32000, "degrade", "选择器未命中任何元素", "重新 element.tree 刷新视图，或退到 PIXEL 层"),
        new("AMBIGUOUS", -32001, "escalate", "多个元素命中且未指定 index", "不要猜：用 element.count 看数量，加 near/within 收缩，或问用户"),
        new("ELEMENT_STALE", -32002, "retry", "elementId 已失效（窗口重建/布局变化）", "重新 element.find"),
        new("UIPI_BLOCKED", -32003, "escalate", "目标完整性级别高于 WinUse，或 Win32 可见但 UIA 不可见", "不要再重试；以管理员身份重启 WinUse，或退回只读能力"),
        new("SECURE_DESKTOP", -32004, "escalate", "当前处于 UAC 安全桌面", "不可自动恢复，需要用户在安全桌面上确认"),
        new("SESSION_LOCKED", -32005, "wait_resume", "桌面已锁定", "等待用户解锁后通过 session.resume 恢复"),
        new("SCREENSAVER_ACTIVE", -32006, "wait_resume", "屏保已激活", "等待用户恢复会话"),
        new("RDP_MINIMIZED", -32007, "wait_resume", "RDP 会话已最小化", "等待会话恢复"),
        new("SESSION_NOT_INTERACTIVE", -32008, "wait_resume", "会话不可用（锁屏/休眠等）", "轮询 session.preflight；持续失败则终止并告知用户"),
        new("TARGET_MINIMIZED", -32009, "retry", "目标窗口已最小化", "先还原窗口再重试"),
        new("TARGET_OCCLUDED", -32010, "degrade", "目标被遮挡（仅影响 PIXEL 通道）", "改用 UIA 通道"),
        new("CAPTURE_PROTECTED", -32011, "abort", "目标启用了防截屏（WDA_EXCLUDEFROMCAPTURE/DRM）", "放弃截图，改走 element.* 结构化读取"),
        new("PATTERN_UNSUPPORTED", -32012, "degrade", "控件不支持所需 Pattern", "查可用 Pattern，走降级链下一层"),
        new("NOT_AUTOMATABLE", -32013, "abort", "目标为自绘 UI 且无任何结构化通道", "只能 PIXEL 层并由人工确认（07 §16）"),
        new("TIMEOUT", -32014, "retry", "等待超时", "检查前置条件；可延长超时重试一次"),
        new("FOCUS_LOST", -32015, "retry", "操作过程中焦点被抢占", "重新 window.focus 后重试"),
        new("PERMISSION_DENIED", -32016, "escalate", "文件系统/注册表权限不足", "申请授权或更换路径"),
        new("APPROVAL_REQUIRED", -32017, "escalate", "该动作需要确认", "等待确认结果；不要重试绕过"),
        new("APPROVAL_DENIED", -32018, "abort", "用户拒绝了该动作", "停止该动作，询问替代方案"),
        new("NEEDS_HUMAN", -32019, "wait_resume", "任务挂起：等待 WinUse 无法代替的外部事件", "这不是失败；用户处理后调用 session.resume"),
        new("VERIFY_FAILED", -32020, "abort", "独立通道验证无法完成，结果不可信", "不得宣称完成；更换验证通道重试"),
        new("VERIFY_MISMATCH", -32021, "abort", "读回校验结果与期望不一致", "见 data.expected / data.actual，换策略重试"),
        new("ROLLBACK_PARTIAL", -32022, "escalate", "事务仅部分回滚", "读 data.rollback_report 决定后续"),
        new("NOT_TRANSACTIONAL", -32023, "abort", "该能力不可纳入事务（如 UI 点击类）", "拆分：可补偿部分入事务，点击类不入"),
        new("PROTECTED_CONTENT", -32024, "abort", "目标为受保护内容（网银/密码控件）", "不应绕过，需用户手动处理"),
        new("RESERVED_KEY", -32025, "abort", "Windows 保留键（Win+L / Ctrl+Alt+Del）", "系统刻意拦截，无自动化路径"),
        new("INTEGRITY_MISMATCH", -32026, "escalate", "完整性级别关系不满足", "同 UIPI_BLOCKED：以管理员重启"),
        new("INVALID_COORDS", -32027, "retry", "坐标超出虚拟桌面范围", "用 display.list 重新获取拓扑并换算"),
        new("STALE_HANDLE", -32028, "retry", "句柄已失效", "重新定位（同 ELEMENT_STALE）"),
        new("NOT_SUPPORTED", -32029, "degrade", "能力被禁用或当前平台不支持", "查显式不支持清单，换等效路径"),
        new("CAPABILITY_NOT_GRANTED", -32030, "escalate", "该能力所属档位未在本次会话声明", "按 data.required_tier 用 session.hello 重新声明后重试"),
        new("VERSION_MISMATCH", -32031, "abort", "API 版本不匹配", "按提示更换协议版本"),
        new("RATE_LIMITED", -32032, "retry", "请求过于频繁", "按 retry_after_ms 退避"),
        new("NOT_IMPLEMENTED", -32033, "abort", "该能力已注册但尚未实现", "等待后续版本；不要重试"),
    ];

    private static readonly Dictionary<string, ErrorDefinition> ByType =
        Business.ToDictionary(d => d.Type, StringComparer.Ordinal);

    public static ErrorDefinition? Find(string type) =>
        ByType.TryGetValue(type, out var d) ? d : null;

    public static RpcError Rpc(string type, string? message = null, object? data = null)
    {
        var d = Find(type) ?? throw new ArgumentOutOfRangeException(nameof(type), $"未知错误类型: {type}");
        return new RpcError
        {
            Code = d.Code,
            Type = d.Type,
            Message = message ?? d.DefaultMessage,
            Recovery = d.Recovery,
            Data = data,
        };
    }

    /// <summary>JSON-RPC 标准错误。</summary>
    public static RpcError Standard(int code, string message, object? data = null)
    {
        var (type, recovery) = code switch
        {
            ParseError => ("PARSE_ERROR", "abort"),
            InvalidRequest => ("INVALID_REQUEST", "abort"),
            MethodNotFound => ("METHOD_NOT_FOUND", "abort"),
            InvalidParams => ("INVALID_PARAMS", "abort"),
            _ => ("INTERNAL_ERROR", "abort"),
        };
        return new RpcError
        {
            Code = code,
            Type = type,
            Message = message,
            Recovery = recovery,
            Data = data,
        };
    }
}
