using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

public sealed partial class PublicSiteProvisioner : IPublicSiteProvisioner
{
  private readonly string _connectionString;

  public PublicSiteProvisioner(IConfiguration configuration)
  {
    _connectionString = configuration.GetConnectionString("OrionDb")
      ?? throw new InvalidOperationException("Missing ConnectionStrings:OrionDb.");
  }

  public async Task<PublicSiteProvisioningPlan> PreviewAsync(
    PublicSiteProvisioningRequest request,
    CancellationToken ct = default)
  {
    var errors = Validate(request);
    if (errors.Count > 0)
      return Plan(request, errors, []);

    await using var connection = new SqlConnection(_connectionString);
    await connection.OpenAsync(ct);
    var state = await connection.QuerySingleAsync<ProvisioningState>(new CommandDefinition(
      """
      SELECT
        CONVERT(bit,CASE WHEN EXISTS
        (
          SELECT 1 FROM orion.Company
          WHERE Rfc=@Rfc AND LegacyTenantKey=@LegacyTenantKey
            AND ISNULL(TaxRfc,'')=ISNULL(@TaxRfc,'')
            AND DisplayName=@DisplayName AND ISNULL(LegalName,'')=ISNULL(@LegalName,'')
            AND IsActive=1
        ) THEN 1 ELSE 0 END) CompanyReady,
        (SELECT COUNT(*) FROM orion.Site siteInfo
         JOIN orion.Company companyInfo ON companyInfo.CompanyId=siteInfo.CompanyId
         JOIN OPENJSON(@SitesJson) WITH
           (SiteKey varchar(100) '$.SiteKey',DisplayName nvarchar(200) '$.DisplayName',TimeZoneId nvarchar(100) '$.TimeZoneId') requested
           ON requested.SiteKey=siteInfo.SiteKey
         WHERE companyInfo.Rfc=@Rfc AND siteInfo.DisplayName=requested.DisplayName
           AND siteInfo.TimeZoneId=requested.TimeZoneId AND siteInfo.IsActive=1) SiteCount,
        (SELECT COUNT(*) FROM OPENJSON(@SitesJson) WITH
           (SiteKey varchar(100) '$.SiteKey',EnabledModules nvarchar(max) '$.EnabledModules' AS JSON) requested
         CROSS APPLY OPENJSON(requested.EnabledModules) WITH(ModuleCode varchar(40) '$') requestedModule
         JOIN orion.Company companyInfo ON companyInfo.Rfc=@Rfc
         JOIN orion.Site siteInfo ON siteInfo.CompanyId=companyInfo.CompanyId AND siteInfo.SiteKey=requested.SiteKey
         JOIN orion.SiteCapability capability ON capability.CompanyId=companyInfo.CompanyId
           AND capability.SiteId=siteInfo.SiteId AND capability.ModuleCode=requestedModule.ModuleCode
           AND capability.IsEnabled=1) CapabilityCount,
        (SELECT COUNT(*) FROM orion.SiteCapability capability
         JOIN orion.Company companyInfo ON companyInfo.CompanyId=capability.CompanyId AND companyInfo.Rfc=@Rfc
         JOIN orion.Site siteInfo ON siteInfo.CompanyId=companyInfo.CompanyId AND siteInfo.SiteId=capability.SiteId
         JOIN OPENJSON(@SitesJson) WITH
           (SiteKey varchar(100) '$.SiteKey',EnabledModules nvarchar(max) '$.EnabledModules' AS JSON) requested
           ON requested.SiteKey=siteInfo.SiteKey
         WHERE capability.IsEnabled=1 AND capability.ModuleCode IN('HOSPITALITY','RESTAURANT')
           AND NOT EXISTS(SELECT 1 FROM OPENJSON(requested.EnabledModules) WITH(ModuleCode varchar(40) '$') requestedModule
             WHERE requestedModule.ModuleCode=capability.ModuleCode)) UnexpectedCapabilityCount,
        (SELECT COUNT(*) FROM orion.PublicSite publicSite
         JOIN orion.Company companyInfo ON companyInfo.CompanyId=publicSite.CompanyId
         JOIN orion.Site siteInfo ON siteInfo.CompanyId=publicSite.CompanyId AND siteInfo.SiteId=publicSite.SiteId
         JOIN OPENJSON(@PublicSitesJson) WITH
           (PublicSiteKey varchar(100) '$.PublicSiteKey',SiteKey varchar(100) '$.SiteKey',
            ModuleCode varchar(40) '$.ModuleCode',CanonicalHost varchar(253) '$.CanonicalHost') requested
           ON requested.PublicSiteKey=publicSite.PublicSiteKey
         WHERE companyInfo.Rfc=@Rfc AND siteInfo.SiteKey=requested.SiteKey
           AND publicSite.ModuleCode=requested.ModuleCode AND publicSite.CanonicalHost=requested.CanonicalHost
           AND publicSite.IsActive=1) PublicSiteCount,
        (SELECT COUNT(*)
         FROM OPENJSON(@PublicSitesJson) WITH
           (PublicSiteKey varchar(100) '$.PublicSiteKey',ModuleCode varchar(40) '$.ModuleCode') requested
         JOIN orion.PublicSite publicSite ON publicSite.PublicSiteKey=requested.PublicSiteKey
         WHERE requested.ModuleCode<>'RESTAURANT' OR EXISTS
         (
           SELECT 1 FROM restaurante.Site localSite
           WHERE localSite.OrionCompanyId=publicSite.CompanyId AND localSite.OrionSiteId=publicSite.SiteId
         )) ModuleBindingCount,
        (SELECT COUNT(*)
         FROM OPENJSON(@PublicSitesJson) WITH(SqlPrincipalName sysname '$.SqlPrincipalName') requested
         WHERE requested.SqlPrincipalName IS NOT NULL
           AND DATABASE_PRINCIPAL_ID(requested.SqlPrincipalName) IS NOT NULL) SqlPrincipalCount,
        (SELECT COUNT(*)
         FROM OPENJSON(@PublicSitesJson) WITH
           (PublicSiteKey varchar(100) '$.PublicSiteKey',SqlPrincipalName sysname '$.SqlPrincipalName') requested
         JOIN orion.PublicSite publicSite ON publicSite.PublicSiteKey=requested.PublicSiteKey
         JOIN orion.PublicSqlPrincipalBinding binding
           ON binding.PrincipalName=requested.SqlPrincipalName AND binding.PublicSiteId=publicSite.PublicSiteId
          AND binding.IsActive=1
         WHERE requested.SqlPrincipalName IS NOT NULL) PermissionBindingCount,
        CONVERT(bit,CASE WHEN EXISTS
        (
          SELECT 1 FROM orion.ProvisioningOperation
          WHERE OperationId=@OperationId AND [Status]='Completed'
        ) THEN 1 ELSE 0 END) OperationCompleted,
        CONVERT(bit,CASE
          WHEN NOT EXISTS(SELECT 1 FROM orion.ProvisioningOperation WHERE OperationId=@OperationId) THEN 1
          WHEN EXISTS
          (
            SELECT 1 FROM orion.ProvisioningOperation operationInfo
            WHERE operationInfo.OperationId=@OperationId
              AND (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='Rfc')=@Rfc
              AND (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='LegacyTenantKey')=@LegacyTenantKey
              AND (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='SitesJson')=@SitesJson
              AND (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='PublicSitesJson')=@PublicSitesJson
          ) THEN 1
          WHEN EXISTS
          (
            SELECT 1 FROM orion.ProvisioningOperation operationInfo
            WHERE operationInfo.OperationId=@OperationId AND operationInfo.[Status]='Completed'
              AND NOT EXISTS(SELECT 1 FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='Rfc')
          ) THEN 1
          ELSE 0 END) OperationRequestMatches;
      """,
      Parameters(request),
      cancellationToken: ct));

    var steps = BuildSteps(request, state);
    var stateErrors = new List<string>();
    if (!state.OperationRequestMatches)
      stateErrors.Add("OperationId ya pertenece a una solicitud de provisionamiento diferente.");
    if (state.OperationCompleted && steps.Where(step => step.StepCode != "health").Any(step => !step.AlreadySatisfied))
      stateErrors.Add("OperationId completado no coincide con el estado solicitado; use un identificador nuevo.");
    return Plan(request, stateErrors, steps);
  }

