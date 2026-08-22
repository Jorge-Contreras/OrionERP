using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Ajustes;
using OrionERP.Application.Features.Ajustes.Catalogos;

namespace OrionERP.Infrastructure.Features.Ajustes.Catalogos;

/// <summary>
/// Dapper implementation of the catalog hub.
///
/// Every catalog shares one set of statements, composed from a hard-coded
/// descriptor. The descriptor is selected by <see cref="CatalogoKey"/> and never
/// built from caller input, so no identifier reaches the SQL text from outside
/// this file. Values always travel as parameters.
/// </summary>
public sealed class CatalogoService : ICatalogoService
{
  /// <summary>
  /// SAT c_FormaPago. These claves are published by the SAT, so the hub allows
  /// their description to be corrected but refuses to delete or renumber them.
  /// The same list is restated in 20260821_orion_training_catalogos.sql, in the
  /// sanitizer's preserved-catalog clamp, and in the attestation.
  /// </summary>
  private static readonly HashSet<string> SatFormaPagoClaves = new(StringComparer.Ordinal)
  {
    "01", "02", "03", "04", "05", "06", "08", "12", "13", "14", "15",
    "17", "23", "24", "25", "26", "27", "28", "29", "30", "31", "99"
  };

  /// <summary>
  /// Table and column names per catalog. Private, constant, and the only source
  /// of identifiers used in the composed SQL below.
  /// </summary>
  private sealed record CatalogoTabla
  {
    public required CatalogoDescriptorDto Descriptor { get; init; }
    public required string Tabla { get; init; }
    public required string LlaveColumna { get; init; }
    public required bool LlaveEsTexto { get; init; }
    public string? CodigoColumna { get; init; }
    public required string NombreColumna { get; init; }
    public string? OrdenColumna { get; init; }
    public string? ActivoColumna { get; init; }
    public string? RfcColumna { get; init; }

    /// <summary>
    /// Counts records that point at one row. Takes @id and @rfc. Used to show
    /// usage in the list, and to refuse a delete that would otherwise surface as
    /// a foreign-key violation.
    /// </summary>
    public required string ReferenciasSql { get; init; }

    /// <summary>Extra NOT NULL columns an insert must supply, as a literal fragment.</summary>
    public string? ColumnasExtraInsert { get; init; }
    public string? ValoresExtraInsert { get; init; }
  }

