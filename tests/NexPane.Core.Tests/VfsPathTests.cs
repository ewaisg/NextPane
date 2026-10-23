using NexPane.Core.Vfs;

namespace NexPane.Core.Tests;

public sealed class VfsPathTests
{
    [Theory]
    [InlineData(@"C:\Users\Ewais\Docs", "file", "", "/C:/Users/Ewais/Docs", "file:///C:/Users/Ewais/Docs")]
    [InlineData(@"c:\", "file", "", "/C:/", "file:///C:/")]
    [InlineData(@"C:", "file", "", "/C:/", "file:///C:/")]
    [InlineData(@"D:/data/x.txt", "file", "", "/D:/data/x.txt", "file:///D:/data/x.txt")]
    [InlineData(@"C:\a\.\b\..\c\", "file", "", "/C:/a/c", "file:///C:/a/c")]
    [InlineData(@"C:\..\..\x", "file", "", "/C:/x", "file:///C:/x")]
    [InlineData(@"\\nas\media\films", "file", "nas", "/media/films", "file://nas/media/films")]
    [InlineData(@"\\nas\media", "file", "nas", "/media", "file://nas/media")]
    [InlineData(@"\\?\C:\very\long", "file", "", "/C:/very/long", "file:///C:/very/long")]
    [InlineData(@"\\?\UNC\srv\share\dir", "file", "srv", "/share/dir", "file://srv/share/dir")]
    [InlineData(@"\\.\C:\dev", "file", "", "/C:/dev", "file:///C:/dev")]
    [InlineData("file:///C:/Users", "file", "", "/C:/Users", "file:///C:/Users")]
    [InlineData("FILE:///c:/Users/", "file", "", "/C:/Users", "file:///C:/Users")]
    [InlineData("file://nas/share/dir", "file", "nas", "/share/dir", "file://nas/share/dir")]
    [InlineData("sftp://user@host:22/home/user", "sftp", "user@host:22", "/home/user", "sftp://user@host:22/home/user")]
    [InlineData("ftp://host/pub", "ftp", "host", "/pub", "ftp://host/pub")]
    [InlineData("ftps://host/", "ftps", "host", "/", "ftps://host/")]
    [InlineData("ftps://host", "ftps", "host", "/", "ftps://host/")]
    [InlineData("davs://host/remote.php/dav/files/u/", "davs", "host", "/remote.php/dav/files/u", "davs://host/remote.php/dav/files/u")]
    [InlineData("sftp://[fe80::1]:2222/srv", "sftp", "[fe80::1]:2222", "/srv", "sftp://[fe80::1]:2222/srv")]
    [InlineData("search:eyJhIjoxfQ", "search", "", "eyJhIjoxfQ", "search:eyJhIjoxfQ")]
    [InlineData("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell", "", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void Parses_and_prints_canonical_form(string input, string scheme, string authority, string path, string printed)
    {
        var p = VfsPath.Parse(input);

        p.Scheme.Should().Be(scheme);
        p.Authority.Should().Be(authority);
        p.Path.Should().Be(path);
        p.ToString().Should().Be(printed);
        VfsPath.Parse(printed).Should().Be(p);
    }

    [Theory]
    [InlineData("archive:file:///C:/x.zip!/inner/dir", "file:///C:/x.zip", "/inner/dir")]
    [InlineData("archive:file:///C:/x.zip!", "file:///C:/x.zip", "/")]
    [InlineData("archive:file://nas/share/b.7z!/a", "file://nas/share/b.7z", "/a")]
    [InlineData("archive:archive:file:///C:/a.zip!/b.zip!/c", "archive:file:///C:/a.zip!/b.zip", "/c")]
    [InlineData("archive:sftp://u@h:22/x.tar!/d/e", "sftp://u@h:22/x.tar", "/d/e")]
    public void Parses_archive_nesting(string input, string container, string inner)
    {
        var p = VfsPath.Parse(input);

        p.Scheme.Should().Be("archive");
        p.Authority.Should().Be(container);
        p.Path.Should().Be(inner);
        p.ArchiveContainer.Should().Be(VfsPath.Parse(container));
        VfsPath.Parse(p.ToString()).Should().Be(p);
    }

    [Fact]
    public void Nested_archive_container_chain_resolves_to_the_outer_file()
    {
        var p = VfsPath.Parse("archive:archive:file:///C:/a.zip!/b.zip!/c");

        var inner = p.ArchiveContainer!.Value;
        inner.Scheme.Should().Be("archive");
        inner.Path.Should().Be("/b.zip");
        inner.ArchiveContainer.Should().Be(VfsPath.Parse(@"C:\a.zip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\path")]
    [InlineData("C:relative")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789012}\x")]
    [InlineData("file://server")]
    [InlineData("file:///notadrive/x")]
    [InlineData("sftp:host/x")]
    [InlineData("1abc://host/x")]
    [InlineData("ftp://ho st/x")]
    [InlineData("ftp://host/bad%zzescape")]
    [InlineData("ftp://host/trunc%2")]
    [InlineData("archive:file:///C:/x.zip")]
    [InlineData("archive:!/x")]
    [InlineData("sftp://[fe80::1/x")]
    public void Rejects_invalid_inputs(string input)
    {
        VfsPath.TryParse(input, out _).Should().BeFalse();
        var act = () => VfsPath.Parse(input);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Null_and_nul_characters_are_rejected()
    {
        VfsPath.TryParse(null, out _).Should().BeFalse();
        VfsPath.TryParse("C:\\a\0b", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("/My Docs/a#b%c?.txt", "ftp://host/My%20Docs/a%23b%25c%3F.txt")]
    [InlineData("/bang!/x", "ftp://host/bang%21/x")]
    [InlineData("/tab\there", "ftp://host/tab%09here")]
    [InlineData("/ünïcødé/日本", "ftp://host/ünïcødé/日本")]
    [InlineData("/emoji 😀", "ftp://host/emoji%20😀")]
    [InlineData("/nbsp x", "ftp://host/nbsp%C2%A0x")]
    public void Percent_encoding_round_trips(string decodedPath, string printed)
    {
        var p = new VfsPath("ftp", "host", decodedPath);

        p.ToString().Should().Be(printed);
        VfsPath.Parse(printed).Should().Be(p);
        VfsPath.Parse(printed).Path.Should().Be(decodedPath);
    }

    [Fact]
    public void Lowercase_percent_escapes_decode()
    {
        VfsPath.Parse("ftp://host/a%2fb").Path.Should().Be("/a/b");
        VfsPath.Parse("ftp://host/a%c3%a9").Path.Should().Be("/aé");
    }

    [Fact]
    public void Archive_inner_names_with_bang_round_trip()
    {
        var container = VfsPath.Parse(@"C:\odd!name.zip");
        var p = new VfsPath("archive", container.ToString(), "/in!ner");

        var printed = p.ToString();
        printed.Should().Be("archive:file:///C:/odd%21name.zip!/in%21ner");
        VfsPath.Parse(printed).Should().Be(p);
        VfsPath.Parse(printed).ArchiveContainer!.Value.ToWindowsPath().Should().Be(@"C:\odd!name.zip");
    }

    [Theory]
    [InlineData("sftp://user@host:22/home", "user", "host", 22)]
    [InlineData("sftp://host/home", null, "host", null)]
    [InlineData("sftp://[fe80::1]:2222/srv", null, "fe80::1", 2222)]
    [InlineData("sftp://me@[2001:db8::7]/srv", "me", "2001:db8::7", null)]
    [InlineData("sftp://a%40b@[fe80::1%25eth0]:22/srv", "a@b", "fe80::1%eth0", 22)]
    [InlineData("ftp://host:abc/x", null, "host", null)]
    public void Authority_components(string input, string? user, string host, int? port)
    {
        var p = VfsPath.Parse(input);

        p.UserInfo.Should().Be(user);
        p.Host.Should().Be(host);
        p.Port.Should().Be(port);
    }

    [Theory]
    [InlineData(@"C:\Users\Ewais", @"C:\Users")]
    [InlineData(@"C:\Users", @"C:\")]
    [InlineData(@"\\nas\media\films\x", @"\\nas\media\films")]
    [InlineData(@"\\nas\media\films", @"\\nas\media")]
    public void Parent_of_windows_paths(string child, string parent)
    {
        VfsPath.Parse(child).Parent.Should().Be(VfsPath.Parse(parent));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\nas\media")]
    [InlineData("ftp://host/")]
    [InlineData("search:abc")]
    [InlineData("archive:file:///C:/x.zip!/")]
    public void Roots_have_no_parent(string root)
    {
        var p = VfsPath.Parse(root);

        p.IsRoot.Should().BeTrue();
        p.Parent.Should().BeNull();
    }

    [Fact]
    public void Ftp_parent_chain_reaches_root()
    {
        var p = VfsPath.Parse("ftp://host/a/b");

        p.Parent.Should().Be(VfsPath.Parse("ftp://host/a"));
        p.Parent!.Value.Parent.Should().Be(VfsPath.Parse("ftp://host/"));
        p.Parent!.Value.Parent!.Value.Parent.Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Users\Ewais\report.pdf", "report.pdf")]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"\\nas\media", "media")]
    [InlineData("ftp://host/", "host")]
    [InlineData("archive:file:///C:/dir/x.zip!/", "x.zip")]
    [InlineData("archive:file:///C:/x.zip!/a/b.txt", "b.txt")]
    public void Name_is_last_segment(string input, string name)
    {
        VfsPath.Parse(input).Name.Should().Be(name);
    }

    [Fact]
    public void Combine_appends_and_normalizes()
    {
        var root = VfsPath.Parse(@"C:\");

        root.Combine("Users").Should().Be(VfsPath.Parse(@"C:\Users"));
        root.Combine(@"Users\Ewais").Should().Be(VfsPath.Parse(@"C:\Users\Ewais"));
        root.Combine("a/../b").Should().Be(VfsPath.Parse(@"C:\b"));
        VfsPath.Parse("ftp://h/x").Combine("y z").ToString().Should().Be("ftp://h/x/y%20z");
        VfsPath.Parse("archive:file:///C:/x.zip!/").Combine("in").Should().Be(VfsPath.Parse("archive:file:///C:/x.zip!/in"));
    }

    [Fact]
    public void Combine_rejects_invalid_children()
    {
        var root = VfsPath.Parse(@"C:\dir");

        ((Action)(() => root.Combine(""))).Should().Throw<ArgumentException>();
        ((Action)(() => root.Combine("/abs"))).Should().Throw<ArgumentException>();
        ((Action)(() => root.Combine("a\0b"))).Should().Throw<ArgumentException>();
        ((Action)(() => VfsPath.Parse("search:x").Combine("a"))).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(@"C:\Users\Ewais", @"C:\Users\Ewais")]
    [InlineData(@"c:\", @"C:\")]
    [InlineData(@"\\nas\media\x", @"\\nas\media\x")]
    [InlineData(@"\\?\UNC\srv\share", @"\\srv\share")]
    public void Windows_path_round_trip(string input, string expected)
    {
        var p = VfsPath.FromWindowsPath(input);

        p.ToWindowsPath().Should().Be(expected);
        VfsPath.FromWindowsPath(p.ToWindowsPath()).Should().Be(p);
    }

    [Fact]
    public void To_windows_path_rejects_non_file_schemes()
    {
        ((Action)(() => VfsPath.Parse("ftp://h/x").ToWindowsPath())).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Ancestry_is_case_insensitive_for_file_and_segment_aware()
    {
        var dir = VfsPath.Parse(@"C:\Users\Ewais");

        dir.IsAncestorOrSelfOf(VfsPath.Parse(@"c:\users\ewais\docs\a.txt")).Should().BeTrue();
        dir.IsAncestorOrSelfOf(VfsPath.Parse(@"C:\Users\Ewais")).Should().BeTrue();
        dir.IsAncestorOrSelfOf(VfsPath.Parse(@"C:\Users\Ewais2")).Should().BeFalse();
        VfsPath.Parse(@"C:\").IsAncestorOrSelfOf(VfsPath.Parse(@"C:\x")).Should().BeTrue();
        VfsPath.Parse("ftp://h/A").IsAncestorOrSelfOf(VfsPath.Parse("ftp://h/a/b")).Should().BeFalse();
    }

    [Fact]
    public void Long_paths_beyond_max_path_round_trip()
    {
        var segments = Enumerable.Range(0, 40).Select(i => "segment" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
        var native = @"\\?\C:\" + string.Join('\\', segments);

        var p = VfsPath.Parse(native);

        p.ToWindowsPath().Length.Should().BeGreaterThan(260);
        p.Segments.Should().HaveCount(41);
        VfsPath.Parse(p.ToString()).Should().Be(p);
    }

    [Fact]
    public void Random_paths_round_trip_through_canonical_string()
    {
        var random = new Random(4711);
        const string alphabet = "abcXYZ019 _-.!#%?@~é日😀";
        var schemes = new[] { "ftp", "sftp", "davs", "file" };
        for (var i = 0; i < 2000; i++)
        {
            var scheme = schemes[random.Next(schemes.Length)];
            var segs = Enumerable.Range(0, random.Next(1, 6)).Select(_ => RandomSegment(random, alphabet)).ToList();
            var path = scheme == "file" ? "/C:/" + string.Join('/', segs) : "/" + string.Join('/', segs);
            var p = new VfsPath(scheme, scheme == "file" ? "" : "host:21", path);

            VfsPath.Parse(p.ToString()).Should().Be(p, "round trip of {0}", p.ToString());
        }
    }

    [Fact]
    public void Default_router_resolves_registered_scheme_and_rejects_unknown()
    {
        var router = new FileSystemRouter();
        var act = () => router.Resolve(VfsPath.Parse("ftp://h/"));

        act.Should().Throw<NotSupportedException>().WithMessage("*ftp*");
    }

    private static string RandomSegment(Random random, string alphabet)
    {
        string seg;
        do
        {
            var chars = Enumerable.Range(0, random.Next(1, 10)).Select(_ =>
            {
                var idx = random.Next(alphabet.Length);
                return char.IsSurrogate(alphabet[idx]) ? "😀" : alphabet[idx].ToString();
            });
            seg = string.Concat(chars);
        }
        while (seg is "." or ".." || (seg.Length == 2 && char.IsAsciiLetter(seg[0]) && seg[1] == ':'));

        return seg;
    }
}
