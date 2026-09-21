namespace CORA.Data.Email;

/// <summary>
/// A fully loaded email message including body content.
/// </summary>
public class EmailMessage
{
    public uint Uid { get; set; }
    public string FolderName { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; }

    public string? HtmlBody { get; set; }
    public string? TextBody { get; set; }

    /// <summary>RFC 5322 <c>Message-Id</c> of this message. Empty if it was not captured.</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// RFC 5322 <c>References</c> chain for this message, space-separated, oldest ancestor
    /// first. Combined with <see cref="MessageId"/> this is what a reply must echo back for
    /// the recipient's mail client to thread the conversation correctly.
    /// </summary>
    public string References { get; set; } = string.Empty;

    public List<AttachmentInfo> Attachments { get; set; } = new();

    public bool HasAttachments => Attachments.Count > 0;

    public string DisplayBody =>
        !string.IsNullOrWhiteSpace(TextBody)
            ? TextBody!
            : HtmlBody ?? string.Empty;

    public bool IsHtml => string.IsNullOrWhiteSpace(TextBody) && !string.IsNullOrWhiteSpace(HtmlBody);
}

/// <summary>
/// Data used to send a new message via SMTP.
/// </summary>
public class OutgoingMessage
{
    public string To { get; set; } = string.Empty;
    public string Bcc { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public List<OutgoingAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// When set, this account is used as the sender instead of
    /// <see cref="IEmailService.CurrentAccount"/>.
    /// </summary>
    public MailAccount? SenderAccount { get; set; }

    /// <summary>
    /// RFC 5322 <c>In-Reply-To</c>: the <c>Message-Id</c> of the message being replied to.
    /// Left empty for a brand-new message or a forward, which are not replies.
    /// </summary>
    public string InReplyTo { get; set; } = string.Empty;

    /// <summary>
    /// RFC 5322 <c>References</c> to emit, space-separated. Per RFC 5322 section 3.6.4 a reply
    /// carries the parent's own References chain with the parent's Message-Id appended. Without
    /// this the recipient's mail client cannot thread the reply, so it surfaces as a new
    /// conversation on their side.
    /// </summary>
    public string References { get; set; } = string.Empty;
}