  private static readonly IReadOnlyDictionary<CatalogoKey, CatalogoTabla> Catalogos =
    new Dictionary<CatalogoKey, CatalogoTabla>
    {
      [CatalogoKey.FormasPago] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.FormasPago,
          Titulo = "Formas de pago",
          Descripcion = "Catálogo SAT c_FormaPago. Alimenta el selector de forma de pago en transacciones y CFDI. Las 22 claves publicadas por el SAT pueden reescribirse pero no eliminarse.",
          EsPorRfc = false,
          TieneCodigo = true,
          CodigoEtiqueta = "Clave",
          NombreEtiqueta = "Descripción",
          CodigoEsLlave = true,
          TieneOrden = false,
          TieneActivo = false,
          TieneFilasProtegidas = true
        },
        Tabla = "dbo.Formas_Pago",
        LlaveColumna = "Clave",
        LlaveEsTexto = true,
        CodigoColumna = "Clave",
        NombreColumna = "Descripcion",
        ReferenciasSql = "SELECT COUNT(*) FROM dbo.Transacciones WHERE Forma_Pago = @id"
      },
      [CatalogoKey.Proyectos] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.Proyectos,
          Titulo = "Proyectos",
          Descripcion = "Alimenta el buscador de proyecto en el editor de transacciones.",
          EsPorRfc = true,
          TieneCodigo = false,
          NombreEtiqueta = "Descripción",
          CodigoEsLlave = false,
          TieneOrden = false,
          TieneActivo = false,
          TieneFilasProtegidas = false
        },
        Tabla = "dbo.Actividad",
        LlaveColumna = "ID",
        LlaveEsTexto = false,
        NombreColumna = "Descripcion",
        RfcColumna = "RFC",
        ReferenciasSql = "SELECT COUNT(*) FROM dbo.Transacciones WHERE ProyectoID = @id AND RFC = @rfc",
        // dbo.Actividad predates the migration folder and requires nine columns
        // no query in the product reads. Without them an insert fails on a NULL
        // the user was never asked for.
        ColumnasExtraInsert =
          "Fecha_Inicio, Fecha_Final, Ubicacion, RazonSocial, Departamento, Tipo_Proyecto, Cliente, Asignacion, Estatus",
        ValoresExtraInsert =
          "CONVERT(date, SYSUTCDATETIME()), DATEADD(year, 1, CONVERT(date, SYSUTCDATETIME())), 'Por Designar', '', '', '', '', 0, N'ACTIVO'"
      },
      [CatalogoKey.Compras] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.Compras,
          Titulo = "Compras",
          Descripcion = "Alimenta el selector de compra en el editor de transacciones.",
          EsPorRfc = true,
          TieneCodigo = false,
          NombreEtiqueta = "Descripción",
          CodigoEsLlave = false,
          TieneOrden = false,
          TieneActivo = false,
          TieneFilasProtegidas = false
        },
        Tabla = "dbo.Compra",
        LlaveColumna = "ID",
        LlaveEsTexto = false,
        NombreColumna = "Descripcion",
        RfcColumna = "RFC",
        ReferenciasSql = "SELECT COUNT(*) FROM dbo.Transacciones WHERE CompraID = @id AND RFC = @rfc",
        // Proveedor_ID is NOT NULL with no default and no foreign key. Zero reads
        // as "sin proveedor", which is what a catalog entry means here.
        ColumnasExtraInsert = "Proveedor_ID",
        ValoresExtraInsert = "0"
      },
      [CatalogoKey.Servicios] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.Servicios,
          Titulo = "Servicios",
          Descripcion = "Alimenta el selector de servicio en el editor de transacciones.",
          EsPorRfc = true,
          TieneCodigo = false,
          NombreEtiqueta = "Descripción",
          CodigoEsLlave = false,
          TieneOrden = false,
          TieneActivo = false,
          TieneFilasProtegidas = false
        },
        Tabla = "dbo.Servicios",
        LlaveColumna = "id",
        LlaveEsTexto = false,
        NombreColumna = "Descripcion",
        RfcColumna = "RFC",
        ReferenciasSql = "SELECT COUNT(*) FROM dbo.Transacciones WHERE ServicioID = @id AND RFC = @rfc",
        // RazonSocial and Entidad_Cobro_ID are NOT NULL with no default. The
        // rowversion column must stay out of the insert entirely.
        ColumnasExtraInsert = "RazonSocial, Entidad_Cobro_ID",
        ValoresExtraInsert = "'', 0"
      },
      [CatalogoKey.CategoriasOrdenTrabajo] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.CategoriasOrdenTrabajo,
          Titulo = "Categorías de orden de trabajo",
          Descripcion = "Clasifica órdenes de trabajo y sus plantillas. Desactivar una categoría la oculta sin afectar las órdenes existentes.",
          EsPorRfc = false,
          TieneCodigo = true,
          CodigoEsLlave = false,
          TieneOrden = true,
          TieneActivo = true,
          TieneFilasProtegidas = false
        },
        Tabla = "dbo.OrdenTrabajoCategoria",
        LlaveColumna = "Id",
        LlaveEsTexto = false,
        CodigoColumna = "Codigo",
        NombreColumna = "Nombre",
        OrdenColumna = "Orden",
        ActivoColumna = "Activa",
        ReferenciasSql = @"
