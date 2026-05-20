using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Ajustes;

namespace OrionERP.Infrastructure.Features.Ajustes;

public sealed class AjustesService : IAjustesService
{
  private const string ApOccurrenceNotificationDaysParameter = "CxcrApNotificationDays";

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
}
