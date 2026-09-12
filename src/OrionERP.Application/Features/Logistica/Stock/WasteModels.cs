using System.ComponentModel.DataAnnotations;

namespace OrionERP.Application.Features.Logistica.Stock;

/// <summary>Estados de un documento de merma.</summary>
public static class WasteStatuses
{
  /// <summary>Transitorio: sólo existe dentro de la transacción que aplica la baja.</summary>
  public const string Draft = "Draft";

  /// <summary>Capturada por alguien sin rango de supervisor. El inventario ya bajó.</summary>
  public const string PendingReview = "PendingReview";

  /// <summary>Capturada por un supervisor, o ya revisada por uno.</summary>
  public const string Approved = "Approved";

  /// <summary>Corregida por un documento compensatorio.</summary>
  public const string Reversed = "Reversed";

  public static string LabelFor(string? status) => status switch
  {
    PendingReview => "Pendiente de revisión",
    Approved => "Aprobada",
    Reversed => "Reversada",
    Draft => "Borrador",
    _ => string.IsNullOrWhiteSpace(status) ? "Sin estado" : status
  };
}

/// <summary>
/// Quién ejecuta la operación. <paramref name="IsSupervisor"/> lo resuelve el componente
/// Blazor con <c>IAuthorizationService</c>: corre en el servidor, así que es una decisión
/// del servidor y no un dato que venga del navegador.
/// </summary>
public sealed record WasteActor(string UserName, bool IsSupervisor);

public sealed class WasteWorkspaceDto
{
  public IReadOnlyList<InventoryLocationOptionDto> Locations { get; set; } = [];
  public IReadOnlyList<InventoryBalanceOptionDto> Balances { get; set; } = [];
  public IReadOnlyList<InventoryLotOptionDto> Lots { get; set; } = [];

  /// <summary>Costo de la merma con fecha de hoy (local).</summary>
  public decimal TodayCost { get; set; }

  /// <summary>Costo de la merma de los últimos siete días.</summary>
  public decimal WeekCost { get; set; }

  public int PendingReviewCount { get; set; }

  /// <summary>Motivo con más costo en los últimos treinta días; vacío si no hay merma.</summary>
  public string TopReasonCode { get; set; } = string.Empty;
}

public sealed class WasteCreateRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;

  /// <summary>Folio del documento. Es la llave de idempotencia: repetirlo no duplica la baja.</summary>
  [Required, StringLength(30)] public string WasteCode { get; set; } = string.Empty;

  [Required, StringLength(WasteReasonCatalog.MaxCodeLength)] public string ReasonCode { get; set; } = string.Empty;

  /// <summary>
  /// Justificación libre. Obligatoria sólo cuando el motivo no se explica solo
  /// (<c>FALTANTE</c>, <c>OTRO</c>): pedir prosa para tirar lechuga caducada es friccion sin
  /// información. Si va vacía se guarda la etiqueta del motivo.
  /// </summary>
  [StringLength(1000)] public string Reason { get; set; } = string.Empty;

  /// <summary>
  /// Día local en que se tiró el producto. <c>CreatedAt</c> es UTC y no sirve para el corte
  /// diario: una merma de las 19:00 en México cae al día siguiente en UTC.
  /// </summary>
  public DateOnly? OccurredOn { get; set; }

  [Required, StringLength(200)] public string EvidenceFileName { get; set; } = string.Empty;

  [Required] public byte[] Evidence { get; set; } = [];

  [MinLength(1)] public List<WasteLineRequest> Lines { get; set; } = [];
}

public sealed class WasteLineRequest
{
  public int LocationId { get; set; }
  public int MaterialId { get; set; }
  public long? MaterialLotId { get; set; }

  /// <summary>
  /// Cantidad que se tira, siempre en positivo: el servicio es quien la convierte en un
  /// delta negativo. Pedirla en negativo era el error de captura más frecuente.
  /// </summary>
  [Range(typeof(decimal), "0.0001", "999999999")] public decimal Quantity { get; set; }
}

