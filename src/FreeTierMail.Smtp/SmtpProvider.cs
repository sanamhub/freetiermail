using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace FreeTierMail.Smtp;

/// <summary>Options for <see cref="SmtpProvider"/>. <see cref="EmailProviderOptions.ApiKey"/> is the SMTP password.</summary>
public sealed class SmtpOptions : EmailProviderOptions
{
    /// <summary>The relay's host name, such as <c>smtp-relay.brevo.com</c>.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The port. 587 (STARTTLS) by default; 465 uses TLS from the first byte.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// The login name. Many relays use a fixed name, such as <c>apikey</c> or the account email.
    /// Required, except on a loopback host, where an empty name skips the login (a local test inbox
    /// such as Mailpit).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <inheritdoc/>
    protected override bool RequiresApiKey => !IsLoopback || Username.Length > 0;

    internal bool IsLoopback =>
        Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(Host, out var address) && IPAddress.IsLoopback(address));

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The host, login or password is missing, the port is out of range, or a limit is invalid.</exception>
    public override void Validate(string providerName)
    {
        base.Validate(providerName);
        if (string.IsNullOrWhiteSpace(Host) || Port is < 1 or > 65535)
        {
            throw new ArgumentException($"{providerName} needs Host and a Port from 1 to 65535.");
        }

        if (!IsLoopback && string.IsNullOrWhiteSpace(Username))
        {
            throw new ArgumentException($"{providerName} needs Username; only a loopback host may skip the login.");
        }
    }
}

/// <summary>
/// Sends through an SMTP relay with MailKit, over STARTTLS, or TLS on connect for port 465. On a
/// loopback host STARTTLS is used when the server offers it, so a local test inbox works. A
/// failure while connecting or logging in sent nothing, so the mailer may fail over; a connection
/// lost while sending may have delivered the message, so it is <see cref="ProviderOutcome.Unknown"/>.
/// </summary>
/// <remarks>
/// SMTP replies map by class (RFC 5321 section 4.2.1) and by the command MailKit says was refused:
/// 4yz is <see cref="ProviderOutcome.Throttled"/>, since relays use it for rate and quota limits
/// alike; a refused recipient or message is <see cref="ProviderOutcome.RecipientRejected"/>; a
/// refused sender or login is <see cref="ProviderOutcome.ProviderFault"/>. Only the reply code is
/// kept, never the server's text, which can quote the address.
/// </remarks>
public sealed class SmtpProvider : IEmailProvider
{
    private readonly SmtpOptions _options;
    private readonly SecureSocketOptions _security;
    private readonly Func<ISmtpSession> _sessions;

    /// <summary>Creates the provider.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public SmtpProvider(SmtpOptions options)
        : this(options, static () => new MailKitSmtpSession())
    {
    }

    internal SmtpProvider(SmtpOptions options, Func<ISmtpSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessions);
        options.Validate("smtp");
        _options = options;
        _sessions = sessions;
        _security = options.Port == 465 ? SecureSocketOptions.SslOnConnect
            : options.IsLoopback ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.StartTls;
        Name = string.IsNullOrWhiteSpace(options.Name) ? "smtp" : options.Name;
        Quota = options.ToQuotaPlan();
        PreferForCritical = options.PreferForCritical;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public QuotaPlan Quota { get; }

    /// <inheritdoc/>
    public bool PreferForCritical { get; }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public async Task<ProviderResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        using var mime = Build(message);
        using var session = _sessions();

