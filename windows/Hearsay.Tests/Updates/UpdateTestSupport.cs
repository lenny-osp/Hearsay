using System.IO.Compression;
using System.Net;
using System.Text;
using Hearsay.Core.Updates;

namespace Hearsay.Tests.Updates;

/// <summary>
/// Answers requests by URL through a <see cref="HttpMessageHandler"/>, the
/// counterpart of <c>ChatStubURLProtocol</c> in
/// mac/HearsayCore/Tests/HearsayCoreTests/NotesPipelineTests.swift as the
/// update tests use it: a status and body per URL, a thrown failure, or a
/// response that never comes. Records every request. Never touches the network.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> stalls = new(StringComparer.Ordinal);
    private readonly List<HttpRequestMessage> requests = [];

    public void Register(string url, HttpStatusCode status, byte[] body)
    {
        lock (gate)
        {
            routes[url] = () => new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        }
    }

    public void Register(string url, HttpStatusCode status, string body) => Register(url, status, Encoding.UTF8.GetBytes(body));

    public void RegisterFailure(string url, Exception error)
    {
        lock (gate)
        {
            failures[url] = error;
        }
    }

    /// <summary>The request waits until it is cancelled (a timeout).</summary>
    public void RegisterStall(string url)
    {
        lock (gate)
        {
            stalls.Add(url);
        }
    }

    public IReadOnlyList<HttpRequestMessage> Requests(string url)
    {
        lock (gate)
        {
            return [.. requests.Where(r => r.RequestUri?.AbsoluteUri == url)];
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? "";
        Func<HttpResponseMessage>? route;
        Exception? failure;
        bool stall;
        lock (gate)
        {
            requests.Add(request);
            routes.TryGetValue(url, out route);
            failures.TryGetValue(url, out failure);
            stall = stalls.Contains(url);
        }
        if (stall)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        if (failure is not null)
        {
            throw failure;
        }
        return route is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) }
            : route();
    }
}

/// <summary>
/// Fake Hearsay.exe files for the install tests: the "exe" is a text file
/// <c>&lt;ProductName&gt;|&lt;version&gt;|&lt;signature&gt;</c> that
/// <see cref="FakeIdentityReader"/> reads, so every step runs against scratch
/// folders without a real PE. <see cref="FileAppIdentityReader"/> itself is
/// tested against real signed and unsigned files.
/// </summary>
internal static class FakeApp
{
    public static string Content(string version, string product = "Hearsay", string signature = "unsigned") =>
        $"{product}|{version}|{signature}";

    /// <summary>A folder holding Hearsay.exe (with <see cref="Content"/>) and a marker file.</summary>
    public static string MakeInstall(string folder, string version, string marker, string signature = "unsigned")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, UpdateInstall.ExeName), Content(version, signature: signature));
        File.WriteAllText(Path.Combine(folder, "Marker.txt"), marker);
        return folder;
    }

    public static string Marker(string folder) => File.ReadAllText(Path.Combine(folder, "Marker.txt"));

    /// <summary>
    /// A release zip: <c>&lt;top&gt;/Hearsay.exe</c>, <c>&lt;top&gt;/Marker.txt</c>
    /// and a nested file, or at the zip root when <paramref name="top"/> is null.
    /// </summary>
    public static byte[] Zip(string version, string marker, string? top = "Hearsay", string signature = "unsigned", string product = "Hearsay")
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var prefix = top is null ? "" : top + "/";
            Add(archive, prefix + UpdateInstall.ExeName, Content(version, product, signature));
            Add(archive, prefix + "Marker.txt", marker);
            Add(archive, prefix + "runtimes/win-x64/native.txt", "native");
        }
        return memory.ToArray();
    }

    public static void Add(ZipArchive archive, string name, string text)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    public static string Sums(string name, byte[] data) =>
        $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data))}  {name}\n";
}

/// <summary>Reads <see cref="FakeApp"/> executables. Signatures: <c>unsigned</c>, <c>invalid</c>, or <c>&lt;status&gt;:&lt;subject&gt;:&lt;thumbprint&gt;</c> with status trusted or untrusted.</summary>
internal sealed class FakeIdentityReader : IAppIdentityReader
{
    public AppIdentity Read(string exePath)
    {
        var parts = File.ReadAllText(exePath).Split('|');
        if (parts.Length != 3)
        {
            return new AppIdentity(null, null, AuthenticodeInfo.NotSigned);
        }
        return new AppIdentity(parts[0], parts[1], Signature(parts[2]));
    }

    public static AuthenticodeInfo Signature(string text)
    {
        if (text == "unsigned")
        {
            return AuthenticodeInfo.NotSigned;
        }
        if (text == "invalid")
        {
            return new AuthenticodeInfo(SignatureStatus.Invalid, "CN=Someone", "00", "0x80096010 The digital signature of the object did not verify.");
        }
        var fields = text.Split(':');
        var status = fields[0] == "trusted" ? SignatureStatus.Trusted : SignatureStatus.UntrustedRoot;
        return new AuthenticodeInfo(status, fields[1], fields[2], status == SignatureStatus.Trusted ? "valid" : "0x800B0109 untrusted root");
    }
}

/// <summary>A folder under the temp directory, deleted on dispose; the name has a space and an apostrophe on purpose.</summary>
internal sealed class ScratchFolder : IDisposable
{
    public string Root { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"Hearsay update tests {Guid.NewGuid():N}", "it's here");

    public ScratchFolder() => Directory.CreateDirectory(Root);

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public void Dispose()
    {
        var top = System.IO.Path.GetDirectoryName(Root);
        if (top is null)
        {
            return;
        }
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(top))
                {
                    foreach (var item in new DirectoryInfo(top).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                    {
                        item.Attributes = item is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
                    }
                    Directory.Delete(top, recursive: true);
                }
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
