using System;
using Xunit;

namespace FreeTierMail.Tests;

public sealed class ModelTests
{
    private static readonly EmailAddress From = new("links@example.org", "Example");
    private static readonly EmailAddress Rider = new("rider@example.com");

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("Rider <rider@example.com>")]
    [InlineData("rider@example.com\r\nBcc: other@example.com")]
    [InlineData("a@b.c, d@e.f")]
    public void Only_a_single_bare_address_is_accepted(string address)
    {
        Assert.Throws<ArgumentException>(() => new EmailAddress(address));
    }

    [Fact]
    public void An_address_over_254_characters_is_refused()
    {
        var address = new string('a', 64) + "@" + new string('b', 186) + ".com";

        Assert.Throws<ArgumentException>(() => new EmailAddress(address));
    }

    [Fact]
    public void A_display_name_with_a_line_break_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new EmailAddress("rider@example.com", "Rider\nBcc: x@example.com"));
    }

    [Fact]
    public void Addresses_and_messages_print_nothing_personal()
    {
        var message = new EmailMessage(From, [Rider], "Your sign-in link") { TextBody = "secret link" };

        Assert.Equal("EmailAddress(***)", Rider.ToString());
        Assert.DoesNotContain("rider", message.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sign-in", message.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", message.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_subject_with_a_line_break_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(From, [Rider], "Hi\r\nBcc: x@example.com"));
    }

    [Fact]
    public void A_message_needs_a_recipient()
    {
        Assert.Throws<ArgumentException>(() => new EmailMessage(From, [], "Hi"));
    }

    [Fact]
    public void A_message_needs_a_body()
    {
        var message = new EmailMessage(From, [Rider], "Hi");

        Assert.Throws<ArgumentException>(message.Validate);
    }

    [Fact]
    public void A_message_has_at_most_50_recipients()
    {
        var many = new EmailAddress[51];
        Array.Fill(many, Rider);
        var message = new EmailMessage(From, many, "Hi") { TextBody = "x" };

        Assert.Throws<ArgumentException>(message.Validate);
    }

    [Fact]
    public void An_empty_idempotency_key_is_refused()
    {
        var message = new EmailMessage(From, [Rider], "Hi") { TextBody = "x", IdempotencyKey = "" };

        Assert.Throws<ArgumentException>(message.Validate);
    }
}
