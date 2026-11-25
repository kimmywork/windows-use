using global::System.Text;
using WinUse.Core.Capabilities;
using WinUse.Core.Policy;
using WinUse.Core.Protocol;

namespace WinUse.Platform.Windows.Capabilities;

internal abstract class FileCapabilityBase : WindowsCapability
{
    protected string RequireAllowedPath(CapabilityContext context, string name)
    {
        var path = context.Args.RequireString(name);
        var policy = context.GetRequiredService<PathPolicy>();
        if (!policy.IsAllowed(path))
        {
            throw new CapabilityResultException("PERMISSION_DENIED",
                $"路径不在白名单内（用户目录/临时目录）: {path}",
                new { path, allowed_roots = policy.AllowedRoots });
        }
        return Path.GetFullPath(path);
    }
}

internal sealed class FileList : FileCapabilityBase
{
    public override string Id => "file.list";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var dir = RequireAllowedPath(context, "path");
        if (!Directory.Exists(dir))
        {
            return Fail("ELEMENT_NOT_FOUND", $"目录不存在: {dir}");
        }

        var pattern = context.Args.GetString("pattern") ?? "*";
        var entries = Directory.EnumerateFileSystemEntries(dir, pattern)
            .Select(p =>
            {
                var isDir = Directory.Exists(p);
                FileSystemInfo info = isDir ? new DirectoryInfo(p) : new FileInfo(p);
                return new
                {
                    name = info.Name,
                    path = info.FullName,
                    type = isDir ? "directory" : "file",
                    size = isDir ? (long?)null : ((FileInfo)info).Length,
                    modified = info.LastWriteTime,
                };
            })
            .ToArray();

        return Ok(new { path = dir, count = entries.Length, entries });
    }
}

internal sealed class FileStat : FileCapabilityBase
{
    public override string Id => "file.stat";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = RequireAllowedPath(context, "path");
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return Fail("ELEMENT_NOT_FOUND", $"路径不存在: {path}");
        }
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return Ok(new
        {
            path = info.FullName,
            type = info is DirectoryInfo ? "directory" : "file",
            size = info is FileInfo f ? (long?)f.Length : null,
            created = info.CreationTime,
            modified = info.LastWriteTime,
            accessed = info.LastAccessTime,
            attributes = info.Attributes.ToString(),
            @readonly = info.Attributes.HasFlag(FileAttributes.ReadOnly),
        });
    }
}

internal sealed class FileExists : FileCapabilityBase
{
    public override string Id => "file.exists";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = RequireAllowedPath(context, "path");
        return Ok(new { exists = File.Exists(path) || Directory.Exists(path), path });
    }
}

internal sealed class FileRead : FileCapabilityBase
{
    public override string Id => "file.read";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = RequireAllowedPath(context, "path");
        if (!File.Exists(path))
        {
            return Fail("ELEMENT_NOT_FOUND", $"文件不存在: {path}");
        }
        var encoding = (context.Args.GetString("encoding")?.ToLowerInvariant()) switch
        {
            "utf16" or "utf-16" => Encoding.Unicode,
            _ => Encoding.UTF8,
        };
        var text = File.ReadAllText(path, encoding);
        return Ok(new { path, text, encoding = encoding.WebName });
    }
}

internal sealed class FileWrite : FileCapabilityBase
{
    public override string Id => "file.write";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = RequireAllowedPath(context, "path");
        var text = context.Args.GetString("content")
                   ?? context.Args.GetString("text")
                   ?? throw new MissingParameterException("content");
        var append = context.Args.GetBool("append") ?? false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (append)
        {
            File.AppendAllText(path, text);
        }
        else
        {
            File.WriteAllText(path, text);
        }
        return Ok(new { path, bytes = new FileInfo(path).Length });
    }
}

internal sealed class FileCopy : FileCapabilityBase
{
    public override string Id => "file.copy";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var source = RequireAllowedPath(context, "source");
        var destination = RequireAllowedPath(context, "destination");
        var overwrite = context.Args.GetBool("overwrite") ?? true;
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination, overwrite);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite);
        }
        return Ok(new { source, destination });
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)), overwrite);
        }
    }
}

internal sealed class FileMove : FileCapabilityBase
{
    public override string Id => "file.move";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var source = RequireAllowedPath(context, "source");
        var destination = RequireAllowedPath(context, "destination");
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: true);
        }
        return Ok(new { source, destination });
    }
}

internal sealed class FileMkdir : FileCapabilityBase
{
    public override string Id => "file.mkdir";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = RequireAllowedPath(context, "path");
        Directory.CreateDirectory(path);
        return Ok(new { path, created = Directory.Exists(path) });
    }
}

// ---- advanced ----

internal sealed class FileDelete : FileCapabilityBase
{
    public override string Id => "file.delete";

    protected override CapabilityResult Run(CapabilityContext context)
    {
        var path = RequireAllowedPath(context, "path");
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: context.Args.GetBool("recursive") ?? true);
        }
        else
        {
            File.Delete(path);
        }
        return Ok(new { path, deleted = !File.Exists(path) && !Directory.Exists(path) });
    }
}
