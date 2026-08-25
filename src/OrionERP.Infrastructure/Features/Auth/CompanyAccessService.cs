using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Auth.Companies;
using OrionERP.Infrastructure.Auth;
using SkiaSharp;

namespace OrionERP.Infrastructure.Features.Auth;

public sealed partial class CompanyAccessService : ICompanyAccessService
{
  private const int MaxLogoBytes = 2 * 1024 * 1024;
  private static readonly IReadOnlySet<string> AllowedLogoTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
  {
    "image/png", "image/jpeg", "image/webp"
  };

  private readonly OrionIdentityDbContext _db;

  public CompanyAccessService(OrionIdentityDbContext db) => _db = db;

  public async Task<IReadOnlyList<CompanyLoginOption>> GetLoginOptionsAsync(string userId, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(userId) || !_db.Database.IsRelational()) return [];
    var connection = _db.Database.GetDbConnection();
    var close = connection.State == ConnectionState.Closed;
    if (close) await connection.OpenAsync(ct);
    try
    {
      const string sql = """
        SELECT company.Rfc,company.DisplayName,company.LegalName,membership.EmployeeId,
               COALESCE(NULLIF(LTRIM(RTRIM(employee.NombreCorto)),''),
                        NULLIF(LTRIM(RTRIM(CONCAT(employee.Nombre,' ',employee.ApellidoPaterno,' ',employee.ApellidoMaterno))),'')) EmployeeName,
               NULLIF(LTRIM(RTRIM(employee.Puesto)),'') Position,company.BrandingVersion,
               CAST(CASE WHEN company.LogoBytes IS NULL THEN 0 ELSE 1 END AS bit) HasLogo
        FROM auth.AspNetUserCompanies membership
        JOIN orion.Company company ON company.Rfc=membership.Rfc AND company.IsActive=1
        LEFT JOIN dbo.Capital_Humano employee ON employee.ID=membership.EmployeeId AND employee.RFC=membership.Rfc
        WHERE membership.UserId=@UserId AND membership.IsActive=1
        ORDER BY company.DisplayName,company.Rfc;
        """;
      var rows = await connection.QueryAsync<CompanyLoginOption>(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
      return rows.AsList();
    }
    finally
    {
      if (close) await connection.CloseAsync();
    }
  }

  public Task<bool> HasActiveMembershipAsync(string userId, string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    return _db.UserCompanies.AsNoTracking().AnyAsync(membership =>
      membership.UserId == userId && membership.Rfc == normalizedRfc && membership.IsActive && membership.Company.IsActive, ct);
  }

  public async Task<IReadOnlyList<CompanySummary>> GetCompaniesAsync(CancellationToken ct = default)
  {
    if (!_db.Database.IsRelational()) return [];
    var connection = _db.Database.GetDbConnection();
    var close = connection.State == ConnectionState.Closed;
    if (close) await connection.OpenAsync(ct);
    try
    {
      const string sql = """
        SELECT company.Rfc,company.DisplayName,company.LegalName,company.IsActive,company.BrandingVersion,
               CAST(CASE WHEN company.LogoBytes IS NULL THEN 0 ELSE 1 END AS bit) HasLogo,
               (SELECT COUNT(*) FROM auth.AspNetUserCompanies membership WHERE membership.Rfc=company.Rfc AND membership.IsActive=1) MemberCount,
               (SELECT COUNT(*) FROM dbo.Capital_Humano employee WHERE employee.RFC=company.Rfc AND UPPER(LTRIM(RTRIM(ISNULL(employee.[Status],''))))='ACTIVO') EmployeeCount,
               (SELECT COUNT(*) FROM auth.AspNetUserCompanies membership WHERE membership.Rfc=company.Rfc AND membership.AccessReviewRequired=1) ReviewCount,
               CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.SatRfcProfile profile WHERE profile.Rfc=company.Rfc) THEN 1 ELSE 0 END AS bit) HasFiscalProfile,
               company.UpdatedAtUtc
        FROM orion.Company company
        ORDER BY company.IsActive DESC,company.DisplayName,company.Rfc;
        """;
      var rows = await connection.QueryAsync<CompanySummary>(new CommandDefinition(sql, cancellationToken: ct));
      return rows.AsList();
    }
    finally
    {
      if (close) await connection.CloseAsync();
    }
  }

