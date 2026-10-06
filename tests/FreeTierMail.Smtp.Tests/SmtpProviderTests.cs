using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Xunit;

namespace FreeTierMail.Smtp.Tests;

public sealed class SmtpProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SmtpOptions Options(int port = 587) => new() { Host = "smtp.example.org", Port = port, Username = "apikey", ApiKey = "test-password-0000", Daily = 100 };

    private static EmailMessage Message() =>
        new(new EmailAddress("links@example.org", "Links"), [new EmailAddress("rider@example.com")], "Sign in")
        {
            TextBody = "Text",
            HtmlBody = "<p>Html</p>",
            ReplyTo = new EmailAddress("help@example.org"),
        };

    [Fact]
    public async Task A_send_connects_with_starttls_logs_in_and_returns_the_message_id()
    {
        var session = new FakeSession();
        var provider = new SmtpProvider(Options(), () => session);

        var result = await provider.SendAsync(Message(), Ct);

        Assert.Equal(ProviderOutcome.Accepted, result.Outcome);
        Assert.Equal(SecureSocketOptions.StartTls, session.Security);
        Assert.Equal(("apikey", "test-password-0000"), session.Login);
        Assert.Equal(session.Sent!.MessageId, result.ProviderMessageId);
        Assert.Equal("help@example.org", session.Sent.ReplyTo.Mailboxes.Single().Address);
        Assert.True(session.Disconnected);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task A_loopback_test_inbox_needs_no_login_and_tls_only_when_offered(string host)
    {
        var session = new FakeSession();

        var result = await new SmtpProvider(new SmtpOptions { Host = host, Port = 1025 }, () => session).SendAsync(Message(), Ct);

        Assert.Equal(ProviderOutcome.Accepted, result.Outcome);
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, session.Security);
        Assert.Equal(default, session.Login);
    }

    [Fact]
    public void A_loopback_login_without_a_password_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new SmtpProvider(new SmtpOptions { Host = "localhost", Port = 1025, Username = "apikey" }));
    }

    [Theory]
    [InlineData("smtp.example.org", "apikey", "")]
    [InlineData("smtp.example.org", "", "")]
    [InlineData("localhost.example.org", "", "")]
    public void A_remote_relay_needs_a_login_and_a_password(string host, string username, string password)
    {
        Assert.Throws<ArgumentException>(() => new SmtpProvider(new SmtpOptions { Host = host, Port = 587, Username = username, ApiKey = password }));
    }

    [Fact]
    public async Task Port_465_uses_tls_on_connect()
    {
        var session = new FakeSession();

        await new SmtpProvider(Options(465), () => session).SendAsync(Message(), Ct);

        Assert.Equal(SecureSocketOptions.SslOnConnect, session.Security);
    }

    [Fact]
    public async Task A_failed_connection_sent_nothing_and_is_unavailable()
    {
        var provider = new SmtpProvider(Options(), () => new FakeSession { OnConnect = new SocketException((int)SocketError.ConnectionRefused) });

        Assert.Equal(ProviderOutcome.Unavailable, (await provider.SendAsync(Message(), Ct)).Outcome);
    }

    [Fact]
    public async Task A_refused_login_is_a_provider_fault()
    {
        var provider = new SmtpProvider(Options(), () => new FakeSession { OnAuthenticate = new AuthenticationException("535 bad credentials for apikey") });

        var result = await provider.SendAsync(Message(), Ct);

        Assert.Equal(ProviderOutcome.ProviderFault, result.Outcome);
        Assert.DoesNotContain("apikey", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_lost_while_sending_is_unknown()
    {
        var provider = new SmtpProvider(Options(), () => new FakeSession { OnSend = new IOException("connection reset") });

        Assert.Equal(ProviderOutcome.Unknown, (await provider.SendAsync(Message(), Ct)).Outcome);
    }

    [Theory]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, ProviderOutcome.RecipientRejected)]
    [InlineData(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.ExceededStorageAllocation, ProviderOutcome.RecipientRejected)]
    [InlineData(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxNameNotAllowed, ProviderOutcome.ProviderFault)]
    [InlineData(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, ProviderOutcome.Throttled)]
    [InlineData(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.InsufficientStorage, ProviderOutcome.Throttled)]
    [InlineData(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationRequired, ProviderOutcome.ProviderFault)]
    public async Task Smtp_replies_map_by_class_and_command(SmtpErrorCode error, SmtpStatusCode status, ProviderOutcome expected)
    {
        var provider = new SmtpProvider(Options(), () => new FakeSession { OnSend = new SmtpCommandException(error, status, "rider@example.com: text from the server") });

        var result = await provider.SendAsync(Message(), Ct);

        Assert.Equal(expected, result.Outcome);
        Assert.DoesNotContain("rider@example.com", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_throws()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SmtpProvider(Options(), () => new FakeSession()).SendAsync(Message(), cancelled.Token));
    }

    [Theory]
    [InlineData("", "apikey", 587)]
    [InlineData("smtp.example.org", "", 587)]
    [InlineData("smtp.example.org", "apikey", 0)]
    public void Missing_settings_are_refused(string host, string username, int port)
    {
        Assert.Throws<ArgumentException>(() => new SmtpProvider(new SmtpOptions { Host = host, Username = username, Port = port, ApiKey = "p" }));
    }

    [Fact]
    public void The_message_has_both_parts_and_all_addresses()
    {
        using var mime = SmtpProvider.Build(Message());

        Assert.Equal("Links", mime.From.Mailboxes.Single().Name);
        Assert.Equal("Text", mime.TextBody);
        Assert.Equal("<p>Html</p>", mime.HtmlBody);
    }

    private sealed class FakeSession : ISmtpSession
    {
        public Exception? OnConnect { get; init; }

        public Exception? OnAuthenticate { get; init; }

        public Exception? OnSend { get; init; }

        public SecureSocketOptions Security { get; private set; }

        public (string, string) Login { get; private set; }

        public MimeMessage? Sent { get; private set; }

        public bool Disconnected { get; private set; }

        public Task ConnectAsync(string host, int port, SecureSocketOptions security, CancellationToken cancellationToken)
        {
            Security = security;
            return OnConnect is null ? Task.CompletedTask : Task.FromException(OnConnect);
        }

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken)
        {
            Login = (userName, password);
            return OnAuthenticate is null ? Task.CompletedTask : Task.FromException(OnAuthenticate);
        }

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            Sent = message;
            return OnSend is null ? Task.CompletedTask : Task.FromException(OnSend);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Disconnected = true;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
