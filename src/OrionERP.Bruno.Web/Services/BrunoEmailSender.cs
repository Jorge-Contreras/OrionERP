using System.Net;
using Microsoft.AspNetCore.Identity;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Auth;
using OrionERP.Infrastructure.Features.Mail;

namespace OrionERP.Bruno.Web.Services;

public sealed class BrunoEmailSender : IEmailSender<BrunoMemberUser>
{
  private readonly IMicrosoftGraphMailClient<BrunoGraphMailOptions> _mailClient;
  private readonly PublicWebsitePresentationDefinition _presentation;
  private readonly IPublicWebsiteInstanceContext _website;

  public BrunoEmailSender(
    IMicrosoftGraphMailClient<BrunoGraphMailOptions> mailClient,
    PublicWebsitePresentationDefinition presentation,
    IPublicWebsiteInstanceContext website)
  {
    _mailClient = mailClient;
    _presentation = presentation;
    _website = website;
  }

  public Task SendConfirmationLinkAsync(BrunoMemberUser user, string email, string confirmationLink) =>
    SendAsync(
      email,
      $"Confirma tu cuenta de {MembershipName}",
      BuildMessage("Confirma tu correo", "Para terminar tu registro, confirma tu correo electrónico.", confirmationLink, "Confirmar correo"));

  public Task SendPasswordResetLinkAsync(BrunoMemberUser user, string email, string resetLink) =>
    SendAsync(
      email,
      $"Restablece tu contraseña de {MembershipName}",
      BuildMessage("Restablece tu contraseña", "Recibimos una solicitud para cambiar tu contraseña.", resetLink, "Crear nueva contraseña"));

  public Task SendPasswordResetCodeAsync(BrunoMemberUser user, string email, string resetCode) =>
    SendAsync(
      email,
      $"Código de recuperación de {MembershipName}",
      $"Tu código es: {WebUtility.HtmlEncode(resetCode)}");

  private Task SendAsync(string email, string subject, string message)
    => _mailClient.SendEmailAsync(email, subject, message);

  private string MembershipName => _presentation.MembershipProgramName ?? "tu membresía";

  private string BuildMessage(string title, string text, string url, string action)
  {
    var logoUrl = new Uri(
      _website.Instance.CanonicalBaseUri,
      _presentation.LogoPath.TrimStart('/')).ToString();
    return
    $"""
    <div style="font-family:Arial,sans-serif;max-width:560px;margin:auto;color:#25211d">
      <p><img src="{WebUtility.HtmlEncode(logoUrl)}" alt="{WebUtility.HtmlEncode(_presentation.PublicName)}" style="display:block;max-width:150px;height:auto"></p>
      <h1 style="color:{_presentation.PrimaryColor}">{WebUtility.HtmlEncode(title)}</h1>
      <p>{WebUtility.HtmlEncode(text)}</p>
      <p><a href="{WebUtility.HtmlEncode(url)}" style="display:inline-block;background:{_presentation.PrimaryColor};color:#fff;padding:12px 18px;border-radius:8px;text-decoration:none">{WebUtility.HtmlEncode(action)}</a></p>
      <p style="font-size:12px;color:#6d6862">Si no solicitaste esta acción, puedes ignorar este mensaje.</p>
      <p style="font-size:12px;color:#6d6862">{WebUtility.HtmlEncode(_presentation.LegalName)}</p>
    </div>
    """;
  }
}