SELECT (SELECT COUNT(*) FROM dbo.OrdenTrabajo WHERE CategoriaId = @id)
     + (SELECT COUNT(*) FROM dbo.OrdenTrabajoPlantilla WHERE CategoriaId = @id)"
      },
      [CatalogoKey.Alergenos] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.Alergenos,
          Titulo = "Alérgenos",
          Descripcion = "Se etiquetan sobre materiales y recetas del módulo de restaurante.",
          EsPorRfc = false,
          TieneCodigo = true,
          CodigoEsLlave = false,
          TieneOrden = false,
          TieneActivo = true,
          TieneFilasProtegidas = false
        },
        Tabla = "logistica.Allergen",
        LlaveColumna = "Id",
        LlaveEsTexto = false,
        CodigoColumna = "Code",
        NombreColumna = "Name",
        ActivoColumna = "IsActive",
        ReferenciasSql = "SELECT COUNT(*) FROM logistica.MaterialAllergen WHERE AllergenId = @id"
      },
      [CatalogoKey.Arrendadores] = new()
      {
        Descriptor = new CatalogoDescriptorDto
        {
          Key = CatalogoKey.Arrendadores,
          Titulo = "Arrendadores",
          Descripcion = "Propietarios de habitaciones. Alimentan el estado de cuenta de arrendadores a través de ROOM.OWNER_ID.",
          EsPorRfc = false,
          TieneCodigo = false,
          NombreEtiqueta = "Razón social",
          CodigoEsLlave = false,
          TieneOrden = false,
          TieneActivo = false,
          TieneFilasProtegidas = false
        },
        Tabla = "dbo.Proveedores",
        LlaveColumna = "id",
        LlaveEsTexto = false,
        NombreColumna = "RazonSocial",
        ReferenciasSql = "SELECT COUNT(*) FROM dbo.ROOM WHERE OWNER_ID = @id",
        // Address and contact columns are NOT NULL with no default. Blanks let an
        // arrendador be registered here and completed in the module that owns it.
        ColumnasExtraInsert = "Calle, Colonia, Ciudad, Estado, CPostal, Giro, Tel",
        ValoresExtraInsert = "'', '', '', '', '', '', ''"
      }
    };

  private readonly string _connectionString;

  public CatalogoService(IConfiguration configuration)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
        ?? throw new InvalidOperationException("Missing connection string 'OrionDb'.");
  }

  public IReadOnlyList<CatalogoDescriptorDto> GetDescriptors()
    => Catalogos.Values.Select(catalogo => catalogo.Descriptor).ToList();

  public async Task<IReadOnlyList<CatalogoItemDto>> GetItemsAsync(
      CatalogoKey key,
      string? rfc,
      string? search,
      bool includeInactive,
      CancellationToken ct = default)
  {
    var catalogo = Resolve(key);
    var normalizedRfc = NormalizeNullable(rfc);
    if (catalogo.RfcColumna is not null && normalizedRfc is null)
    {
      return Array.Empty<CatalogoItemDto>();
    }

    var codigo = catalogo.CodigoColumna is null ? "NULL" : $"CONVERT(nvarchar(100), item.[{catalogo.CodigoColumna}])";
    var orden = catalogo.OrdenColumna is null ? "NULL" : $"item.[{catalogo.OrdenColumna}]";
    var activo = catalogo.ActivoColumna is null ? "CONVERT(bit, 1)" : $"item.[{catalogo.ActivoColumna}]";

    var filters = new List<string>();
    if (catalogo.RfcColumna is not null)
    {
      filters.Add($"item.[{catalogo.RfcColumna}] = @rfc");
    }
    if (catalogo.ActivoColumna is not null && !includeInactive)
    {
      filters.Add($"item.[{catalogo.ActivoColumna}] = 1");
    }
    if (!string.IsNullOrWhiteSpace(search))
    {
      filters.Add(catalogo.CodigoColumna is null
        ? $"item.[{catalogo.NombreColumna}] LIKE @search"
        : $"(item.[{catalogo.NombreColumna}] LIKE @search OR item.[{catalogo.CodigoColumna}] LIKE @search)");
    }

    var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);

    // The reference count is correlated per row, so the list can show usage and
    // the page can disable a delete before the user attempts it.
    var referencias = catalogo.ReferenciasSql
      .Replace("@id", $"item.[{catalogo.LlaveColumna}]", StringComparison.Ordinal)
      .Replace("@rfc", "@rfc", StringComparison.Ordinal);

    var sql = $@"
SELECT
    CONVERT(nvarchar(50), item.[{catalogo.LlaveColumna}]) AS Id,
    {codigo} AS Codigo,
    CONVERT(nvarchar(400), item.[{catalogo.NombreColumna}]) AS Nombre,
    {orden} AS Orden,
    {activo} AS Activo,
    ({referencias}) AS Referencias
