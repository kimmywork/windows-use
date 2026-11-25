namespace WinUse.Core.Policy;

/// <summary>
/// 文件路径策略（06 §11、03 P0-16）。
/// 默认允许：用户目录、临时目录；其余路径需确认（由调用方触发确认门）。
/// </summary>
public sealed class PathPolicy
{
    private readonly string[] _allowedRoots;

    public PathPolicy(IEnumerable<string>? additionalRoots = null)
    {
        var roots = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
        };
        if (additionalRoots is not null)
        {
            roots.AddRange(additionalRoots);
        }
        _allowedRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>路径是否落在白名单根内。</summary>
    public bool IsAllowed(string path)
    {
        var full = Normalize(path);
        return _allowedRoots.Any(root =>
            full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
