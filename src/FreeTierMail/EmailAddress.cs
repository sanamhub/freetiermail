using System;
using System.Net.Mail;

namespace FreeTierMail;

/// <summary>An email address with an optional display name.</summary>
/// <remarks><see cref="ToString"/> never prints the address, so an address cannot reach a log by accident.</remarks>
public sealed record EmailAddress
{
    /// <summary>The longest address accepted, from RFC 5321's path limit.</summary>
    public const int MaxAddressLength = 254;

    /// <summary>The longest display name accepted.</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>Creates an address.</summary>
    /// <param name="address">A bare address such as <c>rider@example.com</c>, without a display name or angle brackets.</param>
    /// <param name="displayName">An optional name shown by mail clients.</param>
    /// <exception cref="ArgumentException">The address is not a single bare address, is longer than
    /// <see cref="MaxAddressLength"/>, or either value contains a line break.</exception>
    public EmailAddress(string address, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (address.Length > MaxAddressLength
            || HasLineBreak(address)
            || !MailAddress.TryCreate(address, out var parsed)
            || !string.Equals(parsed.Address, address, StringComparison.Ordinal))
        {
            throw new ArgumentException("Not a single bare email address.", nameof(address));
        }

        if (displayName is not null && (displayName.Length > MaxDisplayNameLength || HasLineBreak(displayName)))
        {
            throw new ArgumentException($"A display name is at most {MaxDisplayNameLength} characters on one line.", nameof(displayName));
        }

        Address = address;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
    }

    /// <summary>The bare address.</summary>
    public string Address { get; }

    /// <summary>The display name, or null.</summary>
    public string? DisplayName { get; }

    /// <summary>Returns <c>EmailAddress(***)</c>. Never the address.</summary>
    /// <returns>A redacted description.</returns>
    public override string ToString() => "EmailAddress(***)";

    private static bool HasLineBreak(string value) => value.AsSpan().IndexOfAny('\r', '\n') >= 0;
}
