using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace WinUse.Host;

/// <summary>
/// Token 存储（06 §13）：%LOCALAPPDATA%\WinUse\token，
/// 首次启动生成 32 字节随机 token；文件 ACL 收紧为仅当前用户（等价 600）。
/// </summary>
public static class TokenStore
{
    public static string LoadOrCreate()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUse");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "token");

        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file).Trim();
            if (existing.Length > 0)
            {
                return existing;
            }
        }

        var token = Generate();
        File.WriteAllText(file, token);
        TryLockToOwner(file);
        return token;
    }

    public static string Rotate()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinUse");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "token");
        var token = Generate();
        File.WriteAllText(file, token);
        TryLockToOwner(file);
        return token;
    }

    private static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TryLockToOwner(string file)
    {
        try
        {
            var security = new FileSecurity();
            var current = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(current);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            // SYSTEM 仍可访问（杀软/运维），但不保留其他继承主体。
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
        catch
        {
            // ACL 收紧失败不致命；回环绑定 + token 仍构成主要防线。
        }
    }
}
