namespace CORA.Data.Email;

/// <summary>
/// An email kept on this device can't be opened: the mail server no longer has it, and its body
/// was never stored here. The page says so instead of showing an error.
/// </summary>
public sealed class MessageNoLongerOnServerException : InvalidOperationException
{
    public MessageNoLongerOnServerException()
        : base("This email is no longer on the mail server, and its body was never stored on this device.")
    {
    }
}
