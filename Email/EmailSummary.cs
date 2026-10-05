using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CORA.Data.Email;

/// <summary>
/// Lightweight header information for a message in a folder listing.
/// </summary>
public class EmailSummary : INotifyPropertyChanged
{
    private bool _isRead;
    private bool _isSelected;

    /// <summary>IMAP unique id within its folder.</summary>
    public uint Uid { get; set; }
    public string FolderName { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }

    /// <summary>
    /// <see cref="Date"/> in the device's time zone, for display. <see cref="Date"/> keeps the
    /// sender's offset, so formatting it directly showed the sender's clock time (e.g. 6:14 PM
    /// for mail sent at 2:14 PM Eastern with a UTC Date header).
    /// </summary>
    public DateTime LocalDate => Date.LocalDateTime;

    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value)
                return;
            _isRead = value;
            OnPropertyChanged();
        }
    }

    public bool HasAttachments { get; set; }

    /// <summary>
    /// True when the message is flagged as important. Currently defaults to false; intended
    /// to be wired up to a real IMAP flag/header signal in a future change.
    /// </summary>
    public bool IsImportant { get; set; }

    private string _tagName = string.Empty;
    private string _tagColor = "#F0C419";

    /// <summary>Name of the user-defined tag applied to this message, or empty if none.</summary>
    public string TagName
    {
        get => _tagName;
        set
        {
            if (_tagName == value)
                return;
            _tagName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Hex color for the applied tag's chip, used together with <see cref="TagName"/>.</summary>
    public string TagColor
    {
        get => _tagColor;
        set
        {
            if (_tagColor == value)
                return;
            _tagColor = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Id of the applied tag (empty if none), used to update local state after tagging.</summary>
    public string TagId { get; set; } = string.Empty;

    /// <summary>Display bucket used to group messages in the mailbox list (e.g. "Today", "Yesterday").</summary>
    public string DayGroup { get; set; } = string.Empty;

    /// <summary>True when the row's checkbox is checked in the mailbox multi-select UI.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Preview => string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject;

    private string _avatarUrl = string.Empty;

    /// <summary>
    /// Optional URL or local path to an avatar image for the sender. When empty the UI
    /// should fall back to the initials avatar.
    /// </summary>
    public string AvatarUrl
    {
        get => _avatarUrl;
        set
        {
            if (_avatarUrl == value)
                return;
            _avatarUrl = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