  public async Task<PublicSiteProvisioningPlan> ApplyAsync(
    PublicSiteProvisioningRequest request,
    CancellationToken ct = default)
  {
    var preview = await PreviewAsync(request, ct);
    if (!preview.CanApply)
      return preview;

    await using (var connection = new SqlConnection(_connectionString))
    {
      await connection.OpenAsync(ct);
      await connection.ExecuteAsync(new CommandDefinition(
        ApplySql,
        Parameters(request),
        commandTimeout: 120,
        cancellationToken: ct));
      await ApplyPermissionProfilesAsync(connection,request,ct);
      await FinalizeOperationAsync(connection,request,ct);
    }
    return await PreviewAsync(request, ct);
  }

  private static async Task ApplyPermissionProfilesAsync(
    SqlConnection connection,PublicSiteProvisioningRequest request,CancellationToken ct)
  {
    foreach (var publicSite in request.PublicSites.Where(site=>!string.IsNullOrWhiteSpace(site.SqlPrincipalName)))
    {
      var principal=publicSite.SqlPrincipalName!.Trim();
      var exists=await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT CONVERT(bit,CASE WHEN DATABASE_PRINCIPAL_ID(@PrincipalName) IS NULL THEN 0 ELSE 1 END);",
        new { PrincipalName=principal },cancellationToken:ct));
      if (!exists) continue;
      await connection.ExecuteAsync(new CommandDefinition(
        "orion.ApplyPublicPermissionProfile",
        new { PublicSiteKey=publicSite.PublicSiteKey.Trim().ToLowerInvariant(),PrincipalName=principal,ApplyChanges=true },
        commandType:System.Data.CommandType.StoredProcedure,commandTimeout:120,cancellationToken:ct));
    }
  }

  private static Task FinalizeOperationAsync(
    SqlConnection connection,PublicSiteProvisioningRequest request,CancellationToken ct)
    => connection.ExecuteAsync(new CommandDefinition("""
      DECLARE @Requested int=(SELECT COUNT(*) FROM OPENJSON(@PublicSitesJson)
        WITH(SqlPrincipalName sysname '$.SqlPrincipalName') WHERE SqlPrincipalName IS NOT NULL);
      DECLARE @Ready int=(SELECT COUNT(*) FROM OPENJSON(@PublicSitesJson) WITH
        (PublicSiteKey varchar(100) '$.PublicSiteKey',SqlPrincipalName sysname '$.SqlPrincipalName') requested
        JOIN orion.PublicSite publicSite ON publicSite.PublicSiteKey=requested.PublicSiteKey
        JOIN orion.PublicSqlPrincipalBinding binding
          ON binding.PrincipalName=requested.SqlPrincipalName AND binding.PublicSiteId=publicSite.PublicSiteId
         AND binding.IsActive=1
        WHERE requested.SqlPrincipalName IS NOT NULL);
      UPDATE orion.ProvisioningOperation
      SET [Status]=CASE WHEN @Ready=@Requested THEN 'Completed' ELSE 'Applying' END,
        UpdatedAtUtc=SYSUTCDATETIME(),LastError=NULL
      WHERE OperationId=@OperationId;
      """,Parameters(request),cancellationToken:ct));

  private static object Parameters(PublicSiteProvisioningRequest request) => new
  {
    request.OperationId,
    Rfc = request.Rfc.Trim().ToUpperInvariant(),
    TaxRfc = NormalizeOptional(request.TaxRfc)?.ToUpperInvariant(),
    LegacyTenantKey = request.LegacyTenantKey.Trim().ToLowerInvariant(),
    DisplayName = request.DisplayName.Trim(),
    LegalName = NormalizeOptional(request.LegalName),
    SitesJson = JsonSerializer.Serialize(request.Sites.Select(site => new
    {
      SiteKey = site.SiteKey.Trim().ToLowerInvariant(),
      DisplayName = site.DisplayName.Trim(),
      TimeZoneId = site.TimeZoneId.Trim(),
      EnabledModules = (site.EnabledModules ?? []).Select(code => code.Trim().ToUpperInvariant())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
    })),
    PublicSitesJson = JsonSerializer.Serialize(request.PublicSites.Select(site => new
    {
      PublicSiteKey = site.PublicSiteKey.Trim().ToLowerInvariant(),
      SiteKey = site.SiteKey.Trim().ToLowerInvariant(),
      ModuleCode = site.ModuleCode.Trim().ToUpperInvariant(),
      CanonicalHost = site.CanonicalHost.Trim().TrimEnd('.').ToLowerInvariant(),
      SqlPrincipalName = NormalizeOptional(site.SqlPrincipalName)
    }))
  };

  private static List<string> Validate(PublicSiteProvisioningRequest request)
  {
    var errors = new List<string>();
    if (request.OperationId == Guid.Empty) errors.Add("OperationId es obligatorio.");
    if (!RfcPattern().IsMatch(request.Rfc?.Trim() ?? string.Empty)) errors.Add("Rfc no es una identidad técnica válida.");
    if (!KeyPattern().IsMatch(request.LegacyTenantKey?.Trim() ?? string.Empty)) errors.Add("LegacyTenantKey no es válido.");
    if (string.IsNullOrWhiteSpace(request.DisplayName)) errors.Add("DisplayName es obligatorio.");
    if (request.Sites is not { Count: > 0 }) errors.Add("Debe existir al menos una sede.");
    if (request.PublicSites is not { Count: > 0 }) errors.Add("Debe existir al menos un PublicSite.");

    var sites = request.Sites ?? [];
    var publicSites = request.PublicSites ?? [];
    var siteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var site in sites)
    {
      var siteKey = site.SiteKey?.Trim() ?? string.Empty;
      if (!KeyPattern().IsMatch(siteKey) || !siteKeys.Add(siteKey))
        errors.Add($"SiteKey inválido o duplicado: {site.SiteKey}.");
      if (string.IsNullOrWhiteSpace(site.DisplayName) || string.IsNullOrWhiteSpace(site.TimeZoneId))
        errors.Add($"La sede {site.SiteKey} requiere nombre y zona horaria.");
      foreach (var module in site.EnabledModules ?? [])
        if (!IsPublicModule(module)) errors.Add($"Módulo público no soportado: {module}.");
      if ((site.EnabledModules ?? []).Select(module => module?.Trim())
          .Distinct(StringComparer.OrdinalIgnoreCase).Count() != (site.EnabledModules ?? []).Count)
        errors.Add($"La sede {site.SiteKey} contiene módulos duplicados.");
      if ((site.EnabledModules ?? []).Any(module =>
            string.Equals(module, PlatformModuleCodes.Restaurant, StringComparison.OrdinalIgnoreCase)) &&
          siteKey.Length > 30)
        errors.Add($"SiteKey {site.SiteKey} excede el binding Restaurant de 30 caracteres.");
    }

    var publicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var principals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var capabilityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var publicSite in publicSites)
    {
      var publicSiteKey = publicSite.PublicSiteKey?.Trim() ?? string.Empty;
      var canonicalHost = publicSite.CanonicalHost?.Trim().TrimEnd('.') ?? string.Empty;
      if (!KeyPattern().IsMatch(publicSiteKey) || !publicKeys.Add(publicSiteKey))
        errors.Add($"PublicSiteKey inválido o duplicado: {publicSite.PublicSiteKey}.");
      if (!siteKeys.Contains(publicSite.SiteKey ?? string.Empty)) errors.Add($"PublicSite {publicSite.PublicSiteKey} usa una sede inexistente.");
      if (!IsPublicModule(publicSite.ModuleCode)) errors.Add($"Módulo de PublicSite no soportado: {publicSite.ModuleCode}.");
      if (!HostPattern().IsMatch(canonicalHost) || !hosts.Add(canonicalHost))
        errors.Add($"Host inválido o duplicado: {publicSite.CanonicalHost}.");
      if (string.IsNullOrWhiteSpace(publicSite.SqlPrincipalName))
        errors.Add($"PublicSite {publicSite.PublicSiteKey} requiere un principal SQL independiente.");
      else if (!PrincipalPattern().IsMatch(publicSite.SqlPrincipalName.Trim())
            || !principals.Add(publicSite.SqlPrincipalName.Trim()))
        errors.Add($"Principal SQL inválido o duplicado: {publicSite.SqlPrincipalName}.");
      if (!capabilityKeys.Add($"{publicSite.SiteKey}|{publicSite.ModuleCode}"))
        errors.Add($"Solo puede existir un PublicSite activo por sede y módulo: {publicSite.SiteKey}/{publicSite.ModuleCode}.");
      var site = sites.FirstOrDefault(candidate =>
        string.Equals(candidate.SiteKey, publicSite.SiteKey, StringComparison.OrdinalIgnoreCase));
      if (site is not null && !(site.EnabledModules ?? []).Any(module =>
            string.Equals(module, publicSite.ModuleCode, StringComparison.OrdinalIgnoreCase)))
        errors.Add($"PublicSite {publicSite.PublicSiteKey} no tiene una capacidad habilitada.");
    }
    return errors.Distinct(StringComparer.Ordinal).ToList();
  }

  private static IReadOnlyList<PublicSiteProvisioningStep> BuildSteps(
    PublicSiteProvisioningRequest request,
    ProvisioningState state)
  {
    var principalCount=request.PublicSites.Count(site=>!string.IsNullOrWhiteSpace(site.SqlPrincipalName));
    var capabilityCount=request.Sites.Sum(site=>(site.EnabledModules ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    return
    [
      new("company", "Crear o verificar la identidad técnica de empresa.", state.CompanyReady, false),
      new("sites", "Crear sedes y habilitar capacidades por módulo.",
        state.SiteCount == request.Sites.Count && state.CapabilityCount == capabilityCount && state.UnexpectedCapabilityCount == 0, false),
      new("bindings", "Crear bindings locales explícitos para los módulos que los requieren.", state.ModuleBindingCount == request.PublicSites.Count, false),
      new("public-sites", "Crear PublicSites y configuración pública mínima.", state.PublicSiteCount == request.PublicSites.Count, false),
      new("sql-principals", "Crear logins/usuarios y entregar contraseñas mediante el almacén secreto.",
        state.SqlPrincipalCount == principalCount, true),
      new("permissions", "Aplicar el manifiesto SQL exacto de cada módulo.", state.PermissionBindingCount == principalCount, false),
      new("health", "Validar RLS, identidad, permisos y salud de cada host.", state.OperationCompleted, true)
    ];
  }

  private static PublicSiteProvisioningPlan Plan(
    PublicSiteProvisioningRequest request,
    IReadOnlyList<string> errors,
    IReadOnlyList<PublicSiteProvisioningStep> steps)
    => new(request.OperationId, request.Rfc, errors.Count == 0, errors, steps);

  private static bool IsPublicModule(string? value)
    => string.Equals(value?.Trim(), PlatformModuleCodes.Hospitality, StringComparison.OrdinalIgnoreCase)
       || string.Equals(value?.Trim(), PlatformModuleCodes.Restaurant, StringComparison.OrdinalIgnoreCase);

  private static string? NormalizeOptional(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class ProvisioningState
  {
    public bool CompanyReady { get; set; }
    public int SiteCount { get; set; }
    public int CapabilityCount { get; set; }
    public int UnexpectedCapabilityCount { get; set; }
    public int PublicSiteCount { get; set; }
    public int ModuleBindingCount { get; set; }
    public int SqlPrincipalCount { get; set; }
    public int PermissionBindingCount { get; set; }
    public bool OperationCompleted { get; set; }
    public bool OperationRequestMatches { get; set; }
  }

  [GeneratedRegex("^[A-Z0-9&Ñ]{12,20}$", RegexOptions.CultureInvariant)]
  private static partial Regex RfcPattern();
  [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,99}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex KeyPattern();
  [GeneratedRegex("^(?=.{3,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex HostPattern();
  [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{2,127}$",RegexOptions.CultureInvariant)]
  private static partial Regex PrincipalPattern();

  private const string ApplySql = """
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
    BEGIN TRANSACTION;
    BEGIN TRY
      DECLARE @LockResult int;
      DECLARE @LockResource nvarchar(255)=N'OrionERP:Provisioning:'+CONVERT(nvarchar(36),@OperationId);
      EXEC @LockResult=sys.sp_getapplock
        @Resource=@LockResource,
        @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
      IF @LockResult<0 THROW 52500,'No fue posible obtener el lease de provisionamiento.',1;

      IF EXISTS
      (
        SELECT 1 FROM orion.ProvisioningOperation operationInfo
        WHERE operationInfo.OperationId=@OperationId
          AND EXISTS(SELECT 1 FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='Rfc')
          AND
          (
            (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='Rfc')<>@Rfc OR
            (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='LegacyTenantKey')<>@LegacyTenantKey OR
            (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='SitesJson')<>@SitesJson OR
            (SELECT [value] FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='PublicSitesJson')<>@PublicSitesJson
          )
      ) THROW 52499,'OperationId ya pertenece a otra solicitud de provisionamiento.',1;

      IF EXISTS
      (
        SELECT 1 FROM orion.ProvisioningOperation operationInfo
        WHERE operationInfo.OperationId=@OperationId AND operationInfo.[Status]<>'Completed'
          AND NOT EXISTS(SELECT 1 FROM OPENJSON(operationInfo.RequestJson) WHERE [key]='Rfc')
      ) THROW 52498,'La operación heredada no contiene una solicitud reanudable.',1;

      IF EXISTS(SELECT 1 FROM orion.ProvisioningOperation WHERE OperationId=@OperationId AND [Status]='Completed')
      BEGIN
        COMMIT TRANSACTION;
        RETURN;
      END;

      IF EXISTS(SELECT 1 FROM orion.Company WHERE LegacyTenantKey=@LegacyTenantKey AND Rfc<>@Rfc)
        THROW 52501,'LegacyTenantKey ya pertenece a otra empresa.',1;
      IF EXISTS(SELECT 1 FROM orion.Company WHERE Rfc=@Rfc AND LegacyTenantKey IS NOT NULL AND LegacyTenantKey<>@LegacyTenantKey)
        THROW 52502,'Rfc ya pertenece a otro tenant heredado.',1;

      IF NOT EXISTS(SELECT 1 FROM orion.Company WHERE Rfc=@Rfc)
        INSERT orion.Company(Rfc,TaxRfc,LegacyTenantKey,DisplayName,LegalName,IsActive,UpdatedBy)
        VALUES(@Rfc,@TaxRfc,@LegacyTenantKey,@DisplayName,@LegalName,1,N'PublicSiteProvisioner');
      ELSE
        UPDATE orion.Company SET TaxRfc=COALESCE(TaxRfc,@TaxRfc),LegacyTenantKey=@LegacyTenantKey,
          DisplayName=@DisplayName,LegalName=@LegalName,IsActive=1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'PublicSiteProvisioner'
        WHERE Rfc=@Rfc;

      DECLARE @CompanyId bigint=(SELECT CompanyId FROM orion.Company WHERE Rfc=@Rfc);
      DECLARE @Sites TABLE(SiteKey varchar(100) PRIMARY KEY,DisplayName nvarchar(200),TimeZoneId nvarchar(100),EnabledModules nvarchar(max));
      INSERT @Sites SELECT SiteKey,DisplayName,TimeZoneId,EnabledModules
      FROM OPENJSON(@SitesJson) WITH
      (SiteKey varchar(100),DisplayName nvarchar(200),TimeZoneId nvarchar(100),EnabledModules nvarchar(max) AS JSON);
      DECLARE @RequestedModules TABLE(ModuleCode varchar(40) PRIMARY KEY);
      INSERT @RequestedModules SELECT DISTINCT moduleInfo.ModuleCode
      FROM @Sites requested CROSS APPLY OPENJSON(requested.EnabledModules) WITH(ModuleCode varchar(40) '$') moduleInfo;
      IF EXISTS(SELECT 1 FROM @RequestedModules requested LEFT JOIN orion.Module moduleInfo ON moduleInfo.ModuleCode=requested.ModuleCode AND moduleInfo.IsActive=1 WHERE moduleInfo.ModuleCode IS NULL)
        THROW 52503,'El catálogo no contiene todos los módulos solicitados.',1;

      UPDATE target SET DisplayName=requested.DisplayName,TimeZoneId=requested.TimeZoneId,IsActive=1,
        UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'PublicSiteProvisioner'
      FROM orion.Site target JOIN @Sites requested ON requested.SiteKey=target.SiteKey
      WHERE target.CompanyId=@CompanyId;
      INSERT orion.Site(CompanyId,SiteKey,DisplayName,TimeZoneId,IsActive,UpdatedBy)
      SELECT @CompanyId,requested.SiteKey,requested.DisplayName,requested.TimeZoneId,1,N'PublicSiteProvisioner'
      FROM @Sites requested WHERE NOT EXISTS
        (SELECT 1 FROM orion.Site target WHERE target.CompanyId=@CompanyId AND target.SiteKey=requested.SiteKey);

      MERGE orion.CompanyModule AS target
      USING @RequestedModules AS source
        ON target.CompanyId=@CompanyId AND target.ModuleCode=source.ModuleCode
      WHEN MATCHED THEN UPDATE SET [Status]='Enabled',EffectiveFromUtc=NULL,EffectiveToUtc=NULL,
        ConfigurationVersion=target.ConfigurationVersion+1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'PublicSiteProvisioner'
      WHEN NOT MATCHED THEN INSERT(CompanyId,ModuleCode,[Status],UpdatedBy)
        VALUES(@CompanyId,source.ModuleCode,'Enabled',N'PublicSiteProvisioner');

      MERGE orion.SiteCapability AS target
      USING
      (
        SELECT @CompanyId CompanyId,siteInfo.SiteId,moduleInfo.ModuleCode
        FROM @Sites requested JOIN orion.Site siteInfo
          ON siteInfo.CompanyId=@CompanyId AND siteInfo.SiteKey=requested.SiteKey
        CROSS APPLY OPENJSON(requested.EnabledModules) WITH(ModuleCode varchar(40) '$') moduleInfo
      ) AS source
        ON target.CompanyId=source.CompanyId AND target.SiteId=source.SiteId AND target.ModuleCode=source.ModuleCode
      WHEN MATCHED THEN UPDATE SET IsEnabled=1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'PublicSiteProvisioner'
      WHEN NOT MATCHED THEN INSERT(CompanyId,SiteId,ModuleCode,IsEnabled,UpdatedBy)
        VALUES(source.CompanyId,source.SiteId,source.ModuleCode,1,N'PublicSiteProvisioner');

      INSERT restaurante.Site(Rfc,SiteCode,[Name],TimeZoneId,IsEnabled,OrionCompanyId,OrionSiteId)
      SELECT @Rfc,requested.SiteKey,requested.DisplayName,requested.TimeZoneId,1,@CompanyId,platformSite.SiteId
      FROM @Sites requested JOIN orion.Site platformSite
        ON platformSite.CompanyId=@CompanyId AND platformSite.SiteKey=requested.SiteKey
      WHERE EXISTS(SELECT 1 FROM OPENJSON(requested.EnabledModules) WITH(ModuleCode varchar(40) '$') moduleInfo WHERE moduleInfo.ModuleCode='RESTAURANT')
        AND NOT EXISTS(SELECT 1 FROM restaurante.Site localSite WHERE localSite.OrionCompanyId=@CompanyId AND localSite.OrionSiteId=platformSite.SiteId);
      UPDATE localSite SET OrionCompanyId=@CompanyId,OrionSiteId=platformSite.SiteId,IsEnabled=1,UpdatedAt=SYSUTCDATETIME()
      FROM restaurante.Site localSite JOIN @Sites requested ON requested.SiteKey=localSite.SiteCode
      JOIN orion.Site platformSite ON platformSite.CompanyId=@CompanyId AND platformSite.SiteKey=requested.SiteKey
      WHERE localSite.Rfc=@Rfc AND EXISTS
        (SELECT 1 FROM OPENJSON(requested.EnabledModules) WITH(ModuleCode varchar(40) '$') moduleInfo WHERE moduleInfo.ModuleCode='RESTAURANT');

      DECLARE @PublicSites TABLE(PublicSiteKey varchar(100) PRIMARY KEY,SiteKey varchar(100),ModuleCode varchar(40),CanonicalHost varchar(253),SqlPrincipalName sysname NULL);
      INSERT @PublicSites SELECT PublicSiteKey,SiteKey,ModuleCode,CanonicalHost,SqlPrincipalName
      FROM OPENJSON(@PublicSitesJson) WITH
      (PublicSiteKey varchar(100),SiteKey varchar(100),ModuleCode varchar(40),CanonicalHost varchar(253),SqlPrincipalName sysname);
      IF EXISTS(SELECT 1 FROM @PublicSites requested JOIN orion.PublicSite target ON target.PublicSiteKey=requested.PublicSiteKey WHERE target.CompanyId<>@CompanyId)
        THROW 52504,'PublicSiteKey ya pertenece a otra empresa.',1;
      MERGE orion.PublicSite AS target
      USING
      (
        SELECT requested.PublicSiteKey,@CompanyId CompanyId,siteInfo.SiteId,requested.ModuleCode,requested.CanonicalHost
        FROM @PublicSites requested JOIN orion.Site siteInfo
          ON siteInfo.CompanyId=@CompanyId AND siteInfo.SiteKey=requested.SiteKey
      ) AS source ON target.PublicSiteKey=source.PublicSiteKey
      WHEN MATCHED THEN UPDATE SET CanonicalHost=source.CanonicalHost,IsActive=1,
        ConfigurationVersion=target.ConfigurationVersion+1,UpdatedAtUtc=SYSUTCDATETIME(),UpdatedBy=N'PublicSiteProvisioner'
      WHEN NOT MATCHED THEN INSERT(PublicSiteKey,CompanyId,SiteId,ModuleCode,CanonicalHost,IsActive,UpdatedBy)
        VALUES(source.PublicSiteKey,source.CompanyId,source.SiteId,source.ModuleCode,source.CanonicalHost,1,N'PublicSiteProvisioner');

      INSERT restaurante.PublicSiteSettings
      (PublicSiteId,Rfc,SiteId,LegalName,PublicName,HeroEyebrow,HeroTitle,HeroDescription,
       AddressLine,Neighborhood,PostalCode,City,StateName,CountryName,WhatsAppPhone,
       WhatsAppDisplay,MapsUrl,OpeningHoursJson,SeoDescription,IsWebsiteEnabled,
       IsMembershipEnabled,IsLoyaltyAccrualEnabled,IsPromotionsEnabled,UpdatedBy)
      SELECT publicSite.PublicSiteId,@Rfc,localSite.Id,COALESCE(@LegalName,@DisplayName),@DisplayName,
        N'Restaurant',@DisplayName,N'Experiencia pública configurada por PublicSite.',N'',N'',N'',N'',N'',N'México',N'',N'',N'',N'[]',
        N'Sitio público de restaurante.',1,1,1,1,N'PublicSiteProvisioner'
      FROM @PublicSites requested JOIN orion.PublicSite publicSite ON publicSite.PublicSiteKey=requested.PublicSiteKey
      JOIN orion.Site platformSite ON platformSite.SiteId=publicSite.SiteId AND platformSite.CompanyId=@CompanyId
      JOIN restaurante.Site localSite ON localSite.OrionCompanyId=@CompanyId AND localSite.OrionSiteId=platformSite.SiteId
      WHERE requested.ModuleCode='RESTAURANT' AND NOT EXISTS
        (SELECT 1 FROM restaurante.PublicSiteSettings target WHERE target.PublicSiteId=publicSite.PublicSiteId);

      MERGE orion.ProvisioningOperation AS target
      USING(SELECT @OperationId OperationId) source ON target.OperationId=source.OperationId
      WHEN MATCHED THEN UPDATE SET [Status]='Applying',CompanyId=@CompanyId,UpdatedAtUtc=SYSUTCDATETIME(),LastError=NULL
      WHEN NOT MATCHED THEN INSERT(OperationId,OperationKind,[Status],CompanyId,RequestJson,CreatedAtUtc,UpdatedAtUtc)
        VALUES(@OperationId,'PUBLIC_SITE','Applying',@CompanyId,
          (SELECT @Rfc Rfc,@LegacyTenantKey LegacyTenantKey,@SitesJson SitesJson,@PublicSitesJson PublicSitesJson FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
          SYSUTCDATETIME(),SYSUTCDATETIME());
      COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
      IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
      THROW;
    END CATCH;
    """;
}
