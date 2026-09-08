using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Features.Platform;

namespace OrionERP.Infrastructure.Features.Platform;

/// <summary>
/// Resolves the one configured website identity and briefly caches only a
/// successful database verification. A failed verification is never replaced
/// with a fallback tenant.
/// </summary>
public sealed class PublicWebsiteInstanceContext : IPublicWebsiteInstanceContext
{
  private static readonly TimeSpan DefaultValidationInterval = TimeSpan.FromSeconds(30);

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly TimeProvider _timeProvider;
  private readonly ILogger<PublicWebsiteInstanceContext> _logger;
  private readonly TimeSpan _validationInterval;
  private readonly SemaphoreSlim _validationLock = new(1, 1);
  private CacheEntry? _cache;

  public PublicWebsiteInstanceContext(
    PublicWebsiteInstanceDefinition instance,
    PublicWebsitePresentationDefinition presentation,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PublicWebsiteInstanceContext> logger)
    : this(instance, presentation, scopeFactory, timeProvider, logger, DefaultValidationInterval)
  {
  }

  internal PublicWebsiteInstanceContext(
    PublicWebsiteInstanceDefinition instance,
    PublicWebsitePresentationDefinition presentation,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PublicWebsiteInstanceContext> logger,
    TimeSpan validationInterval)
  {
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(presentation);
    ArgumentNullException.ThrowIfNull(scopeFactory);
    ArgumentNullException.ThrowIfNull(timeProvider);
    ArgumentNullException.ThrowIfNull(logger);
    if (validationInterval <= TimeSpan.Zero)
      throw new ArgumentOutOfRangeException(nameof(validationInterval));

    Instance = instance;
    Presentation = presentation;
    _scopeFactory = scopeFactory;
    _timeProvider = timeProvider;
    _logger = logger;
    _validationInterval = validationInterval;
  }

  public PublicWebsiteInstanceDefinition Instance { get; }
  public PublicWebsitePresentationDefinition Presentation { get; }

  public string CurrentRfc => Instance.ExpectedCompanyRfc;

  public async Task<PublicSiteBinding> ResolveRequiredAsync(CancellationToken ct = default)
  {
    var nowTimestamp = _timeProvider.GetTimestamp();
    var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
    var cached = Volatile.Read(ref _cache);
    if (IsCacheFresh(cached, nowTimestamp, nowUtc))
      return cached!.Binding;

    await _validationLock.WaitAsync(ct);
    try
    {
      nowTimestamp = _timeProvider.GetTimestamp();
      nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
      cached = Volatile.Read(ref _cache);
      if (IsCacheFresh(cached, nowTimestamp, nowUtc))
        return cached!.Binding;

      await using var scope = _scopeFactory.CreateAsyncScope();
      var resolver = scope.ServiceProvider.GetRequiredService<IPublicSiteResolver>();
      var binding = await resolver.ResolveRequiredAsync(Instance.ToResolutionRequest(), ct);
      var presentationMatch = PublicWebsitePresentationPolicy.EnsureMatchesBinding(
        Presentation,
        binding,
        nowUtc);

      Volatile.Write(ref _cache, new CacheEntry(
        binding,
        nowTimestamp,
        presentationMatch.ValidUntilUtc));
      _logger.LogInformation(
        "Verified public website binding {PublicSiteKey} at configuration version {ConfigurationVersion} ({PresentationSlot}).",
        binding.PublicSiteKey,
        binding.ConfigurationVersion,
        presentationMatch.IsFallback ? "fallback" : "active");
      return binding;
    }
    finally
    {
      _validationLock.Release();
    }
  }

  private bool IsCacheFresh(CacheEntry? entry, long nowTimestamp, DateTime nowUtc)
    => entry is not null
      && _timeProvider.GetElapsedTime(entry.ValidatedAtTimestamp, nowTimestamp) < _validationInterval
      && (!entry.ValidUntilUtc.HasValue || nowUtc < entry.ValidUntilUtc.Value);

  private sealed record CacheEntry(
    PublicSiteBinding Binding,
    long ValidatedAtTimestamp,
    DateTime? ValidUntilUtc);
}
