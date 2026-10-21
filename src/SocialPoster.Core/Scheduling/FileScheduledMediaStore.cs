using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SocialPoster.Core.Scheduling;

/// <summary>Copies composer uploads into immutable durable files under DataDirectory/scheduled-media.</summary>
public sealed class FileScheduledMediaStore(IOptions<SocialPosterOptions> options) : IScheduledMediaStore
{
    private string DirectoryPath => Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "scheduled-media");

    public async Task<IReadOnlyList<ScheduledMedia>> SaveAsync(IReadOnlyList<MediaAttachment> media, CancellationToken ct = default)
    {
        var result = new List<ScheduledMedia>();
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            foreach (var attachment in media)
            {
                if (attachment.Path is null) throw new ArgumentException("Scheduled media must have a file on disk.");
                var key = Guid.NewGuid().ToString("N");
                var path = FilePath(key);
                // Track the copy before opening it, so incomplete files are removed on cancellation or I/O failure.
                result.Add(new(key, attachment.FileName, attachment.Kind, attachment.ContentType, attachment.Size, attachment.AltText, ""));
                await using (var input = File.OpenRead(attachment.Path))
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    if (input.Length != attachment.Size || input.Length > MediaRules.MaxFileSize)
                        throw new ArgumentException("Attachment size changed or exceeds the supported maximum.");
                    await input.CopyToAsync(output, ct);
                    await output.FlushAsync(ct);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var hash = await HashAsync(path, ct);
                result[^1] = result[^1] with { Sha256 = hash };
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException)
        {
            await DeleteAsync(result, CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<MediaAttachment>> ResolveAsync(IReadOnlyList<ScheduledMedia> media, CancellationToken ct = default)
    {
        var result = new List<MediaAttachment>();
        foreach (var item in media)
        {
            var path = FilePath(item.Key);
            if (!File.Exists(path) || new FileInfo(path).Length != item.Size || await HashAsync(path, ct) != item.Sha256)
                throw new IOException("Durable attachment is missing or changed; delivery was not attempted.");
            result.Add(new() { FileName = item.FileName, Kind = item.Kind, ContentType = item.ContentType,
                Size = item.Size, Path = path, AltText = item.AltText });
        }
        return result;
    }

    public Task DeleteAsync(IReadOnlyList<ScheduledMedia> media, CancellationToken ct = default)
    {
        foreach (var item in media)
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(FilePath(item.Key));
        }
        return Task.CompletedTask;
    }

    private string FilePath(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Invalid durable media key.");
        return Path.Combine(DirectoryPath, key);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
