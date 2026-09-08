using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

public static class PublicWebsiteHostingExtensions
{
  public static IServiceCollection AddPublicWebsiteInstance(
    this IServiceCollection services,
    string connectionString,
    PublicWebsiteInstanceDefinition instance,
    PublicWebsitePresentationDefinition presentation)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(presentation);

    if (!string.Equals(instance.PublicSiteKey, presentation.PublicSiteKey, StringComparison.Ordinal))
      throw new PublicWebsitePresentationException(
        "The public website identity and presentation must use the same PublicSiteKey.");

    services.AddPlatformPublicSiteResolutionServices(connectionString);
    services.AddSingleton(instance);
    services.AddSingleton(presentation);
    services.AddSingleton<PublicWebsiteInstanceContext>();
    services.AddSingleton<IPublicWebsiteInstanceContext>(services =>
      services.GetRequiredService<PublicWebsiteInstanceContext>());
    services.AddSingleton<ICurrentRfcAccessor>(services =>
      services.GetRequiredService<PublicWebsiteInstanceContext>());
    return services;
  }

  /// <summary>
  /// Rejects an unexpected production Host and verifies the configured
  /// PublicSite binding before any website content or business endpoint runs.
  /// The request host is used only as an acceptance check; it never selects an
  /// identity.
  /// </summary>
  public static IApplicationBuilder UseConfiguredPublicWebsite(this IApplicationBuilder app)
  {
    app.UseMiddleware<ConfiguredCanonicalHostMiddleware>();
    app.UseMiddleware<PublicWebsiteBindingGateMiddleware>();
    return app;
  }
}

public sealed class ConfiguredCanonicalHostMiddleware
{
  private readonly RequestDelegate _next;
  private readonly PublicWebsiteInstanceDefinition _instance;
  private readonly IHostEnvironment _environment;

  public ConfiguredCanonicalHostMiddleware(
    RequestDelegate next,
    PublicWebsiteInstanceDefinition instance,
    IHostEnvironment environment)
  {
    _next = next;
    _instance = instance;
    _environment = environment;
  }

  public async Task InvokeAsync(HttpContext context)
  {
    if (IsOperationalProbe(context.Request.Path) || _environment.IsDevelopment())
    {
      await _next(context);
      return;
    }

    var requestedHost = context.Request.Host.Host.Trim().TrimEnd('.').ToLowerInvariant();
    if (string.Equals(requestedHost, _instance.CanonicalHost, StringComparison.Ordinal))
    {
      await _next(context);
      return;
    }

    var wwwAlias = $"www.{_instance.CanonicalHost}";
    if (!_instance.CanonicalHost.StartsWith("www.", StringComparison.Ordinal)
        && string.Equals(requestedHost, wwwAlias, StringComparison.Ordinal))
    {
      var target = $"https://{_instance.CanonicalHost}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
      context.Response.Redirect(target, permanent: true, preserveMethod: true);
      return;
    }

    context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
    context.Response.ContentType = "text/plain; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store";
    await context.Response.WriteAsync("Unexpected website host.", context.RequestAborted);
  }

  private static bool IsOperationalProbe(PathString path)
    => path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
      || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase);
}

public sealed class PublicWebsiteBindingGateMiddleware
{
  public const string BindingItemKey = "OrionERP.PublicWebsiteBinding";

  private readonly RequestDelegate _next;
  private readonly ILogger<PublicWebsiteBindingGateMiddleware> _logger;

  public PublicWebsiteBindingGateMiddleware(
    RequestDelegate next,
    ILogger<PublicWebsiteBindingGateMiddleware> logger)
  {
    _next = next;
    _logger = logger;
  }

  public async Task InvokeAsync(HttpContext context, IPublicWebsiteInstanceContext website)
  {
    if (context.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase))
    {
      await _next(context);
      return;
    }

    try
    {
      context.Items[BindingItemKey] = await website.ResolveRequiredAsync(context.RequestAborted);
    }
    catch (Exception exception)
    {
      _logger.LogError(
        exception,
        "Public website binding verification failed for {PublicSiteKey}.",
        website.Instance.PublicSiteKey);
      context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
      context.Response.ContentType = "text/plain; charset=utf-8";
      context.Response.Headers.CacheControl = "no-store";
      context.Response.Headers.RetryAfter = "30";
      await context.Response.WriteAsync("Website configuration is unavailable.", context.RequestAborted);
      return;
    }

    await _next(context);
  }
}
