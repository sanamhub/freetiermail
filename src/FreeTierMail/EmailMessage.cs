using System;
using System.Collections.Generic;
using System.Linq;

namespace FreeTierMail;

/// <summary>How much a message matters when quota is short.</summary>
public enum EmailPriority
{
    /// <summary>Uses quota up to the critical reserve.</summary>
    Normal = 0,

    /// <summary>May use the critical reserve, and tries providers marked for critical mail first. For sign-in links and similar.</summary>
    Critical = 1,
}

/// <summary>A transactional email.</summary>
/// <remarks><see cref="ToString"/> prints counts only, never addresses, subject or body.</remarks>
public sealed class EmailMessage
{
    /// <summary>The most recipients across To, Cc and Bcc. Several free tiers cap a message at 50.</summary>
    public const int MaxRecipients = 50;

    /// <summary>The longest subject accepted.</summary>
    public const int MaxSubjectLength = 998;

    /// <summary>The longest idempotency key accepted.</summary>
    public const int MaxIdempotencyKeyLength = 200;

    /// <summary>Creates a message.</summary>
    /// <param name="from">The sender. Its domain must be verified at every provider that may send it.</param>
    /// <param name="to">One or more recipients.</param>
    /// <param name="subject">The subject, on one line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="from"/> or <paramref name="to"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="to"/> is empty, or the subject is empty, too long or has a line break.</exception>
    public EmailMessage(EmailAddress from, IEnumerable<EmailAddress> to, string subject)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var recipients = to.ToArray();
        if (recipients.Length == 0 || Array.Exists(recipients, r => r is null))
        {
            throw new ArgumentException("A message needs at least one recipient, and none may be null.", nameof(to));
        }

        if (subject.Length > MaxSubjectLength || subject.AsSpan().IndexOfAny('\r', '\n') >= 0)
        {
            throw new ArgumentException($"A subject is at most {MaxSubjectLength} characters on one line.", nameof(subject));
        }

        From = from;
        To = recipients;
        Subject = subject;
    }

    /// <summary>The sender.</summary>
    public EmailAddress From { get; }

    /// <summary>The recipients.</summary>
    public IReadOnlyList<EmailAddress> To { get; }

    /// <summary>Copy recipients.</summary>
    public IReadOnlyList<EmailAddress> Cc { get; init; } = [];

    /// <summary>Blind copy recipients.</summary>
    public IReadOnlyList<EmailAddress> Bcc { get; init; } = [];

    /// <summary>Where replies go, when not to <see cref="From"/>.</summary>
    public EmailAddress? ReplyTo { get; init; }

    /// <summary>The subject.</summary>
    public string Subject { get; }

    /// <summary>The plain text body. A message needs this, <see cref="HtmlBody"/>, or both.</summary>
    public string? TextBody { get; init; }

    /// <summary>The HTML body.</summary>
    public string? HtmlBody { get; init; }

    /// <summary>Short labels passed to providers that support them, for their own statistics.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>How much the message matters when quota is short.</summary>
    public EmailPriority Priority { get; init; }

    /// <summary>
    /// A key that makes a repeat send return the first result instead of sending again. Passed to
    /// providers that deduplicate by key.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Returns the recipient count and body sizes. Never addresses, subject or body.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() =>
        $"EmailMessage(recipients: {RecipientCount}, text: {TextBody?.Length ?? 0} chars, html: {HtmlBody?.Length ?? 0} chars)";

    internal int RecipientCount => To.Count + Cc.Count + Bcc.Count;

    /// <summary>Checks the rules that init-only properties can break. The mailer calls it before any send.</summary>
    /// <exception cref="ArgumentException">A rule is broken; the message says which.</exception>
    internal void Validate()
    {
        if (string.IsNullOrEmpty(TextBody) && string.IsNullOrEmpty(HtmlBody))
        {
            throw new ArgumentException("A message needs a text body, an HTML body, or both.");
        }

        if (RecipientCount > MaxRecipients)
        {
            throw new ArgumentException($"A message has at most {MaxRecipients} recipients across To, Cc and Bcc.");
        }

        if (Cc.Contains(null!) || Bcc.Contains(null!) || Tags.Contains(null!))
        {
            throw new ArgumentException("Cc, Bcc and Tags may not contain null.");
        }

        if (IdempotencyKey is { } key && (key.Length is 0 or > MaxIdempotencyKeyLength))
        {
            throw new ArgumentException($"An idempotency key is 1 to {MaxIdempotencyKeyLength} characters.");
        }
    }
}
