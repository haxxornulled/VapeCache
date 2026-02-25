using System.Text;
using VapeCache.Console.GroceryStore;

namespace VapeCache.Tests.Console;

public sealed class SessionBinaryCodecTests
{
    [Fact]
    public void RoundTrip_preserves_all_fields()
    {
        var session = new UserSession(
            "user-1",
            "session-1",
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow,
            ["prod-001", "prod-007"],
            "cart-123");

        var payload = SessionBinaryCodec.Serialize(session);
        var ok = SessionBinaryCodec.TryDeserialize(payload, out var decoded);

        Assert.True(ok);
        Assert.Equal(session.UserId, decoded.UserId);
        Assert.Equal(session.SessionId, decoded.SessionId);
        Assert.Equal(session.CreatedAt, decoded.CreatedAt);
        Assert.Equal(session.LastActivityAt, decoded.LastActivityAt);
        Assert.Equal(session.RecentlyViewedProductIds, decoded.RecentlyViewedProductIds);
        Assert.Equal(session.ActiveCartId, decoded.ActiveCartId);
    }

    [Fact]
    public void TryDeserialize_returns_false_for_non_binary_payload()
    {
        var json = Encoding.UTF8.GetBytes("{\"userId\":\"u1\"}");

        var ok = SessionBinaryCodec.TryDeserialize(json, out _);

        Assert.False(ok);
    }
}