        if (await ConnectAsync(session, cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return refused;
        }

        try
        {
            await session.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SmtpCommandException ex)
        {
            return Map(ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or SmtpProtocolException or TimeoutException or OperationCanceledException)
        {
            // MAIL FROM went out; MailKit does not say whether DATA was answered.
            return ProviderResult.Of(ProviderOutcome.Unknown, "The SMTP connection failed while sending; the message may have been sent.");
        }

        await DisconnectQuietlyAsync(session, cancellationToken).ConfigureAwait(false);
        return ProviderResult.Accepted(mime.MessageId);
    }

    internal static MimeMessage Build(EmailMessage message)
    {
        var mime = new MimeMessage { Subject = message.Subject };
        mime.From.Add(Mailbox(message.From));
        foreach (var to in message.To)
        {
            mime.To.Add(Mailbox(to));
        }

        foreach (var cc in message.Cc)
        {
            mime.Cc.Add(Mailbox(cc));
        }

        foreach (var bcc in message.Bcc)
        {
            mime.Bcc.Add(Mailbox(bcc));
        }

        if (message.ReplyTo is { } replyTo)
        {
            mime.ReplyTo.Add(Mailbox(replyTo));
        }

        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }

    private static MailboxAddress Mailbox(EmailAddress address) => new(address.DisplayName ?? string.Empty, address.Address);

    private async Task<ProviderResult?> ConnectAsync(ISmtpSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.ConnectAsync(_options.Host, _options.Port, _security, cancellationToken).ConfigureAwait(false);
            if (_options.Username.Length > 0)
            {
                await session.AuthenticateAsync(_options.Username, _options.ApiKey, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is AuthenticationException or SslHandshakeException or NotSupportedException)
        {
            return ProviderResult.Of(ProviderOutcome.ProviderFault, "The SMTP server refused the login or TLS setup.");
        }
        catch (SmtpCommandException ex)
        {
            return Map(ex);
        }
        catch (Exception ex) when (ex is IOException or SocketException or SmtpProtocolException or TimeoutException or OperationCanceledException)
        {
            // Nothing was sent on this connection.
            return ProviderResult.Of(ProviderOutcome.Unavailable, "Could not connect to the SMTP server.");
        }
    }

    internal static ProviderResult Map(SmtpCommandException ex)
    {
        var status = (int)ex.StatusCode;
        if (status is >= 400 and < 500)
        {
            return ProviderResult.Of(ProviderOutcome.Throttled, $"The SMTP server deferred the message ({status}).");
        }

        return ex.ErrorCode switch
        {
            SmtpErrorCode.RecipientNotAccepted => ProviderResult.Of(ProviderOutcome.RecipientRejected, $"The SMTP server refused the recipient ({status})."),
            SmtpErrorCode.MessageNotAccepted => ProviderResult.Of(ProviderOutcome.RecipientRejected, $"The SMTP server refused the message ({status})."),
            SmtpErrorCode.SenderNotAccepted => ProviderResult.Of(ProviderOutcome.ProviderFault, $"The SMTP server refused the sender ({status}); verify it at this relay."),
            _ when status == (int)SmtpStatusCode.AuthenticationRequired => ProviderResult.Of(ProviderOutcome.ProviderFault, "The SMTP server requires different authentication."),
            _ => ProviderResult.Of(ProviderOutcome.Unavailable, $"The SMTP server refused the command ({status})."),
        };
    }

    private static async Task DisconnectQuietlyAsync(ISmtpSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or SmtpProtocolException or SmtpCommandException or TimeoutException or OperationCanceledException)
        {
            // The server already accepted the message; a failed QUIT does not change that.
        }
    }
}

/// <summary>One SMTP connection, split into the phases the mapping tells apart. Tests replace it.</summary>
internal interface ISmtpSession : IDisposable
{
    Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken);

    Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken);

    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>A MailKit <see cref="SmtpClient"/> per send, with a 30 second timeout.</summary>
internal sealed class MailKitSmtpSession : ISmtpSession
{
    private readonly SmtpClient _client = new() { Timeout = 30_000 };

    public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken) =>
        _client.ConnectAsync(host, port, security, cancellationToken);

    public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken) =>
        _client.AuthenticateAsync(new NetworkCredential(userName, password), cancellationToken);

    public Task SendAsync(MimeMessage message, CancellationToken cancellationToken) =>
        _client.SendAsync(message, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        _client.DisconnectAsync(quit: true, cancellationToken);

    public void Dispose() => _client.Dispose();
}
