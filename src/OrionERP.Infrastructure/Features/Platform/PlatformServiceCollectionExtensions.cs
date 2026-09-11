using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrionERP.Application.Features.Platform;
using OrionERP.Infrastructure.Features.Platform.Data;

namespace OrionERP.Infrastructure.Features.Platform;

public static class PlatformServiceCollectionExtensions
{
  /// <summary>
  /// Registers only the projection and fail-closed resolver required by a
  /// public website. It deliberately has no authenticated administration
  /// dependencies.
  /// </summary>
  public static IServiceCollection AddPlatformPublicSiteResolutionServices(
    this IServiceCollection services,
    string connectionString)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

    services.AddDbContext<PlatformDbContext>(options => options.UseSqlServer(connectionString));
    services.TryAddSingleton(TimeProvider.System);
    services.AddScoped<PublicSiteResolver>();
    services.AddScoped<IPublicSiteResolver>(provider => provider.GetRequiredService<PublicSiteResolver>());
    services.AddScoped<IPlatformExecutionScopeResolver>(provider => provider.GetRequiredService<PublicSiteResolver>());
    services.AddScoped<IOrionSqlSessionFactory>(_ => new OrionSqlSessionFactory(connectionString));
    services.AddScoped<IModuleSiteBindingResolver, ModuleSiteBindingResolver>();
    services.AddScoped<IEnabledModuleSiteEnumerator, EnabledModuleSiteEnumerator>();
    services.AddScoped<IModuleJobLeaseManager, ModuleJobLeaseManager>();
    services.AddScoped<IPublicIntegrationSettingsResolver, PublicIntegrationSettingsResolver>();
    return services;
  }

  /// <summary>
  /// Registers the company-scoped platform administration foundation. This
  /// method does not seed, provision or enable any company module or public site.
  /// </summary>
  public static IServiceCollection AddPlatformFoundationReadServices(
    this IServiceCollection services,
    string connectionString)
  {
    services.AddPlatformPublicSiteResolutionServices(connectionString);
    services.AddScoped<IPlatformAdministrationAccessValidator, PlatformAdministrationAccessValidator>();
    services.AddScoped<IPlatformAdministrationScopeAccessor, PlatformAdministrationScopeAccessor>();
    services.AddScoped<IPlatformAdministrationReader, PlatformAdministrationReader>();
    services.AddScoped<IPlatformAdministrationService, PlatformAdministrationService>();
    services.AddScoped<IPublicSiteProvisioner, PublicSiteProvisioner>();
    return services;
  }
}