  public async Task<CompanyEditor?> GetCompanyAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    return await _db.Companies.AsNoTracking()
      .Where(company => company.Rfc == normalizedRfc)
      .Select(company => new CompanyEditor(
        company.Rfc, company.DisplayName, company.LegalName, company.IsActive,
        company.BrandingVersion, company.LogoBytes != null))
      .SingleOrDefaultAsync(ct);
  }

  public async Task<CompanyCommandResult> SaveCompanyAsync(CompanySaveRequest request, CancellationToken ct = default)
  {
    var rfc = NormalizeRfc(request.Rfc);
    var displayName = request.DisplayName?.Trim();
    if (!RfcPattern().IsMatch(rfc)) return CompanyCommandResult.Fail("El identificador RFC debe contener entre 12 y 20 letras y números.");
    if (string.IsNullOrWhiteSpace(displayName)) return CompanyCommandResult.Fail("El nombre visible es obligatorio.");
    if (string.IsNullOrWhiteSpace(request.ActorUserId)) return CompanyCommandResult.Fail("No fue posible identificar al administrador.");

    var company = await _db.Companies.SingleOrDefaultAsync(candidate => candidate.Rfc == rfc, ct);
    var action = company is null ? "COMPANY_CREATED" : "COMPANY_UPDATED";
    if (company is null)
    {
      company = new OrionCompany { Rfc = rfc, CreatedAtUtc = DateTime.UtcNow };
      _db.Companies.Add(company);
    }

    company.DisplayName = displayName;
    company.LegalName = NullIfWhiteSpace(request.LegalName);
    company.IsActive = request.IsActive;
    company.UpdatedAtUtc = DateTime.UtcNow;
    company.UpdatedBy = request.ActorUserId;
    AddAudit(request.ActorUserId, action, rfc, new { company.DisplayName, company.LegalName, company.IsActive });
    await InvalidateCompanyUsersAsync(rfc, ct);
    await _db.SaveChangesAsync(ct);
    return CompanyCommandResult.Ok("Empresa guardada correctamente.", rfc);
  }

  public async Task<CompanyCommandResult> SaveLogoAsync(string rfc, byte[] bytes, string contentType, string actorUserId, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var normalizedContentType = contentType?.Trim().ToLowerInvariant() ?? string.Empty;
    if (bytes is not { Length: > 0 } || bytes.Length > MaxLogoBytes)
      return CompanyCommandResult.Fail("El logotipo debe pesar como máximo 2 MB.");
    if (!AllowedLogoTypes.Contains(normalizedContentType) || !HasExpectedSignature(bytes, normalizedContentType))
      return CompanyCommandResult.Fail("Usa un logotipo PNG, JPEG o WebP válido.");

    var normalizedLogo = ResizeLogo(bytes, normalizedContentType);
    if (normalizedLogo is null)
      return CompanyCommandResult.Fail("No fue posible procesar el logotipo. Verifica que el archivo no esté dañado.");
    if (normalizedLogo.Length > MaxLogoBytes)
      return CompanyCommandResult.Fail("El logotipo procesado excede 2 MB. Usa una imagen con menos detalle.");

    var company = await _db.Companies.SingleOrDefaultAsync(candidate => candidate.Rfc == normalizedRfc, ct);
    if (company is null) return CompanyCommandResult.Fail("La empresa no existe.");
    company.LogoBytes = normalizedLogo;
    company.LogoContentType = normalizedContentType;
    company.BrandingVersion++;
    company.UpdatedAtUtc = DateTime.UtcNow;
    company.UpdatedBy = actorUserId;
    AddAudit(actorUserId, "COMPANY_LOGO_UPDATED", normalizedRfc, new { ContentType = normalizedContentType, SourceSize = bytes.Length, StoredSize = normalizedLogo.Length, MaxDimension = 1024 });
    await InvalidateCompanyUsersAsync(normalizedRfc, ct);
    await _db.SaveChangesAsync(ct);
    return CompanyCommandResult.Ok("Logotipo actualizado.", normalizedRfc);
  }

  public async Task<CompanyLogo?> GetLogoAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    return await _db.Companies.AsNoTracking()
      .Where(company => company.Rfc == normalizedRfc && company.LogoBytes != null && company.LogoContentType != null)
      .Select(company => new CompanyLogo(company.LogoBytes!, company.LogoContentType!, company.BrandingVersion))
      .SingleOrDefaultAsync(ct);
  }

  private void AddAudit(string actorUserId, string action, string rfc, object detail)
    => _db.CompanyAccessAudits.Add(new CompanyAccessAudit
    {
      OccurredAtUtc = DateTime.UtcNow,
      ActorUserId = actorUserId,
      Action = action,
      Rfc = rfc,
      DetailJson = JsonSerializer.Serialize(detail)
    });

  private async Task InvalidateCompanyUsersAsync(string rfc, CancellationToken ct)
  {
    var userIds = await _db.UserCompanies.Where(link => link.Rfc == rfc).Select(link => link.UserId).Distinct().ToListAsync(ct);
    var users = await _db.Users.Where(user => userIds.Contains(user.Id)).ToListAsync(ct);
    foreach (var user in users) user.SecurityStamp = Guid.NewGuid().ToString("N");
  }

  private static string NormalizeRfc(string? rfc) => (rfc ?? string.Empty).Trim().ToUpperInvariant();
  private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static bool HasExpectedSignature(byte[] bytes, string contentType) => contentType switch
  {
    "image/png" => bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
    "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
    "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
    _ => false
  };

  private static byte[]? ResizeLogo(byte[] bytes, string contentType)
  {
    try
    {
      using var source = SKBitmap.Decode(bytes);
      if (source is null || source.Width <= 0 || source.Height <= 0) return null;
      var scale = Math.Min(1d, 1024d / Math.Max(source.Width, source.Height));
      var width = Math.Max(1, (int)Math.Round(source.Width * scale));
      var height = Math.Max(1, (int)Math.Round(source.Height * scale));
      using var resized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
      using (var canvas = new SKCanvas(resized))
      {
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
      }

      using var image = SKImage.FromBitmap(resized);
      var format = contentType switch
      {
        "image/jpeg" => SKEncodedImageFormat.Jpeg,
        "image/webp" => SKEncodedImageFormat.Webp,
        _ => SKEncodedImageFormat.Png
      };
      using var data = image.Encode(format, format == SKEncodedImageFormat.Png ? 100 : 88);
      return data?.ToArray();
    }
    catch
    {
      return null;
    }
  }

  [GeneratedRegex("^[A-Z&Ñ0-9]{12,20}$", RegexOptions.CultureInvariant)]
  private static partial Regex RfcPattern();
}
