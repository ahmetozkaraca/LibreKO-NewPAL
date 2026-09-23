using LibreKO.Domain;
using Xunit;

namespace LibreKO.Tests;

public class PendingReplyTests
{
    [Fact]
    public void ATimeoutReleasesARequestThatNeverGotAReply()
    {
        var reply = new PendingReply();
        int token = reply.Begin();

        Assert.True(reply.Expire(token));
        Assert.False(reply.Waiting);
    }

    [Fact]
    public void ATimeoutAfterTheReplyDoesNothing()
    {
        var reply = new PendingReply();
        int token = reply.Begin();
        reply.Settle();

        Assert.False(reply.Expire(token));
    }

    [Fact]
    public void AnOlderTimeoutCannotReleaseANewerRequest()
    {
        var reply = new PendingReply();
        int first = reply.Begin();
        reply.Settle();
        reply.Begin();

        Assert.False(reply.Expire(first));
        Assert.True(reply.Waiting);
    }
}
