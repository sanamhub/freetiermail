using System.Threading.Tasks;
using FreeTierMail.Testing;
using Xunit;

namespace FreeTierMail.Tests;

public sealed class FakeEmailProviderTests
{
    [Fact]
    public async Task A_scripted_fake_drives_the_mailer()
    {
        var first = new FakeEmailProvider("first").Then(ProviderOutcome.Unavailable);
        var second = new FakeEmailProvider("second");
        var mailer = new FreeTierMailer([first, second], new FreeTierMailerOptions { Strategy = RoutingStrategy.Ordered });
        var message = new EmailMessage(new EmailAddress("links@example.org"), [new EmailAddress("rider@example.com")], "Hi") { TextBody = "x" };

        var result = await mailer.SendAsync(message, TestContext.Current.CancellationToken);

        Assert.Equal("second", result.Provider);
        Assert.Single(first.Sent);
        Assert.Same(message, Assert.Single(second.Sent));
    }
}
