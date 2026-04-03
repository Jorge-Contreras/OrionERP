using System.Data;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.BusinessPartners;
using OrionERP.Application.Features.Logistica.Shared;

namespace OrionERP.Infrastructure.Features.Logistica.BusinessPartners;

public sealed class BusinessPartnerService : IBusinessPartnerService
{
  private static readonly string[] DefaultRoles =
  [
    "Vendor",
    "ServiceProvider",
    "Utility",
    "Landlord",
    "Customer"
  ];

  private readonly IDbConnectionFactory _connectionFactory;

  public BusinessPartnerService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<BusinessPartnerListItemDto>> GetPartnersAsync(BusinessPartnerFilter filter, CancellationToken ct = default)
  {
    filter ??= new BusinessPartnerFilter();

    var sql = new StringBuilder(
      """
      WITH PartnerRoles AS (
          SELECT
              r.BusinessPartnerId,
              MAX(CASE WHEN r.RoleCode = 'Vendor' THEN 1 ELSE 0 END) AS HasVendorRole,
              MIN(r.RoleCode) AS PrimaryRole
          FROM dbo.BusinessPartnerRole r
          GROUP BY r.BusinessPartnerId
      ),
      MaterialCounts AS (
          SELECT m.BusinessPartnerId, COUNT(*) AS MaterialCount
          FROM logistica.Material m
          WHERE m.BusinessPartnerId IS NOT NULL
          GROUP BY m.BusinessPartnerId
      )
      SELECT
          bp.Id,
          bp.LegacyProveedorId,
          bp.PartnerName AS DisplayName,
          bp.Rfc,
          bp.Email,
          bp.Phone,
          bp.IsActive,
          CAST(CASE WHEN vp.BusinessPartnerId IS NOT NULL THEN 1 ELSE 0 END AS bit) AS HasVendorProfile,
          COALESCE(pr.PrimaryRole, 'Unassigned') AS PrimaryRole,
          ISNULL(mc.MaterialCount, 0) AS MaterialCount
      FROM dbo.BusinessPartner bp
      LEFT JOIN PartnerRoles pr
        ON pr.BusinessPartnerId = bp.Id
      LEFT JOIN logistica.VendorProfile vp
        ON vp.BusinessPartnerId = bp.Id
      LEFT JOIN MaterialCounts mc
        ON mc.BusinessPartnerId = bp.Id
      WHERE 1 = 1
      """);

    var parameters = new DynamicParameters();

    if (!filter.IncludeInactive)
    {
      sql.AppendLine(" AND bp.IsActive = 1");
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql.AppendLine(" AND (bp.PartnerName LIKE @Search OR bp.Rfc LIKE @Search OR bp.Email LIKE @Search OR bp.Phone LIKE @Search)");
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    if (!string.IsNullOrWhiteSpace(filter.Role))
    {
      sql.AppendLine(" AND EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = @Role)");
      parameters.Add("@Role", filter.Role.Trim(), DbType.String);
    }

    if (filter.VendorOnly)
    {
      sql.AppendLine(
        """
         AND (
             EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = 'Vendor')
             OR EXISTS (SELECT 1 FROM logistica.VendorProfile vp2 WHERE vp2.BusinessPartnerId = bp.Id)
         )
        """);
    }

    sql.AppendLine("ORDER BY bp.PartnerName, bp.Id;");

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<BusinessPartnerListItemDto>(
      new CommandDefinition(sql.ToString(), parameters, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<BusinessPartnerDetailDto?> GetPartnerAsync(int businessPartnerId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          bp.Id,
          bp.LegacyProveedorId,
          bp.PartnerName AS DisplayName,
          bp.Rfc,
          bp.Email,
          bp.Phone,
          bp.Street,
          bp.Neighborhood,
          bp.City,
          bp.[State] AS [State],
          bp.PostalCode,
          bp.BusinessLine,
          bp.Notes,
          bp.IsActive
      FROM dbo.BusinessPartner bp
      WHERE bp.Id = @BusinessPartnerId;

      SELECT r.RoleCode
      FROM dbo.BusinessPartnerRole r
      WHERE r.BusinessPartnerId = @BusinessPartnerId
      ORDER BY r.RoleCode;

      SELECT
          vp.BusinessPartnerId,
          vp.PaymentTerms,
          vp.DefaultLeadTimeDays,
          vp.IsApproved,
          vp.Notes
      FROM logistica.VendorProfile vp
      WHERE vp.BusinessPartnerId = @BusinessPartnerId;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { BusinessPartnerId = businessPartnerId }, cancellationToken: ct));

    var detail = await multi.ReadFirstOrDefaultAsync<BusinessPartnerDetailDto>();
    if (detail is null)
    {
      return null;
    }

    detail.Roles = (await multi.ReadAsync<string>()).AsList();
    detail.VendorProfile = await multi.ReadFirstOrDefaultAsync<VendorProfileDto>();
    return detail;
  }

  public async Task<IReadOnlyList<LookupOptionDto>> GetVendorLookupAsync(CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          bp.Id,
          bp.PartnerName AS Name,
          bp.Rfc AS Code
      FROM dbo.BusinessPartner bp
      WHERE bp.IsActive = 1
        AND (
            EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole r WHERE r.BusinessPartnerId = bp.Id AND r.RoleCode = 'Vendor')
            OR EXISTS (SELECT 1 FROM logistica.VendorProfile vp WHERE vp.BusinessPartnerId = bp.Id)
        )
      ORDER BY bp.PartnerName, bp.Id;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<LookupOptionDto>(new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
  }

  public Task<BusinessPartnerCatalogDto> GetCatalogAsync(CancellationToken ct = default)
    => Task.FromResult(new BusinessPartnerCatalogDto
    {
      Roles = DefaultRoles.Select(role => new LookupOptionDto { Name = role, Code = role }).ToArray()
    });

  public async Task<LogisticsCommandResult> SavePartnerAsync(BusinessPartnerUpsertRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var name = request.DisplayName?.Trim();
    if (string.IsNullOrWhiteSpace(name))
    {
      return LogisticsCommandResult.Fail("El nombre del socio de negocio es obligatorio.");
    }

    var roles = request.Roles
      .Where(role => !string.IsNullOrWhiteSpace(role))
      .Select(role => role.Trim())
      .Distinct(StringComparer.OrdinalIgnoreCase)
      .ToList();

    if (request.VendorProfile is not null && !roles.Contains("Vendor", StringComparer.OrdinalIgnoreCase))
    {
      roles.Add("Vendor");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      var partnerId = request.Id ?? 0;

      if (request.Id.HasValue && request.Id.Value > 0)
      {
        const string updateSql =
          """
          UPDATE dbo.BusinessPartner
          SET PartnerName = @PartnerName,
              Rfc = @Rfc,
              Email = @Email,
              Phone = @Phone,
              Street = @Street,
              Neighborhood = @Neighborhood,
              City = @City,
              [State] = @State,
              PostalCode = @PostalCode,
              BusinessLine = @BusinessLine,
              Notes = @Notes,
              IsActive = @IsActive,
              UpdatedAt = SYSUTCDATETIME()
          WHERE Id = @Id;
          """;

        await conn.ExecuteAsync(
          new CommandDefinition(
            updateSql,
            new
            {
              Id = request.Id.Value,
              PartnerName = name,
              Rfc = NullIfWhiteSpace(request.Rfc),
              Email = NullIfWhiteSpace(request.Email),
              Phone = NullIfWhiteSpace(request.Phone),
              Street = NullIfWhiteSpace(request.Street),
              Neighborhood = NullIfWhiteSpace(request.Neighborhood),
              City = NullIfWhiteSpace(request.City),
              State = NullIfWhiteSpace(request.State),
              PostalCode = NullIfWhiteSpace(request.PostalCode),
              BusinessLine = NullIfWhiteSpace(request.BusinessLine),
              Notes = NullIfWhiteSpace(request.Notes),
              request.IsActive
            },
            tx,
            cancellationToken: ct));
      }
      else
      {
        const string insertSql =
          """
          INSERT INTO dbo.BusinessPartner
          (
              LegacyProveedorId,
              PartnerName,
              Rfc,
              Email,
              Phone,
              Street,
              Neighborhood,
              City,
              [State],
              PostalCode,
              BusinessLine,
              Notes,
              IsActive
          )
          VALUES
          (
              @LegacyProveedorId,
              @PartnerName,
              @Rfc,
              @Email,
              @Phone,
              @Street,
              @Neighborhood,
              @City,
              @State,
              @PostalCode,
              @BusinessLine,
              @Notes,
              @IsActive
          );

          SELECT CAST(SCOPE_IDENTITY() AS int);
          """;

        partnerId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            insertSql,
            new
            {
              request.LegacyProveedorId,
              PartnerName = name,
              Rfc = NullIfWhiteSpace(request.Rfc),
              Email = NullIfWhiteSpace(request.Email),
              Phone = NullIfWhiteSpace(request.Phone),
              Street = NullIfWhiteSpace(request.Street),
              Neighborhood = NullIfWhiteSpace(request.Neighborhood),
              City = NullIfWhiteSpace(request.City),
              State = NullIfWhiteSpace(request.State),
              PostalCode = NullIfWhiteSpace(request.PostalCode),
              BusinessLine = NullIfWhiteSpace(request.BusinessLine),
              Notes = NullIfWhiteSpace(request.Notes),
              request.IsActive
            },
            tx,
            cancellationToken: ct));
      }

      await conn.ExecuteAsync(
        new CommandDefinition(
          "DELETE FROM dbo.BusinessPartnerRole WHERE BusinessPartnerId = @BusinessPartnerId;",
          new { BusinessPartnerId = partnerId },
          tx,
          cancellationToken: ct));

      if (roles.Count > 0)
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            "INSERT INTO dbo.BusinessPartnerRole (BusinessPartnerId, RoleCode) VALUES (@BusinessPartnerId, @RoleCode);",
            roles.Select(role => new { BusinessPartnerId = partnerId, RoleCode = role }),
            tx,
            cancellationToken: ct));
      }

      if (roles.Contains("Vendor", StringComparer.OrdinalIgnoreCase) || request.VendorProfile is not null)
      {
        const string vendorSql =
          """
          MERGE logistica.VendorProfile AS target
          USING (SELECT @BusinessPartnerId AS BusinessPartnerId) AS src
          ON target.BusinessPartnerId = src.BusinessPartnerId
          WHEN MATCHED THEN
              UPDATE SET
                  PaymentTerms = @PaymentTerms,
                  DefaultLeadTimeDays = @DefaultLeadTimeDays,
                  IsApproved = @IsApproved,
                  Notes = @Notes,
                  UpdatedAt = SYSUTCDATETIME()
          WHEN NOT MATCHED THEN
              INSERT (BusinessPartnerId, PaymentTerms, DefaultLeadTimeDays, IsApproved, Notes)
              VALUES (@BusinessPartnerId, @PaymentTerms, @DefaultLeadTimeDays, @IsApproved, @Notes);
          """;

        await conn.ExecuteAsync(
          new CommandDefinition(
            vendorSql,
            new
            {
              BusinessPartnerId = partnerId,
              PaymentTerms = NullIfWhiteSpace(request.VendorProfile?.PaymentTerms),
              request.VendorProfile?.DefaultLeadTimeDays,
              IsApproved = request.VendorProfile?.IsApproved ?? true,
              Notes = NullIfWhiteSpace(request.VendorProfile?.Notes)
            },
            tx,
            cancellationToken: ct));
      }
      else
      {
        await conn.ExecuteAsync(
          new CommandDefinition(
            "DELETE FROM logistica.VendorProfile WHERE BusinessPartnerId = @BusinessPartnerId;",
            new { BusinessPartnerId = partnerId },
            tx,
            cancellationToken: ct));
      }

      await tx.CommitAsync(ct);
      return LogisticsCommandResult.Ok($"Socio de negocio {name} guardado correctamente.", partnerId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return LogisticsCommandResult.Fail("Ya existe un socio de negocio con la misma clave o relación heredada.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private SqlConnection CreateConnection()
    => _connectionFactory.Create() as SqlConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una SqlConnection.");

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
