using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Logging;
using OrionERP.Infrastructure.Features.Mail;

namespace OrionERP.Web.Identity;

public sealed class MicrosoftGraphEmailSender : IEmailSender
{
  private readonly IMicrosoftGraphMailClient<GraphMailOptions> _mailClient;
  private readonly ILogger<MicrosoftGraphEmailSender> _logger;

  public MicrosoftGraphEmailSender(
    IMicrosoftGraphMailClient<GraphMailOptions> mailClient,
    ILogger<MicrosoftGraphEmailSender> logger)
  {
    _mailClient = mailClient;
    _logger = logger;
  }

  public async Task SendEmailAsync(string email, string subject, string htmlMessage)
  {
    await _mailClient.SendEmailAsync(email, subject, htmlMessage);
    _logger.LogInformation("Password reset email queued for {Recipient}.", email);
  }
}
