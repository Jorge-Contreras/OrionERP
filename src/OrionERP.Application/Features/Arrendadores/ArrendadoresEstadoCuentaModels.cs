namespace OrionERP.Application.Features.Arrendadores;

public sealed class ArrendadorListItemDto
{
  public int Id { get; set; }
  public string RazonSocial { get; set; } = string.Empty;
  public int RoomCount { get; set; }
}

public sealed class ArrendadorRoomListItemDto
{
  public int RoomId { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string RoomType { get; set; } = string.Empty;
  public decimal? BasePrice { get; set; }
}

public sealed class ArrendadorEstadoCuentaDto
{
  public ArrendadorEstadoCuentaContextDto? Context { get; set; }
  public ArrendadorEstadoCuentaResumenDto? Summary { get; set; }
  public IReadOnlyList<ArrendadorEstadoCuentaDetalleDto> Details { get; set; } = [];
  public IReadOnlyList<ArrendadorEstadoCuentaExclusionDto> Exclusions { get; set; } = [];
}

public sealed class ArrendadorEstadoCuentaContextDto
{
  public int OwnerId { get; set; }
  public string RazonSocial { get; set; } = string.Empty;
  public int RoomId { get; set; }
  public string RoomName { get; set; } = string.Empty;
  public string RoomType { get; set; } = string.Empty;
  public int Year { get; set; }
  public int Month { get; set; }
}

public sealed class ArrendadorEstadoCuentaResumenDto
{
  public string Mes { get; set; } = string.Empty;
  public int NochesOcupadas { get; set; }
  public decimal Cobrado { get; set; }
  public decimal Arrendador30 { get; set; }
  public decimal Isr10 { get; set; }
  public decimal PagoFinalArrendador { get; set; }
}

public sealed class ArrendadorEstadoCuentaDetalleDto
{
  public int RoomCalendarId { get; set; }
  public DateTime Noche { get; set; }
  public string Casa { get; set; } = string.Empty;
  public string? HuespedOBloqueo { get; set; }
  public int ReservationId { get; set; }
  public string ReservationStatus { get; set; } = string.Empty;
  public DateTime CheckIn { get; set; }
  public DateTime CheckOut { get; set; }
  public decimal ReservationTotal { get; set; }
  public decimal TotalPagadoContabilizado { get; set; }
  public int TransaccionesPago { get; set; }
  public DateTime FechaUltimoPago { get; set; }
  public decimal CobradoNoche { get; set; }
  public decimal Arrendador30 { get; set; }
  public decimal Isr10 { get; set; }
  public decimal PagoFinalArrendador { get; set; }
  public string? ReservationNotes { get; set; }
}

public sealed class ArrendadorEstadoCuentaExclusionDto
{
  public int RoomCalendarId { get; set; }
  public DateTime Noche { get; set; }
  public string Casa { get; set; } = string.Empty;
  public string? HuespedOBloqueo { get; set; }
  public int? ReservationId { get; set; }
  public decimal? ReservationTotal { get; set; }
  public decimal? TotalPagadoContabilizado { get; set; }
  public int? TransaccionesPago { get; set; }
  public decimal CobradoNoche { get; set; }
  public string MotivoExclusion { get; set; } = string.Empty;
}
