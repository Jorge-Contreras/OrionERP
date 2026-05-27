using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Ajustes;
using OrionERP.Application.Features.Reservaciones.Extras;

namespace OrionERP.Infrastructure.Features.Ajustes;

public sealed class AjustesService : IAjustesService
{
  private const string ApOccurrenceNotificationDaysParameter = "CxcrApNotificationDays";

  private static readonly IReadOnlyDictionary<string, CfdiPolizaCuentaDefaultRoleDto> CfdiDefaultRolesByKey =
      CfdiPolizaCuentaDefaultRoles.Required.ToDictionary(
          role => role.CuentaClave,
          StringComparer.OrdinalIgnoreCase);

  private static readonly HashSet<string> ValidNaturalezas = new(StringComparer.OrdinalIgnoreCase)
  {
    "DEBE",
    "HABER"
  };

  private static readonly HashSet<string> ValidMontoTipos = new(StringComparer.OrdinalIgnoreCase)
  {
    "MONTO_TOTAL",
    "SUBTOTAL_IVA_16",
    "IVA_16"
  };

  private static readonly HashSet<string> ValidConceptoTipos = new(StringComparer.OrdinalIgnoreCase)
  {
    "TRANSACCION",
    "FIJO"
  };

  private readonly string _connectionString;

  public AjustesService(IConfiguration configuration)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
        ?? throw new InvalidOperationException("Missing connection string 'OrionDb'.");
  }

  public async Task<AjustesGeneralSettingsDto> GetGeneralSettingsAsync(CancellationToken ct = default)
  {
    const string sql = @"
SELECT TOP (1) VALOR1
FROM dbo.PARAMETROS_CONFIGURACION
WHERE PARAMETRO = @parameter
ORDER BY ID DESC;";

    using var connection = new SqlConnection(_connectionString);
    var storedValue = await connection.QueryFirstOrDefaultAsync<string?>(
        new CommandDefinition(
            sql,
            new { parameter = ApOccurrenceNotificationDaysParameter },
            cancellationToken: ct));

    return new AjustesGeneralSettingsDto
    {
      ApOccurrenceNotificationDays = NormalizeApOccurrenceNotificationDays(storedValue)
    };
  }

  public async Task<AjustesCommandResult> SaveGeneralSettingsAsync(AjustesGeneralSettingsSaveRequest request, CancellationToken ct = default)
  {
    var notificationDays = NormalizeApOccurrenceNotificationDays(request.ApOccurrenceNotificationDays);
    const string sql = @"
UPDATE dbo.PARAMETROS_CONFIGURACION
SET VALOR1 = @value,
    VALOR2 = N'Dias de anticipacion para notificar CxCR en dashboard',
    VALOR3 = NULL,
    VALOR4 = NULL,
    VALOR5 = NULL
WHERE PARAMETRO = @parameter;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO dbo.PARAMETROS_CONFIGURACION
    (
        PARAMETRO,
        VALOR1,
        VALOR2,
        VALOR3,
        VALOR4,
        VALOR5
    )
    VALUES
    (
        @parameter,
        @value,
        N'Dias de anticipacion para notificar CxCR en dashboard',
        NULL,
        NULL,
        NULL
    );
END;";

    using var connection = new SqlConnection(_connectionString);
    await connection.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              parameter = ApOccurrenceNotificationDaysParameter,
              value = notificationDays.ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken: ct));

    return AjustesCommandResult.Ok("Ajustes generales guardados correctamente.");
  }

  public async Task<IReadOnlyList<ExtraCatalogItemDto>> GetExtraCatalogAsync(
      string? search,
      bool includeInactive,
      CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    e.ExtraID AS ExtraId,
    e.[Name],
    e.[Description],
    CAST(ISNULL(e.Price, 0) AS decimal(18,2)) AS Price,
    CAST(ISNULL(e.IsActive, 0) AS bit) AS IsActive,
    e.LegacyRoomID AS LegacyRoomId,
    e.CreatedAtUtc,
    e.UpdatedAtUtc
FROM dbo.Extra AS e
WHERE (@includeInactive = 1 OR e.IsActive = 1)
  AND
  (
      @search IS NULL
      OR e.[Name] LIKE @searchLike
      OR e.[Description] LIKE @searchLike
  )
ORDER BY e.IsActive DESC, e.[Name], e.ExtraID;";

    var normalizedSearch = NormalizeNullable(search);

    using var connection = new SqlConnection(_connectionString);
    var rows = await connection.QueryAsync<ExtraCatalogItemDto>(
        new CommandDefinition(
            sql,
            new
            {
              includeInactive,
              search = normalizedSearch,
              searchLike = normalizedSearch is null ? null : $"%{normalizedSearch}%"
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<AjustesCommandResult> SaveExtraCatalogItemAsync(
      ExtraCatalogSaveRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
    {
      return AjustesCommandResult.Fail("No se recibio el extra.");
    }

    var name = NormalizeNullable(request.Name);
    if (name is null)
    {
      return AjustesCommandResult.Fail("Captura el nombre del extra.");
    }

    if (request.Price < 0m)
    {
      return AjustesCommandResult.Fail("El precio del extra no puede ser negativo.");
    }

    var description = NormalizeNullable(request.Description);
    var extraId = request.ExtraId.GetValueOrDefault();

    const string duplicateSql = @"
SELECT TOP (1) ExtraID
FROM dbo.Extra
WHERE UPPER(LTRIM(RTRIM([Name]))) = UPPER(@name)
  AND ExtraID <> @extraId;";

    const string insertSql = @"
INSERT INTO dbo.Extra
(
    [Name],
    [Description],
    Price,
    IsActive,
    CreatedAtUtc,
    UpdatedAtUtc
)
OUTPUT INSERTED.ExtraID
VALUES
(
    @name,
    @description,
    @price,
    @isActive,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
);";

    const string updateSql = @"
UPDATE dbo.Extra
SET [Name] = @name,
    [Description] = @description,
    Price = @price,
    IsActive = @isActive,
    UpdatedAtUtc = SYSUTCDATETIME()
WHERE ExtraID = @extraId;";

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);

    var duplicateId = await connection.ExecuteScalarAsync<int?>(
        new CommandDefinition(duplicateSql, new { name, extraId }, cancellationToken: ct));
    if (duplicateId.HasValue)
    {
      return AjustesCommandResult.Fail("Ya existe un extra con ese nombre.");
    }

    var parameters = new
    {
      extraId,
      name,
      description,
      price = request.Price,
      isActive = request.IsActive
    };

    if (extraId <= 0)
    {
      var createdId = await connection.ExecuteScalarAsync<int>(
          new CommandDefinition(insertSql, parameters, cancellationToken: ct));
      return AjustesCommandResult.Ok("Extra guardado correctamente.", createdId);
    }

    var affected = await connection.ExecuteAsync(
        new CommandDefinition(updateSql, parameters, cancellationToken: ct));

    return affected == 0
        ? AjustesCommandResult.Fail("El extra seleccionado ya no existe.")
        : AjustesCommandResult.Ok("Extra guardado correctamente.", extraId);
  }

  public async Task<AjustesCommandResult> DeleteExtraCatalogItemAsync(int extraId, CancellationToken ct = default)
  {
    if (extraId <= 0)
    {
      return AjustesCommandResult.Fail("Selecciona un extra valido.");
    }

    const string sql = @"
UPDATE dbo.Extra
SET IsActive = 0,
    UpdatedAtUtc = SYSUTCDATETIME()
WHERE ExtraID = @extraId;";

    using var connection = new SqlConnection(_connectionString);
    var affected = await connection.ExecuteAsync(
        new CommandDefinition(sql, new { extraId }, cancellationToken: ct));

    return affected == 0
        ? AjustesCommandResult.Fail("El extra seleccionado ya no existe.")
        : AjustesCommandResult.Ok("Extra desactivado correctamente.", extraId);
  }

  public async Task<CfdiPolizaCuentaDefaultsDto> GetCfdiPolizaCuentaDefaultsAsync(string? rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeNullable(rfc);
    if (normalizedRfc is null)
    {
      return BuildEmptyCfdiPolizaCuentaDefaults(string.Empty);
    }

    const string sql = @"
SELECT
    d.CuentaClave,
    d.CuentaContableId,
    cc.RFC AS CuentaRfc,
    cc.Nivel1,
    cc.Nivel2,
    cc.Nivel3,
    cc.Descripcion AS CuentaDescripcion
FROM dbo.CfdiPolizaCuentaDefault AS d
INNER JOIN dbo.CuentasContables AS cc
    ON cc.id = d.CuentaContableId
WHERE d.Rfc = @rfc;";

    using var connection = new SqlConnection(_connectionString);
    var storedRows = await connection.QueryAsync<CfdiPolizaCuentaDefaultAccountDto>(
        new CommandDefinition(sql, new { rfc = normalizedRfc }, cancellationToken: ct));
    var rowsByKey = storedRows.ToDictionary(row => row.CuentaClave, StringComparer.OrdinalIgnoreCase);

    return new CfdiPolizaCuentaDefaultsDto
    {
      Rfc = normalizedRfc,
      Cuentas = CfdiPolizaCuentaDefaultRoles.Required
          .Select(role => rowsByKey.TryGetValue(role.CuentaClave, out var row)
              ? row
              : new CfdiPolizaCuentaDefaultAccountDto { CuentaClave = role.CuentaClave })
          .ToList()
    };
  }

  public async Task<AjustesCommandResult> SaveCfdiPolizaCuentaDefaultsAsync(
      CfdiPolizaCuentaDefaultsSaveRequest request,
      CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeNullable(request?.Rfc);
    if (normalizedRfc is null)
    {
      return AjustesCommandResult.Fail("Selecciona un RFC antes de guardar las cuentas CFDI.");
    }

    var normalizedItems = request!.Cuentas
        .Select(item => new CfdiPolizaCuentaDefaultSaveItem
        {
          CuentaClave = NormalizeRequired(item.CuentaClave).ToUpperInvariant(),
          CuentaContableId = item.CuentaContableId
        })
        .ToList();

    var validationError = ValidateCfdiPolizaCuentaDefaults(normalizedItems);
    if (validationError is not null)
    {
      return AjustesCommandResult.Fail(validationError);
    }

    var cuentaIds = normalizedItems
        .Select(item => item.CuentaContableId)
        .Distinct()
        .ToArray();

    const string accountSql = @"
SELECT id AS Id,
       RFC AS Rfc
FROM dbo.CuentasContables
WHERE id IN @cuentaIds;";

    using var connection = new SqlConnection(_connectionString);
    var accountRows = (await connection.QueryAsync<CuentaContableRfcRow>(
        new CommandDefinition(accountSql, new { cuentaIds }, cancellationToken: ct))).AsList();
    var accountsById = accountRows.ToDictionary(row => row.Id);

    foreach (var item in normalizedItems)
    {
      if (!accountsById.TryGetValue(item.CuentaContableId, out var account))
      {
        return AjustesCommandResult.Fail("Una de las cuentas seleccionadas ya no existe.");
      }

      if (!string.Equals(account.Rfc, normalizedRfc, StringComparison.OrdinalIgnoreCase))
      {
        var roleName = CfdiDefaultRolesByKey[item.CuentaClave].Nombre;
        return AjustesCommandResult.Fail($"La cuenta seleccionada para {roleName} no pertenece al RFC actual.");
      }
    }

    const string deleteSql = @"DELETE dbo.CfdiPolizaCuentaDefault WHERE Rfc = @rfc;";
    const string insertSql = @"
INSERT INTO dbo.CfdiPolizaCuentaDefault
(
    Rfc,
    CuentaClave,
    CuentaContableId,
    CreadoEn,
    ActualizadoEn
)
VALUES
(
    @rfc,
    @cuentaClave,
    @cuentaContableId,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
);";

    await connection.OpenAsync(ct);
    using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

    try
    {
      await connection.ExecuteAsync(new CommandDefinition(deleteSql, new { rfc = normalizedRfc }, tx, cancellationToken: ct));

      foreach (var item in normalizedItems.OrderBy(item => item.CuentaClave, StringComparer.OrdinalIgnoreCase))
      {
        await connection.ExecuteAsync(
            new CommandDefinition(
                insertSql,
                new
                {
                  rfc = normalizedRfc,
                  cuentaClave = item.CuentaClave,
                  cuentaContableId = item.CuentaContableId
                },
                tx,
                cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return AjustesCommandResult.Ok("Cuentas contables CFDI guardadas correctamente.");
    }
    catch
    {
      await RollbackQuietlyAsync(tx, ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<PlantillaContableListItemDto>> GetPlantillasAsync(
      string? rfc,
      string? search,
      bool includeInactive,
      CancellationToken ct = default)
  {
    const string sql = @"
SELECT
    p.PlantillaContableID AS PlantillaContableId,
    p.CategoriaID AS CategoriaId,
    p.Nombre,
    p.Descripcion,
    p.RFC AS Rfc,
    p.TipoPoliza,
    p.Activa,
    p.Origen,
    COUNT(CASE WHEN l.Activa = 1 THEN 1 END) AS LineCount,
    p.ActualizadaEn
FROM dbo.PlantillaContable AS p
LEFT JOIN dbo.PlantillaContableLinea AS l
    ON l.PlantillaContableID = p.PlantillaContableID
WHERE (@includeInactive = 1 OR p.Activa = 1)
  AND (@rfc IS NULL OR p.RFC = @rfc OR p.RFC IS NULL)
  AND
  (
      @search IS NULL
      OR p.Nombre LIKE @searchLike
      OR p.Descripcion LIKE @searchLike
      OR CONVERT(varchar(20), p.CategoriaID) LIKE @searchLike
  )
GROUP BY
    p.PlantillaContableID,
    p.CategoriaID,
    p.Nombre,
    p.Descripcion,
    p.RFC,
    p.TipoPoliza,
    p.Activa,
    p.Origen,
    p.ActualizadaEn
ORDER BY
    p.Activa DESC,
    p.Nombre ASC,
    p.PlantillaContableID ASC;";

    var normalizedRfc = NormalizeNullable(rfc);
    var normalizedSearch = NormalizeNullable(search);

    using var connection = new SqlConnection(_connectionString);
    var rows = await connection.QueryAsync<PlantillaContableListItemDto>(
        new CommandDefinition(
            sql,
            new
            {
              rfc = normalizedRfc,
              includeInactive,
              search = normalizedSearch,
              searchLike = normalizedSearch is null ? null : $"%{normalizedSearch}%"
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<PlantillaContableDetailDto?> GetPlantillaAsync(int plantillaContableId, CancellationToken ct = default)
  {
    const string sql = @"
SELECT TOP (1)
    p.PlantillaContableID AS PlantillaContableId,
    p.CategoriaID AS CategoriaId,
    p.Nombre,
    p.Descripcion,
    p.RFC AS Rfc,
    p.TipoPoliza,
    p.Activa,
    p.Origen,
    p.CreadaEn,
    p.ActualizadaEn
FROM dbo.PlantillaContable AS p
WHERE p.PlantillaContableID = @plantillaContableId;

SELECT
    l.PlantillaContableLineaID AS PlantillaContableLineaId,
    l.PlantillaContableID AS PlantillaContableId,
    l.Orden,
    l.CuentaContableID,
    cc.RFC AS CuentaRfc,
    cc.Nivel1,
    cc.Nivel2,
    cc.Nivel3,
    cc.Descripcion AS CuentaContable,
    l.Naturaleza,
    l.MontoTipo,
    l.Factor,
    l.ConceptoTipo,
    l.ConceptoFijo,
    l.Activa
FROM dbo.PlantillaContableLinea AS l
INNER JOIN dbo.CuentasContables AS cc
    ON cc.id = l.CuentaContableID
WHERE l.PlantillaContableID = @plantillaContableId
  AND l.Activa = 1
ORDER BY l.Orden, l.PlantillaContableLineaID;";

    using var connection = new SqlConnection(_connectionString);
    using var multi = await connection.QueryMultipleAsync(
        new CommandDefinition(sql, new { plantillaContableId }, cancellationToken: ct));

    var header = await multi.ReadFirstOrDefaultAsync<PlantillaContableDetailDto>();
    if (header is null)
    {
      return null;
    }

    var lines = (await multi.ReadAsync<PlantillaContableLineaDto>()).AsList();
    return header with { Lineas = lines };
  }

  public async Task<AjustesCommandResult> SavePlantillaAsync(PlantillaContableSaveRequest request, CancellationToken ct = default)
  {
    var validationError = ValidateSaveRequest(request);
    if (validationError is not null)
    {
      return AjustesCommandResult.Fail(validationError);
    }

    var activeLines = request.Lineas
        .OrderBy(line => line.Orden)
        .Select((line, index) => line with { Orden = index + 1 })
        .ToList();

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

    try
    {
      var plantillaId = request.PlantillaContableId.GetValueOrDefault();
      if (plantillaId <= 0)
      {
        plantillaId = await InsertPlantillaAsync(connection, tx, request, ct);
      }
      else
      {
        var affected = await UpdatePlantillaAsync(connection, tx, plantillaId, request, ct);
        if (affected == 0)
        {
          await tx.RollbackAsync(ct);
          return AjustesCommandResult.Fail("La plantilla seleccionada ya no existe.");
        }
      }

      await SaveLineasAsync(connection, tx, plantillaId, activeLines, ct);

      await tx.CommitAsync(ct);
      return AjustesCommandResult.Ok("Plantilla guardada correctamente.", plantillaId);
    }
    catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
    {
      await RollbackQuietlyAsync(tx, ct);
      return AjustesCommandResult.Fail("Ya existe una plantilla con ese codigo interno. Intenta guardar de nuevo.");
    }
    catch
    {
      await RollbackQuietlyAsync(tx, ct);
      throw;
    }
  }

  public async Task<AjustesCommandResult> DeletePlantillaAsync(int plantillaContableId, CancellationToken ct = default)
  {
    const string sql = @"
UPDATE dbo.PlantillaContable
SET Activa = 0,
    ActualizadaEn = SYSDATETIME()
WHERE PlantillaContableID = @plantillaContableId;

UPDATE dbo.PlantillaContableLinea
SET Activa = 0,
    ActualizadaEn = SYSDATETIME()
WHERE PlantillaContableID = @plantillaContableId;";

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    using var tx = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

    try
    {
      var affected = await connection.ExecuteAsync(
          new CommandDefinition(sql, new { plantillaContableId }, tx, cancellationToken: ct));

      await tx.CommitAsync(ct);
      return affected == 0
          ? AjustesCommandResult.Fail("La plantilla seleccionada ya no existe.")
          : AjustesCommandResult.Ok("Plantilla desactivada correctamente.", plantillaContableId);
    }
    catch
    {
      await RollbackQuietlyAsync(tx, ct);
      throw;
    }
  }

  private static async Task<int> InsertPlantillaAsync(
      SqlConnection connection,
      SqlTransaction tx,
      PlantillaContableSaveRequest request,
      CancellationToken ct)
  {
    const string sql = @"
DECLARE @CategoriaID int;

SELECT @CategoriaID = ISNULL(MAX(CategoriaID), 0) + 1
FROM dbo.PlantillaContable WITH (UPDLOCK, HOLDLOCK);

INSERT INTO dbo.PlantillaContable
(
    Nombre,
    Descripcion,
    CategoriaID,
    RFC,
    TipoPoliza,
    Activa,
    Origen
)
OUTPUT INSERTED.PlantillaContableID
VALUES
(
    @nombre,
    @descripcion,
    @CategoriaID,
    @rfc,
    @tipoPoliza,
    @activa,
    N'Manual'
);";

    return await connection.ExecuteScalarAsync<int>(
        new CommandDefinition(
            sql,
            new
            {
              nombre = request.Nombre.Trim(),
              descripcion = NormalizeNullable(request.Descripcion),
              rfc = NormalizeNullable(request.Rfc),
              tipoPoliza = NormalizeNullable(request.TipoPoliza),
              activa = request.Activa
            },
            tx,
            cancellationToken: ct));
  }

  private static async Task<int> UpdatePlantillaAsync(
      SqlConnection connection,
      SqlTransaction tx,
      int plantillaId,
      PlantillaContableSaveRequest request,
      CancellationToken ct)
  {
    const string sql = @"
UPDATE dbo.PlantillaContable
SET Nombre = @nombre,
    Descripcion = @descripcion,
    RFC = @rfc,
    TipoPoliza = @tipoPoliza,
    Activa = @activa,
    ActualizadaEn = SYSDATETIME()
WHERE PlantillaContableID = @plantillaId;";

    return await connection.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              plantillaId,
              nombre = request.Nombre.Trim(),
              descripcion = NormalizeNullable(request.Descripcion),
              rfc = NormalizeNullable(request.Rfc),
              tipoPoliza = NormalizeNullable(request.TipoPoliza),
              activa = request.Activa
            },
            tx,
            cancellationToken: ct));
  }

  private static async Task SaveLineasAsync(
      SqlConnection connection,
      SqlTransaction tx,
      int plantillaId,
      IReadOnlyList<PlantillaContableLineaSaveRequest> lines,
      CancellationToken ct)
  {
    var existingLineIds = lines
        .Where(line => line.PlantillaContableLineaId.GetValueOrDefault() > 0)
        .Select(line => line.PlantillaContableLineaId!.Value)
        .Distinct()
        .ToArray();

    if (existingLineIds.Length == 0)
    {
      await connection.ExecuteAsync(
          new CommandDefinition(
              @"UPDATE dbo.PlantillaContableLinea
SET Activa = 0,
    ActualizadaEn = SYSDATETIME()
WHERE PlantillaContableID = @plantillaId;",
              new { plantillaId },
              tx,
              cancellationToken: ct));
    }
    else
    {
      await connection.ExecuteAsync(
          new CommandDefinition(
              @"UPDATE dbo.PlantillaContableLinea
SET Activa = 0,
    ActualizadaEn = SYSDATETIME()
WHERE PlantillaContableID = @plantillaId
  AND PlantillaContableLineaID NOT IN @lineIds;",
              new { plantillaId, lineIds = existingLineIds },
              tx,
              cancellationToken: ct));
    }

    foreach (var line in lines)
    {
      if (line.PlantillaContableLineaId.GetValueOrDefault() > 0)
      {
        await UpdateLineaAsync(connection, tx, plantillaId, line, ct);
      }
      else
      {
        await InsertLineaAsync(connection, tx, plantillaId, line, ct);
      }
    }
  }

  private static Task InsertLineaAsync(
      SqlConnection connection,
      SqlTransaction tx,
      int plantillaId,
      PlantillaContableLineaSaveRequest line,
      CancellationToken ct)
  {
    const string sql = @"
INSERT INTO dbo.PlantillaContableLinea
(
    PlantillaContableID,
    Orden,
    CuentaContableID,
    Naturaleza,
    MontoTipo,
    Factor,
    ConceptoTipo,
    ConceptoFijo,
    Activa
)
VALUES
(
    @plantillaId,
    @orden,
    @cuentaContableId,
    @naturaleza,
    @montoTipo,
    @factor,
    @conceptoTipo,
    @conceptoFijo,
    1
);";

    return connection.ExecuteAsync(
        new CommandDefinition(
            sql,
            BuildLineParameters(plantillaId, line),
            tx,
            cancellationToken: ct));
  }

  private static Task UpdateLineaAsync(
      SqlConnection connection,
      SqlTransaction tx,
      int plantillaId,
      PlantillaContableLineaSaveRequest line,
      CancellationToken ct)
  {
    const string sql = @"
UPDATE dbo.PlantillaContableLinea
SET Orden = @orden,
    CuentaContableID = @cuentaContableId,
    Naturaleza = @naturaleza,
    MontoTipo = @montoTipo,
    Factor = @factor,
    ConceptoTipo = @conceptoTipo,
    ConceptoFijo = @conceptoFijo,
    Activa = 1,
    ActualizadaEn = SYSDATETIME()
WHERE PlantillaContableLineaID = @plantillaContableLineaId
  AND PlantillaContableID = @plantillaId;";

    return connection.ExecuteAsync(
        new CommandDefinition(
            sql,
            BuildLineParameters(plantillaId, line),
            tx,
            cancellationToken: ct));
  }

  private static object BuildLineParameters(int plantillaId, PlantillaContableLineaSaveRequest line)
    => new
    {
      plantillaId,
      plantillaContableLineaId = line.PlantillaContableLineaId,
      orden = line.Orden,
      cuentaContableId = line.CuentaContableId,
      naturaleza = NormalizeRequired(line.Naturaleza).ToUpperInvariant(),
      montoTipo = NormalizeRequired(line.MontoTipo).ToUpperInvariant(),
      factor = line.Factor,
      conceptoTipo = NormalizeRequired(line.ConceptoTipo).ToUpperInvariant(),
      conceptoFijo = NormalizeNullable(line.ConceptoFijo)
    };

  private static string? ValidateSaveRequest(PlantillaContableSaveRequest request)
  {
    if (request is null)
    {
      return "No se recibio la plantilla.";
    }

    if (string.IsNullOrWhiteSpace(request.Nombre))
    {
      return "Captura el nombre de la plantilla.";
    }

    if (request.Lineas.Count == 0)
    {
      return "Agrega al menos una linea a la plantilla.";
    }

    var hasDebe = false;
    var hasHaber = false;
    foreach (var line in request.Lineas)
    {
      var naturaleza = NormalizeRequired(line.Naturaleza).ToUpperInvariant();
      var montoTipo = NormalizeRequired(line.MontoTipo).ToUpperInvariant();
      var conceptoTipo = NormalizeRequired(line.ConceptoTipo).ToUpperInvariant();

      if (line.CuentaContableId <= 0)
      {
        return "Todas las lineas deben tener una cuenta contable.";
      }

      if (!ValidNaturalezas.Contains(naturaleza))
      {
        return "La naturaleza de una linea no es valida.";
      }

      if (!ValidMontoTipos.Contains(montoTipo))
      {
        return "El tipo de monto de una linea no es valido.";
      }

      if (!ValidConceptoTipos.Contains(conceptoTipo))
      {
        return "El tipo de concepto de una linea no es valido.";
      }

      if (line.Factor <= 0)
      {
        return "El factor de las lineas debe ser mayor a cero.";
      }

      if (conceptoTipo == "FIJO" && string.IsNullOrWhiteSpace(line.ConceptoFijo))
      {
        return "Captura el concepto fijo de las lineas que no usan el concepto de la poliza.";
      }

      hasDebe |= naturaleza == "DEBE";
      hasHaber |= naturaleza == "HABER";
    }

    if (!hasDebe || !hasHaber)
    {
      return "La plantilla debe tener al menos una linea de Debe y una de Haber.";
    }

    return null;
  }

  private static CfdiPolizaCuentaDefaultsDto BuildEmptyCfdiPolizaCuentaDefaults(string rfc)
    => new()
    {
      Rfc = rfc,
      Cuentas = CfdiPolizaCuentaDefaultRoles.Required
          .Select(role => new CfdiPolizaCuentaDefaultAccountDto { CuentaClave = role.CuentaClave })
          .ToList()
    };

  private static string? ValidateCfdiPolizaCuentaDefaults(IReadOnlyList<CfdiPolizaCuentaDefaultSaveItem> items)
  {
    var invalidRole = items.FirstOrDefault(item => !CfdiPolizaCuentaDefaultRoles.IsRequired(item.CuentaClave));
    if (invalidRole is not null)
    {
      return $"La cuenta CFDI '{invalidRole.CuentaClave}' no es valida.";
    }

    var duplicatedRole = items
        .GroupBy(item => item.CuentaClave, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(group => group.Count() > 1);
    if (duplicatedRole is not null)
    {
      return $"La cuenta CFDI '{duplicatedRole.Key}' esta duplicada.";
    }

    var missingRoles = CfdiPolizaCuentaDefaultRoles.Required
        .Where(role => !items.Any(item => string.Equals(item.CuentaClave, role.CuentaClave, StringComparison.OrdinalIgnoreCase)))
        .Select(role => role.Nombre)
        .ToList();
    if (missingRoles.Count > 0)
    {
      return $"Captura todas las cuentas contables CFDI: {string.Join(", ", missingRoles)}.";
    }

    if (items.Any(item => item.CuentaContableId <= 0))
    {
      return "Todas las cuentas CFDI deben tener una cuenta contable seleccionada.";
    }

    return null;
  }

  private static string? NormalizeNullable(string? value)
  {
    var trimmed = value?.Trim();
    return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
  }

  private static int NormalizeApOccurrenceNotificationDays(string? value)
    => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
      ? NormalizeApOccurrenceNotificationDays(parsed)
      : AjustesGeneralSettingsDto.DefaultApOccurrenceNotificationDays;

  private static int NormalizeApOccurrenceNotificationDays(int value)
    => Math.Clamp(
      value,
      AjustesGeneralSettingsDto.MinApOccurrenceNotificationDays,
      AjustesGeneralSettingsDto.MaxApOccurrenceNotificationDays);

  private static string NormalizeRequired(string? value)
    => NormalizeNullable(value) ?? string.Empty;

  private static async Task RollbackQuietlyAsync(SqlTransaction tx, CancellationToken ct)
  {
    try
    {
      await tx.RollbackAsync(ct);
    }
    catch
    {
      // ignored
    }
  }

  private sealed record CuentaContableRfcRow
  {
    public int Id { get; init; }
    public string Rfc { get; init; } = string.Empty;
  }
}
