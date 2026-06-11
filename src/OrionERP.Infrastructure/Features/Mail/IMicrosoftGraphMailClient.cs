namespace OrionERP.Infrastructure.Features.Mail;

public interface IMicrosoftGraphMailClient<TOptions>
  where TOptions : MicrosoftGraphMailOptions
{
  Task SendEmailAsync(
    MicrosoftGraphMailMessage mail,
    CancellationToken ct = default);

  Task SendEmailAsync(
    string email,
    string subject,
    string message,
    CancellationToken ct = default);
}

public sealed class MicrosoftGraphMailMessage
{
  public IReadOnlyList<string> ToRecipients { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> CcRecipients { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> BccRecipients { get; set; } = Array.Empty<string>();
  public string Subject { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
}
