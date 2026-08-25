using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano;

namespace OrionERP.Infrastructure.Features.CapitalHumano;

public sealed partial class CapitalHumanoService : ICapitalHumanoService
{
  private static readonly string[] DefaultStatuses = ["ACTIVO", "INACTIVO"];
  private static readonly string[] DefaultSexos = ["F", "M", "H"];
  private static readonly string[] DefaultBloodTypes = ["O+", "O-", "A+", "A-", "B+", "B-", "AB+", "AB-"];
  private static readonly string[] DefaultTiposCapitalHumano = ["NOMINA", "PROVEEDOR", "USUARIO_SISTEMA", "JOVENES", "BECARIO"];

  private readonly IDbConnectionFactory _connectionFactory;

  public CapitalHumanoService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<CapitalHumanoListItemDto>> GetEmployeesAsync(CapitalHumanoFilter filter, CancellationToken ct = default)
  {
    filter ??= new CapitalHumanoFilter();
    var rfc = RequireText(filter.Rfc, "El RFC de la compania es obligatorio.");
    var skip = Math.Max(filter.Skip, 0);
    var take = Math.Clamp(filter.Take, 1, 500);
    var sql = new StringBuilder(
      """
      SELECT
          ch.ID AS Id,
          ch.Nombre,
          ch.ApellidoPaterno,
          ch.ApellidoMaterno,
          ch.NombreCorto,
          ISNULL(NULLIF(LTRIM(RTRIM(ch.[Status])), ''), 'INACTIVO') AS [Status],
          ch.Puesto,
          ch.RFC_Capital_Humano,
          ch.Telefono,
          ch.Fecha_Alta,
          ch.Fecha_Baja,
          CAST(CASE WHEN ch.Fotografia IS NULL THEN 0 ELSE 1 END AS bit) AS HasPhoto,
          CAST(CASE WHEN auth.AuthUserCount > 0 THEN 1 ELSE 0 END AS bit) AS HasAuthUser,
          ISNULL(auth.AuthUserCount, 0) AS AuthUserCount,
          auth.AuthUserName,
          auth.AuthEmail
      FROM dbo.Capital_Humano ch
      OUTER APPLY (
          SELECT
              COUNT(*) AS AuthUserCount,
              MAX(au.UserName) AS AuthUserName,
              MAX(au.Email) AS AuthEmail
          FROM auth.AspNetUserCompanies membership
          JOIN auth.AspNetUsers au ON au.Id = membership.UserId
          WHERE membership.EmployeeId = ch.ID AND membership.Rfc = ch.RFC AND membership.IsActive = 1
      ) auth
      WHERE ch.RFC = @Rfc
      """);
    sql.AppendLine();

    var parameters = new DynamicParameters();
    parameters.Add("@Rfc", rfc, DbType.String);

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
      sql.AppendLine(" AND UPPER(LTRIM(RTRIM(ISNULL(ch.[Status], '')))) = @Status");
      parameters.Add("@Status", filter.Status.Trim().ToUpperInvariant(), DbType.String);
    }

    if (!string.IsNullOrWhiteSpace(filter.Puesto))
    {
      sql.AppendLine(" AND LTRIM(RTRIM(ISNULL(ch.Puesto, ''))) = @Puesto");
      parameters.Add("@Puesto", filter.Puesto.Trim(), DbType.String);
    }

    if (filter.HasPhoto.HasValue)
    {
      sql.AppendLine(filter.HasPhoto.Value ? " AND ch.Fotografia IS NOT NULL" : " AND ch.Fotografia IS NULL");
    }

    var searchText = NullIfWhiteSpace(filter.SearchText);
    if (searchText is not null)
    {
      sql.AppendLine(
        """
        AND (
          ch.Nombre LIKE @Search
          OR ch.ApellidoPaterno LIKE @Search
          OR ch.ApellidoMaterno LIKE @Search
          OR ch.NombreCorto LIKE @Search
          OR ch.RFC_Capital_Humano LIKE @Search
          OR ch.CURP LIKE @Search
          OR ch.Puesto LIKE @Search
          OR ch.Telefono LIKE @Search
        )
        """);
      parameters.Add("@Search", $"%{searchText}%", DbType.String);
    }