public sealed class WasteReversalRequest
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public long AdjustmentId { get; set; }

  /// <summary>Obligatorio: una reversa sin motivo no es auditable.</summary>
  [Required, StringLength(1000)] public string Reason { get; set; } = string.Empty;
}

public sealed class WasteHistoryQuery
{
  [Required] public string Rfc { get; set; } = string.Empty;
  public DateOnly? FromDate { get; set; }
  public DateOnly? ToDate { get; set; }
  public int? LocationId { get; set; }
  public string? ReasonCode { get; set; }
  public string? Status { get; set; }
  public int Page { get; set; } = 1;
  public int PageSize { get; set; } = 25;
}

public sealed class WasteHistoryDto
{
  public IReadOnlyList<WasteDocumentDto> Documents { get; set; } = [];
  public int TotalCount { get; set; }

  /// <summary>Costo total del filtro completo, no sólo de la página mostrada.</summary>
  public decimal TotalCost { get; set; }
}

public sealed class WasteDocumentDto
{
  public long Id { get; set; }
  public string WasteCode { get; set; } = string.Empty;
  public string ReasonCode { get; set; } = string.Empty;
  public string Reason { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;

  /// <summary>
  /// Día local en que ocurrió, nulo en los documentos anteriores a que existiera la columna.
  /// Va como <c>DateTime?</c> y no como <c>DateOnly?</c> porque Dapper sólo entiende
  /// <c>DateOnly</c> con el manejador que registra el host Web, y esta consulta no puede
  /// depender de eso.
  /// </summary>
  public DateTime? OccurredOn { get; set; }

  public DateTime CreatedAt { get; set; }
  public string CreatedBy { get; set; } = string.Empty;
  public string? ApprovedBy { get; set; }
  public DateTime? ApprovedAt { get; set; }

  /// <summary>Documento que corrigió a éste, si ya fue reversado.</summary>
  public long? ReversedByAdjustmentId { get; set; }

  /// <summary>Documento original, cuando éste es la reversa de otro.</summary>
  public long? ReversalOfAdjustmentId { get; set; }

  /// <summary>Se expone como bandera para no arrastrar la foto en el listado.</summary>
  public bool HasEvidence { get; set; }

  public string EvidenceFileName { get; set; } = string.Empty;

  /// <summary>Costo de la baja en positivo, aunque las partidas lleven delta negativo.</summary>
  public decimal TotalCost { get; set; }

  public IReadOnlyList<WasteDocumentLineDto> Lines { get; set; } = [];

  public bool IsReversal => ReversalOfAdjustmentId.HasValue;
  public bool CanReview => Status == WasteStatuses.PendingReview;
  public bool CanReverse => !IsReversal
    && !ReversedByAdjustmentId.HasValue
    && Status is WasteStatuses.PendingReview or WasteStatuses.Approved;
}

public sealed class WasteDocumentLineDto
{
  public long Id { get; set; }
  public int MaterialId { get; set; }
  public string MaterialCode { get; set; } = string.Empty;
  public string MaterialName { get; set; } = string.Empty;
  public string UnitCode { get; set; } = string.Empty;
  public int LocationId { get; set; }
  public string LocationName { get; set; } = string.Empty;
  public string? LotCode { get; set; }

  /// <summary>Delta tal como quedó en la base: negativo en una baja, positivo en su reversa.</summary>
  public decimal QuantityDelta { get; set; }

  public decimal FrozenUnitCost { get; set; }

  /// <summary>Cantidad en positivo para mostrar.</summary>
  public decimal Quantity => Math.Abs(QuantityDelta);

  public decimal Cost => Math.Abs(QuantityDelta) * FrozenUnitCost;
}

public sealed class WasteEvidenceDto
{
  public byte[] Content { get; set; } = [];
  public string FileName { get; set; } = string.Empty;

  /// <summary>Resuelto en la capa de datos, que es la que sabe leer la firma del archivo.</summary>
  public string ContentType { get; set; } = "application/octet-stream";
}