FROM {catalogo.Tabla} AS item
{where}
ORDER BY {(catalogo.OrdenColumna is not null ? $"item.[{catalogo.OrdenColumna}], " : string.Empty)}{(catalogo.CodigoColumna is not null ? $"item.[{catalogo.CodigoColumna}], " : string.Empty)}item.[{catalogo.NombreColumna}];";

    using var connection = new SqlConnection(_connectionString);
    var rows = await connection.QueryAsync<CatalogoRow>(
        new CommandDefinition(
            sql,
            new { rfc = normalizedRfc, search = $"%{search?.Trim()}%" },
            cancellationToken: ct));

    return rows
      .Select(row => new CatalogoItemDto
      {
        Id = row.Id,
        Codigo = row.Codigo,
        Nombre = row.Nombre,
        Orden = row.Orden,
        Activo = row.Activo,
        EsProtegido = IsProtected(key, row.Id),
        Referencias = row.Referencias
      })
      .ToList();
  }

  public async Task<AjustesCommandResult> SaveItemAsync(CatalogoSaveRequest request, CancellationToken ct = default)
  {
    var catalogo = Resolve(request.Key);
    var descriptor = catalogo.Descriptor;

    var nombre = NormalizeNullable(request.Nombre);
    if (nombre is null)
    {
      return AjustesCommandResult.Fail($"{descriptor.NombreEtiqueta} es obligatorio.");
    }

    var codigo = NormalizeNullable(request.Codigo);
    if (descriptor.TieneCodigo && codigo is null)
    {
      return AjustesCommandResult.Fail($"{descriptor.CodigoEtiqueta} es obligatorio.");
    }

    string? rfc = null;
    if (catalogo.RfcColumna is not null)
    {
      rfc = NormalizeNullable(request.Rfc);
      if (rfc is null)
      {
        return AjustesCommandResult.Fail("Selecciona un RFC antes de guardar en este catálogo.");
      }
    }

    var id = NormalizeNullable(request.Id);
    var isInsert = id is null;

    // A SAT clave is the row's identity, so renaming one would mean deleting a
    // published key and inventing another. The description stays editable.
    if (descriptor.CodigoEsLlave && !isInsert && !string.Equals(id, codigo, StringComparison.Ordinal))
    {
      return AjustesCommandResult.Fail(
        $"La clave {id} no puede renumerarse. Elimina el registro y crea uno nuevo si realmente cambió.");
    }

    if (!isInsert && !catalogo.LlaveEsTexto && !int.TryParse(id, out _))
    {
      return AjustesCommandResult.Fail("El registro seleccionado ya no es válido.");
    }

    if (descriptor.CodigoEsLlave && isInsert && IsProtected(request.Key, codigo!))
    {
      return AjustesCommandResult.Fail(
        $"La clave {codigo} pertenece al catálogo SAT y ya debería existir. Búscala en la lista en lugar de crearla.");
    }

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);

    // Duplicate probe on the natural key: the code when there is one, otherwise
    // the name. Tenant-scoped catalogs compare inside their own RFC only.
    var duplicateColumn = catalogo.CodigoColumna ?? catalogo.NombreColumna;
    var duplicateValue = catalogo.CodigoColumna is null ? nombre : codigo!;
    var duplicateSql = $@"
