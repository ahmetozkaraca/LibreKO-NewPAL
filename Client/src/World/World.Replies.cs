using System;
using LibreKO.Domain;

namespace LibreKO;

public partial class World
{
    private const string NoReplyText = PendingReply.NoReplyText;

    private void AwaitReply(PendingReply reply, Action onTimeout)
    {
        int token = reply.Begin();
        GetTree().CreateTimer(PendingReply.TimeoutSeconds).Timeout += () =>
        {
            if (reply.Expire(token)) onTimeout();
        };
    }
}
