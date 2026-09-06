namespace OrionERP.Application.Features.Restaurante;

/// <summary>
/// Lo que el diagnóstico de venta encontró, reagrupado para operarlo en el punto de venta: por
/// material y no por producto. El reporte completo responde "¿qué platillo falla?"; la caja
/// necesita lo contrario, "¿qué material tengo que reponer o contar?", porque un solo faltante
/// tumba varios platillos a la vez.
/// </summary>
public sealed class RestaurantSaleAlertBoard
{
  public DateTimeOffset GeneratedAtLocal { get; init; }
  public bool AllowSupervisorDeficit { get; init; }

  /// <summary>Materiales cuyo faltante detiene la venta sin remedio en caja.</summary>
  public int BlockedCount { get; init; }

  /// <summary>Materiales que sí se pueden vender, pero pidiendo autorización de supervisor.</summary>
  public int SupervisorCount { get; init; }

  /// <summary>Materiales que alcanzan para esta venta pero quedarían en o debajo del mínimo.</summary>
  public int WarningCount { get; init; }

  /// <summary>Productos con BOM, unidades o modificadores mal configurados: no se arreglan contando.</summary>
  public int ConfigurationIssueCount { get; init; }

  /// <summary>
  /// El número del badge en el header. Cuenta todo lo que va a lanzar un error o pedir supervisor
  /// al cobrar; deja fuera las advertencias, que todavía dejan vender de corrido.
  /// </summary>
  public int BadgeCount => BlockedCount + SupervisorCount + ConfigurationIssueCount;

  public bool HasBlockers => BlockedCount > 0 || ConfigurationIssueCount > 0;
  public bool IsClean => Materials.Count == 0 && ProductIssues.Count == 0;

  public IReadOnlyList<RestaurantSaleAlertMaterial> Materials { get; init; } = [];
  public IReadOnlyList<RestaurantSaleAlertProduct> ProductIssues { get; init; } = [];
}

/// <summary>Un material que va a estorbar la venta, con todo lo necesario para decidir si contarlo.</summary>
public sealed class RestaurantSaleAlertMaterial
{
  public int MaterialId { get; init; }
  public string MaterialCode { get; init; } = string.Empty;
  public string MaterialName { get; init; } = string.Empty;
  public string BaseUnit { get; init; } = string.Empty;

  /// <summary>El peor estatus del material entre todos los productos que lo usan.</summary>
  public string Status { get; init; } = RestaurantSaleReadinessStatuses.Ready;

  /// <summary>
  /// El faltante más grande observado. No se suma entre productos: cada renglón del reporte simula
  /// vender <em>una</em> unidad de ese producto, no la carta entera.
  /// </summary>
  public decimal ShortageQuantity { get; init; }

  public decimal UsableQuantity { get; init; }
  public decimal MinimumQuantity { get; init; }

  /// <summary>«MakeToStock» se repone produciendo, no comprando.</summary>
  public string FulfillmentMode { get; init; } = string.Empty;

  public int AffectedProductCount { get; init; }

  /// <summary>Los primeros platillos afectados, para explicar el impacto sin volcar la carta.</summary>
  public IReadOnlyList<string> AffectedProducts { get; init; } = [];

  public string RecommendedAction { get; init; } = string.Empty;

  /// <summary>El mismo texto que el POS mostrará al cobrar, para que caja reconozca el mensaje.</summary>
  public string? PredictedPosMessage { get; init; }

  public bool IsBlocking => Status is RestaurantSaleReadinessStatuses.InventoryBlocked
    or RestaurantSaleReadinessStatuses.SupervisorRequired;

  public string Label => string.IsNullOrWhiteSpace(MaterialCode)
    ? MaterialName
    : $"{MaterialCode} · {MaterialName}";
}

/// <summary>Un producto que no se puede vender por configuración; contar inventario no lo resuelve.</summary>
public sealed class RestaurantSaleAlertProduct
{
  public long ProductId { get; init; }
  public string Sku { get; init; } = string.Empty;
  public string ProductName { get; init; } = string.Empty;
  public string Status { get; init; } = RestaurantSaleReadinessStatuses.ConfigurationBlocked;
  public string? PredictedPosMessage { get; init; }
  public string SuggestedAction { get; init; } = string.Empty;
}
