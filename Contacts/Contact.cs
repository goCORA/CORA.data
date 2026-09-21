using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiteDB;

namespace CORA.Data.Contacts;

public class Contact : INotifyPropertyChanged
{
    [BsonId]
    public int Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Multiple emails stored semicolon-delimited in the single Email column.
    /// </summary>
    public List<string> Emails
    {
        get => Email
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        set => Email = string.Join(";", value.Select(e => e.Trim()).Where(e => e.Length > 0));
    }
    public string Phone { get; set; } = string.Empty;
    public string PhotoPath { get; set; } = string.Empty;
    public bool   IsGoCORA  { get; set; }
    public string Tags      { get; set; } = string.Empty;

    public string FullName => $"{FirstName} {LastName}".Trim();

    public string Initials
    {
        get
        {
            var f = string.IsNullOrWhiteSpace(FirstName) ? "" : FirstName[0].ToString().ToUpperInvariant();
            var l = string.IsNullOrWhiteSpace(LastName)  ? "" : LastName[0].ToString().ToUpperInvariant();
            return f + l;
        }
    }

    private bool _isSelected;

    /// <summary>True when the row's checkbox is checked in the contacts multi-select UI. Not persisted.</summary>
    [BsonIgnore]
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

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
