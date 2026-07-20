namespace OrionERP.Application.Features.Logistica.Shared;

public static class LogisticsRfc
{
  public static string Require(string? rfc)
  {
    var normalized = Normalize(rfc);
    if (string.IsNullOrEmpty(normalized))
    {
      throw new InvalidOperationException("Debe seleccionar un RFC antes de operar Logística.");
    }

    return normalized;
  }

  public static string Normalize(string? rfc)
    => string.IsNullOrWhiteSpace(rfc) ? string.Empty : rfc.Trim().ToUpperInvariant();
}
