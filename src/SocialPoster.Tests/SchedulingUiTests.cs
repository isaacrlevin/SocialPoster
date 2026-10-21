using System.Reflection;
using Microsoft.AspNetCore.Components;
using SocialPoster.Components.Shared;

namespace SocialPoster.Tests;

[Trait("Category", "Scheduling")]
public class SchedulingUiTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("2099-01-15T12:30", 0, 0)]
    [InlineData("2099-01-15T12:30:00", 0, 0)]
    [InlineData("2099-01-15T12:30:45", 45, 0)]
    [InlineData("2099-01-15T12:30:45.123", 45, 123)]
    public void Valid_browser_input_enables_scheduling_and_preserves_local_time(string input, int seconds, int milliseconds)
    {
        var panel = ChangeLocalTime(input);

        Assert.Null(GetField(panel, "timeValidationMessage"));
        Assert.True((bool)typeof(SchedulingPanel).GetProperty("CanSubmitTime", PrivateInstance)!.GetValue(panel)!);
        object?[] arguments = [null, null];
        Assert.True((bool)typeof(SchedulingPanel).GetMethod("TryGetRequestTime", PrivateInstance)!.Invoke(panel, arguments)!);
        var local = Assert.IsType<DateTime>(arguments[0]);
        Assert.Equal(new DateTime(2099, 1, 15, 12, 30, seconds).AddMilliseconds(milliseconds), local);
        Assert.Equal(DateTimeKind.Unspecified, local.Kind);
        Assert.Null(arguments[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("2099-02-30T12:30:00")]
    [InlineData("2099-01-15T25:30:00")]
    [InlineData("2099-01-15T12:30:60")]
    [InlineData("01/15/2099 12:30 PM")]
    public void Invalid_input_still_shows_validation_error(string input)
    {
        var panel = ChangeLocalTime(input);

        Assert.Equal("Enter a valid local date and time.", GetField(panel, "timeValidationMessage"));
        Assert.False((bool)typeof(SchedulingPanel).GetProperty("CanSubmitTime", PrivateInstance)!.GetValue(panel)!);
    }

    [Theory]
    [InlineData("2000-01-15T12:30:00", "UTC")]
    [InlineData("2099-01-15T12:30:00", "Not/AZone")]
    [InlineData("2099-03-08T02:30:00", "America/Los_Angeles")]
    [InlineData("2099-11-01T01:30:00", "America/Los_Angeles")]
    public void Valid_format_does_not_bypass_schedule_time_validation(string input, string zone)
    {
        var panel = ChangeLocalTime(input, zone);

        var message = Assert.IsType<string>(GetField(panel, "timeValidationMessage"));
        Assert.NotEqual("Enter a valid local date and time.", message);
        Assert.False((bool)typeof(SchedulingPanel).GetProperty("CanSubmitTime", PrivateInstance)!.GetValue(panel)!);
    }

    private static SchedulingPanel ChangeLocalTime(string input, string zone = "UTC")
    {
        var panel = new SchedulingPanel();
        typeof(SchedulingPanel).GetField("timeZoneId", PrivateInstance)!.SetValue(panel, zone);
        typeof(SchedulingPanel).GetMethod("OnLocalTimeChanged", PrivateInstance)!
            .Invoke(panel, [new ChangeEventArgs { Value = input }]);
        return panel;
    }

    private static object? GetField(SchedulingPanel panel, string name) =>
        typeof(SchedulingPanel).GetField(name, PrivateInstance)!.GetValue(panel);
}
