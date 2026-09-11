using Microsoft.Extensions.Configuration;

namespace OrionERP.Infrastructure.Features.Platform;

/// <summary>Temporary, fail-closed aliases for configuration names retired on 2026-12-31.</summary>
public static class LegacyConfigurationAliases
{
  public static bool ApplySectionAlias(
    ConfigurationManager configuration,
    string currentSection,
    string legacySection)
  {
    ArgumentNullException.ThrowIfNull(configuration);
    ArgumentException.ThrowIfNullOrWhiteSpace(currentSection);
    ArgumentException.ThrowIfNullOrWhiteSpace(legacySection);

    var legacyValues = configuration.GetSection(legacySection)
      .AsEnumerable(makePathsRelative: true)
      .Where(item => !string.IsNullOrWhiteSpace(item.Value))
      .ToArray();
    if (legacyValues.Length == 0)
      return false;

    foreach (var legacy in legacyValues)
    {
      var relativeKey = legacy.Key ?? string.Empty;
      var currentKey = string.IsNullOrEmpty(relativeKey)
        ? currentSection
        : $"{currentSection}:{relativeKey}";
      var currentValue = configuration[currentKey];
      if (!string.IsNullOrWhiteSpace(currentValue) &&
          !string.Equals(currentValue, legacy.Value, StringComparison.Ordinal))
      {
        throw new InvalidOperationException(
          $"Configuration sections '{currentSection}' and legacy '{legacySection}' disagree at '{relativeKey}'.");
      }

      if (string.IsNullOrWhiteSpace(currentValue))
        configuration[currentKey] = legacy.Value;
    }

    return true;
  }
}