SELECT TOP (1) CONVERT(nvarchar(50), item.[{catalogo.LlaveColumna}])
FROM {catalogo.Tabla} AS item
WHERE UPPER(LTRIM(RTRIM(CONVERT(nvarchar(400), item.[{duplicateColumn}])))) = UPPER(@value)
{(catalogo.RfcColumna is not null ? $"  AND item.[{catalogo.RfcColumna}] = @rfc" : string.Empty)}
  AND (@id IS NULL OR CONVERT(nvarchar(50), item.[{catalogo.LlaveColumna}]) <> @id);";

    var duplicate = await connection.ExecuteScalarAsync<string?>(
        new CommandDefinition(duplicateSql, new { value = duplicateValue, rfc, id }, cancellationToken: ct));
    if (duplicate is not null)
    {
      return AjustesCommandResult.Fail($"Ya existe un registro con ese valor ({duplicateValue}).");
    }

    if (isInsert)
    {
      var columns = new List<string>();
      var values = new List<string>();

      if (catalogo.LlaveEsTexto)
      {
        // A text key is supplied by the caller rather than generated.
        columns.Add($"[{catalogo.LlaveColumna}]");
        values.Add("@codigo");
      }
      if (catalogo.CodigoColumna is not null && !catalogo.LlaveEsTexto)
      {
        columns.Add($"[{catalogo.CodigoColumna}]");
        values.Add("@codigo");
      }
      columns.Add($"[{catalogo.NombreColumna}]");
      values.Add("@nombre");
      if (catalogo.OrdenColumna is not null)
      {
        columns.Add($"[{catalogo.OrdenColumna}]");
        values.Add("@orden");
      }
      if (catalogo.ActivoColumna is not null)
      {
        columns.Add($"[{catalogo.ActivoColumna}]");
        values.Add("@activo");
      }
      if (catalogo.RfcColumna is not null)
      {
        columns.Add($"[{catalogo.RfcColumna}]");
        values.Add("@rfc");
      }
      if (catalogo.ColumnasExtraInsert is not null)
      {
        columns.Add(catalogo.ColumnasExtraInsert);
        values.Add(catalogo.ValoresExtraInsert!);
      }

      // dbo.Actividad, dbo.Compra, dbo.Servicios and dbo.Proveedores predate the
      // migration folder and may or may not have an identity key, so the key is
      // generated here when the table does not generate it itself.
      var needsGeneratedKey = !catalogo.LlaveEsTexto;
      var insertSql = needsGeneratedKey
        ? $@"
DECLARE @hasIdentity bit = CONVERT(bit, ISNULL(OBJECTPROPERTY(OBJECT_ID(N'{catalogo.Tabla}'), 'TableHasIdentity'), 0));

IF @hasIdentity = 1
BEGIN
    INSERT INTO {catalogo.Tabla} ({string.Join(", ", columns)})
    OUTPUT CONVERT(nvarchar(50), INSERTED.[{catalogo.LlaveColumna}])
    VALUES ({string.Join(", ", values)});
END
ELSE
BEGIN
    DECLARE @nextId int;
    SELECT @nextId = ISNULL(MAX([{catalogo.LlaveColumna}]), 0) + 1
    FROM {catalogo.Tabla} WITH (UPDLOCK, HOLDLOCK);

    INSERT INTO {catalogo.Tabla} ([{catalogo.LlaveColumna}], {string.Join(", ", columns)})
    OUTPUT CONVERT(nvarchar(50), INSERTED.[{catalogo.LlaveColumna}])
    VALUES (@nextId, {string.Join(", ", values)});
END;"
        : $@"
INSERT INTO {catalogo.Tabla} ({string.Join(", ", columns)})
VALUES ({string.Join(", ", values)});
SELECT @codigo;";

      var newId = await connection.ExecuteScalarAsync<string?>(
          new CommandDefinition(
              insertSql,
              new { codigo, nombre, orden = request.Orden ?? 0, activo = request.Activo, rfc },
              cancellationToken: ct));

      return AjustesCommandResult.Ok(
        $"{descriptor.Titulo}: registro creado correctamente.",
        int.TryParse(newId, out var parsedNew) ? parsedNew : null);
    }

    var assignments = new List<string> { $"[{catalogo.NombreColumna}] = @nombre" };
    if (catalogo.CodigoColumna is not null && !catalogo.LlaveEsTexto)
    {
      assignments.Add($"[{catalogo.CodigoColumna}] = @codigo");
    }
    if (catalogo.OrdenColumna is not null)
    {
      assignments.Add($"[{catalogo.OrdenColumna}] = @orden");
    }
    if (catalogo.ActivoColumna is not null)
    {
      assignments.Add($"[{catalogo.ActivoColumna}] = @activo");
    }

    var updateSql = $@"
