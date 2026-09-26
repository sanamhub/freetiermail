using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeTierMail.Brevo;
using FreeTierMail.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FreeTierMail.Tests.Webhooks;

/// <summary>PLAN phase 2: a suppressed address gets nothing from any provider.</summary>
public sealed class SuppressionTests
{
    private static readonly EmailAddress From = new("links@example.org");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EmailMessage Message() =>
        new(From, [new EmailAddress("rider@example.com")], "Road closed") { TextBody = "The road is closed." };

    [Fact]
    public async Task A_suppressed_recipient_stops_the_send_before_any_provider()
    {
        var store = new InMemorySuppressionStore();
        await store.AddAsync(new Suppression("Rider@Example.com", SuppressionReason.HardBounce, "resend", DateTimeOffset.UnixEpoch), Ct);
        var first = new FakeEmailProvider("first");
        var second = new FakeEmailProvider("second");
        var mailer = new FreeTierMailer([first, second], new FreeTierMailerOptions { SuppressionStore = store });

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(SendStatus.Failed, result.Status);
        Assert.True(result.Suppressed);
        Assert.Empty(result.Attempts);
        Assert.Empty(first.Sent);
        Assert.Empty(second.Sent);
    }

    [Fact]
    public async Task A_suppressed_copy_recipient_stops_it_too_and_removal_lets_mail_through()
    {
        var store = new InMemorySuppressionStore();
        await store.AddAsync(new Suppression("guide@example.com", SuppressionReason.Complaint, "mailgun", DateTimeOffset.UnixEpoch), Ct);
        var provider = new FakeEmailProvider();
        var mailer = new FreeTierMailer([provider], new FreeTierMailerOptions { SuppressionStore = store });
        var message = new EmailMessage(From, [new EmailAddress("rider@example.com")], "Road closed") { TextBody = "The road is closed.", Bcc = [new EmailAddress("guide@example.com")] };

        Assert.True((await mailer.SendAsync(message, Ct)).Suppressed);
        Assert.True(await store.RemoveAsync("guide@example.com", Ct));
        Assert.Equal(SendStatus.Sent, (await mailer.SendAsync(message, Ct)).Status);
    }

    [Fact]
    public async Task Without_a_store_every_address_is_tried()
    {
        var mailer = new FreeTierMailer([new FakeEmailProvider()]);

        var result = await mailer.SendAsync(Message(), Ct);

        Assert.Equal(SendStatus.Sent, result.Status);
        Assert.False(result.Suppressed);
    }

    [Fact]
    public async Task Webhooks_added_through_di_feed_the_store_the_mailer_reads()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEmailProvider>(new FakeEmailProvider());
        services.AddFreeTierMail().AddWebhook(new BrevoWebhook("test-brevo-token-0000"));
        await using var provider = services.BuildServiceProvider();

        var receiver = provider.GetRequiredService<WebhookReceiver>();
        var request = new WebhookRequest(
            [new("Authorization", "Bearer test-brevo-token-0000")],
            """{"event":"hard_bounce","email":"rider@example.com"}"""u8.ToArray());
        Assert.Equal(WebhookResult.Accepted, await receiver.ReceiveAsync("brevo", request, Ct));

        var result = await provider.GetRequiredService<FreeTierMailer>().SendAsync(Message(), Ct);
        Assert.True(result.Suppressed);
        Assert.Equal(["brevo"], receiver.Names);
    }
}
