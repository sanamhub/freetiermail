using System;
using System.Collections.Generic;

namespace FreeTierMail;

/// <summary>What one provider did with one message.</summary>
public enum ProviderOutcome
{
    /// <summary>The provider took the message.</summary>
    Accepted = 0,

    /// <summary>The address or content was refused by rule. Another provider would refuse it too, so the mailer stops.</summary>
    RecipientRejected = 1,

    /// <summary>Rate limited. The mailer skips the provider until <see cref="ProviderResult.RetryAfter"/> and tries the next.</summary>
    Throttled = 2,

    /// <summary>A daily or monthly quota is used up. The mailer marks it exhausted until it resets and tries the next.</summary>
    QuotaExhausted = 3,

    /// <summary>The provider refuses this account: key revoked, domain not verified, account suspended. The mailer disables it and tries the next.</summary>
    ProviderFault = 4,

    /// <summary>The provider could not be reached or answered with a server error. The mailer tries the next.</summary>
    Unavailable = 5,

    /// <summary>The request was sent and no answer came. The message may have been sent, so the mailer stops unless told otherwise.</summary>
    Unknown = 6,
}

/// <summary>A provider's answer for one message.</summary>
/// <param name="Outcome">What happened.</param>
public sealed record ProviderResult(ProviderOutcome Outcome)
{
    /// <summary>The provider's id for the message, when it gave one.</summary>
    public string? ProviderMessageId { get; init; }

    /// <summary>When the provider asked to wait before the next send, if it said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>For <see cref="ProviderOutcome.QuotaExhausted"/>: which quota, when the provider said. Null marks the one that resets soonest.</summary>
    public QuotaPeriod? ExhaustedPeriod { get; init; }

    /// <summary>FreeTierMail's own short description. Never the provider's response body, which can echo addresses.</summary>
    public string? Reason { get; init; }

    /// <summary>The provider took the message.</summary>
    /// <param name="providerMessageId">The provider's id, if any.</param>
    /// <returns>An <see cref="ProviderOutcome.Accepted"/> result.</returns>
    public static ProviderResult Accepted(string? providerMessageId = null) => new(ProviderOutcome.Accepted) { ProviderMessageId = providerMessageId };

    /// <summary>A result with a reason.</summary>
    /// <param name="outcome">What happened.</param>
    /// <param name="reason">FreeTierMail's own description.</param>
    /// <param name="retryAfter">A wait the provider asked for.</param>
    /// <returns>The result.</returns>
    public static ProviderResult Of(ProviderOutcome outcome, string reason, TimeSpan? retryAfter = null) =>
        new(outcome) { Reason = reason, RetryAfter = retryAfter };
}

/// <summary>What happened to a message across every provider tried.</summary>
public enum SendStatus
{
    /// <summary>A provider accepted it.</summary>
    Sent = 0,

    /// <summary>No provider accepted it, and none may have sent it.</summary>
    Failed = 1,

    /// <summary>A provider may have sent it. Resending risks a duplicate.</summary>
    Unknown = 2,
}

/// <summary>One provider tried for one message.</summary>
/// <param name="Provider">The provider's name.</param>
/// <param name="Outcome">What it did.</param>
/// <param name="Duration">How long the attempt took. Zero when the mailer skipped the provider without a request.</param>
/// <param name="Reason">FreeTierMail's own description.</param>
public sealed record SendAttempt(string Provider, ProviderOutcome Outcome, TimeSpan Duration, string? Reason);

/// <summary>The result of sending one message.</summary>
/// <param name="Status">Sent, failed, or unknown.</param>
/// <param name="Attempts">Each provider tried, in order.</param>
public sealed record SendResult(SendStatus Status, IReadOnlyList<SendAttempt> Attempts)
{
    /// <summary>The provider that accepted the message, or the one whose answer was lost.</summary>
    public string? Provider { get; init; }

    /// <summary>The accepting provider's id for the message.</summary>
    public string? ProviderMessageId { get; init; }

    /// <summary>True when this result was stored for the same idempotency key and nothing was sent now.</summary>
    public bool IsReplay { get; init; }

    /// <summary>True when a recipient is on the suppression list, so no provider was tried.</summary>
    public bool Suppressed { get; init; }
}