UPDATE {catalogo.Tabla}
SET {string.Join(", ", assignments)}
WHERE CONVERT(nvarchar(50), [{catalogo.LlaveColumna}]) = @id
{(catalogo.RfcColumna is not null ? $"  AND [{catalogo.RfcColumna}] = @rfc" : string.Empty)};";

    var affected = await connection.ExecuteAsync(
        new CommandDefinition(
            updateSql,
            new { id, codigo, nombre, orden = request.Orden ?? 0, activo = request.Activo, rfc },
            cancellationToken: ct));

    return affected == 0
      ? AjustesCommandResult.Fail("El registro seleccionado ya no existe.")
      : AjustesCommandResult.Ok(
          $"{descriptor.Titulo}: registro actualizado correctamente.",
          int.TryParse(id, out var parsed) ? parsed : null);
  }

  public async Task<AjustesCommandResult> DeleteItemAsync(
      CatalogoKey key,
      string id,
      string? rfc,
      CancellationToken ct = default)
  {
    var catalogo = Resolve(key);
    var descriptor = catalogo.Descriptor;

    var normalizedId = NormalizeNullable(id);
    if (normalizedId is null)
    {
      return AjustesCommandResult.Fail("Selecciona un registro antes de eliminar.");
    }

    if (IsProtected(key, normalizedId))
    {
      return AjustesCommandResult.Fail(
        $"La clave {normalizedId} pertenece al catálogo SAT c_FormaPago y no puede eliminarse. Puedes corregir su descripción.");
    }

    string? normalizedRfc = null;
    if (catalogo.RfcColumna is not null)
    {
      normalizedRfc = NormalizeNullable(rfc);
      if (normalizedRfc is null)
      {
        return AjustesCommandResult.Fail("Selecciona un RFC antes de eliminar en este catálogo.");
      }
    }

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);

    // Deactivation is always safe, so the referential check only gates a real
    // delete. Reporting the count beats letting SQL Server raise error 547.
    if (catalogo.ActivoColumna is null)
    {
      var references = await connection.ExecuteScalarAsync<int>(
          new CommandDefinition(
              catalogo.ReferenciasSql,
              new { id = normalizedId, rfc = normalizedRfc },
              cancellationToken: ct));

      if (references > 0)
      {
        return AjustesCommandResult.Fail(
          $"No se puede eliminar: {references} registro(s) siguen usando este valor.");
      }

      var deleteSql = $@"
DELETE FROM {catalogo.Tabla}
WHERE CONVERT(nvarchar(50), [{catalogo.LlaveColumna}]) = @id
{(catalogo.RfcColumna is not null ? $"  AND [{catalogo.RfcColumna}] = @rfc" : string.Empty)};";

      var removed = await connection.ExecuteAsync(
          new CommandDefinition(deleteSql, new { id = normalizedId, rfc = normalizedRfc }, cancellationToken: ct));

      return removed == 0
        ? AjustesCommandResult.Fail("El registro seleccionado ya no existe.")
        : AjustesCommandResult.Ok($"{descriptor.Titulo}: registro eliminado correctamente.");
    }

    var deactivateSql = $@"
