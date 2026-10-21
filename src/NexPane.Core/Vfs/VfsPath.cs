using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace NexPane.Core.Vfs;

/// <summary>
/// Scheme-qualified location (ARCHITECTURE §10.1). Examples:
/// <list type="bullet">
/// <item><c>file:///C:/Users/Ewais/Docs</c> (local drive) and <c>file://nas/share/dir</c> (UNC)</item>
/// <item><c>sftp://user@host:22/home/user</c>, <c>ftp://host/pub</c>, <c>ftps://host/</c>, <c>davs://host/remote.php/dav/files/u/</c></item>
/// <item><c>archive:file:///C:/x.zip!/inner/dir</c> (archives nest: <c>archive:archive:file:///C:/a.zip!/b.zip!/c</c>)</item>
/// <item><c>search:&lt;base64url(ast)&gt;</c> and <c>shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}</c> (opaque)</item>
/// </list>
/// <para>
/// <see cref="Path"/> holds the decoded hierarchical path (always starting with <c>/</c> for hierarchical schemes);
/// <see cref="ToString"/> percent-encodes the characters that would be ambiguous (<c>%</c>, <c>#</c>, <c>?</c>, <c>!</c>,
/// whitespace and control characters), so <c>Parse(p.ToString()) == p</c> always holds.
/// For <c>archive:</c> the <see cref="Authority"/> is the canonical string of the container path.
/// Native Windows paths (<c>C:\x</c>, <c>\\server\share\x</c>, <c>\\?\C:\x</c>, <c>\\?\UNC\server\share\x</c>) are accepted by
/// <see cref="Parse"/> and normalized to the <c>file:</c> scheme.
/// </para>
/// </summary>
public readonly record struct VfsPath
{
    public const string FileScheme = "file";
    public const string ArchiveScheme = "archive";
    public const string SearchScheme = "search";
    public const string ShellScheme = "shell";

    private static readonly HashSet<string> OpaqueSchemes = new(StringComparer.Ordinal) { SearchScheme, ShellScheme };

    public VfsPath(string scheme, string authority, string path)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(path);
        if (!IsValidScheme(scheme))
        {
            throw new ArgumentException($"Invalid VFS scheme '{scheme}'.", nameof(scheme));
        }

        Scheme = scheme.ToLowerInvariant();
        Authority = authority;
        Path = OpaqueSchemes.Contains(Scheme) ? path : NormalizeHierarchicalPath(Scheme, path);
    }

    /// <summary>Lower-case scheme, e.g. <c>file</c>, <c>sftp</c>, <c>archive</c>.</summary>
    public string Scheme { get; }

    /// <summary>Authority (<c>user@host:port</c>, UNC server, or the archive container path). Empty for local drives.</summary>
    public string Authority { get; }

    /// <summary>Decoded path. Hierarchical schemes: starts with <c>/</c>, no trailing slash except for roots.</summary>
    public string Path { get; }

    /// <summary>True for schemes whose path is an opaque payload (<c>search:</c>, <c>shell:</c>).</summary>
    public bool IsOpaque => OpaqueSchemes.Contains(Scheme);

    /// <summary>True when this is a local drive path (<c>file:///C:/...</c>).</summary>
    public bool IsLocalDrive => Scheme == FileScheme && Authority.Length == 0 && IsDriveRootPrefix(Path);

    /// <summary>True when this is a UNC path (<c>file://server/share/...</c>).</summary>
    public bool IsUnc => Scheme == FileScheme && Authority.Length > 0;

    /// <summary>The archive container (for <c>archive:</c> paths), otherwise null.</summary>
    public VfsPath? ArchiveContainer => Scheme == ArchiveScheme ? Parse(Authority) : null;

    /// <summary>User info part of the authority (<c>user</c> in <c>user@host:22</c>), decoded; null when absent.</summary>
    public string? UserInfo
    {
        get
        {
            if (Scheme == ArchiveScheme)
            {
                return null;
            }

            var at = Authority.LastIndexOf('@');
            return at < 0 ? null : Uri.UnescapeDataString(Authority[..at]);
        }
    }

    /// <summary>Host part of the authority without brackets (IPv6 literals are returned without <c>[ ]</c>).</summary>
    public string Host
    {
        get
        {
            if (Scheme == ArchiveScheme)
            {
                return string.Empty;
            }

            var hostPort = HostPort();
            if (hostPort.StartsWith('['))
            {
                var close = hostPort.IndexOf(']', StringComparison.Ordinal);
                return close < 0 ? hostPort[1..] : Uri.UnescapeDataString(hostPort[1..close]);
            }

            var colon = hostPort.LastIndexOf(':');
            return colon < 0 ? hostPort : hostPort[..colon];
        }
    }

    /// <summary>Port from the authority, or null.</summary>
    public int? Port
    {
        get
        {
            if (Scheme == ArchiveScheme)
            {
                return null;
            }

            var hostPort = HostPort();
            var portStart = hostPort.StartsWith('[') ? hostPort.IndexOf("]:", StringComparison.Ordinal) + 1 : hostPort.LastIndexOf(':');
            if (portStart <= 0 || portStart >= hostPort.Length - 1)
            {
                return null;
            }

            return int.TryParse(hostPort.AsSpan(portStart + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : null;
        }
    }

    /// <summary>Last path segment (decoded). For a drive root returns <c>C:</c>; for a UNC share root the share name;
    /// for the bare root <c>/</c> the authority (or empty).</summary>
    public string Name
    {
        get
        {
            if (IsOpaque)
            {
                return Path;
            }

            if (Path.Length <= 1)
            {
                return Scheme == ArchiveScheme ? (ArchiveContainer?.Name ?? string.Empty) : Authority;
            }

            var trimmed = Path.EndsWith('/') ? Path[..^1] : Path;
            var slash = trimmed.LastIndexOf('/');
            return trimmed[(slash + 1)..];
        }
    }

    /// <summary>True for <c>/</c>, a drive root <c>/C:/</c> or a UNC share root <c>//server/share</c>.</summary>
    public bool IsRoot
    {
        get
        {
            if (IsOpaque || Path == "/")
            {
                return true;
            }

            if (Scheme == FileScheme && Authority.Length == 0 && Path.Length == 4 && IsDriveRootPrefix(Path))
            {
                return true;
            }

            return Scheme == FileScheme && Authority.Length > 0 && Path.IndexOf('/', 1) < 0;
        }
    }

    /// <summary>Parent location, or null at a root.</summary>
    public VfsPath? Parent
    {
        get
        {
            if (IsRoot)
            {
                return null;
            }

            var slash = Path.LastIndexOf('/');
            var parentPath = slash <= 0 ? "/" : Path[..slash];
            if (Scheme == FileScheme && Authority.Length == 0 && parentPath.Length == 3 && IsDriveRootPrefix(parentPath + "/"))
            {
                parentPath += "/";
            }

            return new VfsPath(Scheme, Authority, parentPath);
        }
    }

    /// <summary>Path segments (decoded), excluding the empty root segment.</summary>
    public IReadOnlyList<string> Segments =>
        IsOpaque ? [Path] : Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Appends one or more child segments separated by <c>/</c>. <c>.</c> and <c>..</c> are resolved.</summary>
    public VfsPath Combine(string childName)
    {
        ArgumentNullException.ThrowIfNull(childName);
        if (IsOpaque)
        {
            throw new InvalidOperationException($"Cannot combine into an opaque '{Scheme}:' location.");
        }

        if (childName.Length == 0 || childName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Child name must be non-empty and must not contain NUL.", nameof(childName));
        }

        if (Scheme == FileScheme && childName.Contains('\\', StringComparison.Ordinal))
        {
            childName = childName.Replace('\\', '/');
        }

        if (childName.StartsWith('/'))
        {
            throw new ArgumentException("Child name must be relative.", nameof(childName));
        }

        var basePath = Path.EndsWith('/') ? Path : Path + "/";
        return new VfsPath(Scheme, Authority, basePath + childName);
    }

    /// <summary>Returns the Windows path for <c>file:</c> locations (<c>C:\x</c> or <c>\\server\share\x</c>).</summary>
    public string ToWindowsPath()
    {
        if (Scheme != FileScheme)
        {
            throw new InvalidOperationException($"'{this}' is not a file: path.");
        }

        if (Authority.Length > 0)
        {
            return @"\\" + Authority + Path.Replace('/', '\\');
        }

        if (!IsDriveRootPrefix(Path))
        {
            throw new InvalidOperationException($"'{this}' has no drive letter.");
        }

        var rest = Path[1..].Replace('/', '\\');
        return rest.Length == 2 ? rest + "\\" : rest;
    }

    /// <summary>Creates a <c>file:</c> path from a Windows path (drive, UNC, or <c>\\?\</c> forms).</summary>
    public static VfsPath FromWindowsPath(string windowsPath)
    {
        if (!TryFromWindowsPath(windowsPath, out var result))
        {
            throw new FormatException($"'{windowsPath}' is not an absolute Windows path.");
        }

        return result;
    }

    /// <summary>Parses a canonical VFS string or a native Windows path.</summary>
    public static VfsPath Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new FormatException($"'{value}' is not a valid VFS path.");
        }

        return result;
    }

    public static bool TryParse([NotNullWhen(true)] string? value, out VfsPath path)
    {
        path = default;
        if (string.IsNullOrEmpty(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        if (TryFromWindowsPath(value, out path))
        {
            return true;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !IsValidScheme(value.AsSpan(0, colon)))
        {
            return false;
        }

        var scheme = value[..colon].ToLowerInvariant();
        var rest = value[(colon + 1)..];

        if (OpaqueSchemes.Contains(scheme))
        {
            path = new VfsPath(scheme, string.Empty, rest);
            return true;
        }

        if (scheme == ArchiveScheme)
        {
            return TryParseArchive(rest, out path);
        }

        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[2..];
        var pathStart = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = pathStart < 0 ? rest : rest[..pathStart];
        var encodedPath = pathStart < 0 ? "/" : rest[pathStart..];
        if (!TryDecode(encodedPath, out var decodedPath) || !IsValidAuthority(authority))
        {
            return false;
        }

        if (scheme == FileScheme)
        {
            if (authority.Length == 0 && !IsDriveRootPrefix(decodedPath.Length == 3 ? decodedPath + "/" : decodedPath) && decodedPath != "/")
            {
                return false;
            }

            if (authority.Length > 0 && decodedPath.Length <= 1)
            {
                return false; // a UNC location needs at least a share name
            }
        }

        try
        {
            path = new VfsPath(scheme, authority, decodedPath);
            return true;
        }
        catch (ArgumentException)
        {
            path = default;
            return false;
        }
    }

    public override string ToString()
    {
        if (IsOpaque)
        {
            return Scheme + ":" + Path;
        }

        if (Scheme == ArchiveScheme)
        {
            return Scheme + ":" + Authority + "!" + Encode(Path);
        }

        return Scheme + "://" + Authority + Encode(Path);
    }

    public bool Equals(VfsPath other) =>
        string.Equals(Scheme, other.Scheme, StringComparison.Ordinal) &&
        string.Equals(Authority, other.Authority, StringComparison.Ordinal) &&
        string.Equals(Path, other.Path, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Scheme, Authority, Path);

    /// <summary>True when <paramref name="other"/> is this path or below it (ordinal, case-insensitive for <c>file:</c>).</summary>
    public bool IsAncestorOrSelfOf(VfsPath other)
    {
        if (!string.Equals(Scheme, other.Scheme, StringComparison.Ordinal))
        {
            return false;
        }

        var comparison = Scheme == FileScheme ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Authority, other.Authority, comparison))
        {
            return false;
        }

        if (string.Equals(Path, other.Path, comparison))
        {
            return true;
        }

        var prefix = Path.EndsWith('/') ? Path : Path + "/";
        return other.Path.StartsWith(prefix, comparison);
    }

    private string HostPort()
    {
        var at = Authority.LastIndexOf('@');
        return at < 0 ? Authority : Authority[(at + 1)..];
    }

    private static bool TryParseArchive(string rest, out VfsPath path)
    {
        path = default;
        var bang = rest.LastIndexOf('!');
        if (bang <= 0)
        {
            return false;
        }

        var container = rest[..bang];
        var inner = rest[(bang + 1)..];
        if (inner.Length == 0)
        {
            inner = "/";
        }

        if (!inner.StartsWith('/') || !TryParse(container, out var containerPath) || containerPath.IsOpaque ||
            !TryDecode(inner, out var decodedInner))
        {
            return false;
        }

        path = new VfsPath(ArchiveScheme, containerPath.ToString(), decodedInner);
        return true;
    }

    private static bool TryFromWindowsPath(string value, out VfsPath path)
    {
        path = default;
        if (value.Length < 2)
        {
            return false;
        }

        string native = value;
        if (native.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            native = @"\\" + native[8..];
        }
        else if (native.StartsWith(@"\\?\", StringComparison.Ordinal) || native.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            native = native[4..];
            if (!(native.Length >= 2 && char.IsAsciiLetter(native[0]) && native[1] == ':'))
            {
                return false; // device / volume GUID paths are not file: locations
            }
        }

        if (native.Length >= 2 && char.IsAsciiLetter(native[0]) && native[1] == ':')
        {
            if (native.Length > 2 && native[2] != '\\' && native[2] != '/')
            {
                return false; // drive-relative "C:foo" is not absolute
            }

            var drive = char.ToUpperInvariant(native[0]) + ":";
            var tail = native.Length > 2 ? native[2..].Replace('\\', '/') : "/";
            path = new VfsPath(FileScheme, string.Empty, "/" + drive + tail);
            return true;
        }

        if (native.StartsWith(@"\\", StringComparison.Ordinal) && native.Length > 2 && native[2] != '\\')
        {
            var body = native[2..].Replace('\\', '/');
            var slash = body.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == body.Length - 1)
            {
                return false; // \\server alone is not a share location
            }

            var server = body[..slash];
            if (!IsValidAuthority(server))
            {
                return false;
            }

            path = new VfsPath(FileScheme, server, body[slash..]);
            return true;
        }

        return false;
    }

    private static string NormalizeHierarchicalPath(string scheme, string path)
    {
        if (path.Length == 0)
        {
            return "/";
        }

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Path must not contain NUL.", nameof(path));
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var parts = path.Split('/');
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                // Never pop a drive designator.
                if (stack.Count > 0 && !(scheme == FileScheme && stack.Count == 1 && IsDriveSegment(stack[0])))
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            return "/";
        }

        if (scheme == FileScheme && IsDriveSegment(stack[0]))
        {
            stack[0] = char.ToUpperInvariant(stack[0][0]) + ":";
            if (stack.Count == 1)
            {
                return "/" + stack[0] + "/";
            }
        }

        return "/" + string.Join('/', stack);
    }

    private static bool IsDriveSegment(string segment) =>
        segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':';

    private static bool IsDriveRootPrefix(string path) =>
        path.Length >= 4 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' && path[3] == '/';

    private static bool IsValidScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.Length == 0 || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (var c in scheme)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
            {
                return false;
            }
        }

        // A single letter is a drive letter, never a scheme.
        return scheme.Length > 1;
    }

    private static bool IsValidAuthority(string authority)
    {
        foreach (var c in authority)
        {
            if (c is '/' or '\\' or '\0' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return false;
            }
        }

        var open = authority.IndexOf('[', StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = authority.IndexOf(']', open);
            if (close < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static string Encode(string path)
    {
        StringBuilder? sb = null;
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c is '%' or '#' or '?' or '!' || char.IsWhiteSpace(c) || char.IsControl(c))
            {
                sb ??= new StringBuilder(path, 0, i, path.Length + 16);
                var count = Encoding.UTF8.GetBytes(path.AsSpan(i, char.IsHighSurrogate(c) && i + 1 < path.Length ? 2 : 1), utf8);
                for (var b = 0; b < count; b++)
                {
                    sb.Append('%').Append(utf8[b].ToString("X2", CultureInfo.InvariantCulture));
                }

                if (char.IsHighSurrogate(c) && i + 1 < path.Length)
                {
                    i++;
                }
            }
            else
            {
                sb?.Append(c);
            }
        }

        return sb?.ToString() ?? path;
    }

    private static bool TryDecode(string encoded, out string decoded)
    {
        if (!encoded.Contains('%', StringComparison.Ordinal))
        {
            decoded = encoded;
            return true;
        }

        var bytes = new List<byte>(encoded.Length);
        var sb = new StringBuilder(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%')
            {
                if (i + 2 >= encoded.Length || !byte.TryParse(encoded.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    decoded = string.Empty;
                    return false;
                }

                bytes.Add(b);
                i += 2;
                continue;
            }

            FlushBytes(bytes, sb);
            sb.Append(encoded[i]);
        }

        FlushBytes(bytes, sb);
        decoded = sb.ToString();
        return true;
    }

    private static void FlushBytes(List<byte> bytes, StringBuilder sb)
    {
        if (bytes.Count == 0)
        {
            return;
        }

        sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
        bytes.Clear();
    }
}
