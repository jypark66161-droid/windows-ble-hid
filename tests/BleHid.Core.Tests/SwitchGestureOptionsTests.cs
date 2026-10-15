using BleHid.Core;
using Xunit;

namespace BleHid.Core.Tests;

public sealed class SwitchGestureOptionsTests
{
    [Fact]
    public void A_missing_file_leaves_the_gesture_disabled_without_complaining()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blehid-gesture-{Guid.NewGuid():N}.json");

        var options = SwitchGestureOptions.Load(path);

        Assert.False(options.Enabled);
        Assert.Null(options.Warning);
    }

    [Fact]
    public void An_unreadable_file_disables_the_gesture_and_explains_why()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blehid-gesture-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var options = SwitchGestureOptions.Load(path);

            Assert.False(options.Enabled);
            Assert.NotNull(options.Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_gesture_stays_off_unless_the_file_turns_it_on()
    {
        var options = SwitchGestureOptions.Parse("""{"minStrokePx":120}""");

        Assert.False(options.Enabled);
        Assert.Equal(120, options.MinStrokePx);
    }

    [Fact]
    public void Unset_values_fall_back_to_the_defaults()
    {
        var options = SwitchGestureOptions.Parse("""{"enabled":true}""");

        Assert.True(options.Enabled);
        Assert.Equal(80, options.MinStrokePx);
        Assert.Equal(3, options.RequiredReversals);
        Assert.Equal(250, options.WindowMs);
        Assert.Null(options.Warning);
    }

    [Fact]
    public void Configured_values_are_kept()
    {
        var options = SwitchGestureOptions.Parse(
            """{"enabled":true,"minStrokePx":150,"requiredReversals":5,"windowMs":1200}""");

        Assert.Equal(150, options.MinStrokePx);
        Assert.Equal(5, options.RequiredReversals);
        Assert.Equal(1200, options.WindowMs);
        Assert.Null(options.Warning);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(5000, 500)]
    public void An_out_of_range_stroke_length_is_clamped_and_reported(int configured, int expected)
    {
        var options = SwitchGestureOptions.Parse($$"""{"enabled":true,"minStrokePx":{{configured}}}""");

        Assert.Equal(expected, options.MinStrokePx);
        Assert.Contains("minStrokePx", options.Warning);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(99, 10)]
    public void An_out_of_range_reversal_count_is_clamped_and_reported(int configured, int expected)
    {
        var options = SwitchGestureOptions.Parse(
            $$"""{"enabled":true,"requiredReversals":{{configured}}}""");

        Assert.Equal(expected, options.RequiredReversals);
        Assert.Contains("requiredReversals", options.Warning);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(60000, 5000)]
    public void An_out_of_range_window_is_clamped_and_reported(int configured, int expected)
    {
        var options = SwitchGestureOptions.Parse($$"""{"enabled":true,"windowMs":{{configured}}}""");

        Assert.Equal(expected, options.WindowMs);
        Assert.Contains("windowMs", options.Warning);
    }

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blehid-gesture-{Guid.NewGuid():N}.json");
        var options = SwitchGestureOptions.ForTest(true, 150, 5, 1200);
        try
        {
            Assert.True(options.TrySave(path, out var error));
            Assert.Null(error);

            var reloaded = SwitchGestureOptions.Load(path);
            Assert.True(reloaded.Enabled);
            Assert.Equal(150, reloaded.MinStrokePx);
            Assert.Equal(5, reloaded.RequiredReversals);
            Assert.Equal(1200, reloaded.WindowMs);
            Assert.Null(reloaded.Warning);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Toggling_keeps_the_tuning_values(bool enabled)
    {
        var options = SwitchGestureOptions.ForTest(!enabled, 150, 5, 1200);

        var toggled = options.WithEnabled(enabled);

        Assert.Equal(enabled, toggled.Enabled);
        Assert.Equal(150, toggled.MinStrokePx);
        Assert.Equal(5, toggled.RequiredReversals);
        Assert.Equal(1200, toggled.WindowMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_toggle_written_to_disk_reaches_a_running_watcher(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"blehid-gesture-{Guid.NewGuid():N}.json");
        Assert.True(SwitchGestureOptions.ForTest(!enabled, 80, 3, 800).TrySave(path, out _));
        try
        {
            using var watcher = new SwitchGestureWatcher(path);
            Assert.Equal(!enabled, watcher.Current.Enabled);

            var seen = new TaskCompletionSource<SwitchGestureOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += options => seen.TrySetResult(options);

            Assert.True(SwitchGestureOptions.ForTest(enabled, 80, 3, 800).TrySave(path, out _));

            var finished = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(ReferenceEquals(finished, seen.Task), "the watcher never reported the change");
            Assert.Equal(enabled, (await seen.Task).Enabled);
            Assert.Equal(enabled, watcher.Current.Enabled);
        }
        finally
        {
            DeleteWithRetry(path);
        }
    }

    [Fact]
    public void Reopening_a_watcher_loads_changes_made_while_it_was_disposed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"blehid-gesture-{Guid.NewGuid():N}.json");
        try
        {
            Assert.True(SwitchGestureOptions.ForTest(false, 80, 3, 800).TrySave(path, out _));
            using (var watcher = new SwitchGestureWatcher(path))
                Assert.False(watcher.Current.Enabled);

            Assert.True(SwitchGestureOptions.ForTest(true, 150, 5, 1200).TrySave(path, out _));
            using var reopened = new SwitchGestureWatcher(path);
            Assert.True(reopened.Current.Enabled);
            Assert.Equal(150, reopened.Current.MinStrokePx);
            Assert.Equal(5, reopened.Current.RequiredReversals);
            Assert.Equal(1200, reopened.Current.WindowMs);
        }
        finally
        {
            File.Delete(path);
        }
    }
    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { File.Delete(path); return; }
            catch (IOException) when (attempt < 19) { Thread.Sleep(50); }
        }
    }

}
