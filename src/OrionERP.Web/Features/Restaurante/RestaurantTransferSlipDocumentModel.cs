namespace OrionERP.Web.Features.Restaurante;

using OrionERP.Application.Features.Restaurante;

/// <summary>
/// Comprobante de 80 mm con los datos bancarios que el cajero entrega al cliente
/// cuando paga con transferencia electrónica de fondos (SPEI). Se imprime antes de
/// cobrar, por lo que la orden todavía puede no tener folio.
/// </summary>
public sealed class RestaurantTransferSlipDocumentModel
{
  public string SiteName { get; init; } = string.Empty;
  public string AccountHolder { get; init; } = string.Empty;
  public string? BankName { get; init; }
  public string? AccountNumber { get; init; }
  public string? Clabe { get; init; }
  public string? CardNumber { get; init; }
  public string? Instructions { get; init; }
  public decimal Amount { get; init; }
  public string? Reference { get; init; }
  public int? Folio { get; init; }
  public DateTimeOffset CreatedAt { get; init; }

  public static RestaurantTransferSlipDocumentModel FromSite(
    RestaurantSiteDto site,
    decimal amount,
    string? reference,
    DateTimeOffset createdAt,
    int? folio = null)
  {
    ArgumentNullException.ThrowIfNull(site);

    return new RestaurantTransferSlipDocumentModel
    {
      SiteName = site.Name,
      AccountHolder = RestaurantTransferPaymentRules.NormalizeText(site.TransferAccountHolder) ?? string.Empty,
      BankName = RestaurantTransferPaymentRules.NormalizeText(site.TransferBankName),
      AccountNumber = RestaurantTransferPaymentRules.NormalizeDigits(site.TransferAccountNumber),
      Clabe = RestaurantTransferPaymentRules.NormalizeDigits(site.TransferClabe),
      CardNumber = RestaurantTransferPaymentRules.NormalizeDigits(site.TransferCardNumber),
      Instructions = RestaurantTransferPaymentRules.NormalizeText(site.TransferInstructions),
      Amount = amount,
      Reference = RestaurantTransferPaymentRules.NormalizeText(reference),
      Folio = folio,
      CreatedAt = createdAt
    };
  }
}
