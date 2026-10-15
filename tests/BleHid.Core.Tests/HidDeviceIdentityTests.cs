using Xunit;

namespace BleHid.Core.Tests;

public sealed class HidDeviceIdentityTests
{
    [Fact]
    public void Pnp_id_is_seven_bytes_little_endian()
    {
        var identity = new HidDeviceIdentity(2, 0x1234, 0xabcd, 0x0123);
        Assert.Equal(new byte[] { 2, 0x34, 0x12, 0xcd, 0xab, 0x23, 0x01 },
            identity.ToPnpIdBytes());
    }

    [Theory]
    [InlineData("1:1234:abcd:0100")]
    [InlineData("2:0x1234:0x0001:0x0201")]
    public void Accepts_explicit_registered_id_format(string value) =>
        Assert.True(HidDeviceIdentity.TryParse(value, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1:0000:0001:0100")]
    [InlineData("1:ffff:0001:0100")]
    [InlineData("0:1234:0001:0100")]
    [InlineData("3:1234:0001:0100")]
    [InlineData("1:1234:0001:01ff")]
    [InlineData("1:abcd:0001")]
    public void Rejects_missing_or_reserved_identity(string? value) =>
        Assert.False(HidDeviceIdentity.TryParse(value, out _));
}
