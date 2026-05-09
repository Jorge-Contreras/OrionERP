using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OrionERP.Application.Features.Cfdi.Facturama;

public sealed class FacturamaIssuedCfdiRequest
{
  [JsonIgnore]
  public FacturamaIssuedCfdiHeader Header { get; set; } = new();

  [JsonPropertyName("Folio")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? Folio
  {
    get => Header.Folio;
    set => Header.Folio = value;
  }

  [JsonPropertyName("Date")]
  public string Date
  {
    get => Header.Date;
    set => Header.Date = value;
  }

  [JsonPropertyName("Currency")]
  public string Currency
  {
    get => Header.Currency;
    set => Header.Currency = value;
  }

  [JsonPropertyName("ExpeditionPlace")]
  public string ExpeditionPlace
  {
    get => Header.ExpeditionPlace;
    set => Header.ExpeditionPlace = value;
  }

  [JsonPropertyName("CfdiType")]
  public string CfdiType
  {
    get => Header.CfdiType;
    set => Header.CfdiType = value;
  }

  [JsonPropertyName("PaymentForm")]
  public string PaymentForm
  {
    get => Header.PaymentForm;
    set => Header.PaymentForm = value;
  }

  [JsonPropertyName("PaymentMethod")]
  public string PaymentMethod
  {
    get => Header.PaymentMethod;
    set => Header.PaymentMethod = value;
  }

  [JsonPropertyName("TaxZipCode")]
  public string TaxZipCode
  {
    get => Header.TaxZipCode;
    set => Header.TaxZipCode = value;
  }

  [JsonPropertyName("Receiver")]
  public FacturamaReceiver Receiver { get; set; } = new();

  [JsonPropertyName("Items")]
  public IReadOnlyList<FacturamaIssuedCfdiItem> Items { get; set; } = Array.Empty<FacturamaIssuedCfdiItem>();

  [JsonPropertyName("GlobalInformation")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public FacturamaGlobalInformation? GlobalInformation { get; set; }
}

public sealed class FacturamaIssuedCfdiHeader
{
  [JsonPropertyName("Folio")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? Folio { get; set; }

  [JsonPropertyName("Date")]
  public string Date { get; set; } = string.Empty;

  [JsonPropertyName("Currency")]
  public string Currency { get; set; } = "MXN";

  [JsonPropertyName("ExpeditionPlace")]
  public string ExpeditionPlace { get; set; } = string.Empty;

  [JsonPropertyName("CfdiType")]
  public string CfdiType { get; set; } = "I";

  [JsonPropertyName("PaymentForm")]
  public string PaymentForm { get; set; } = string.Empty;

  [JsonPropertyName("PaymentMethod")]
  public string PaymentMethod { get; set; } = string.Empty;

  [JsonPropertyName("TaxZipCode")]
  public string TaxZipCode { get; set; } = string.Empty;
}

public sealed class FacturamaReceiver
{
  [JsonPropertyName("Rfc")]
  public string Rfc { get; set; } = string.Empty;

  [JsonPropertyName("Name")]
  public string Name { get; set; } = string.Empty;

  [JsonPropertyName("CfdiUse")]
  public string CfdiUse { get; set; } = string.Empty;

  [JsonPropertyName("FiscalRegime")]
  public string FiscalRegime { get; set; } = string.Empty;

  [JsonPropertyName("TaxZipCode")]
  public string TaxZipCode { get; set; } = string.Empty;
}

public sealed class FacturamaGlobalInformation
{
  [JsonPropertyName("Periodicity")]
  public string Periodicity { get; set; } = string.Empty;

  [JsonPropertyName("Months")]
  public string Months { get; set; } = string.Empty;

  [JsonPropertyName("Year")]
  public string Year { get; set; } = string.Empty;
}

public sealed class FacturamaIssuedCfdiItem
{
  [JsonPropertyName("ProductCode")]
  public string ProductCode { get; set; } = string.Empty;

  [JsonPropertyName("IdentificationNumber")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? IdentificationNumber { get; set; }

  [JsonPropertyName("Description")]
  public string Description { get; set; } = string.Empty;

  [JsonPropertyName("Unit")]
  public string Unit { get; set; } = string.Empty;

  [JsonPropertyName("UnitCode")]
  public string UnitCode { get; set; } = string.Empty;

  [JsonPropertyName("UnitPrice")]
  public decimal UnitPrice { get; set; }

  [JsonPropertyName("Quantity")]
  public decimal Quantity { get; set; }

  [JsonPropertyName("Subtotal")]
  public decimal Subtotal { get; set; }

  [JsonPropertyName("Discount")]
  public decimal Discount { get; set; }

  [JsonPropertyName("TaxObject")]
  public string TaxObject { get; set; } = string.Empty;

  [JsonPropertyName("Taxes")]
  public IReadOnlyList<FacturamaIssuedCfdiTax> Taxes { get; set; } = Array.Empty<FacturamaIssuedCfdiTax>();

  [JsonPropertyName("Total")]
  public decimal Total { get; set; }
}

public sealed class FacturamaIssuedCfdiTax
{
  [JsonPropertyName("Name")]
  public string Name { get; set; } = string.Empty;

  [JsonPropertyName("Rate")]
  public decimal Rate { get; set; }

  [JsonPropertyName("Total")]
  public decimal Total { get; set; }

  [JsonPropertyName("Base")]
  public decimal Base { get; set; }

  [JsonPropertyName("IsRetention")]
  public bool IsRetention { get; set; }
}

public sealed class FacturamaReceiverValidationRequest
{
  [JsonPropertyName("Rfc")]
  public string Rfc { get; set; } = string.Empty;

  [JsonPropertyName("Name")]
  public string Name { get; set; } = string.Empty;

  [JsonPropertyName("CfdiUse")]
  public string CfdiUse { get; set; } = string.Empty;

  [JsonPropertyName("FiscalRegime")]
  public string FiscalRegime { get; set; } = string.Empty;

  [JsonPropertyName("ZipCode")]
  public string TaxZipCode { get; set; } = string.Empty;
}

public sealed class FacturamaReceiverValidationResult
{
  [JsonPropertyName("ExistRfc")]
  public bool ExistRfc { get; set; }

  [JsonPropertyName("MatchName")]
  public bool MatchName { get; set; }

  [JsonPropertyName("MatchZipCode")]
  public bool MatchZipCode { get; set; }

  [JsonPropertyName("MatchFiscalRegime")]
  public bool MatchFiscalRegime { get; set; }

  [JsonPropertyName("IsValid")]
  public bool IsValid { get; set; }
}

public sealed class FacturamaTaxEntity
{
  [JsonPropertyName("FiscalRegime")]
  public string? FiscalRegime { get; set; }

  [JsonPropertyName("ComercialName")]
  public string? ComercialName { get; set; }

  [JsonPropertyName("Rfc")]
  public string? Rfc { get; set; }

  [JsonPropertyName("TaxName")]
  public string? TaxName { get; set; }

  [JsonPropertyName("Email")]
  public string? Email { get; set; }

  [JsonPropertyName("Phone")]
  public string? Phone { get; set; }

  [JsonPropertyName("TaxAddress")]
  public FacturamaAddress? TaxAddress { get; set; }

  [JsonPropertyName("IssuedIn")]
  public FacturamaIssuedInAddress? IssuedIn { get; set; }
}

public class FacturamaAddress
{
  [JsonPropertyName("Street")]
  public string? Street { get; set; }

  [JsonPropertyName("ExteriorNumber")]
  public string? ExteriorNumber { get; set; }

  [JsonPropertyName("Neighborhood")]
  public string? Neighborhood { get; set; }

  [JsonPropertyName("ZipCode")]
  public string? ZipCode { get; set; }

  [JsonPropertyName("Locality")]
  public string? Locality { get; set; }

  [JsonPropertyName("Municipality")]
  public string? Municipality { get; set; }

  [JsonPropertyName("State")]
  public string? State { get; set; }

  [JsonPropertyName("Country")]
  public string? Country { get; set; }
}

public sealed class FacturamaIssuedInAddress : FacturamaAddress
{
  [JsonPropertyName("Id")]
  public string? Id { get; set; }
}
