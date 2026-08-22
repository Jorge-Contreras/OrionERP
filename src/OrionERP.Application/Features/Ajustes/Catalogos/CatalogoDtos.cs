namespace OrionERP.Application.Features.Ajustes.Catalogos;

/// <summary>
/// Stable identifiers for the catalogs the settings hub can edit.
///
/// These are the lookup tables that feed dropdowns across OrionERP but had no
/// editor anywhere: until now they were maintained by hand in SSMS, which is why
/// a sanitized Training database could not recover from an empty
/// <c>dbo.Formas_Pago</c> or an empty chart of accounts.
///
/// The enum is the only thing a caller may name. Table and column names are
/// resolved from it through a hard-coded descriptor, never composed from input.
///
/// Deliberately absent: <c>rh.LeaveType</c>, whose rows also carry IsPaid and
/// RequiresBalance. Those do not fit the shape below, and a leave type edited
/// without them would be a lossy editor sitting outside the module that owns
/// leave configuration. It belongs on /capital-humano/ausencias instead.
/// </summary>
public enum CatalogoKey
{
  FormasPago,
  Proyectos,
  Compras,
  Servicios,
  CategoriasOrdenTrabajo,
  Alergenos,
  Arrendadores
}

/// <summary>
/// What a catalog supports, so the page can render and validate one editor for
/// all of them instead of eight near-identical ones.
/// </summary>
public sealed record CatalogoDescriptorDto
{
  public required CatalogoKey Key { get; init; }

  /// <summary>Tab label.</summary>
  public required string Titulo { get; init; }

  /// <summary>One line explaining what the catalog feeds, shown under the tab.</summary>
  public required string Descripcion { get; init; }

  /// <summary>True when rows belong to a tenant and the RFC picker applies.</summary>
  public required bool EsPorRfc { get; init; }

  /// <summary>True when the catalog has a short business code beside its name.</summary>
  public required bool TieneCodigo { get; init; }

  /// <summary>Label for the code column, when there is one.</summary>
  public string CodigoEtiqueta { get; init; } = "Código";

  /// <summary>Label for the name column.</summary>
  public string NombreEtiqueta { get; init; } = "Nombre";

  /// <summary>True when the code is the primary key and cannot be regenerated.</summary>
  public required bool CodigoEsLlave { get; init; }

  /// <summary>True when the catalog carries a display-order column.</summary>
  public required bool TieneOrden { get; init; }

  /// <summary>
  /// True when the catalog has an active flag, in which case deleting means
  /// deactivating. Catalogs without one are removed outright, and only after a
  /// referential check.
  /// </summary>
  public required bool TieneActivo { get; init; }

  /// <summary>
  /// True when some rows are protected from deletion because an external
  /// authority owns them, as with the SAT payment-method claves.
  /// </summary>
  public required bool TieneFilasProtegidas { get; init; }
}

public sealed record CatalogoItemDto
{
  /// <summary>
  /// The row key as text. Most catalogs use an int identity; dbo.Formas_Pago is
  /// keyed by its SAT clave, so this is a string rather than forcing a surrogate
  /// onto a published catalog.
  /// </summary>
  public required string Id { get; init; }

  public string? Codigo { get; init; }

  public required string Nombre { get; init; }

  public int? Orden { get; init; }

  public bool Activo { get; init; } = true;

  /// <summary>
  /// True when the row belongs to an externally published set and may be
  /// renamed but never deleted or renumbered.
  /// </summary>
  public bool EsProtegido { get; init; }

  /// <summary>How many records reference this row. Drives the delete guard.</summary>
  public int Referencias { get; init; }
}

public sealed record CatalogoSaveRequest
{
  public required CatalogoKey Key { get; init; }

  /// <summary>Null or empty creates a row; anything else updates that row.</summary>
  public string? Id { get; init; }

  public string? Codigo { get; init; }

  public required string Nombre { get; init; }

  public int? Orden { get; init; }

  public bool Activo { get; init; } = true;

  /// <summary>Required for tenant-scoped catalogs, ignored otherwise.</summary>
  public string? Rfc { get; init; }
}

/// <summary>
/// A chart-of-accounts node. Kept separate from the flat catalogs because
/// CuentasContables is a three-level hierarchy the account picker depends on:
/// a Nivel1 header is Nivel2='00' and Nivel3='00', a Nivel2 header is
/// Nivel3='00', and anything else is a postable leaf.
/// </summary>
public sealed record CuentaContableNodeDto
{
  public required int Id { get; init; }

  public required string Nivel1 { get; init; }

  public required string Nivel2 { get; init; }

  public required string Nivel3 { get; init; }

  public required string Descripcion { get; init; }

  public string Clave => $"{Nivel1}-{Nivel2}-{Nivel3}";

  /// <summary>1, 2 or 3. Derived from the '00' sentinel, not stored.</summary>
  public int Nivel => Nivel2 == "00" && Nivel3 == "00" ? 1 : Nivel3 == "00" ? 2 : 3;

  public bool EsEncabezado => Nivel < 3;

  /// <summary>Child accounts. A header with children cannot be deleted.</summary>
  public int Hijos { get; init; }

  /// <summary>Records referencing this account. A referenced leaf cannot be deleted.</summary>
  public int Referencias { get; init; }
}

public sealed record CuentaContableSaveRequest
{
  /// <summary>Null creates an account; anything else updates that account.</summary>
  public int? Id { get; init; }

  public required string Rfc { get; init; }

  public required string Nivel1 { get; init; }

  public required string Nivel2 { get; init; }

  public required string Nivel3 { get; init; }

  public required string Descripcion { get; init; }
}
