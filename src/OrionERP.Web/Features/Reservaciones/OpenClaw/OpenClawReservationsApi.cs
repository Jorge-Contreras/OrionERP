using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using OrionERP.Application.Features.Reservaciones.OpenClaw;
using OrionERP.Web.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Web.Features.Reservaciones.OpenClaw;

public static class OpenClawReservationsApi
{
  public static IEndpointRouteBuilder MapOpenClawReservationsApi(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapPost("/api/openclaw/reservations", CreateReservationAsync);
    endpoints.MapGet("/api/openclaw/reservations/{reservationId:int}/pdf", DownloadReservationPdfAsync);
    return endpoints;
  }

  private static async Task<IResult> CreateReservationAsync(
    OpenClawReservationCreateRequest request,
    HttpContext httpContext,
    IOptions<OpenClawApiOptions> options,
    IOpenClawReservationsService reservationsService,
    IOpenClawReservationPdfTokenService pdfTokenService,
    CancellationToken ct)
  {
    if (!IsAuthorized(httpContext, options.Value, out var authError))
    {
      return authError!;
    }

    try
    {
      var result = await reservationsService.CreateReservationAsync(request, ct);
      var pdfUrl = BuildPdfUrl(httpContext, options.Value, result.ReservationId, pdfTokenService.CreateToken(result.ReservationId));

      return Results.Ok(new OpenClawReservationCreateResponse
      {
        ReservationId = result.ReservationId,
        ClientName = result.ClientName,
        CheckIn = result.CheckIn,
        CheckOut = result.CheckOut,
        Status = result.Status,
        RequiresCfdi = result.RequiresCfdi,
        Taxable = result.RequiresCfdi,
        SuiteNames = result.SuiteNames,
        Extras = result.Extras,
        SuiteSubtotal = result.SuiteSubtotal,
        ExtrasSubtotal = result.ExtrasSubtotal,
        TotalPrice = result.TotalPrice,
        PdfUrl = pdfUrl
      });
    }
    catch (OpenClawReservationValidationException ex)
    {
      return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
    }
    catch (OpenClawReservationConflictException ex)
    {
      return Results.Problem(title: "Conflicto de disponibilidad", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
    }
  }

  private static async Task<IResult> DownloadReservationPdfAsync(
    int reservationId,
    string? token,
    IOpenClawReservationPdfTokenService pdfTokenService,
    IOpenClawReservationsService reservationsService,
    IReservacionPdfDocumentFactory pdfDocumentFactory,
    IReservacionPdfService pdfService,
    CancellationToken ct)
  {
    if (!pdfTokenService.TryValidate(reservationId, token, out var errorMessage))
    {
      return Results.Problem(title: "Acceso no autorizado", detail: errorMessage, statusCode: StatusCodes.Status401Unauthorized);
    }

    var detail = await reservationsService.GetReservationDetailAsync(reservationId, ct);
    if (detail is null)
    {
      return Results.NotFound();
    }

    var document = pdfDocumentFactory.CreateFromDetail(detail);
    var bytes = pdfService.Generate(document);
    var fileName = $"reservacion-{reservationId:D6}.pdf";

    return Results.File(bytes, "application/pdf", fileName);
  }

  private static bool IsAuthorized(HttpContext httpContext, OpenClawApiOptions options, out IResult? errorResult)
  {
    const string headerName = "X-Orion-Api-Key";

    errorResult = null;
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
      errorResult = Results.Problem(
        title: "Configuración inválida",
        detail: "La API interna de OpenClaw no tiene una llave configurada.",
        statusCode: StatusCodes.Status500InternalServerError);
      return false;
    }

    var providedValue = httpContext.Request.Headers[headerName].ToString();
    if (!string.Equals(providedValue, options.ApiKey, StringComparison.Ordinal))
    {
      errorResult = Results.Problem(
        title: "Acceso no autorizado",
        detail: $"Incluye el header {headerName}.",
        statusCode: StatusCodes.Status401Unauthorized);
      return false;
    }

    return true;
  }

  private static string BuildPdfUrl(HttpContext httpContext, OpenClawApiOptions options, int reservationId, string token)
  {
    var path = $"/api/openclaw/reservations/{reservationId}/pdf?token={Uri.EscapeDataString(token)}";
    var configuredBaseUrl = options.PublicBaseUrl?.Trim();
    if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
    {
      if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out var baseUri))
      {
        throw new InvalidOperationException("OpenClawApi:PublicBaseUrl must be an absolute URL.");
      }

      return new Uri(baseUri, path).ToString();
    }

    return $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{path}";
  }
}
