namespace OrionERP.Application.Features.Ajustes;

public static class PlantillaContableContextos
{
  public const string Transaccion = "TRANSACCION";
  public const string Pago20Recibido = "PAGO20_RECIBIDO";
  public const string Pago20Emitido = "PAGO20_EMITIDO";

  public static bool IsPago20(string? contexto)
    => string.Equals(contexto, Pago20Recibido, StringComparison.OrdinalIgnoreCase)
      || string.Equals(contexto, Pago20Emitido, StringComparison.OrdinalIgnoreCase);
}

public static class PlantillaContableMontoTipos
{
  public const string MontoTotal = "MONTO_TOTAL";
  public const string SubtotalIva16 = "SUBTOTAL_IVA_16";
  public const string Iva16 = "IVA_16";
  public const string Pago20TotalAsignado = "PAGO20_TOTAL_ASIGNADO";
  public const string Pago20Subtotal = "PAGO20_SUBTOTAL";
  public const string Pago20TrasladoIsr = "PAGO20_TRASLADO_ISR";
  public const string Pago20TrasladoIva = "PAGO20_TRASLADO_IVA";
  public const string Pago20TrasladoIeps = "PAGO20_TRASLADO_IEPS";
  public const string Pago20RetencionIsr = "PAGO20_RETENCION_ISR";
  public const string Pago20RetencionIva = "PAGO20_RETENCION_IVA";
  public const string Pago20RetencionIeps = "PAGO20_RETENCION_IEPS";

  public static readonly IReadOnlyList<string> TransaccionTipos =
  [
    MontoTotal,
    SubtotalIva16,
    Iva16
  ];

  public static readonly IReadOnlyList<string> Pago20Tipos =
  [
    Pago20TotalAsignado,
    Pago20Subtotal,
    Pago20TrasladoIsr,
    Pago20TrasladoIva,
    Pago20TrasladoIeps,
    Pago20RetencionIsr,
    Pago20RetencionIva,
    Pago20RetencionIeps
  ];
}

public sealed record PlantillaContableListItemDto
{
  public int PlantillaContableId { get; init; }
  public string Nombre { get; init; } = string.Empty;
  public string? Descripcion { get; init; }
  public string? Rfc { get; init; }
  public string? TipoPoliza { get; init; }
  public string Contexto { get; init; } = PlantillaContableContextos.Transaccion;
  public bool Activa { get; init; }
  public string Origen { get; init; } = string.Empty;
  public int LineCount { get; init; }
  public DateTime ActualizadaEn { get; init; }
}

public sealed record PlantillaContableDetailDto
{
  public int PlantillaContableId { get; init; }
  public string Nombre { get; init; } = string.Empty;
  public string? Descripcion { get; init; }
  public string? Rfc { get; init; }
  public string? TipoPoliza { get; init; }
  public string Contexto { get; init; } = PlantillaContableContextos.Transaccion;
  public bool Activa { get; init; }
  public string Origen { get; init; } = string.Empty;
  public DateTime CreadaEn { get; init; }
  public DateTime ActualizadaEn { get; init; }
  public IReadOnlyList<PlantillaContableLineaDto> Lineas { get; init; } = Array.Empty<PlantillaContableLineaDto>();
}

public sealed record PlantillaContableLineaDto
{
  public int PlantillaContableLineaId { get; init; }
  public int PlantillaContableId { get; init; }
  public int Orden { get; init; }
  public int CuentaContableId { get; init; }
  public string CuentaRfc { get; init; } = string.Empty;
  public string Nivel1 { get; init; } = string.Empty;
  public string Nivel2 { get; init; } = string.Empty;
  public string Nivel3 { get; init; } = string.Empty;
  public string CuentaContable { get; init; } = string.Empty;
  public string Naturaleza { get; init; } = "DEBE";
  public string MontoTipo { get; init; } = "MONTO_TOTAL";
  public decimal Factor { get; init; } = 1m;
  public string ConceptoTipo { get; init; } = "TRANSACCION";
  public string? ConceptoFijo { get; init; }
  public bool Activa { get; init; }
}

public sealed record PlantillaContableSaveRequest
{
  public int? PlantillaContableId { get; init; }
  public string Nombre { get; init; } = string.Empty;
  public string? Descripcion { get; init; }
  public string? Rfc { get; init; }
  public string? TipoPoliza { get; init; }
  public string Contexto { get; init; } = PlantillaContableContextos.Transaccion;
  public bool Activa { get; init; } = true;
  public IReadOnlyList<PlantillaContableLineaSaveRequest> Lineas { get; init; } = Array.Empty<PlantillaContableLineaSaveRequest>();
}

public sealed record PlantillaContableLineaSaveRequest
{
  public int? PlantillaContableLineaId { get; init; }
  public int Orden { get; init; }
  public int CuentaContableId { get; init; }
  public string Naturaleza { get; init; } = "DEBE";
  public string MontoTipo { get; init; } = "MONTO_TOTAL";
  public decimal Factor { get; init; } = 1m;
  public string ConceptoTipo { get; init; } = "TRANSACCION";
  public string? ConceptoFijo { get; init; }
}

public sealed record AjustesCommandResult(bool Success, string Message, int? EntityId = null)
{
  public static AjustesCommandResult Ok(string message, int? entityId = null)
    => new(true, message, entityId);

  public static AjustesCommandResult Fail(string message, int? entityId = null)
    => new(false, message, entityId);
}