    sql.AppendLine(
      """
      ORDER BY
          CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(ch.[Status], '')))) = 'ACTIVO' THEN 0 ELSE 1 END,
          ch.NombreCorto,
          ch.ApellidoPaterno,
          ch.ApellidoMaterno,
          ch.Nombre,
          ch.ID
      OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
      """);
    parameters.Add("@Skip", skip, DbType.Int32);
    parameters.Add("@Take", take, DbType.Int32);

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<CapitalHumanoListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<CapitalHumanoDetailDto?> GetEmployeeAsync(int id, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          ch.ID AS Id,
          ch.RFC AS Rfc,
          ch.Nombre,
          ch.ApellidoPaterno,
          ch.ApellidoMaterno,
          ISNULL(NULLIF(LTRIM(RTRIM(ch.[Status])), ''), 'INACTIVO') AS [Status],
          ch.NombreCorto,
          ch.CURP,
          ch.Fecha_Nacimiento,
          ch.RFC_Capital_Humano,
          ch.Seguro_Social,
          ch.Calle,
          ch.Colonia,
          ch.Comunidad,
          ch.Ciudad,
          ch.Estado,
          ch.Tipo_Sangre,
          ch.Telefono,
          ch.Numero_Emergencia,
          CAST(ch.Sueldo_Mensual AS decimal(18,2)) AS Sueldo_Mensual,
          ch.Puesto,
          ch.Sexo,
          ch.Edad,
          ch.Dependientes,
          ch.Beneficiarios,
          ch.Fecha_Alta,
          ch.Fecha_Baja,
          ch.Nacionalidad,
          ch.Tipo_Contrato,
          ch.Sede_Contratada,
          ch.Jornada,
          ch.Lactancia,
          ch.Horario_Alimentos,
          ch.Esquema_Pagos,
          ch.Tipo_Capital_Humano,
          ch.Nivel_Maximo_Estudios,
          ch.Descanso_Semanal,
          CAST(CASE WHEN ch.Fotografia IS NULL THEN 0 ELSE 1 END AS bit) AS HasPhoto,
          CAST(CASE WHEN auth.AuthUserCount > 0 THEN 1 ELSE 0 END AS bit) AS HasAuthUser,
          ISNULL(auth.AuthUserCount, 0) AS AuthUserCount,
          auth.AuthUserName,
          auth.AuthEmail
      FROM dbo.Capital_Humano ch
      OUTER APPLY (
          SELECT
              COUNT(*) AS AuthUserCount,
              MAX(au.UserName) AS AuthUserName,
              MAX(au.Email) AS AuthEmail
          FROM auth.AspNetUserCompanies membership
          JOIN auth.AspNetUsers au ON au.Id = membership.UserId
          WHERE membership.EmployeeId = ch.ID AND membership.Rfc = ch.RFC AND membership.IsActive = 1
      ) auth
      WHERE ch.ID = @Id
        AND ch.RFC = @Rfc;
      """;

    using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<CapitalHumanoDetailDto>(
      new CommandDefinition(sql, new { Id = id, Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") }, cancellationToken: ct));
  }

  public async Task<CapitalHumanoCatalogDto> GetCatalogAsync(string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT DISTINCT NULLIF(LTRIM(RTRIM([Status])), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM([Status])), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Puesto)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Puesto)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Sexo)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Sexo)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Tipo_Sangre)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Tipo_Sangre)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Tipo_Contrato)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Tipo_Contrato)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Sede_Contratada)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Sede_Contratada)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Esquema_Pagos)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Esquema_Pagos)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Tipo_Capital_Humano)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Tipo_Capital_Humano)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Nivel_Maximo_Estudios)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Nivel_Maximo_Estudios)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Nacionalidad)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Nacionalidad)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Estado)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Estado)), '') IS NOT NULL ORDER BY Value;
      SELECT DISTINCT NULLIF(LTRIM(RTRIM(Descanso_Semanal)), '') AS Value FROM dbo.Capital_Humano WHERE RFC = @Rfc AND NULLIF(LTRIM(RTRIM(Descanso_Semanal)), '') IS NOT NULL ORDER BY Value;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") }, cancellationToken: ct));

    return new CapitalHumanoCatalogDto
    {
      Statuses = MergeOptions(DefaultStatuses, await ReadOptionValuesAsync(multi)),
      Puestos = await ReadOptionValuesAsync(multi),
      Sexos = MergeOptions(DefaultSexos, await ReadOptionValuesAsync(multi)),
      TiposSangre = MergeOptions(DefaultBloodTypes, await ReadOptionValuesAsync(multi)),
      TiposContrato = await ReadOptionValuesAsync(multi),
      Sedes = await ReadOptionValuesAsync(multi),
      EsquemasPago = await ReadOptionValuesAsync(multi),
      TiposCapitalHumano = MergeOptions(DefaultTiposCapitalHumano, await ReadOptionValuesAsync(multi)),
      NivelesEstudios = await ReadOptionValuesAsync(multi),
      Nacionalidades = await ReadOptionValuesAsync(multi),
      Estados = await ReadOptionValuesAsync(multi),
      DescansosSemanales = await ReadOptionValuesAsync(multi)
    };
  }

  public async Task<CapitalHumanoBinaryContent?> GetPhotoAsync(int id, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          ch.ID AS Id,
          CONCAT('capital-humano-', ch.ID, '.jpg') AS FileName,
          ch.Fotografia AS Bytes
      FROM dbo.Capital_Humano ch
      WHERE ch.ID = @Id
        AND ch.RFC = @Rfc
        AND ch.Fotografia IS NOT NULL;
      """;

    using var conn = CreateConnection();
    var content = await conn.QueryFirstOrDefaultAsync<CapitalHumanoBinaryContent>(
      new CommandDefinition(sql, new { Id = id, Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") }, cancellationToken: ct));
    if (content is null)
    {
      return null;
    }

    content.ContentType = InferImageContentType(content.Bytes);
    content.FileName = $"capital-humano-{content.Id}.{GetImageExtension(content.ContentType)}";
    return content;
  }

  public async Task<IReadOnlyList<CapitalHumanoBinaryContent>> GetThumbnailsAsync(string rfc, IEnumerable<int> employeeIds, CancellationToken ct = default)
  {
    var ids = employeeIds?
      .Where(id => id > 0)
      .Distinct()
      .ToArray() ?? [];

    if (ids.Length == 0)
    {
      return Array.Empty<CapitalHumanoBinaryContent>();
    }

    const string sql =
      """
      SELECT
          ch.ID AS Id,
          CONCAT('capital-humano-', ch.ID, '.jpg') AS FileName,
          ch.Fotografia AS Bytes
      FROM dbo.Capital_Humano ch
      WHERE ch.RFC = @Rfc
        AND ch.ID IN @EmployeeIds
        AND ch.Fotografia IS NOT NULL;
      """;

    using var conn = CreateConnection();
    var rows = (await conn.QueryAsync<CapitalHumanoBinaryContent>(
      new CommandDefinition(sql, new { Rfc = RequireText(rfc, "El RFC de la compania es obligatorio."), EmployeeIds = ids }, cancellationToken: ct))).AsList();

    foreach (var row in rows)
    {
      row.ContentType = InferImageContentType(row.Bytes);
      row.FileName = $"capital-humano-{row.Id}.{GetImageExtension(row.ContentType)}";
    }

    return rows;
  }

  public async Task<IReadOnlyList<CapitalHumanoAttachmentDto>> GetEmployeeAttachmentsAsync(int employeeId, string rfc, CancellationToken ct = default)
  {
    if (employeeId <= 0)
    {
      return Array.Empty<CapitalHumanoAttachmentDto>();
    }

    const string sql =
      """
      SELECT
          ea.ID AS Id,
          ea.EmpID AS EmployeeId,
          ISNULL(ea.AttachmentName, CONCAT('Archivo ', ea.ID)) AS AttachmentName,
          ISNULL(ea.AttachmentExtension, '') AS AttachmentExtension,
          ISNULL(ea.AttachmentDescription, '') AS AttachmentDescription,
          CAST(DATALENGTH(ea.Attachment) AS bigint) AS [Length]
      FROM dbo.EMPLOYEE_ATTACHMENT ea
      INNER JOIN dbo.Capital_Humano ch
          ON ch.ID = ea.EmpID
      WHERE ea.EmpID = @EmployeeId
        AND ch.RFC = @Rfc
      ORDER BY ea.ID DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<CapitalHumanoAttachmentDto>(
      new CommandDefinition(
        sql,
        new { EmployeeId = employeeId, Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") },
        cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<CapitalHumanoAttachmentContent?> GetEmployeeAttachmentContentAsync(int attachmentId, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT TOP (1)
          ea.AttachmentName,
          ea.AttachmentExtension,
          ea.Attachment
      FROM dbo.EMPLOYEE_ATTACHMENT ea
      INNER JOIN dbo.Capital_Humano ch
          ON ch.ID = ea.EmpID
      WHERE ea.ID = @AttachmentId
        AND ch.RFC = @Rfc;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<(string? AttachmentName, string? AttachmentExtension, byte[]? Attachment)>(
      new CommandDefinition(
        sql,
        new { AttachmentId = attachmentId, Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") },
        cancellationToken: ct));

    if (row.Attachment is null || row.Attachment.Length == 0)
    {
      return null;
    }

    var extension = NormalizeAttachmentExtension(row.AttachmentExtension, row.AttachmentName);
    var fileName = BuildAttachmentDownloadFileName(row.AttachmentName, extension, attachmentId);

    return new CapitalHumanoAttachmentContent
    {
      AttachmentId = attachmentId,
      FileName = fileName,
      ContentType = ResolveAttachmentContentType(extension),
      Bytes = row.Attachment
    };
  }

  public async Task<CapitalHumanoAttachmentDto> AddEmployeeAttachmentAsync(CapitalHumanoAttachmentCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    ValidateAttachmentContent(request.Content);

    var normalized = new NormalizedAttachmentInput(
      request.EmployeeId,
      RequireText(request.Rfc, "El RFC de la compania es obligatorio.").ToUpperInvariant(),
      NormalizeAttachmentName(request.FileName),
      NormalizeAttachmentExtension(request.Extension, request.FileName),
      NormalizeAttachmentDescription(request.Description),
      request.Content);

    const string insertSql =
      """
      INSERT INTO dbo.EMPLOYEE_ATTACHMENT
      (
          EmpID,
          Attachment,
          AttachmentName,
          AttachmentExtension,
          AttachmentDescription
      )
      VALUES
      (
          @EmployeeId,
          @Attachment,
          @AttachmentName,
          @AttachmentExtension,
          @AttachmentDescription
      );
      SELECT CAST(SCOPE_IDENTITY() AS int);
      """;

    using var conn = CreateConnection();
    await EnsureEmployeeExistsAsync(conn, normalized.EmployeeId, normalized.Rfc, ct);
    var attachmentId = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        insertSql,
        new
        {
          normalized.EmployeeId,
          Attachment = normalized.Content,
          normalized.AttachmentName,
          normalized.AttachmentExtension,
          normalized.AttachmentDescription
        },
        cancellationToken: ct));

    return await GetEmployeeAttachmentOrThrowAsync(conn, attachmentId, normalized.Rfc, ct);
  }

  public async Task<CapitalHumanoAttachmentDto> UpdateEmployeeAttachmentAsync(CapitalHumanoAttachmentUpdateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    if (request.Content is { Length: > 0 })
    {
      ValidateAttachmentContent(request.Content);
    }
    else if (request.Content is { Length: 0 })
    {
      throw new ArgumentException("El archivo adjunto no contiene datos.", nameof(request));
    }

    var normalized = new NormalizedAttachmentInput(
      request.EmployeeId,
      RequireText(request.Rfc, "El RFC de la compania es obligatorio.").ToUpperInvariant(),
      NormalizeAttachmentName(request.FileName),
      NormalizeAttachmentExtension(request.Extension, request.FileName),
      NormalizeAttachmentDescription(request.Description),
      request.Content);

    var hasReplacement = normalized.Content is { Length: > 0 };
    var updateSql = new StringBuilder(
      """
      UPDATE ea
      SET AttachmentName = @AttachmentName,
          AttachmentExtension = @AttachmentExtension,
          AttachmentDescription = @AttachmentDescription
      """);

    if (hasReplacement)
    {
      updateSql.AppendLine(", Attachment = @Attachment");
    }
    else
    {
      updateSql.AppendLine();
    }

    updateSql.AppendLine(
      """
      FROM dbo.EMPLOYEE_ATTACHMENT ea
      INNER JOIN dbo.Capital_Humano ch
          ON ch.ID = ea.EmpID
      WHERE ea.ID = @AttachmentId
        AND ea.EmpID = @EmployeeId
        AND ch.RFC = @Rfc;
      """);

    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        updateSql.ToString(),
        new
        {
          request.AttachmentId,
          normalized.EmployeeId,
          normalized.Rfc,
          normalized.AttachmentName,
          normalized.AttachmentExtension,
          normalized.AttachmentDescription,
          Attachment = normalized.Content
        },
        cancellationToken: ct));

    if (affected == 0)
    {
      throw new InvalidOperationException("El archivo no existe para el empleado y RFC seleccionados.");
    }

    return await GetEmployeeAttachmentOrThrowAsync(conn, request.AttachmentId, normalized.Rfc, ct);
  }

  public async Task<CapitalHumanoCommandResult> DeleteEmployeeAttachmentAsync(int attachmentId, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      DELETE ea
      FROM dbo.EMPLOYEE_ATTACHMENT ea
      INNER JOIN dbo.Capital_Humano ch
          ON ch.ID = ea.EmpID
      WHERE ea.ID = @AttachmentId
        AND ch.RFC = @Rfc;
      """;

    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(
        sql,
        new { AttachmentId = attachmentId, Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") },
        cancellationToken: ct));

    return affected == 0
      ? CapitalHumanoCommandResult.Fail("El archivo no existe para el RFC seleccionado.", attachmentId)
      : CapitalHumanoCommandResult.Ok("Archivo eliminado correctamente.", attachmentId);
  }

  public async Task<CapitalHumanoCommandResult> SaveEmployeeAsync(CapitalHumanoSaveRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var validation = ValidateRequest(request);
    if (validation is not null)
    {
      return CapitalHumanoCommandResult.Fail(validation, request.Id);
    }

    var normalized = NormalizeRequest(request);
    var hasNewPhoto = normalized.FotografiaBytes is { Length: > 0 };

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      int employeeId;
      if (normalized.Id.HasValue && normalized.Id.Value > 0)
      {
        var updateSql = new StringBuilder(
          """
          UPDATE dbo.Capital_Humano
          SET Nombre = @Nombre,
              ApellidoPaterno = @ApellidoPaterno,
              ApellidoMaterno = @ApellidoMaterno,
              [Status] = @Status,
              NombreCorto = @NombreCorto,
              CURP = @CURP,
              Fecha_Nacimiento = @Fecha_Nacimiento,
              RFC_Capital_Humano = @RFC_Capital_Humano,
              Seguro_Social = @Seguro_Social,
              Calle = @Calle,
              Colonia = @Colonia,
              Comunidad = @Comunidad,
              Ciudad = @Ciudad,
              Estado = @Estado,
              Tipo_Sangre = @Tipo_Sangre,
              Telefono = @Telefono,
              Numero_Emergencia = @Numero_Emergencia,
              Sueldo_Mensual = @Sueldo_Mensual,
              Puesto = @Puesto,
              Sexo = @Sexo,
              Edad = @Edad,
              Dependientes = @Dependientes,
              Beneficiarios = @Beneficiarios,
              Fecha_Alta = @Fecha_Alta,
              Fecha_Baja = @Fecha_Baja,
              Nacionalidad = @Nacionalidad,
              Tipo_Contrato = @Tipo_Contrato,
              Sede_Contratada = @Sede_Contratada,
              Jornada = @Jornada,
              Lactancia = @Lactancia,
              Horario_Alimentos = @Horario_Alimentos,
              Esquema_Pagos = @Esquema_Pagos,
              Tipo_Capital_Humano = @Tipo_Capital_Humano,
              Nivel_Maximo_Estudios = @Nivel_Maximo_Estudios,
              Descanso_Semanal = @Descanso_Semanal
          """);

        if (hasNewPhoto)
        {
          updateSql.AppendLine(", Fotografia = @Fotografia");
        }

        updateSql.AppendLine(
          """
          WHERE ID = @Id
            AND RFC = @Rfc;
          """);

        var affected = await conn.ExecuteAsync(
          new CommandDefinition(updateSql.ToString(), BuildParameters(normalized, hasNewPhoto), tx, cancellationToken: ct));
        if (affected == 0)
        {
          await tx.RollbackAsync(ct);
          return CapitalHumanoCommandResult.Fail("El empleado no existe para el RFC seleccionado.", normalized.Id);
        }

        employeeId = normalized.Id.Value;
      }
      else
      {
        employeeId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            """
            SELECT ISNULL(MAX(ID), 0) + 1
            FROM dbo.Capital_Humano WITH (UPDLOCK, HOLDLOCK);
            """,
            transaction: tx,
            cancellationToken: ct));
        normalized.Id = employeeId;

        const string insertSql =
          """
          INSERT INTO dbo.Capital_Humano
          (
              ID,
              Nombre,
              ApellidoPaterno,
              ApellidoMaterno,
              [Status],
              NombreCorto,
              CURP,
              Fecha_Nacimiento,
              RFC_Capital_Humano,
              Seguro_Social,
              Calle,
              Colonia,
              Comunidad,
              Ciudad,
              Estado,
              Tipo_Sangre,
              Telefono,
              Numero_Emergencia,
              Sueldo_Mensual,
              Fotografia,
              Puesto,
              Sexo,
              Edad,
              Dependientes,
              Beneficiarios,
              Fecha_Alta,
              Fecha_Baja,
              Nacionalidad,
              Tipo_Contrato,
              Sede_Contratada,
              Jornada,
              Lactancia,
              Horario_Alimentos,
              Esquema_Pagos,
              Tipo_Capital_Humano,
              Nivel_Maximo_Estudios,
              Descanso_Semanal,
              Contrasena_Acceso,
              Usuario_Acceso,
              RFC
          )
          VALUES
          (
              @Id,
              @Nombre,
              @ApellidoPaterno,
              @ApellidoMaterno,
              @Status,
              @NombreCorto,
              @CURP,
              @Fecha_Nacimiento,
              @RFC_Capital_Humano,
              @Seguro_Social,
              @Calle,
              @Colonia,
              @Comunidad,
              @Ciudad,
              @Estado,
              @Tipo_Sangre,
              @Telefono,
              @Numero_Emergencia,
              @Sueldo_Mensual,
              @Fotografia,
              @Puesto,
              @Sexo,
              @Edad,
              @Dependientes,
              @Beneficiarios,
              @Fecha_Alta,
              @Fecha_Baja,
              @Nacionalidad,
              @Tipo_Contrato,
              @Sede_Contratada,
              @Jornada,
              @Lactancia,
              @Horario_Alimentos,
              @Esquema_Pagos,
              @Tipo_Capital_Humano,
              @Nivel_Maximo_Estudios,
              @Descanso_Semanal,
              '',
              '',
              @Rfc
          );
          """;

        await conn.ExecuteAsync(
          new CommandDefinition(insertSql, BuildParameters(normalized, includePhoto: true), tx, cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return CapitalHumanoCommandResult.Ok("Empleado guardado correctamente.", employeeId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return CapitalHumanoCommandResult.Fail("Ya existe un empleado con la misma llave.", normalized.Id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<CapitalHumanoCommandResult> DeactivateEmployeeAsync(int id, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      UPDATE dbo.Capital_Humano
      SET [Status] = 'INACTIVO',
          Fecha_Baja = ISNULL(Fecha_Baja, CONVERT(date, GETDATE()))
      WHERE ID = @Id
        AND RFC = @Rfc;
      """;

    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(
      new CommandDefinition(sql, new { Id = id, Rfc = RequireText(rfc, "El RFC de la compania es obligatorio.") }, cancellationToken: ct));

    return affected == 0
      ? CapitalHumanoCommandResult.Fail("El empleado no existe para el RFC seleccionado.", id)
      : CapitalHumanoCommandResult.Ok("Empleado desactivado correctamente.", id);
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fabrica de conexiones no devolvio una DbConnection.");

  private static DynamicParameters BuildParameters(CapitalHumanoSaveRequest request, bool includePhoto)
  {
    var p = new DynamicParameters();
    p.Add("@Id", request.Id, DbType.Int32);
    p.Add("@Rfc", request.Rfc, DbType.String);
    p.Add("@Nombre", request.Nombre, DbType.String);
    p.Add("@ApellidoPaterno", request.ApellidoPaterno, DbType.String);
    p.Add("@ApellidoMaterno", request.ApellidoMaterno, DbType.String);
    p.Add("@Status", request.Status, DbType.String);
    p.Add("@NombreCorto", request.NombreCorto, DbType.String);
    p.Add("@CURP", request.CURP, DbType.String);
    p.Add("@Fecha_Nacimiento", request.Fecha_Nacimiento, DbType.DateTime);
    p.Add("@RFC_Capital_Humano", request.RFC_Capital_Humano, DbType.String);
    p.Add("@Seguro_Social", request.Seguro_Social, DbType.String);
    p.Add("@Calle", request.Calle, DbType.String);
    p.Add("@Colonia", request.Colonia, DbType.String);
    p.Add("@Comunidad", request.Comunidad, DbType.String);
    p.Add("@Ciudad", request.Ciudad, DbType.String);
    p.Add("@Estado", request.Estado, DbType.String);
    p.Add("@Tipo_Sangre", request.Tipo_Sangre, DbType.String);
    p.Add("@Telefono", request.Telefono, DbType.String);
    p.Add("@Numero_Emergencia", request.Numero_Emergencia, DbType.String);
    p.Add("@Sueldo_Mensual", request.Sueldo_Mensual, DbType.Decimal);
    p.Add("@Puesto", request.Puesto, DbType.String);
    p.Add("@Sexo", request.Sexo, DbType.String);
    p.Add("@Edad", CalculateAgeText(request.Fecha_Nacimiento), DbType.String);
    p.Add("@Dependientes", request.Dependientes, DbType.String);
    p.Add("@Beneficiarios", request.Beneficiarios, DbType.String);
    p.Add("@Fecha_Alta", request.Fecha_Alta, DbType.Date);
    p.Add("@Fecha_Baja", request.Fecha_Baja, DbType.Date);
    p.Add("@Nacionalidad", request.Nacionalidad, DbType.String);
    p.Add("@Tipo_Contrato", request.Tipo_Contrato, DbType.String);
    p.Add("@Sede_Contratada", request.Sede_Contratada, DbType.String);
    p.Add("@Jornada", request.Jornada, DbType.String);
    p.Add("@Lactancia", request.Lactancia, DbType.String);
    p.Add("@Horario_Alimentos", request.Horario_Alimentos, DbType.String);
    p.Add("@Esquema_Pagos", request.Esquema_Pagos, DbType.String);
    p.Add("@Tipo_Capital_Humano", request.Tipo_Capital_Humano, DbType.String);
    p.Add("@Nivel_Maximo_Estudios", request.Nivel_Maximo_Estudios, DbType.String);
    p.Add("@Descanso_Semanal", request.Descanso_Semanal, DbType.String);
    if (includePhoto)
    {
      p.Add("@Fotografia", request.FotografiaBytes, DbType.Binary);
    }

    return p;
  }

  private static CapitalHumanoSaveRequest NormalizeRequest(CapitalHumanoSaveRequest request)
    => new()
    {
      Id = request.Id,
      Rfc = RequireText(request.Rfc, "El RFC de la compania es obligatorio.").ToUpperInvariant(),
      Nombre = TrimMax(RequireText(request.Nombre, "El nombre es obligatorio."), 255),
      ApellidoPaterno = TrimMax(RequireText(request.ApellidoPaterno, "El apellido paterno es obligatorio."), 255),
      ApellidoMaterno = TrimMax(RequireText(request.ApellidoMaterno, "El apellido materno es obligatorio."), 255),
      NombreCorto = TrimMax(RequireText(request.NombreCorto, "El nombre corto es obligatorio."), 255),
      Status = TrimMax(NullIfWhiteSpace(request.Status)?.ToUpperInvariant() ?? "ACTIVO", 255),
      CURP = UpperTrimMax(request.CURP, 255),
      Fecha_Nacimiento = request.Fecha_Nacimiento?.Date,
      RFC_Capital_Humano = UpperTrimMax(request.RFC_Capital_Humano, 50),
      Seguro_Social = TrimMaxOrNull(request.Seguro_Social, 50),
      Calle = TrimMaxOrNull(request.Calle, 255),
      Colonia = TrimMaxOrNull(request.Colonia, 255),
      Comunidad = TrimMaxOrNull(request.Comunidad, 255),
      Ciudad = TrimMaxOrNull(request.Ciudad, 255),
      Estado = TrimMaxOrNull(request.Estado, 255),
      Tipo_Sangre = UpperTrimMax(request.Tipo_Sangre, 10),
      Telefono = TrimMaxOrNull(request.Telefono, 50),
      Numero_Emergencia = TrimMaxOrNull(request.Numero_Emergencia, 50),
      Sueldo_Mensual = request.Sueldo_Mensual,
      Puesto = TrimMaxOrNull(request.Puesto, 50),
      Sexo = UpperTrimMax(request.Sexo, 50),
      Dependientes = NullIfWhiteSpace(request.Dependientes),
      Beneficiarios = NullIfWhiteSpace(request.Beneficiarios),
      Fecha_Alta = request.Fecha_Alta?.Date ?? (request.Id.HasValue ? null : DateTime.Today),
      Fecha_Baja = request.Fecha_Baja?.Date,
      Nacionalidad = TrimMaxOrNull(request.Nacionalidad, 100),
      Tipo_Contrato = TrimMaxOrNull(request.Tipo_Contrato, 100),
      Sede_Contratada = TrimMaxOrNull(request.Sede_Contratada, 500),
      Jornada = NullIfWhiteSpace(request.Jornada),
      Lactancia = TrimMaxOrNull(request.Lactancia, 500),
      Horario_Alimentos = TrimMaxOrNull(request.Horario_Alimentos, 500),
      Esquema_Pagos = TrimMaxOrNull(request.Esquema_Pagos, 100),
      Tipo_Capital_Humano = TrimMaxOrNull(request.Tipo_Capital_Humano, 100),
      Nivel_Maximo_Estudios = TrimMaxOrNull(request.Nivel_Maximo_Estudios, 100),
      Descanso_Semanal = TrimMaxOrNull(request.Descanso_Semanal, 100),
      FotografiaBytes = request.FotografiaBytes
    };

  private static string? ValidateRequest(CapitalHumanoSaveRequest request)
  {
    if (NullIfWhiteSpace(request.Rfc) is null)
    {
      return "El RFC de la compania es obligatorio.";
    }

    if (NullIfWhiteSpace(request.Nombre) is null)
    {
      return "El nombre es obligatorio.";
    }

    if (NullIfWhiteSpace(request.ApellidoPaterno) is null)
    {
      return "El apellido paterno es obligatorio.";
    }

    if (NullIfWhiteSpace(request.ApellidoMaterno) is null)
    {
      return "El apellido materno es obligatorio.";
    }

    if (NullIfWhiteSpace(request.NombreCorto) is null)
    {
      return "El nombre corto es obligatorio.";
    }

    var workerRfc = UpperTrimMax(request.RFC_Capital_Humano, 50);
    if (workerRfc is not null && !IsValidMexicanPhysicalRfc(workerRfc))
    {
      return "El RFC del trabajador debe tener formato de persona fisica: 4 letras, fecha AAMMDD y 3 caracteres de homoclave.";
    }

    if (request.Fecha_Alta.HasValue && request.Fecha_Baja.HasValue && request.Fecha_Baja.Value.Date < request.Fecha_Alta.Value.Date)
    {
      return "La fecha de baja no puede ser anterior a la fecha de alta.";
    }

    return null;
  }

  private static async Task<IReadOnlyList<string>> ReadOptionValuesAsync(SqlMapper.GridReader multi)
  {
    var rows = await multi.ReadAsync<string>();
    return rows
      .Where(value => !string.IsNullOrWhiteSpace(value))
      .Select(value => value.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
      .ToArray();
  }

  private static async Task EnsureEmployeeExistsAsync(DbConnection conn, int employeeId, string rfc, CancellationToken ct)
  {
    if (employeeId <= 0)
    {
      throw new ArgumentException("El empleado es obligatorio.", nameof(employeeId));
    }

    const string sql =
      """
      SELECT COUNT(1)
      FROM dbo.Capital_Humano
      WHERE ID = @EmployeeId
        AND RFC = @Rfc;
      """;

    var exists = await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(sql, new { EmployeeId = employeeId, Rfc = rfc }, cancellationToken: ct));
    if (exists == 0)
    {
      throw new InvalidOperationException("El empleado no existe para el RFC seleccionado.");
    }
  }

  private static async Task<CapitalHumanoAttachmentDto> GetEmployeeAttachmentOrThrowAsync(
    DbConnection conn,
    int attachmentId,
    string rfc,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          ea.ID AS Id,
          ea.EmpID AS EmployeeId,
          ISNULL(ea.AttachmentName, CONCAT('Archivo ', ea.ID)) AS AttachmentName,
          ISNULL(ea.AttachmentExtension, '') AS AttachmentExtension,
          ISNULL(ea.AttachmentDescription, '') AS AttachmentDescription,
          CAST(DATALENGTH(ea.Attachment) AS bigint) AS [Length]
      FROM dbo.EMPLOYEE_ATTACHMENT ea
      INNER JOIN dbo.Capital_Humano ch
          ON ch.ID = ea.EmpID
      WHERE ea.ID = @AttachmentId
        AND ch.RFC = @Rfc;
      """;

    var dto = await conn.QueryFirstOrDefaultAsync<CapitalHumanoAttachmentDto>(
      new CommandDefinition(sql, new { AttachmentId = attachmentId, Rfc = rfc }, cancellationToken: ct));

    return dto ?? throw new InvalidOperationException("No se pudo recuperar el archivo.");
  }

  private static void ValidateAttachmentContent(byte[]? content)
  {
    if (content is null || content.Length == 0)
    {
      throw new ArgumentException("El archivo adjunto no contiene datos.", nameof(content));
    }

    if (content.Length > CapitalHumanoAttachmentCreateRequest.MaxFileSizeBytes)
    {
      throw new InvalidOperationException("El archivo adjunto excede el tamaño máximo permitido (5 MB).");
    }
  }

  private static string NormalizeAttachmentName(string? fileName)
  {
    var safeFileName = Path.GetFileName(RequireText(fileName, "El nombre del archivo es obligatorio."));
    return TrimMax(RequireText(safeFileName, "El nombre del archivo es obligatorio."), 200);
  }

  private static string NormalizeAttachmentExtension(string? extension, string? fileName)
  {
    var cleanExtension = NullIfWhiteSpace(extension)?.TrimStart('.');
    if (cleanExtension is null && !string.IsNullOrWhiteSpace(fileName))
    {
      cleanExtension = NullIfWhiteSpace(Path.GetExtension(fileName))?.TrimStart('.');
    }

    return TrimMax(cleanExtension ?? string.Empty, 200);
  }

  private static string NormalizeAttachmentDescription(string? description)
    => TrimMax(NullIfWhiteSpace(description) ?? "Archivo adjunto", 500);

  private static string BuildAttachmentDownloadFileName(string? attachmentName, string? extension, int attachmentId)
  {
    var fileName = string.IsNullOrWhiteSpace(attachmentName)
      ? $"archivo-{attachmentId}"
      : TrimMax(Path.GetFileName(attachmentName.Trim()), 200);

    var cleanExtension = NullIfWhiteSpace(extension);
    if (!string.IsNullOrWhiteSpace(cleanExtension) &&
        !fileName.EndsWith($".{cleanExtension}", StringComparison.OrdinalIgnoreCase))
    {
      fileName = $"{fileName}.{cleanExtension}";
    }

    return fileName;
  }

  private static string ResolveAttachmentContentType(string? extension)
  {
    if (string.IsNullOrWhiteSpace(extension))
    {
      return "application/octet-stream";
    }

    return extension.Trim().TrimStart('.').ToLowerInvariant() switch
    {
      "pdf" => "application/pdf",
      "xml" => "application/xml",
      "jpg" or "jpeg" => "image/jpeg",
      "png" => "image/png",
      "txt" => "text/plain",
      "csv" => "text/csv",
      "xls" => "application/vnd.ms-excel",
      "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      "doc" => "application/msword",
      "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
      _ => "application/octet-stream"
    };
  }

  private static IReadOnlyList<string> MergeOptions(IEnumerable<string> defaults, IEnumerable<string> values)
    => defaults
      .Concat(values)
      .Where(value => !string.IsNullOrWhiteSpace(value))
      .Select(value => value.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
      .ToArray();

  private static string? CalculateAgeText(DateTime? birthDate)
  {
    if (!birthDate.HasValue)
    {
      return null;
    }

    var today = DateTime.Today;
    var age = today.Year - birthDate.Value.Year;
    if (birthDate.Value.Date > today.AddYears(-age))
    {
      age--;
    }

    return age < 0 ? null : age.ToString(CultureInfo.InvariantCulture);
  }

  private static bool IsValidMexicanPhysicalRfc(string value)
  {
    if (!PhysicalPersonRfcRegex().IsMatch(value))
    {
      return false;
    }

    var datePart = value.Substring(4, 6);
    var year = int.Parse(datePart[..2], CultureInfo.InvariantCulture);
    var month = int.Parse(datePart.Substring(2, 2), CultureInfo.InvariantCulture);
    var day = int.Parse(datePart.Substring(4, 2), CultureInfo.InvariantCulture);
    return IsValidDate(1900 + year, month, day) || IsValidDate(2000 + year, month, day);
  }

  private static bool IsValidDate(int year, int month, int day)
  {
    try
    {
      _ = new DateTime(year, month, day);
      return true;
    }
    catch (ArgumentOutOfRangeException)
    {
      return false;
    }
  }

  private static string InferImageContentType(byte[] bytes)
  {
    if (bytes.Length > 3)
    {
      if (bytes[0] == 0xFF && bytes[1] == 0xD8)
      {
        return "image/jpeg";
      }

      if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
      {
        return "image/png";
      }

      if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
      {
        return "image/gif";
      }
    }

    return "image/jpeg";
  }

  private static string GetImageExtension(string contentType)
    => contentType.ToLowerInvariant() switch
    {
      "image/png" => "png",
      "image/gif" => "gif",
      _ => "jpg"
    };

  private static string RequireText(string? value, string message)
    => NullIfWhiteSpace(value) ?? throw new ArgumentException(message);

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static string? UpperTrimMax(string? value, int maxLength)
    => TrimMaxOrNull(value, maxLength)?.ToUpperInvariant();

  private static string? TrimMaxOrNull(string? value, int maxLength)
  {
    var trimmed = NullIfWhiteSpace(value);
    return trimmed is null ? null : TrimMax(trimmed, maxLength);
  }

  private static string TrimMax(string value, int maxLength)
    => value.Length <= maxLength ? value : value[..maxLength];

  [GeneratedRegex(@"^[A-ZÑ&]{4}\d{6}[A-Z0-9]{3}$", RegexOptions.CultureInvariant)]
  private static partial Regex PhysicalPersonRfcRegex();

  private sealed record NormalizedAttachmentInput(
    int EmployeeId,
    string Rfc,
    string AttachmentName,
    string AttachmentExtension,
    string AttachmentDescription,
    byte[]? Content);
}
