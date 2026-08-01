using Microsoft.AspNetCore.Identity;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Mail;

namespace OrionERP.Bruno.Web.Services;

public sealed class BrunoEmailSender : IEmailSender<BrunoMemberUser>
{
  private readonly IMicrosoftGraphMailClient<BrunoGraphMailOptions> _mailClient;

  public BrunoEmailSender(
    IMicrosoftGraphMailClient<BrunoGraphMailOptions> mailClient)
  {
    _mailClient = mailClient;
  }

  public Task SendConfirmationLinkAsync(BrunoMemberUser user, string email, string confirmationLink) =>
    SendAsync(
      email,
      "Confirma tu cuenta de Club Bruno",
      BuildMessage("Confirma tu correo", "Para terminar tu registro, confirma tu correo electrónico.", confirmationLink, "Confirmar correo"));

  public Task SendPasswordResetLinkAsync(BrunoMemberUser user, string email, string resetLink) =>
    SendAsync(
      email,
      "Restablece tu contraseña de Club Bruno",
      BuildMessage("Restablece tu contraseña", "Recibimos una solicitud para cambiar tu contraseña.", resetLink, "Crear nueva contraseña"));

  public Task SendPasswordResetCodeAsync(BrunoMemberUser user, string email, string resetCode) =>
    SendAsync(email, "Código de recuperación de Club Bruno", $"Tu código es: {resetCode}");

  private Task SendAsync(string email, string subject, string message)
    => _mailClient.SendEmailAsync(email, subject, message);

  private static string BuildMessage(string title, string text, string url, string action) =>
    $"""
    <div style="font-family:Arial,sans-serif;max-width:560px;margin:auto;color:#25211d">
      <h1 style="color:#7d2118">{title}</h1>
      <p>{text}</p>
      <p><a href="{System.Net.WebUtility.HtmlEncode(url)}" style="display:inline-block;background:#8e291e;color:#fff;padding:12px 18px;border-radius:8px;text-decoration:none">{action}</a></p>
      <p style="font-size:12px;color:#6d6862">Si no solicitaste esta acción, puedes ignorar este mensaje.</p>
    </div>
    """;
}
