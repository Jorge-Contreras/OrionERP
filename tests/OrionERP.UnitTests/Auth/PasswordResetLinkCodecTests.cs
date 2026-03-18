using OrionERP.Web.Identity;

namespace OrionERP.UnitTests.Auth;

public class PasswordResetLinkCodecTests
{
    [Fact]
    public void EncodeAndDecode_RoundTripsUserIdAndCode()
    {
        var payload = PasswordResetLinkCodec.Encode("user-123", "token-value-with-specials+/=");

        var decoded = PasswordResetLinkCodec.TryDecode(payload, out var userId, out var code);

        Assert.True(decoded);
        Assert.Equal("user-123", userId);
        Assert.Equal("token-value-with-specials+/=", code);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_ForInvalidPayload()
    {
        var decoded = PasswordResetLinkCodec.TryDecode("not-a-valid-payload", out var userId, out var code);

        Assert.False(decoded);
        Assert.Equal(string.Empty, userId);
        Assert.Equal(string.Empty, code);
    }
}
