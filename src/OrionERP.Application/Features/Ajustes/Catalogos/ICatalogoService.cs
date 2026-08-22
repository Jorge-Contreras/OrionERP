namespace OrionERP.Application.Features.Ajustes.Catalogos;

/// <summary>
/// Editing for the lookup tables that feed dropdowns across OrionERP and had no
/// editor anywhere in the product.
///
/// Kept apart from <see cref="IAjustesService"/> deliberately: that service and
/// its page already carry general settings, extras, CFDI defaults and accounting
/// templates, and folding eight more catalogs into them would make both hard to
/// review. Results still use <see cref="AjustesCommandResult"/>, so the calling
/// page keeps one success/failure shape.
/// </summary>
public interface ICatalogoService
{
  /// <summary>
  /// What each catalog supports. Drives the hub's tabs and its single editor, so
  /// the UI has no per-catalog branching of its own.
  /// </summary>
  IReadOnlyList<CatalogoDescriptorDto> GetDescriptors();

  Task<IReadOnlyList<CatalogoItemDto>> GetItemsAsync(
      CatalogoKey key,
      string? rfc,
      string? search,
      bool includeInactive,
      CancellationToken ct = default);

  Task<AjustesCommandResult> SaveItemAsync(CatalogoSaveRequest request, CancellationToken ct = default);

  /// <summary>
  /// Deactivates the row when the catalog has an active flag, and otherwise
  /// removes it. Either way the call fails with an explanatory message rather
  /// than a foreign-key violation when something still references the row, and
  /// refuses outright for rows an external authority owns.
  /// </summary>
  Task<AjustesCommandResult> DeleteItemAsync(
      CatalogoKey key,
      string id,
      string? rfc,
      CancellationToken ct = default);

  /// <summary>
  /// The whole chart of accounts for one tenant, headers included, ordered by
  /// clave. Reading goes through here rather than ICuentasContablesRepository
  /// because the hub needs the child and reference counts that drive its
  /// delete guards.
  /// </summary>
  Task<IReadOnlyList<CuentaContableNodeDto>> GetCuentasAsync(
      string rfc,
      string? search,
      CancellationToken ct = default);

  Task<AjustesCommandResult> SaveCuentaAsync(CuentaContableSaveRequest request, CancellationToken ct = default);

  Task<AjustesCommandResult> DeleteCuentaAsync(string rfc, int id, CancellationToken ct = default);
}
