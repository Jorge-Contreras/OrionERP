namespace OrionERP.Application.Features.Ajustes;

public sealed record PlantillaContableListItemDto
{
  public int PlantillaContableId { get; init; }
  public string Nombre { get; init; } = string.Empty;
  public string? Descripcion { get; init; }
  public string? Rfc { get; init; }
  public string? TipoPoliza { get; init; }
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
