using WinUse.Core.Protocol;

namespace WinUse.Core.Dispatch;

/// <summary>会话状态（06 §3）：握手后建立，单例。</summary>
public sealed class SessionState
{
    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N");
    public string Mode { get; private set; } = "standard";
    public string? Client { get; private set; }
    public string? ClientVersion { get; private set; }
    public IReadOnlySet<Tier> GrantedTiers { get; private set; } = new HashSet<Tier> { Tier.Core };
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;
    public bool HelloDone { get; private set; }

    public void ApplyHello(string? client, string? clientVersion, string? mode, IEnumerable<Tier> tiers)
    {
        HelloDone = true;
        Client = client;
        ClientVersion = clientVersion;
        if (!string.IsNullOrEmpty(mode))
        {
            Mode = mode!;
        }
        var set = new HashSet<Tier> { Tier.Core };
        foreach (var t in tiers)
        {
            set.Add(t);
        }
        GrantedTiers = set;
    }
}
