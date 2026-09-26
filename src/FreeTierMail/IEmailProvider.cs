using System.Threading;
using System.Threading.Tasks;

namespace FreeTierMail;

/// <summary>One email provider account, such as a Brevo or Resend API key.</summary>
public interface IEmailProvider
{
    /// <summary>A unique name for this provider in the mailer, used in results, logs and quota keys.</summary>
    string Name { get; }

    /// <summary>True to try this provider first for <see cref="EmailPriority.Critical"/> messages.</summary>
    bool PreferForCritical { get; }

    /// <summary>
    /// Sends one message. Maps every answer, including network failures, to a
    /// <see cref="ProviderResult"/>; throws only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="message">The message, already validated by the mailer.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>What the provider did.</returns>
    /// <exception cref="System.OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ProviderResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
