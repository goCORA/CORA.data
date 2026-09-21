namespace CORA.Data.Email;

/// <summary>
/// Metadata for an attachment on an email message. Content is not loaded
/// eagerly; use <see cref="IEmailService.DownloadAttachmentAsync"/> with the
/// <see cref="FolderName"/>, <see cref="Uid"/> and <see cref="PartSpecifier"/>
/// to stream the bytes on demand.
/// </summary>
public class AttachmentInfo
{
    public string FolderName { get; set; } = string.Empty;
    public uint Uid { get; set; }

    /// <summary>IMAP body part specifier used to fetch just this part.</summary>
    public string PartSpecifier { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }

    /// <summary>
    /// Local cache file path for POP3 attachments already synced to disk. Empty for
    /// IMAP, where attachments are streamed live via <see cref="PartSpecifier"/>.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    public string DisplaySize => Size switch
    {
        >= 1024 * 1024 => $"{Size / (1024d * 1024d):0.0} MB",
        >= 1024 => $"{Size / 1024d:0.0} KB",
        _ => $"{Size} B",
    };

    public string Display => $"{FileName} ({DisplaySize})";
}

/// <summary>
/// A file to attach to an outgoing message. The file is streamed from
/// <see cref="FilePath"/> at send time rather than buffered in memory.
/// </summary>
public class OutgoingAttachment
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public string FilePath { get; set; } = string.Empty;
}
