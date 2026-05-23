namespace OrionERP.Application.Features.Ajustes;

public static class CfdiPolizaCuentaDefaultRoles
{
  public const string SubtotalGasto = "SUBTOTAL_GASTO";
  public const string SubtotalIngreso = "SUBTOTAL_INGRESO";
  public const string IvaTrasladado = "IVA_TRASLADADO";
  public const string IvaAcreditable = "IVA_ACREDITABLE";
  public const string IepsTrasladado = "IEPS_TRASLADADO";
  public const string IepsAcreditable = "IEPS_ACREDITABLE";
  public const string RetencionIva = "RETENCION_IVA";
  public const string RetencionIsr = "RETENCION_ISR";
  public const string RetencionIeps = "RETENCION_IEPS";
  public const string TotalGasto = "TOTAL_GASTO";
  public const string TotalIngreso = "TOTAL_INGRESO";

  public static readonly IReadOnlyList<CfdiPolizaCuentaDefaultRoleDto> Required =
  [
    new(SubtotalGasto, "Subtotal gasto", "Cuenta para subtotal de CFDI recibido."),
    new(SubtotalIngreso, "Subtotal ingreso", "Cuenta para subtotal de CFDI emitido."),
    new(IvaTrasladado, "IVA trasladado", "IVA cobrado en CFDI emitido."),
    new(IvaAcreditable, "IVA acreditable", "IVA pagado en CFDI recibido."),
    new(IepsTrasladado, "IEPS trasladado", "IEPS cobrado en CFDI emitido."),
    new(IepsAcreditable, "IEPS acreditable", "IEPS pagado en CFDI recibido."),
    new(RetencionIva, "Retencion IVA", "IVA retenido."),
    new(RetencionIsr, "Retencion ISR", "ISR retenido."),
    new(RetencionIeps, "Retencion IEPS", "IEPS retenido."),
    new(TotalGasto, "Total gasto", "Cuenta de contrapartida para CFDI recibido."),
    new(TotalIngreso, "Total ingreso", "Cuenta de contrapartida para CFDI emitido.")
  ];

  public static bool IsRequired(string? cuentaClave)
    => Required.Any(role => string.Equals(role.CuentaClave, cuentaClave, StringComparison.OrdinalIgnoreCase));
}

public sealed record CfdiPolizaCuentaDefaultRoleDto(
    string CuentaClave,
    string Nombre,
    string Descripcion);

public sealed record CfdiPolizaCuentaDefaultsDto
{
  public string Rfc { get; init; } = string.Empty;
  public IReadOnlyList<CfdiPolizaCuentaDefaultAccountDto> Cuentas { get; init; } = Array.Empty<CfdiPolizaCuentaDefaultAccountDto>();
}

public sealed record CfdiPolizaCuentaDefaultAccountDto
{
  public string CuentaClave { get; init; } = string.Empty;
  public int? CuentaContableId { get; init; }
  public string? CuentaRfc { get; init; }
  public string? Nivel1 { get; init; }
  public string? Nivel2 { get; init; }
  public string? Nivel3 { get; init; }
  public string? CuentaDescripcion { get; init; }
}

public sealed record CfdiPolizaCuentaDefaultsSaveRequest
{
  public string Rfc { get; init; } = string.Empty;
  public IReadOnlyList<CfdiPolizaCuentaDefaultSaveItem> Cuentas { get; init; } = Array.Empty<CfdiPolizaCuentaDefaultSaveItem>();
}

public sealed record CfdiPolizaCuentaDefaultSaveItem
{
  public string CuentaClave { get; init; } = string.Empty;
  public int CuentaContableId { get; init; }
}
