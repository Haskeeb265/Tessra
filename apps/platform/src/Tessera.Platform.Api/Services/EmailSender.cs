namespace Tessera.Platform.Api.Services;

/// <summary>
/// Sends transactional email (verification, password reset, invitations).
/// The concrete implementation is pluggable: today only the
/// <see cref="ConsoleEmailSender"/> is registered, which logs the message
/// (with the action links) instead of delivering it. Swap in a real
/// provider (Resend, Postmark, SendGrid, SES…) by adding an implementation
/// and registering it in Program.cs — no other code changes needed.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Development/default sender: writes the email to the log so flows
/// (verification links, reset links, invites) are testable without an
/// SMTP/API provider. Production should replace this with a real sender.
/// </summary>
public class ConsoleEmailSender(
    ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(
        string to,
        string subject,
        string htmlBody,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "📧 [email-sender] To: {To} | Subject: {Subject}\n{Body}",
            to,
            subject,
            htmlBody);

        return Task.CompletedTask;
    }
}
