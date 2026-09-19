using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantOnlineQuoteSnapshot
{
  public Guid QuoteId { get; set; }
  public long PublicSiteId { get; set; }
  public string PublicSiteKey { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public int SiteId { get; set; }
  public long SettingsConfigurationVersion { get; set; }
  public Guid? MemberId { get; set; }
  public string TermsVersion { get; set; } = string.Empty;
  public string PrivacyVersion { get; set; } = string.Empty;
  public RestaurantOnlineQuoteRequest Request { get; set; } = new();
  public IReadOnlyList<RestaurantOnlineQuoteLineDto> Lines { get; set; } = Array.Empty<RestaurantOnlineQuoteLineDto>();
  public IReadOnlyList<RestaurantPromotionAdjustmentDto> Promotions { get; set; } = Array.Empty<RestaurantPromotionAdjustmentDto>();
  public decimal Subtotal { get; set; }
  public decimal PromotionDiscount { get; set; }
  public decimal Tax { get; set; }
  public decimal DeliveryFee { get; set; }
  public decimal Total { get; set; }
  public string Currency { get; set; } = "MXN";
  public DateTime IssuedAtUtc { get; set; }
  public DateTime ExpiresAtUtc { get; set; }
  public string Fingerprint { get; set; } = string.Empty;
}

public interface IOnlineRestaurantQuoteTokenService
{
  string Protect(RestaurantOnlineQuoteSnapshot quote);
  bool TryUnprotect(string token, out RestaurantOnlineQuoteSnapshot? quote);
}

public static class RestaurantOnlineQuotePolicy
{
  public static string CreateFingerprint(RestaurantOnlineQuoteSnapshot quote)
  {
    ArgumentNullException.ThrowIfNull(quote);
    var builder = new StringBuilder()
      .Append(quote.PublicSiteId.ToString(CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.PublicSiteKey.Trim().ToLowerInvariant()).Append('|')
      .Append(quote.Rfc.Trim().ToUpperInvariant()).Append('|')
      .Append(quote.SiteId.ToString(CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.SettingsConfigurationVersion.ToString(CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.MemberId?.ToString("N") ?? "guest").Append('|')
      .Append(quote.TermsVersion.Trim()).Append('|')
      .Append(quote.PrivacyVersion.Trim()).Append('|')
      .Append(quote.Currency.Trim().ToUpperInvariant()).Append('|')
      .Append(quote.Subtotal.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.PromotionDiscount.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.Tax.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.DeliveryFee.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.Total.ToString("0.00", CultureInfo.InvariantCulture)).Append('|')
      .Append(quote.Request.PromotionCode?.Trim().ToUpperInvariant() ?? string.Empty).Append('|')
      .Append(NormalizeFulfillment(quote.Request.Fulfillment));

    foreach (var line in quote.Request.Lines)
    {
      builder.Append('|').Append(line.ProductId).Append(':').Append(line.MenuSectionId).Append(':')
        .Append(line.Quantity.ToString("0.####", CultureInfo.InvariantCulture)).Append(':')
        .Append(NormalizeNote(line.Notes));
      foreach (var optionId in line.ModifierOptionIds.Order())
        builder.Append(":m").Append(optionId);
      foreach (var combo in line.ComboSelections.OrderBy(item => item.ComboSlotId).ThenBy(item => item.ComboSlotOptionId))
      {
        builder.Append(":c").Append(combo.ComboSlotId).Append('-').Append(combo.ComboSlotOptionId)
          .Append('-').Append(NormalizeNote(combo.Notes));
        foreach (var optionId in combo.ModifierOptionIds.Order())
          builder.Append("m").Append(optionId).Append(',');
      }
    }

    foreach (var line in quote.Lines.OrderBy(item => item.Index))
    {
      builder.Append("|l").Append(line.Index).Append(':').Append(line.ProductId).Append(':')
        .Append(line.MenuSectionId).Append(':')
        .Append(line.Quantity.ToString("0.####", CultureInfo.InvariantCulture)).Append(':')
        .Append(line.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
        .Append(line.DiscountAmount.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
        .Append(line.TaxAmount.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
        .Append(line.Total.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
        .Append(NormalizeNote(line.Notes));
    }

    foreach (var promotion in quote.Promotions
      .OrderBy(item => item.PromotionId)
      .ThenBy(item => item.RuleType, StringComparer.Ordinal)
      .ThenBy(item => item.Code, StringComparer.Ordinal))
    {
      builder.Append("|p").Append(promotion.PromotionId).Append(':')
        .Append(promotion.RuleType.Trim().ToUpperInvariant()).Append(':')
        .Append(promotion.Code?.Trim().ToUpperInvariant() ?? string.Empty).Append(':')
        .Append(promotion.DiscountAmount.ToString("0.00", CultureInfo.InvariantCulture)).Append(':')
        .Append(promotion.IsCombinable ? '1' : '0');
    }

    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
  }

  public static bool IsOpen(string? scheduleJson, string timeZoneId, DateTimeOffset utcNow)
  {
    if (string.IsNullOrWhiteSpace(scheduleJson)) return false;
    try
    {
      var local = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
      using var document = JsonDocument.Parse(scheduleJson);
      if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
      var todayName = local.DayOfWeek.ToString();
      var previousName = ((DayOfWeek)(((int)local.DayOfWeek + 6) % 7)).ToString();
      var now = TimeOnly.FromDateTime(local.DateTime);
      var today = ReadIntervals(document.RootElement, todayName);
      var previous = ReadIntervals(document.RootElement, previousName);
      return today.Any(interval => interval.Opens < interval.Closes
          ? now >= interval.Opens && now < interval.Closes
          : interval.Opens > interval.Closes && now >= interval.Opens)
        || previous.Any(interval => interval.Opens > interval.Closes && now < interval.Closes);
    }
    catch (JsonException) { return false; }
    catch (TimeZoneNotFoundException) { return false; }
    catch (InvalidTimeZoneException) { return false; }
  }

  public static bool IsSameQuote(RestaurantOnlineQuoteSnapshot expected, RestaurantOnlineQuoteSnapshot actual)
    => expected.PublicSiteId == actual.PublicSiteId
      && expected.MemberId == actual.MemberId
      && expected.SettingsConfigurationVersion == actual.SettingsConfigurationVersion
      && string.Equals(expected.TermsVersion, actual.TermsVersion, StringComparison.Ordinal)
      && string.Equals(expected.PrivacyVersion, actual.PrivacyVersion, StringComparison.Ordinal)
      && string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.Ordinal)
      && expected.Total == actual.Total
      && string.Equals(expected.Currency, actual.Currency, StringComparison.OrdinalIgnoreCase);

  private static IReadOnlyList<(TimeOnly Opens, TimeOnly Closes)> ReadIntervals(JsonElement root, string dayName)
  {
    if (!TryGetProperty(root, dayName, out var values) || values.ValueKind != JsonValueKind.Array)
      return Array.Empty<(TimeOnly, TimeOnly)>();
    var result = new List<(TimeOnly, TimeOnly)>();
    foreach (var value in values.EnumerateArray())
    {
      if (value.ValueKind != JsonValueKind.Object
          || !TryGetProperty(value, "opens", out var opensValue)
          || !TryGetProperty(value, "closes", out var closesValue)
          || !TimeOnly.TryParseExact(opensValue.GetString(), "HH:mm", out var opens)
          || !TimeOnly.TryParseExact(closesValue.GetString(), "HH:mm", out var closes)
          || opens == closes)
        return Array.Empty<(TimeOnly, TimeOnly)>();
      result.Add((opens, closes));
    }
    return result;
  }

  private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
  {
    foreach (var property in element.EnumerateObject())
    {
      if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
      {
        value = property.Value;
        return true;
      }
    }
    value = default;
    return false;
  }

  private static string NormalizeNote(string? value)
    => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();

  private static string NormalizeFulfillment(RestaurantOnlineFulfillmentRequest? fulfillment)
  {
    fulfillment ??= new RestaurantOnlineFulfillmentRequest();
    return string.Join(':',
      NormalizeNote(fulfillment.Type),
      NormalizeNote(fulfillment.AddressLine),
      NormalizeNote(fulfillment.AddressComplement),
      fulfillment.Latitude?.ToString("0.000000", CultureInfo.InvariantCulture) ?? string.Empty,
      fulfillment.Longitude?.ToString("0.000000", CultureInfo.InvariantCulture) ?? string.Empty,
      NormalizeNote(fulfillment.GooglePlaceId),
      NormalizeNote(fulfillment.AddressVerificationStatus),
      NormalizeNote(fulfillment.DropoffPreference),
      NormalizeNote(fulfillment.Instructions),
      fulfillment.ManualAddressAcknowledged ? "1" : "0");
  }
}

/// <summary>
/// Creates the stable, unguessable public status capability from the browser's
/// random client-attempt identifier. Only its SHA-256 hash is persisted.
/// </summary>
public static class RestaurantOnlineTrackingTokenPolicy
{
  public static string Create(long publicSiteId, Guid clientAttemptId)
  {
    if (publicSiteId <= 0) throw new ArgumentOutOfRangeException(nameof(publicSiteId));
    if (clientAttemptId == Guid.Empty) throw new ArgumentException("A client attempt id is required.", nameof(clientAttemptId));
    var material = Encoding.UTF8.GetBytes($"restaurant-tracking-v1|{publicSiteId}|{clientAttemptId:N}");
    return Convert.ToBase64String(SHA256.HashData(material))
      .TrimEnd('=')
      .Replace('+', '-')
      .Replace('/', '_');
  }

  public static byte[] Hash(string token)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(token);
    return SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
  }
}
