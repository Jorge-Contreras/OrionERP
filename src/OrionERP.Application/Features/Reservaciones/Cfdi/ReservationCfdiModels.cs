using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Reservaciones.Cfdi;

public sealed class ReservationCfdiContextDto
{
  public int ReservationId { get; set; }
  public string ReservationCliente { get; set; } = string.Empty;
  public bool Taxable { get; set; }
  public decimal TotalSuites { get; set; }
  public decimal TotalExtras { get; set; }
  public decimal SubTotal { get; set; }
  public decimal Tax { get; set; }
  public decimal Ish { get; set; }
  public decimal TotalReservacion { get; set; }
  public bool HasUnsupportedIsh => Ish > 0.009m;
  public int? AutoSelectedTransaccionId { get; set; }
  public IReadOnlyList<ReservationCfdiPolizaOptionDto> PolizaOptions { get; set; } = Array.Empty<ReservationCfdiPolizaOptionDto>();
  public IReadOnlyList<ReservationCfdiItemPreviewDto> Items { get; set; } = Array.Empty<ReservationCfdiItemPreviewDto>();
  public IReadOnlyList<ReservationCfdiCustomerSuggestionDto> SuggestedCustomers { get; set; } = Array.Empty<ReservationCfdiCustomerSuggestionDto>();
  public IReadOnlyList<ReservationCfdiLinkedDocumentDto> ExistingDocuments { get; set; } = Array.Empty<ReservationCfdiLinkedDocumentDto>();
  public ReservationCfdiCustomerUpsertRequest ReceiverDraft { get; set; } = new();
}

public sealed class ReservationCfdiPolizaOptionDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal Monto { get; set; }
  public bool HasExistingCfdi { get; set; }
  public bool MatchesReservationTotal { get; set; }
  public bool IsEligible { get; set; }
}

public sealed class ReservationCfdiItemPreviewDto
{
  public string SourceType { get; set; } = string.Empty;
  public int SourceId { get; set; }
  public DateTime? Fecha { get; set; }
  public string Description { get; set; } = string.Empty;
  public string ProductCode { get; set; } = string.Empty;
  public string UnitCode { get; set; } = string.Empty;
  public string Unit { get; set; } = string.Empty;
  public decimal Quantity { get; set; }
  public decimal UnitPrice { get; set; }
  public decimal Subtotal { get; set; }
  public decimal Discount { get; set; }
  public decimal Tax { get; set; }
  public decimal Total { get; set; }
  public string TaxObject { get; set; } = string.Empty;
}

public sealed class ReservationCfdiCustomerSuggestionDto
{
  public int? BusinessPartnerId { get; set; }
  public string DisplayName { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public string FiscalName { get; set; } = string.Empty;
  public string TaxZipCode { get; set; } = string.Empty;
  public string FiscalRegime { get; set; } = string.Empty;
  public string CfdiUse { get; set; } = string.Empty;
  public string? Email { get; set; }
  public bool IsPersisted { get; set; }
  public string SourceLabel { get; set; } = string.Empty;
  public DateTime? LastUsedAt { get; set; }
}

public sealed class ReservationCfdiLinkedDocumentDto
{
  public int TransaccionId { get; set; }
  public long ComprobanteId { get; set; }
  public DateTime Fecha { get; set; }
  public string? Serie { get; set; }
  public string? Folio { get; set; }
  public string? Uuid { get; set; }
  public string? ReceptorRfc { get; set; }
  public string? ReceptorNombre { get; set; }
  public decimal Total { get; set; }
}

public sealed class ReservationCfdiCustomerUpsertRequest
{
  public int? BusinessPartnerId { get; set; }
  public string DisplayName { get; set; } = string.Empty;
  public string Rfc { get; set; } = string.Empty;
  public string FiscalName { get; set; } = string.Empty;
  public string TaxZipCode { get; set; } = string.Empty;
  public string FiscalRegime { get; set; } = string.Empty;
  public string CfdiUse { get; set; } = string.Empty;
  public string? Email { get; set; }
}

public sealed class ReservationCfdiReceiverValidationDto
{
  public bool IsValid { get; set; }
  public bool ExistRfc { get; set; }
  public bool MatchName { get; set; }
  public bool MatchZipCode { get; set; }
  public bool MatchFiscalRegime { get; set; }
  public bool IsSandbox { get; set; }
  public string Message { get; set; } = string.Empty;
  public DateTime ValidatedAtUtc { get; set; }
  public bool BlocksStamping => !IsSandbox && !IsValid;
  public string EnvironmentLabel => IsSandbox ? "Sandbox" : "Produccion";
}

public sealed class ReservationCfdiCreateRequest
{
  public int ReservationId { get; set; }
  public string IssuerRfc { get; set; } = string.Empty;
  public bool CreateNewPoliza { get; set; }
  public int? TransaccionId { get; set; }
  public bool PersistCustomer { get; set; }
  public string FormaPago { get; set; } = string.Empty;
  public string MetodoPago { get; set; } = string.Empty;
  public ReservationCfdiCustomerUpsertRequest Receiver { get; set; } = new();
}

public sealed record class ReservationCfdiCustomerSaveResult(bool Success, string Message, int? BusinessPartnerId)
{
  public static ReservationCfdiCustomerSaveResult Ok(string message, int businessPartnerId)
    => new(true, message, businessPartnerId);

  public static ReservationCfdiCustomerSaveResult Fail(string message, int? businessPartnerId = null)
    => new(false, message, businessPartnerId);
}

public sealed record class ReservationCfdiCreateResult(bool Success, string Message, int? TransaccionId)
{
  public static ReservationCfdiCreateResult Ok(string message, int transaccionId)
    => new(true, message, transaccionId);

  public static ReservationCfdiCreateResult Fail(string message, int? transaccionId = null)
    => new(false, message, transaccionId);
}
