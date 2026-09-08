using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform.Data;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed class PlatformAdministrationService : IPlatformAdministrationService
{
  public static readonly TimeSpan PresentationRollbackWindow = TimeSpan.FromMinutes(30);
  private const string ApplicationName = "OrionERP.PlatformAdministration";
  private const string ClearAuditSessionContextSql = """
    EXEC sys.sp_set_session_context @key=N'OrionERP.CorrelationId', @value=NULL, @read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionERP.Application', @value=NULL, @read_only=0;
    EXEC sys.sp_set_session_context @key=N'OrionERP.UserName', @value=NULL, @read_only=0;
    """;
  private readonly PlatformDbContext _db;
  private readonly IPlatformAdministrationScopeAccessor _scopeAccessor;
  private readonly TimeProvider _timeProvider;

  public PlatformAdministrationService(
    PlatformDbContext db,
    IPlatformAdministrationScopeAccessor scopeAccessor,
    TimeProvider timeProvider)
  {
    _db = db;
    _scopeAccessor = scopeAccessor;
    _timeProvider = timeProvider;
  }

  public Task UpdateCompanyMetadataAsync(
    UpdatePlatformCompanyMetadataCommand command,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    var taxRfc = PlatformAdministrationPolicy.NormalizeTaxRfc(command.TaxRfc);
    var legacyTenantKey = PlatformAdministrationPolicy.NormalizeLegacyTenantKey(command.LegacyTenantKey);

    return ExecuteWriteWithoutResultAsync((scope, company, now) =>
    {
      ApplyConcurrencyToken(company, command.ConcurrencyToken);
      company.TaxRfc = taxRfc;
      company.LegacyTenantKey = legacyTenantKey;
      company.UpdatedAtUtc = now;
      company.UpdatedBy = scope.ActorUserId;
      return Task.CompletedTask;
    }, ct);
  }

  public async Task<long> CreateSiteAsync(
    CreatePlatformSiteCommand command,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    var siteKey = PlatformAdministrationPolicy.NormalizeSiteKey(command.SiteKey);
    var displayName = PlatformAdministrationPolicy.NormalizeDisplayName(command.DisplayName);
    var timeZoneId = PlatformAdministrationPolicy.NormalizeTimeZoneId(command.TimeZoneId);

    var entity = await ExecuteWriteAsync(async (scope, company, now) =>
    {
      if (await _db.Sites.AnyAsync(
            site => site.CompanyId == company.CompanyId && site.SiteKey == siteKey,
            ct))
        throw Invalid("Ya existe una sede con esa clave en la empresa actual.");

      var site = new PlatformSiteEntity
      {
        CompanyId = company.CompanyId,
        SiteKey = siteKey,
        DisplayName = displayName,
        TimeZoneId = timeZoneId,
        IsActive = command.IsActive,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        UpdatedBy = scope.ActorUserId
      };
      _db.Sites.Add(site);
      return site;
    }, ct);

    return entity.SiteId;
  }

  public Task UpdateSiteAsync(UpdatePlatformSiteCommand command, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    if (command.SiteId <= 0)
      throw Invalid("La sede indicada no es válida.");
    var displayName = PlatformAdministrationPolicy.NormalizeDisplayName(command.DisplayName);
    var timeZoneId = PlatformAdministrationPolicy.NormalizeTimeZoneId(command.TimeZoneId);

    return ExecuteWriteWithoutResultAsync(async (scope, company, now) =>
    {
      var site = await FindOwnedSiteAsync(company.CompanyId, command.SiteId, ct);
      ApplyConcurrencyToken(site, command.ConcurrencyToken);

      if (!command.IsActive && site.IsActive
          && await _db.PublicSites.AnyAsync(
            publicSite => publicSite.CompanyId == company.CompanyId
              && publicSite.SiteId == site.SiteId
              && publicSite.IsActive,
            ct))
        throw Invalid("Desactiva primero los websites públicos de esta sede.");

      site.DisplayName = displayName;
      site.TimeZoneId = timeZoneId;
      site.IsActive = command.IsActive;
      site.UpdatedAtUtc = now;
      site.UpdatedBy = scope.ActorUserId;
    }, ct);
  }

  public Task DeleteSiteAsync(long siteId, string concurrencyToken, CancellationToken ct = default)
  {
    if (siteId <= 0)
      throw Invalid("La sede indicada no es válida.");

    return ExecuteWriteWithoutResultAsync(async (_, company, _) =>
    {
      var site = await FindOwnedSiteAsync(company.CompanyId, siteId, ct);
      ApplyConcurrencyToken(site, concurrencyToken);
      if (site.IsActive)
        throw Invalid("Desactiva la sede antes de eliminarla.");
      if (await _db.SiteCapabilities.AnyAsync(
            capability => capability.CompanyId == company.CompanyId && capability.SiteId == siteId,
            ct)
          || await _db.PublicSites.AnyAsync(
            publicSite => publicSite.CompanyId == company.CompanyId && publicSite.SiteId == siteId,
            ct))
        throw Invalid("La sede todavía tiene capacidades o websites asociados.");
      _db.Sites.Remove(site);
    }, ct);
  }

  public Task SetCompanyModuleAsync(
    SetPlatformCompanyModuleCommand command,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    var moduleCode = PlatformAdministrationPolicy.NormalizeModuleCode(command.ModuleCode);
    var status = PlatformAdministrationPolicy.NormalizeCompanyModuleStatus(command.Status);
    var (effectiveFromUtc, effectiveToUtc) = PlatformAdministrationPolicy.NormalizeEffectiveRange(
      command.EffectiveFromUtc,
      command.EffectiveToUtc);

    if (moduleCode == PlatformModuleCodes.AccountingCore
        && status != PlatformCompanyModuleStatuses.Enabled)
      throw Invalid("Contabilidad es obligatoria y únicamente puede permanecer habilitada.");
    if (moduleCode == PlatformModuleCodes.AccountingCore
        && (effectiveFromUtc.HasValue || effectiveToUtc.HasValue))
      throw Invalid("Contabilidad es permanente y no admite fechas de vigencia.");

    return ExecuteWriteWithoutResultAsync(async (scope, company, now) =>
    {
      var module = await FindModuleAsync(moduleCode, ct);
      if (!module.IsActive && status == PlatformCompanyModuleStatuses.Enabled)
        throw Invalid("No se puede habilitar un módulo inactivo en el catálogo.");

      var assignment = await _db.CompanyModules.SingleOrDefaultAsync(
        item => item.CompanyId == company.CompanyId && item.ModuleCode == moduleCode,
        ct);
      if (assignment is null)
      {
        if (!string.IsNullOrWhiteSpace(command.ConcurrencyToken))
          throw new PlatformAdministrationConcurrencyException();
        assignment = new PlatformCompanyModuleEntity
        {
          CompanyId = company.CompanyId,
          ModuleCode = moduleCode,
          Status = status,
          ConfigurationVersion = 1,
          EffectiveFromUtc = effectiveFromUtc,
          EffectiveToUtc = effectiveToUtc,
          CreatedAtUtc = now,
          UpdatedAtUtc = now,
          UpdatedBy = scope.ActorUserId
        };
        _db.CompanyModules.Add(assignment);
        return;
      }

      ApplyConcurrencyToken(assignment, command.ConcurrencyToken);
      if (status != PlatformCompanyModuleStatuses.Enabled
          && assignment.Status == PlatformCompanyModuleStatuses.Enabled
          && await _db.SiteCapabilities.AnyAsync(
            capability => capability.CompanyId == company.CompanyId
              && capability.ModuleCode == moduleCode
              && capability.IsEnabled,
            ct))
        throw Invalid("Deshabilita primero las capacidades activas de este módulo.");

      assignment.Status = status;
      assignment.EffectiveFromUtc = effectiveFromUtc;
      assignment.EffectiveToUtc = effectiveToUtc;
      assignment.ConfigurationVersion = checked(assignment.ConfigurationVersion + 1);
      assignment.UpdatedAtUtc = now;
      assignment.UpdatedBy = scope.ActorUserId;
    }, ct);
  }

  public Task DeleteCompanyModuleAsync(
    string moduleCode,
    string concurrencyToken,
    CancellationToken ct = default)
  {
    var normalizedModuleCode = PlatformAdministrationPolicy.NormalizeModuleCode(moduleCode);
    if (normalizedModuleCode == PlatformModuleCodes.AccountingCore)
      throw Invalid("La asignación de Contabilidad es obligatoria y no se puede eliminar.");

    return ExecuteWriteWithoutResultAsync(async (_, company, _) =>
    {
      var assignment = await FindOwnedCompanyModuleAsync(company.CompanyId, normalizedModuleCode, ct);
      ApplyConcurrencyToken(assignment, concurrencyToken);
      if (assignment.Status == PlatformCompanyModuleStatuses.Enabled)
        throw Invalid("Suspende el módulo antes de eliminar su asignación.");
      if (await _db.SiteCapabilities.AnyAsync(
            capability => capability.CompanyId == company.CompanyId
              && capability.ModuleCode == normalizedModuleCode,
            ct)
          || await _db.PublicSites.AnyAsync(
            publicSite => publicSite.CompanyId == company.CompanyId
              && publicSite.ModuleCode == normalizedModuleCode,
            ct))
        throw Invalid("El módulo todavía tiene capacidades o websites asociados.");
      _db.CompanyModules.Remove(assignment);
    }, ct);
  }

  public Task SetSiteCapabilityAsync(
    SetPlatformSiteCapabilityCommand command,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    if (command.SiteId <= 0)
      throw Invalid("La sede indicada no es válida.");
    var moduleCode = PlatformAdministrationPolicy.NormalizeModuleCode(command.ModuleCode);
    if (!PlatformModuleCodes.PublicWebsiteModules.Contains(moduleCode))
      throw Invalid("Contabilidad no es una capacidad asignable a una sede.");

    return ExecuteWriteWithoutResultAsync(async (scope, company, now) =>
    {
      var site = await FindOwnedSiteAsync(company.CompanyId, command.SiteId, ct);
      var module = await FindModuleAsync(moduleCode, ct);
      var assignment = await FindOwnedCompanyModuleAsync(company.CompanyId, moduleCode, ct);

      if (!module.RequiresSite)
        throw Invalid("El módulo seleccionado no admite capacidades por sede.");
      if (command.IsEnabled && (!site.IsActive || !module.IsActive
          || !PublicSiteResolutionPolicy.IsEffective(
            assignment.Status,
            assignment.EffectiveFromUtc,
            assignment.EffectiveToUtc,
            now)))
        throw Invalid("Para habilitar la capacidad, la sede y el módulo deben estar activos y vigentes.");

      var capability = await _db.SiteCapabilities.SingleOrDefaultAsync(
        item => item.CompanyId == company.CompanyId
          && item.SiteId == command.SiteId
          && item.ModuleCode == moduleCode,
        ct);
      if (capability is null)
      {
        if (!string.IsNullOrWhiteSpace(command.ConcurrencyToken))
          throw new PlatformAdministrationConcurrencyException();
        capability = new PlatformSiteCapabilityEntity
        {
          CompanyId = company.CompanyId,
          SiteId = command.SiteId,
          ModuleCode = moduleCode,
          IsEnabled = command.IsEnabled,
          CreatedAtUtc = now,
          UpdatedAtUtc = now,
          UpdatedBy = scope.ActorUserId
        };
        _db.SiteCapabilities.Add(capability);
        return;
      }

      ApplyConcurrencyToken(capability, command.ConcurrencyToken);
      if (!command.IsEnabled && capability.IsEnabled
          && await _db.PublicSites.AnyAsync(
            publicSite => publicSite.CompanyId == company.CompanyId
              && publicSite.SiteId == command.SiteId
              && publicSite.ModuleCode == moduleCode
              && publicSite.IsActive,
            ct))
        throw Invalid("Desactiva primero el website público ligado a esta capacidad.");

      capability.IsEnabled = command.IsEnabled;
      capability.UpdatedAtUtc = now;
      capability.UpdatedBy = scope.ActorUserId;
    }, ct);
  }

  public Task DeleteSiteCapabilityAsync(
    long siteId,
    string moduleCode,
    string concurrencyToken,
    CancellationToken ct = default)
  {
    if (siteId <= 0)
      throw Invalid("La sede indicada no es válida.");
    var normalizedModuleCode = PlatformAdministrationPolicy.NormalizeModuleCode(moduleCode);

    return ExecuteWriteWithoutResultAsync(async (_, company, _) =>
    {
      var capability = await FindOwnedCapabilityAsync(
        company.CompanyId,
        siteId,
        normalizedModuleCode,
        ct);
      ApplyConcurrencyToken(capability, concurrencyToken);
      if (capability.IsEnabled)
        throw Invalid("Deshabilita la capacidad antes de eliminarla.");
      if (await _db.PublicSites.AnyAsync(
          publicSite => publicSite.CompanyId == company.CompanyId
            && publicSite.SiteId == siteId
            && publicSite.ModuleCode == normalizedModuleCode,
          ct))
        throw Invalid("La capacidad todavía tiene un website asociado.");
      _db.SiteCapabilities.Remove(capability);
    }, ct);
  }

  public async Task<long> CreatePublicSiteAsync(
    CreatePlatformPublicSiteCommand command,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    if (command.SiteId <= 0)
      throw Invalid("La sede indicada no es válida.");
    var publicSiteKey = PlatformAdministrationPolicy.NormalizePublicSiteKey(command.PublicSiteKey);
    var moduleCode = PlatformAdministrationPolicy.NormalizeModuleCode(command.ModuleCode);
    var canonicalHost = PlatformAdministrationPolicy.NormalizeCanonicalHost(command.CanonicalHost);

    var entity = await ExecuteWriteAsync(async (scope, company, now) =>
    {
      var chain = await GetPublicActivationChainAsync(company, command.SiteId, moduleCode, ct);
      if (command.IsActive)
        EnsureCompleteActivationChain(company, chain, now);

      if (await _db.PublicSites.AnyAsync(
            publicSite => publicSite.PublicSiteKey == publicSiteKey
              || publicSite.CanonicalHost == canonicalHost,
            ct))
        throw Invalid("La clave del website o el dominio ya están registrados.");
      if (await _db.PublicSites.AnyAsync(
            publicSite => publicSite.CompanyId == company.CompanyId
              && publicSite.SiteId == command.SiteId
              && publicSite.ModuleCode == moduleCode,
            ct))
        throw Invalid("Esta sede ya tiene un website para el módulo seleccionado.");

      var publicSite = new PlatformPublicSiteEntity
      {
        PublicSiteKey = publicSiteKey,
        CompanyId = company.CompanyId,
        SiteId = command.SiteId,
        ModuleCode = moduleCode,
        CanonicalHost = canonicalHost,
        IsActive = command.IsActive,
        ConfigurationVersion = 1,
        BrandingVersion = 1,
        ContentVersion = 1,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        UpdatedBy = scope.ActorUserId
      };
      _db.PublicSites.Add(publicSite);
      return publicSite;
    }, ct);

    return entity.PublicSiteId;
  }

  public Task UpdatePublicSiteAsync(
    UpdatePlatformPublicSiteCommand command,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    if (command.PublicSiteId <= 0)
      throw Invalid("El website indicado no es válido.");
    var canonicalHost = PlatformAdministrationPolicy.NormalizeCanonicalHost(command.CanonicalHost);
    var brandingVersion = PlatformAdministrationPolicy.NormalizePublicSiteVersion(
      command.BrandingVersion,
      "marca");
    var contentVersion = PlatformAdministrationPolicy.NormalizePublicSiteVersion(
      command.ContentVersion,
      "contenido");

    return ExecuteWriteWithoutResultAsync(async (scope, company, now) =>
    {
      var publicSite = await FindOwnedPublicSiteAsync(company.CompanyId, command.PublicSiteId, ct);
      ApplyConcurrencyToken(publicSite, command.ConcurrencyToken);
      var chain = await GetPublicActivationChainAsync(
        company,
        publicSite.SiteId,
        publicSite.ModuleCode,
        ct);
      if (command.IsActive)
        EnsureCompleteActivationChain(company, chain, now);

      if (await _db.PublicSites.AnyAsync(
          item => item.PublicSiteId != publicSite.PublicSiteId
            && item.CanonicalHost == canonicalHost,
          ct))
        throw Invalid("El dominio ya está registrado para otro website.");

      if (brandingVersion < publicSite.BrandingVersion
          || contentVersion < publicSite.ContentVersion)
        throw Invalid("Las versiones de marca y contenido no pueden disminuir.");

      EnsureValidPresentationFallback(publicSite);
      var presentationChanged = brandingVersion != publicSite.BrandingVersion
        || contentVersion != publicSite.ContentVersion;
      if (presentationChanged)
      {
        if (publicSite.FallbackUntilUtc > now)
          throw Invalid("Finaliza o revierte la activación de presentación vigente antes de preparar otra versión.");

        publicSite.FallbackBrandingVersion = publicSite.BrandingVersion;
        publicSite.FallbackContentVersion = publicSite.ContentVersion;
        publicSite.FallbackUntilUtc = now.Add(PresentationRollbackWindow);
      }
      else if (publicSite.FallbackUntilUtc <= now)
      {
        ClearPresentationFallback(publicSite);
      }

      publicSite.CanonicalHost = canonicalHost;
      publicSite.IsActive = command.IsActive;
      publicSite.BrandingVersion = brandingVersion;
      publicSite.ContentVersion = contentVersion;
      publicSite.ConfigurationVersion = checked(publicSite.ConfigurationVersion + 1);
      publicSite.UpdatedAtUtc = now;
      publicSite.UpdatedBy = scope.ActorUserId;
    }, ct);
  }

  public Task RollbackPublicSitePresentationAsync(
    long publicSiteId,
    string concurrencyToken,
    CancellationToken ct = default)
  {
    if (publicSiteId <= 0)
      throw Invalid("El website indicado no es válido.");

    return ExecuteWriteWithoutResultAsync(async (scope, company, now) =>
    {
      var publicSite = await FindOwnedPublicSiteAsync(company.CompanyId, publicSiteId, ct);
      ApplyConcurrencyToken(publicSite, concurrencyToken);
      EnsureValidPresentationFallback(publicSite);
      if (!publicSite.FallbackUntilUtc.HasValue)
        throw Invalid("El website no tiene una versión anterior preparada para reversión.");
      if (publicSite.FallbackUntilUtc.Value <= now)
        throw Invalid("La ventana de reversión ya venció; no se modificaron las versiones activas.");

      publicSite.BrandingVersion = publicSite.FallbackBrandingVersion!.Value;
      publicSite.ContentVersion = publicSite.FallbackContentVersion!.Value;
      ClearPresentationFallback(publicSite);
      publicSite.ConfigurationVersion = checked(publicSite.ConfigurationVersion + 1);
      publicSite.UpdatedAtUtc = now;
      publicSite.UpdatedBy = scope.ActorUserId;
    }, ct);
  }

  public Task FinalizePublicSitePresentationAsync(
    long publicSiteId,
    string concurrencyToken,
    CancellationToken ct = default)
  {
    if (publicSiteId <= 0)
      throw Invalid("El website indicado no es válido.");

    return ExecuteWriteWithoutResultAsync(async (scope, company, now) =>
    {
      var publicSite = await FindOwnedPublicSiteAsync(company.CompanyId, publicSiteId, ct);
      ApplyConcurrencyToken(publicSite, concurrencyToken);
      EnsureValidPresentationFallback(publicSite);
      if (!publicSite.FallbackUntilUtc.HasValue)
        throw Invalid("El website no tiene una activación de presentación por finalizar.");

      ClearPresentationFallback(publicSite);
      publicSite.ConfigurationVersion = checked(publicSite.ConfigurationVersion + 1);
      publicSite.UpdatedAtUtc = now;
      publicSite.UpdatedBy = scope.ActorUserId;
    }, ct);
  }

  public Task DeletePublicSiteAsync(
    long publicSiteId,
    string concurrencyToken,
    CancellationToken ct = default)
  {
    if (publicSiteId <= 0)
      throw Invalid("El website indicado no es válido.");

    return ExecuteWriteWithoutResultAsync(async (_, company, _) =>
    {
      var publicSite = await FindOwnedPublicSiteAsync(company.CompanyId, publicSiteId, ct);
      ApplyConcurrencyToken(publicSite, concurrencyToken);
      if (publicSite.IsActive)
        throw Invalid("Desactiva el website antes de eliminarlo.");
      _db.PublicSites.Remove(publicSite);
    }, ct);
  }

  private async Task<TResult> ExecuteWriteAsync<TResult>(
    Func<PlatformAdministrationScope, PlatformCompanyEntity, DateTime, Task<TResult>> operation,
    CancellationToken ct)
  {
    var scope = await _scopeAccessor.GetRequiredScopeAsync(ct);
    Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction = null;
    var openedConnection = false;
    try
    {
      if (_db.Database.IsRelational())
      {
        await _db.Database.OpenConnectionAsync(ct);
        openedConnection = true;
        // Administration is low-volume and depends on a complete entitlement
        // chain. Serializable isolation keeps each validation and write atomic.
        transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var correlationId = Guid.NewGuid();
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
          EXEC sys.sp_set_session_context @key=N'OrionERP.UserName', @value={scope.ActorUserId}, @read_only=0;
          EXEC sys.sp_set_session_context @key=N'OrionERP.Application', @value={ApplicationName}, @read_only=0;
          EXEC sys.sp_set_session_context @key=N'OrionERP.CorrelationId', @value={correlationId}, @read_only=0;
          """, ct);
      }

      var company = await FindScopedCompanyAsync(scope.CompanyRfc, ct);
      var now = _timeProvider.GetUtcNow().UtcDateTime;
      var result = await operation(scope, company, now);
      await _db.SaveChangesAsync(ct);
      if (transaction is not null)
        await transaction.CommitAsync(ct);
      _db.ChangeTracker.Clear();
      return result;
    }
    catch (DbUpdateConcurrencyException)
    {
      if (transaction is not null)
        await transaction.RollbackAsync(CancellationToken.None);
      _db.ChangeTracker.Clear();
      throw new PlatformAdministrationConcurrencyException();
    }
    catch (DbUpdateException exception)
    {
      if (transaction is not null)
        await transaction.RollbackAsync(CancellationToken.None);
      _db.ChangeTracker.Clear();
      throw TranslatePersistenceFailure(exception);
    }
    catch
    {
      if (transaction is not null)
        await transaction.RollbackAsync(CancellationToken.None);
      _db.ChangeTracker.Clear();
      throw;
    }
    finally
    {
      if (transaction is not null)
        await transaction.DisposeAsync();
      if (openedConnection)
      {
        try
        {
          // SESSION_CONTEXT belongs to the physical SQL session. Clear it
          // before the connection can return to the shared pool.
          await _db.Database.ExecuteSqlRawAsync(
            ClearAuditSessionContextSql,
            CancellationToken.None);
        }
        catch
        {
          // A connection whose context could not be cleared must not be reused.
          if (_db.Database.GetDbConnection() is SqlConnection sqlConnection)
            SqlConnection.ClearPool(sqlConnection);
        }
        finally
        {
          await _db.Database.CloseConnectionAsync();
        }
      }
    }
  }

  private async Task ExecuteWriteWithoutResultAsync(
    Func<PlatformAdministrationScope, PlatformCompanyEntity, DateTime, Task> operation,
    CancellationToken ct)
    => await ExecuteWriteAsync(async (scope, company, now) =>
    {
      await operation(scope, company, now);
      return true;
    }, ct);

  private async Task<PlatformCompanyEntity> FindScopedCompanyAsync(string rfc, CancellationToken ct)
  {
    var normalizedRfc = rfc.Trim().ToUpperInvariant();
    var company = await _db.Companies.SingleOrDefaultAsync(item => item.Rfc == normalizedRfc, ct)
      ?? throw new UnauthorizedAccessException("La empresa de la sesión no está registrada en la plataforma.");
    if (!company.IsActive)
      throw new UnauthorizedAccessException("La empresa de la sesión está inactiva.");
    return company;
  }

  private async Task<PlatformSiteEntity> FindOwnedSiteAsync(long companyId, long siteId, CancellationToken ct)
    => await _db.Sites.SingleOrDefaultAsync(
        site => site.CompanyId == companyId && site.SiteId == siteId,
        ct)
      ?? throw Invalid("La sede no existe en la empresa actual.");

  private async Task<PlatformModuleEntity> FindModuleAsync(string moduleCode, CancellationToken ct)
    => await _db.Modules.SingleOrDefaultAsync(module => module.ModuleCode == moduleCode, ct)
      ?? throw Invalid("El módulo no está disponible en el catálogo.");

  private async Task<PlatformCompanyModuleEntity> FindOwnedCompanyModuleAsync(
    long companyId,
    string moduleCode,
    CancellationToken ct)
    => await _db.CompanyModules.SingleOrDefaultAsync(
        assignment => assignment.CompanyId == companyId && assignment.ModuleCode == moduleCode,
        ct)
      ?? throw Invalid("El módulo no está asignado a la empresa actual.");

  private async Task<PlatformSiteCapabilityEntity> FindOwnedCapabilityAsync(
    long companyId,
    long siteId,
    string moduleCode,
    CancellationToken ct)
    => await _db.SiteCapabilities.SingleOrDefaultAsync(
        capability => capability.CompanyId == companyId
          && capability.SiteId == siteId
          && capability.ModuleCode == moduleCode,
        ct)
      ?? throw Invalid("La capacidad no existe en la empresa actual.");

  private async Task<PlatformPublicSiteEntity> FindOwnedPublicSiteAsync(
    long companyId,
    long publicSiteId,
    CancellationToken ct)
    => await _db.PublicSites.SingleOrDefaultAsync(
        publicSite => publicSite.CompanyId == companyId && publicSite.PublicSiteId == publicSiteId,
        ct)
      ?? throw Invalid("El website no existe en la empresa actual.");

  private static void EnsureValidPresentationFallback(PlatformPublicSiteEntity publicSite)
  {
    var hasBranding = publicSite.FallbackBrandingVersion.HasValue;
    var hasContent = publicSite.FallbackContentVersion.HasValue;
    var hasExpiry = publicSite.FallbackUntilUtc.HasValue;
    if (hasBranding != hasContent
        || hasBranding != hasExpiry
        || (hasBranding
          && (publicSite.FallbackBrandingVersion <= 0
            || publicSite.FallbackContentVersion <= 0)))
      throw Invalid("La ventana de reversión del website está incompleta o dañada.");
  }

  private static void ClearPresentationFallback(PlatformPublicSiteEntity publicSite)
  {
    publicSite.FallbackBrandingVersion = null;
    publicSite.FallbackContentVersion = null;
    publicSite.FallbackUntilUtc = null;
  }

  private async Task<PublicActivationChain> GetPublicActivationChainAsync(
    PlatformCompanyEntity company,
    long siteId,
    string moduleCode,
    CancellationToken ct)
  {
    if (!PlatformModuleCodes.PublicWebsiteModules.Contains(moduleCode))
      throw Invalid("El módulo seleccionado no puede exponerse como website público.");
    var site = await FindOwnedSiteAsync(company.CompanyId, siteId, ct);
    var module = await FindModuleAsync(moduleCode, ct);
    var assignment = await FindOwnedCompanyModuleAsync(company.CompanyId, moduleCode, ct);
    var capability = await FindOwnedCapabilityAsync(company.CompanyId, siteId, moduleCode, ct);
    return new PublicActivationChain(site, module, assignment, capability);
  }

  private static void EnsureCompleteActivationChain(
    PlatformCompanyEntity company,
    PublicActivationChain chain,
    DateTime now)
  {
    if (!PlatformAdministrationPolicy.IsCompletePublicActivationChain(
      company.IsActive,
      chain.Site.IsActive,
      chain.Module.IsActive,
      chain.Module.RequiresSite,
      chain.Module.ModuleCode,
      chain.Assignment.Status,
      chain.Assignment.EffectiveFromUtc,
      chain.Assignment.EffectiveToUtc,
      chain.Capability.IsEnabled,
      now))
      throw Invalid("No se puede activar el website: empresa, sede, módulo y capacidad deben estar activos y vigentes.");
  }

  private void ApplyConcurrencyToken<TEntity>(TEntity entity, string? token)
    where TEntity : class
  {
    var expected = PlatformAdministrationPolicy.DecodeConcurrencyToken(token);
    var property = _db.Entry(entity).Property<byte[]>(nameof(PlatformSiteEntity.RowVersion));
    if (!property.CurrentValue.AsSpan().SequenceEqual(expected))
      throw new PlatformAdministrationConcurrencyException();
    property.OriginalValue = expected;
  }

  private static PlatformAdministrationValidationException Invalid(string message)
    => new(message);

  private static PlatformAdministrationValidationException TranslatePersistenceFailure(
    DbUpdateException exception)
  {
    if (exception.InnerException is not SqlException sqlException)
      return Invalid("La operación contradice otra configuración o cambió mientras se guardaba. Actualiza e inténtalo de nuevo.");

    return sqlException.Number switch
    {
      2601 or 2627 => Invalid("La clave, el RFC fiscal o el dominio ya están registrados."),
      547 => Invalid("La configuración todavía tiene relaciones que impiden completar la operación."),
      51235 => Invalid("La empresa y la clave técnica de una sede son inmutables."),
      51236 => Invalid("Desactiva primero los websites públicos de la sede."),
      51237 => Invalid("Contabilidad debe permanecer habilitada para la empresa."),
      51238 => Invalid("Deshabilita primero las capacidades activas del módulo."),
      51239 => Invalid("La capacidad requiere empresa, sede y módulo habilitados."),
      51240 => Invalid("Desactiva primero el website público de la capacidad."),
      51241 => Invalid("La clave y el vínculo de un website son inmutables."),
      51242 => Invalid("El website requiere empresa, sede, módulo y capacidad activos."),
      _ => Invalid("La operación contradice otra configuración o cambió mientras se guardaba. Actualiza e inténtalo de nuevo.")
    };
  }

  private sealed record PublicActivationChain(
    PlatformSiteEntity Site,
    PlatformModuleEntity Module,
    PlatformCompanyModuleEntity Assignment,
    PlatformSiteCapabilityEntity Capability);
}
