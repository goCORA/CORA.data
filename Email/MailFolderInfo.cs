namespace CORA.Data.Email;

/// <summary>
/// Describes a mailbox folder on the server.
/// </summary>
public class MailFolderInfo
{
    public string FullName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Unread { get; set; }
    public int Total { get; set; }

    public string Display => Unread > 0 ? $"{Name} ({Unread})" : Name;
}