UPDATE {catalogo.Tabla}
SET [{catalogo.ActivoColumna}] = 0
WHERE CONVERT(nvarchar(50), [{catalogo.LlaveColumna}]) = @id
{(catalogo.RfcColumna is not null ? $"  AND [{catalogo.RfcColumna}] = @rfc" : string.Empty)};";

    var deactivated = await connection.ExecuteAsync(
        new CommandDefinition(deactivateSql, new { id = normalizedId, rfc = normalizedRfc }, cancellationToken: ct));

    return deactivated == 0
      ? AjustesCommandResult.Fail("El registro seleccionado ya no existe.")
      : AjustesCommandResult.Ok($"{descriptor.Titulo}: registro desactivado correctamente.");
  }

  public async Task<IReadOnlyList<CuentaContableNodeDto>> GetCuentasAsync(
      string rfc,
      string? search,
      CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeNullable(rfc);
    if (normalizedRfc is null)
    {
      return Array.Empty<CuentaContableNodeDto>();
    }

    // Hijos counts the accounts one level below this node, which is what makes a
    // header undeletable. Referencias counts everything that points at the
    // account itself.
    const string sql = @"
SELECT
    account.id           AS Id,
    account.Nivel1       AS Nivel1,
    account.Nivel2       AS Nivel2,
    account.Nivel3       AS Nivel3,
    account.Descripcion  AS Descripcion,
    (
        SELECT COUNT(*)
        FROM dbo.CuentasContables child
        WHERE child.RFC = account.RFC
          AND child.Nivel1 = account.Nivel1
          AND child.id <> account.id
          AND
          (
              (account.Nivel2 = '00' AND account.Nivel3 = '00')
              OR (account.Nivel3 = '00' AND child.Nivel2 = account.Nivel2 AND child.Nivel3 <> '00')
          )
    ) AS Hijos,
    (
        (SELECT COUNT(*) FROM dbo.PlantillaContableLinea line WHERE line.CuentaContableID = account.id)
      + (SELECT COUNT(*) FROM dbo.CfdiPolizaCuentaDefault fallback WHERE fallback.CuentaContableId = account.id)
      + (SELECT COUNT(*) FROM bancos.Cuentas_Banco bank WHERE bank.Cuenta_Contable_ID = account.id)
    ) AS Referencias
FROM dbo.CuentasContables AS account
WHERE account.RFC = @rfc
  AND (@search IS NULL
       OR account.Descripcion LIKE @search
       OR account.Nivel1 + '-' + account.Nivel2 + '-' + account.Nivel3 LIKE @search)
ORDER BY account.Nivel1, account.Nivel2, account.Nivel3;";

    using var connection = new SqlConnection(_connectionString);
    var rows = await connection.QueryAsync<CuentaContableNodeDto>(
        new CommandDefinition(
            sql,
            new { rfc = normalizedRfc, search = string.IsNullOrWhiteSpace(search) ? null : $"%{search.Trim()}%" },
            cancellationToken: ct));

    return rows.ToList();
  }

  public async Task<AjustesCommandResult> SaveCuentaAsync(
      CuentaContableSaveRequest request,
      CancellationToken ct = default)
  {
    var rfc = NormalizeNullable(request.Rfc);
    if (rfc is null)
    {
      return AjustesCommandResult.Fail("Selecciona un RFC antes de guardar una cuenta.");
    }

    var nivel1 = NormalizeNullable(request.Nivel1);
    if (nivel1 is null)
    {
      return AjustesCommandResult.Fail("Nivel 1 es obligatorio.");
    }

    var nivel2 = NormalizeTwoDigits(request.Nivel2);
    var nivel3 = NormalizeTwoDigits(request.Nivel3);

    var descripcion = NormalizeNullable(request.Descripcion);
    if (descripcion is null)
    {
      return AjustesCommandResult.Fail("La descripción es obligatoria.");
    }

    // '00' is the sentinel that marks a header. A Nivel1 header is 00/00 and a
    // Nivel2 header is Nivel3='00'; a leaf can never reuse the sentinel.
    if (nivel2 == "00" && nivel3 != "00")
    {
      return AjustesCommandResult.Fail(
        "Una cuenta con Nivel 2 igual a 00 es un encabezado de Nivel 1 y su Nivel 3 también debe ser 00.");
    }

    using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);

    // The account picker resolves both parents with a double self-join, so a leaf
    // without its headers renders with blank group names. Refuse to create one.
    if (nivel3 != "00" || nivel2 != "00")
    {
      var parentSql = @"
SELECT
    (SELECT COUNT(*) FROM dbo.CuentasContables
     WHERE RFC = @rfc AND Nivel1 = @nivel1 AND Nivel2 = '00' AND Nivel3 = '00') AS Nivel1Existe,
    (SELECT COUNT(*) FROM dbo.CuentasContables
     WHERE RFC = @rfc AND Nivel1 = @nivel1 AND Nivel2 = @nivel2 AND Nivel3 = '00') AS Nivel2Existe;";

      var parents = await connection.QuerySingleAsync<(int Nivel1Existe, int Nivel2Existe)>(
          new CommandDefinition(parentSql, new { rfc, nivel1, nivel2 }, cancellationToken: ct));

      if (parents.Nivel1Existe == 0)
      {
        return AjustesCommandResult.Fail(
          $"Falta el encabezado de Nivel 1 ({nivel1}-00-00). Créalo antes de agregar cuentas debajo.");
      }
      if (nivel3 != "00" && parents.Nivel2Existe == 0)
      {
        return AjustesCommandResult.Fail(
          $"Falta el encabezado de Nivel 2 ({nivel1}-{nivel2}-00). Créalo antes de agregar cuentas debajo.");
      }
    }

    const string duplicateSql = @"
SELECT TOP (1) id
FROM dbo.CuentasContables
WHERE RFC = @rfc AND Nivel1 = @nivel1 AND Nivel2 = @nivel2 AND Nivel3 = @nivel3
  AND (@id IS NULL OR id <> @id);";

    var duplicate = await connection.ExecuteScalarAsync<int?>(
        new CommandDefinition(
            duplicateSql,
            new { rfc, nivel1, nivel2, nivel3, id = request.Id },
            cancellationToken: ct));
    if (duplicate is not null)
    {
      return AjustesCommandResult.Fail($"Ya existe la cuenta {nivel1}-{nivel2}-{nivel3} para este RFC.");
    }

    if (request.Id is null)
    {
      const string insertSql = @"
INSERT INTO dbo.CuentasContables (RFC, Nivel1, Nivel2, Nivel3, Descripcion)
OUTPUT INSERTED.id
VALUES (@rfc, @nivel1, @nivel2, @nivel3, @descripcion);";

      var newId = await connection.ExecuteScalarAsync<int>(
          new CommandDefinition(
              insertSql,
              new { rfc, nivel1, nivel2, nivel3, descripcion },
              cancellationToken: ct));

      return AjustesCommandResult.Ok($"Cuenta {nivel1}-{nivel2}-{nivel3} creada correctamente.", newId);
    }

    // Moving an account would silently re-point every poliza line that uses it,
    // so only the description is editable once the account exists.
    const string updateSql = @"
