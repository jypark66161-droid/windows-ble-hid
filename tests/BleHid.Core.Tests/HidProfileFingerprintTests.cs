using Xunit;

namespace BleHid.Core.Tests;

public sealed class HidProfileFingerprintTests
{
    [Fact]
    public void Fingerprint_is_stable_for_same_HID_layout()
    {
        var first = HidProfileFingerprint.Calculate();
        var second = HidProfileFingerprint.Calculate();
        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Persisted_baseline_detects_changed_profile_without_overwriting_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "blehid-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "profile.sha256");
        try
        {
            Assert.Contains("baseline recorded", HidProfileFingerprint.DescribePersistedState(path));
            var original = File.ReadAllText(path);
            Assert.Contains("unchanged", HidProfileFingerprint.DescribePersistedState(path));
            File.WriteAllText(path, "DIFFERENT");
            Assert.Contains("layout changed", HidProfileFingerprint.DescribePersistedState(path));
            Assert.Equal("DIFFERENT", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
