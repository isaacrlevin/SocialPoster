using System.Security.Cryptography;
using SocialPoster.Core.Scheduling;

namespace SocialPoster.Tests;

[Trait("Category", "Scheduling")]
public class SchedulingTimeAndMediaTests
{
    private static readonly DateTimeOffset BeforeDst = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("UTC", 1, 15, 12, 12)]
    [InlineData("America/Los_Angeles", 1, 15, 12, 20)]
    [InlineData("America/Los_Angeles", 7, 15, 12, 19)]
    [InlineData("Asia/Kolkata", 1, 15, 12, 6)]
    public void Resolve_UtcAndBrowserIanaZones_ConvertsToUtc(string zone, int month, int day, int hour, int utcHour)
    {
        var actual = ScheduleTime.Resolve(new DateTime(2030, month, day, hour, 0, 0, DateTimeKind.Local), zone, null, BeforeDst);
        Assert.Equal(new DateTimeOffset(2030, month, day, utcHour, zone == "Asia/Kolkata" ? 30 : 0, 0, TimeSpan.Zero), actual);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
    }

    [Fact]
    public void Resolve_SpringGap_RejectsNonexistentLocalTime() =>
        Assert.Throws<ArgumentException>(() => ScheduleTime.Resolve(new(2030, 3, 10, 2, 30, 0), "America/Los_Angeles", null, BeforeDst));

    [Theory]
    [InlineData(null)]
    [InlineData(-6)]
    public void Resolve_AutumnFoldWithoutValidChoice_Rejects(int? hours) =>
        Assert.Throws<ArgumentException>(() => ScheduleTime.Resolve(new(2030, 11, 3, 1, 30, 0),
            "America/Los_Angeles", hours.HasValue ? TimeSpan.FromHours(hours.Value) : null, BeforeDst));

    [Theory]
    [InlineData(-7, 8)]
    [InlineData(-8, 9)]
    public void Resolve_AutumnFoldExplicitOffsets_SelectsDistinctInstant(int hours, int utcHour) =>
        Assert.Equal(new DateTimeOffset(2030, 11, 3, utcHour, 30, 0, TimeSpan.Zero),
            ScheduleTime.Resolve(new(2030, 11, 3, 1, 30, 0), "America/Los_Angeles", TimeSpan.FromHours(hours), BeforeDst));

    [Theory]
    [InlineData(-7, false)]
    [InlineData(-8, true)]
    public void Resolve_NormalTimeExplicitOffset_MustMatchZone(int offsetHours, bool valid)
    {
        var local = new DateTime(2030, 1, 15, 12, 0, 0);
        if (valid)
            Assert.Equal(new DateTimeOffset(2030, 1, 15, 20, 0, 0, TimeSpan.Zero),
                ScheduleTime.Resolve(local, "America/Los_Angeles", TimeSpan.FromHours(offsetHours), BeforeDst));
        else
            Assert.Throws<ArgumentException>(() => ScheduleTime.Resolve(local, "America/Los_Angeles", TimeSpan.FromHours(offsetHours), BeforeDst));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Resolve_NowOrPast_Rejects(int minutes) =>
        Assert.Throws<ArgumentException>(() => ScheduleTime.Resolve(BeforeDst.AddMinutes(minutes).UtcDateTime, "UTC", null, BeforeDst));

    [Fact]
    public void Resolve_UnknownZone_Rejects() =>
        Assert.Throws<TimeZoneNotFoundException>(() => ScheduleTime.Resolve(new(2030, 2, 1), "Not/AZone", null, BeforeDst));

    [Fact]
    public async Task Media_SaveAndRestart_SourceDeletedStillResolvesBytesAndAltText()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var upload = await host.UploadAsync();
        var saved = Assert.Single(await host.Media.SaveAsync([upload]));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })), saved.Sha256);
        File.Delete(upload.Path!);
        await host.RestartAsync();
        var resolved = Assert.Single(await host.Media.ResolveAsync([saved]));
        Assert.Equal(upload.AltText, resolved.AltText);
        Assert.Equal(upload.FileName, resolved.FileName);
        Assert.Equal(upload.Kind, resolved.Kind);
        Assert.Equal(upload.ContentType, resolved.ContentType);
        Assert.Equal(upload.Size, resolved.Size);
        Assert.NotEqual(upload.Path, resolved.Path);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(resolved.Path!));
        await host.Media.DeleteAsync([saved]);
        Assert.False(File.Exists(resolved.Path));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("missing")]
    public async Task Media_ChangedDurableFile_RejectsResolution(string change)
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var saved = await host.Media.SaveAsync([await host.UploadAsync()]);
        var resolved = Assert.Single(await host.Media.ResolveAsync(saved));
        if (change == "missing") File.Delete(resolved.Path!);
        else await File.WriteAllBytesAsync(resolved.Path!, change == "hash" ? [4, 3, 2, 1] : [1]);
        await Assert.ThrowsAsync<IOException>(() => host.Media.ResolveAsync(saved));
    }

    [Fact]
    public async Task Media_SizeMismatch_CleansPartialAndEarlierCopies()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var first = await host.UploadAsync();
        var second = await host.UploadAsync();
        await File.WriteAllBytesAsync(second.Path!, [1]);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Media.SaveAsync([first, second]));
        Assert.Empty(Directory.GetFiles(Path.Combine(host.DirectoryPath, "scheduled-media")));
        Assert.True(File.Exists(first.Path));
    }

    [Fact]
    public async Task Media_InvalidKey_CannotResolveOutsideDurableDirectory()
    {
        await using var host = await SchedulingTestHost.CreateAsync();
        var saved = Assert.Single(await host.Media.SaveAsync([await host.UploadAsync()]));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Media.ResolveAsync([saved with { Key = "../outside" }]));
    }
}