UPDATE dbo.CuentasContables
SET Descripcion = @descripcion
WHERE id = @id AND RFC = @rfc AND Nivel1 = @nivel1 AND Nivel2 = @nivel2 AND Nivel3 = @nivel3;";

    var affected = await connection.ExecuteAsync(
        new CommandDefinition(
            updateSql,
            new { id = request.Id, rfc, nivel1, nivel2, nivel3, descripcion },
            cancellationToken: ct));

    return affected == 0
      ? AjustesCommandResult.Fail(
          "La cuenta seleccionada ya no existe, o intentaste cambiar su clave. Para reclasificar, crea la cuenta nueva y elimina la anterior.")
      : AjustesCommandResult.Ok($"Cuenta {nivel1}-{nivel2}-{nivel3} actualizada correctamente.", request.Id);
  }

  public async Task<AjustesCommandResult> DeleteCuentaAsync(string rfc, int id, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeNullable(rfc);
    if (normalizedRfc is null)
    {
      return AjustesCommandResult.Fail("Selecciona un RFC antes de eliminar una cuenta.");
    }

    var cuentas = await GetCuentasAsync(normalizedRfc, null, ct);
    var cuenta = cuentas.FirstOrDefault(item => item.Id == id);
    if (cuenta is null)
    {
      return AjustesCommandResult.Fail("La cuenta seleccionada ya no existe.");
    }

    if (cuenta.Hijos > 0)
    {
      return AjustesCommandResult.Fail(
        $"No se puede eliminar {cuenta.Clave}: tiene {cuenta.Hijos} cuenta(s) debajo. Elimina primero las cuentas hijas.");
    }

    if (cuenta.Referencias > 0)
    {
      return AjustesCommandResult.Fail(
        $"No se puede eliminar {cuenta.Clave}: {cuenta.Referencias} registro(s) la usan (plantillas, cuentas CFDI o cuentas bancarias).");
    }

    const string deleteSql = "DELETE FROM dbo.CuentasContables WHERE id = @id AND RFC = @rfc;";

    using var connection = new SqlConnection(_connectionString);
    var affected = await connection.ExecuteAsync(
        new CommandDefinition(deleteSql, new { id, rfc = normalizedRfc }, cancellationToken: ct));

    return affected == 0
      ? AjustesCommandResult.Fail("La cuenta seleccionada ya no existe.")
      : AjustesCommandResult.Ok($"Cuenta {cuenta.Clave} eliminada correctamente.");
  }

  private static CatalogoTabla Resolve(CatalogoKey key)
    => Catalogos.TryGetValue(key, out var catalogo)
      ? catalogo
      : throw new ArgumentOutOfRangeException(nameof(key), key, "Catálogo no soportado.");

  private static bool IsProtected(CatalogoKey key, string id)
    => key == CatalogoKey.FormasPago && SatFormaPagoClaves.Contains(id.Trim());

  private static string? NormalizeNullable(string? value)
  {
    var trimmed = value?.Trim();
    return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
  }

  /// <summary>
  /// Mirrors CuentasContablesRepository.NormalizeTwoDigits: the chart of accounts
  /// stores Nivel2 and Nivel3 as exactly two characters, and '00' is the header
  /// sentinel the account picker keys on.
  /// </summary>
  private static string NormalizeTwoDigits(string? value)
  {
    var trimmed = value?.Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
    {
      return "00";
    }

    return trimmed.Length >= 2
      ? trimmed[^2..]
      : trimmed.PadLeft(2, '0');
  }

  private sealed class CatalogoRow
  {
    public string Id { get; set; } = string.Empty;
    public string? Codigo { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public int? Orden { get; set; }
    public bool Activo { get; set; }
    public int Referencias { get; set; }
  }
}
