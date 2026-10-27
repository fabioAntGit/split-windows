using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using static Split.Tests.Harness;

namespace Split.Tests;

/// <summary>The built program, read without running it: which libraries it links, what it can call and which addresses it carries.</summary>
public static class SecurityAudit
{
    private static readonly string[] Assemblies = ["Split.Core.dll", "Split.Windows.dll", "Split.dll", "splitapp.dll"];

    private static readonly string[] ForbiddenAssemblies =
        ["System.Net.Http", "System.Net.Sockets", "System.Net.WebClient", "System.Net.Requests", "System.Net.WebSockets", "System.Net.WebSockets.Client",
         "System.Net.Security", "System.Net.Mail", "Microsoft.Web.WebView2.Core", "Microsoft.Web.WebView2.Wpf"];

    private static readonly string[] ForbiddenTypes =
        ["HttpClient", "WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest", "TcpClient", "TcpListener", "UdpClient", "Socket", "WebSocket", "ClientWebSocket", "WebView2", "SmtpClient"];

    private static readonly string[] NetworkLibraries = ["ws2_32", "wsock32", "winhttp", "wininet", "urlmon", "mswsock", "httpapi", "webservices"];

    /// <summary>Where web addresses may appear: names of XML formats, identifiers that are never fetched.</summary>
    private static readonly string[] AllowedHosts =
        ["schemas.openxmlformats.org", "schemas.microsoft.com", "purl.org", "www.w3.org", "www.idpf.org", "www.daisy.org", "ns.adobe.com"];

    /// <summary>Native libraries that link a network library, with the reason why they never connect.</summary>
    private static readonly Dictionary<string, string> NativeAllowed = new()
    {
        // The picture library links the networking it needs for its URL, HTTP, HTTPS and FTP readers; the security policy set before
        // its first use switches those coders off, and the hostile picture tests above show no connection.
        ["Magick.Native"] = "coders off by policy",
    };

    public static void Register()
    {
        Test("Security", "The built app uses no networking API", () =>
        {
            var folder = AppContext.BaseDirectory;
            foreach (var name in Assemblies)
            {
                var path = Path.Combine(folder, name);
                Require(File.Exists(path), $"{name} is missing from {folder}");
                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();

                foreach (var handle in reader.AssemblyReferences)
                {
                    var reference = reader.GetString(reader.GetAssemblyReference(handle).Name);
                    Check(!ForbiddenAssemblies.Contains(reference, StringComparer.OrdinalIgnoreCase), $"{name} references {reference}");
                }
                foreach (var handle in reader.TypeReferences)
                {
                    var type = reader.GetTypeReference(handle);
                    var (space, typeName) = (reader.GetString(type.Namespace), reader.GetString(type.Name));
                    Check(!space.StartsWith("System.Net.Http", StringComparison.Ordinal) && !space.StartsWith("System.Net.Sockets", StringComparison.Ordinal)
                          && !space.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal), $"{name} uses {space}.{typeName}");
                    Check(!(space.StartsWith("System.Net", StringComparison.Ordinal) && ForbiddenTypes.Contains(typeName)), $"{name} uses {space}.{typeName}");
                }
                foreach (var handle in reader.MethodDefinitions)
                {
                    var import = reader.GetMethodDefinition(handle).GetImport();
                    if (import.Module.IsNil) continue;
                    var library = Path.GetFileNameWithoutExtension(reader.GetString(reader.GetModuleReference(import.Module).Name)).ToLowerInvariant();
                    Check(!NetworkLibraries.Contains(library), $"{name} calls into {library}");
                }

                // The only web addresses inside are names of XML formats, never fetched.
                var hosts = new HashSet<string>();
                for (var user = MetadataTokens.UserStringHandle(1); !user.IsNil; user = reader.GetNextHandle(user))
                {
                    foreach (Match match in Regex.Matches(reader.GetUserString(user), @"https?://([A-Za-z0-9.-]+)"))
                        hosts.Add(match.Groups[1].Value.ToLowerInvariant());
                }
                Check(hosts.Except(AllowedHosts).Count() == 0, $"{name} has unexpected addresses: {string.Join(", ", hosts.Except(AllowedHosts))}");
            }
        });

        Test("Security", "The native libraries that ship with the app link no network library, or are known not to use it", () =>
        {
            var natives = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "runtimes"), "*.dll", SearchOption.AllDirectories)
                .Where(file => file.Contains("native", StringComparison.OrdinalIgnoreCase)).ToList();
            Require(natives.Any(f => Path.GetFileName(f) == "pdfium.dll"), "pdfium.dll should ship with the app");
            foreach (var file in natives)
            {
                var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
                var linked = Regex.Matches(text, @"[A-Za-z0-9_]{3,20}\.dll").Select(m => Path.GetFileNameWithoutExtension(m.Value).ToLowerInvariant())
                    .Where(NetworkLibraries.Contains).Distinct().ToList();
                var name = Path.GetFileName(file);
                var allowed = NativeAllowed.Keys.Any(k => name.StartsWith(k, StringComparison.OrdinalIgnoreCase));
                Check(linked.Count == 0 || allowed, $"{Path.GetRelativePath(AppContext.BaseDirectory, file)} links {string.Join(", ", linked)}");
            }
        });
    }
}
