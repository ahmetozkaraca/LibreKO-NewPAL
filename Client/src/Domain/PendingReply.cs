namespace LibreKO.Domain;

public sealed class PendingReply
{
    public const double TimeoutSeconds = 10.0;
    public const string NoReplyText = "The server did not answer. Please try again.";

    private int _token;

    public bool Waiting { get; private set; }

    public int Begin()
    {
        Waiting = true;
        return ++_token;
    }

    public void Settle()
    {
        Waiting = false;
        _token++;
    }

    public bool Expire(int token)
    {
        if (!Waiting || token != _token)
            return false;

        Waiting = false;
        return true;
    }
}
