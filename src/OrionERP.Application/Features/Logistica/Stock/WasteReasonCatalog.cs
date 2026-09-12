namespace OrionERP.Application.Features.Logistica.Stock;

/// <summary>
/// Motivo por el que se da de baja producto. Se guarda en
/// <c>logistica.InventoryAdjustment.ReasonCode</c>, que es <c>varchar(30)</c>.
/// </summary>
/// <param name="Code">Clave estable; nunca se traduce ni se renombra: es la dimensión de reporte.</param>
/// <param name="Label">Texto del botón en la captura.</param>
/// <param name="Help">Una línea que evita que dos motivos se confundan entre sí.</param>
/// <param name="RequiresNote">Obliga a escribir la justificación porque la clave no explica nada por sí sola.</param>
public sealed record WasteReason(string Code, string Label, string Help, bool RequiresNote);

/// <summary>
/// Lista fija de motivos de merma. Es una lista en código y no una tabla porque necesita ser
/// la misma para la captura, la validación del servicio y las etiquetas del historial: con
/// texto libre la merma no tiene dimensión de reporte, y con una tabla por RFC cada empresa
/// inventaría claves distintas y tampoco la tendría.
/// </summary>
public static class WasteReasonCatalog
{
  public const string Expired = "CADUCIDAD";
  public const string Preparation = "MERMA_PREP";
  public const string Damage = "DANO";
  public const string Spill = "DERRAME";
  public const string Contamination = "CONTAMINACION";
  public const string Overproduction = "SOBREPRODUCCION";
  public const string CustomerReturn = "DEVOLUCION";
  public const string Missing = "FALTANTE";
  public const string Other = "OTRO";

  /// <summary>Motivo del documento compensatorio. No se ofrece en la captura.</summary>
  public const string Reversal = "REVERSA";

  /// <summary>Máximo de <c>ReasonCode</c> en la base; ninguna clave puede excederlo.</summary>
  public const int MaxCodeLength = 30;

  /// <summary>
  /// Motivos que puede elegir quien captura, en el orden en que se muestran.
  /// <see cref="Expired"/> va primero: el producto caducado es el caso que originó el módulo.
  /// </summary>
  public static IReadOnlyList<WasteReason> Selectable { get; } =
  [
    new(Expired, "Caducidad",
      "Pasó su fecha de caducidad o de consumo preferente.", RequiresNote: false),
    new(Preparation, "Merma de preparación",
      "Recortes, cáscaras y sobras normales de cocinar.", RequiresNote: false),
    new(Damage, "Daño o rotura",
      "Se rompió, se golpeó o el empaque quedó inservible.", RequiresNote: false),
    new(Spill, "Derrame",
      "Se tiró o se derramó durante el manejo o el servicio.", RequiresNote: false),
    new(Contamination, "Contaminación",
      "Quedó expuesto, fuera de temperatura o en contacto con algo que lo inutiliza.", RequiresNote: false),
    new(Overproduction, "Sobreproducción",
      "Se preparó más de lo que se vendió y ya no se puede servir.", RequiresNote: false),
    new(CustomerReturn, "Devolución de cliente",
      "Regresó del comedor y no se puede reutilizar.", RequiresNote: false),
    new(Missing, "Faltante",
      "No aparece y no hay movimiento que lo explique.", RequiresNote: true),
    new(Other, "Otro",
      "Cualquier caso que no encaje arriba. Explica qué pasó.", RequiresNote: true)
  ];

  public static WasteReason? Find(string? code)
    => string.IsNullOrWhiteSpace(code)
      ? null
      : Selectable.FirstOrDefault(reason =>
          string.Equals(reason.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

  /// <summary>Sólo los motivos de la lista pasan; <see cref="Reversal"/> es interno.</summary>
  public static bool IsSelectable(string? code) => Find(code) is not null;

  public static string LabelFor(string? code)
    => string.Equals(code?.Trim(), Reversal, StringComparison.OrdinalIgnoreCase)
      ? "Reversa"
      : Find(code)?.Label ?? (string.IsNullOrWhiteSpace(code) ? "Sin motivo" : code.Trim());

  /// <summary>Normaliza a como se guarda en la base.</summary>
  public static string Normalize(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();
}
