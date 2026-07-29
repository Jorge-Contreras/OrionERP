using System.Security.Cryptography;
using System.Text;

namespace OrionERP.Web.Features.Restaurante;

public static class RestaurantQzTraySigningApi
{
  public static IEndpointRouteBuilder MapRestaurantQzTraySigningApi(this IEndpointRouteBuilder endpoints)
  {
    var group = endpoints
      .MapGroup("/api/restaurant/qz")
      .RequireAuthorization("RestaurantQzBridge");

    group.MapGet("/certificate", GetCertificate);
    group.MapPost("/sign", Sign);
    return endpoints;
  }

  private static IResult GetCertificate(
    HttpContext context,
    IRestaurantQzTraySigningService signingService,
    ILogger<RestaurantQzTraySigningService> logger)
  {
    SetPrivateResponseHeaders(context);
    try
    {
      return Results.Text(
        signingService.GetCertificate(),
        "text/plain",
        Encoding.UTF8,
        StatusCodes.Status200OK);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
      logger.LogError(ex, "QZ Tray certificate configuration is unavailable.");
      return Results.Problem(
        title: "Firma de QZ Tray no disponible",
        detail: "El servidor no pudo cargar el certificado configurado.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static IResult Sign(
    RestaurantQzSignRequest payload,
    HttpContext context,
    IRestaurantQzTraySigningService signingService,
    ILogger<RestaurantQzTraySigningService> logger)
  {
    SetPrivateResponseHeaders(context);
    try
    {
      return Results.Text(
        signingService.Sign(payload.Request),
        "text/plain",
        Encoding.UTF8,
        StatusCodes.Status200OK);
    }
    catch (ArgumentException ex)
    {
      return Results.Problem(
        title: "Solicitud de firma inválida",
        detail: ex.Message,
        statusCode: StatusCodes.Status400BadRequest);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException)
    {
      logger.LogError(ex, "QZ Tray request signing failed.");
      return Results.Problem(
        title: "Firma de QZ Tray no disponible",
        detail: "El servidor no pudo firmar la solicitud.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
    }
  }

  private static void SetPrivateResponseHeaders(HttpContext context)
  {
    context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
    context.Response.Headers.Pragma = "no-cache";
  }
}

public sealed class RestaurantQzSignRequest
{
  public string Request { get; init; } = string.Empty;
}
