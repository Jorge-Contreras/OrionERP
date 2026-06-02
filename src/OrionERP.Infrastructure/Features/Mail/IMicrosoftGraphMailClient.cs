namespace OrionERP.Infrastructure.Features.Mail;

public interface IMicrosoftGraphMailClient<TOptions>
  where TOptions : MicrosoftGraphMailOptions
{
  Task SendEmailAsync(
    string email,
    string subject,
    string message,
    CancellationToken ct = default);
}
