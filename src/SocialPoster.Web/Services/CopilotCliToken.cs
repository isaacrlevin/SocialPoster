using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace SocialPoster.Web.Services;

/// <summary>
/// Finds the GitHub token that the Copilot CLI saved when you ran <c>copilot login</c>.
/// The SDK doesn't read it for us, so without this the agent fails with
/// "No GitHub OAuth token or Copilot HMAC key provided" unless a token is set by hand.
/// </summary>
public static class CopilotCliToken
{
    /// <summary>Returns a token from the environment or the Copilot CLI's saved credentials, or null.</summary>
    public static string? Find(ILogger? logger = null)
    {
        foreach (var name in (string[])["GH_TOKEN", "GITHUB_TOKEN"])
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            foreach (var (host, login) in LoggedInUsers())
            {
                // The CLI stores one generic credential per account, e.g. "https://github.com:octocat.copilot-cli".
                if (TryReadCredential($"{host}:{login}.copilot-cli", out var token))
                {
                    return token;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Reading the Copilot CLI credential failed");
        }

        return null;
    }

    private static IEnumerable<(string Host, string Login)> LoggedInUsers()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "config.json");
        if (!File.Exists(path))
        {
            yield break;
        }

        JsonDocument doc;
        try
        {
            // The file starts with // comments.
            doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var user in Users(root))
            {
                var host = Text(user, "host") ?? "https://github.com";
                var login = Text(user, "login");
                if (login is { Length: > 0 } && seen.Add($"{host}:{login}"))
                {
                    yield return (host.TrimEnd('/'), login);
                }
            }
        }

        static IEnumerable<JsonElement> Users(JsonElement root)
        {
            if (root.TryGetProperty("lastLoggedInUser", out var last) && last.ValueKind == JsonValueKind.Object)
            {
                yield return last;
            }

            if (root.TryGetProperty("loggedInUsers", out var all) && all.ValueKind == JsonValueKind.Array)
            {
                foreach (var user in all.EnumerateArray())
                {
                    yield return user;
                }
            }
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool TryReadCredential(string target, out string? token)
    {
        token = null;
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            return false;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return false;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            // The CLI writes UTF-16; fall back to UTF-8 in case that changes.
            var text = (bytes.Length % 2 == 0 && bytes.Length > 1 && bytes[1] == 0
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes)).Trim('\0', ' ');

            token = text.Length > 0 ? text : null;
            return token is not null;
        }
        finally
        {
            CredFree(handle);
        }
    }

    private const uint CredTypeGeneric = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
