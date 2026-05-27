using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Contabilidad.Bancos;

public sealed class BankMovementLinkingWorkspaceDto
{
  public BankMovementLinkingSummaryDto? Summary { get; set; }
  public List<BankMovementTransactionLinkDto> Links { get; } = [];
  public List<BankMovementTransactionCandidateDto> Candidates { get; } = [];
}

public sealed class BankMovementLinkingSummaryDto
{
  public long MovimientoId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public int CuentaBancoId { get; set; }
  public string NombreBanco { get; set; } = string.Empty;
  public string NumeroCuenta { get; set; } = string.Empty;
  public DateTime Dia { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public string Tipo { get; set; } = string.Empty;
  public decimal Cargo { get; set; }
  public decimal Abono { get; set; }
  public decimal Saldo { get; set; }
  public int? CuentaContableId { get; set; }
  public string BankAccountNivel1 { get; set; } = string.Empty;
  public string BankAccountNivel2 { get; set; } = string.Empty;
  public string BankAccountNivel3 { get; set; } = string.Empty;
  public string BankAccountDescription { get; set; } = string.Empty;
  public decimal ExpectedDebe { get; set; }
  public decimal ExpectedHaber { get; set; }
  public decimal LinkedDebe { get; set; }
  public decimal LinkedHaber { get; set; }
  public bool MappingValid { get; set; }
  public string? SetupIssue { get; set; }

  public decimal RemainingDebe => ExpectedDebe - LinkedDebe;
  public decimal RemainingHaber => ExpectedHaber - LinkedHaber;
  public decimal MovementAmount => ExpectedDebe > 0m ? ExpectedDebe : ExpectedHaber;
  public bool IsCargo => ExpectedDebe > 0m;
}

public sealed class BankMovementTransactionLinkDto
{
  public long MovimientoId { get; set; }
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal TransaccionMonto { get; set; }
  public string TipoPoliza { get; set; } = string.Empty;
  public string FormaPago { get; set; } = string.Empty;
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
  public decimal BankRegistroDebe { get; set; }
  public decimal BankRegistroHaber { get; set; }
  public decimal OtherLinkedDebe { get; set; }
  public decimal OtherLinkedHaber { get; set; }
  public decimal AvailableDebe { get; set; }
  public decimal AvailableHaber { get; set; }
  public string MatchStatus { get; set; } = "OK";
}

public sealed class BankMovementTransactionCandidateDto
{
  public int TransaccionId { get; set; }
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal TransaccionMonto { get; set; }
  public string TipoPoliza { get; set; } = string.Empty;
  public string FormaPago { get; set; } = string.Empty;
  public decimal BankRegistroDebe { get; set; }
  public decimal BankRegistroHaber { get; set; }
  public decimal LinkedDebe { get; set; }
  public decimal LinkedHaber { get; set; }
  public decimal AvailableDebe { get; set; }
  public decimal AvailableHaber { get; set; }
  public int MatchScore { get; set; }
  public string MatchStatus { get; set; } = "POSIBLE";
  public bool HasBankLine { get; set; }
  public bool IsOtherCandidate { get; set; }
}

public sealed class BankMovementLinkSaveRequest
{
  public long MovimientoId { get; set; }
  public string? Actor { get; set; }
  public List<BankMovementLinkSaveItem> Links { get; set; } = [];
}

public sealed class BankMovementLinkSaveItem
{
  public int TransaccionId { get; set; }
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
}

public sealed class BankMovementAccountingFixRequest
{
  public long MovimientoId { get; set; }
  public int TransaccionId { get; set; }
  public decimal Debe { get; set; }
  public decimal Haber { get; set; }
  public string? Actor { get; set; }
}

public sealed class BankTransactionMovementWorkspaceDto
{
  public BankTransactionMovementSummaryDto? Summary { get; set; }
  public List<BankMovementDto> Links { get; } = [];
  public List<BankTransactionMovementCandidateDto> Candidates { get; } = [];
}

public sealed class BankTransactionMovementSummaryDto
{
  public int TransaccionId { get; set; }
  public string Rfc { get; set; } = string.Empty;
  public DateTime Fecha { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public decimal Monto { get; set; }
  public string TipoPoliza { get; set; } = string.Empty;
  public string FormaPago { get; set; } = string.Empty;
  public int BankLineCount { get; set; }
  public decimal BankRegistroDebe { get; set; }
  public decimal BankRegistroHaber { get; set; }
  public decimal LinkedDebe { get; set; }
  public decimal LinkedHaber { get; set; }
  public bool HasBankAccountMapping { get; set; }
  public string? SetupIssue { get; set; }

  public decimal AvailableDebe => BankRegistroDebe - LinkedDebe;
  public decimal AvailableHaber => BankRegistroHaber - LinkedHaber;
}

public sealed class BankTransactionMovementCandidateDto
{
  public long MovimientoId { get; set; }
  public int CuentaBancoId { get; set; }
  public string NombreBanco { get; set; } = string.Empty;
  public string NumeroCuenta { get; set; } = string.Empty;
  public DateTime Dia { get; set; }
  public string Concepto { get; set; } = string.Empty;
  public string Tipo { get; set; } = string.Empty;
  public decimal Cargo { get; set; }
  public decimal Abono { get; set; }
  public decimal Saldo { get; set; }
  public decimal ExpectedDebe { get; set; }
  public decimal ExpectedHaber { get; set; }
  public decimal LinkedDebe { get; set; }
  public decimal LinkedHaber { get; set; }
  public decimal RemainingDebe { get; set; }
  public decimal RemainingHaber { get; set; }
  public decimal TransactionAvailableDebe { get; set; }
  public decimal TransactionAvailableHaber { get; set; }
  public bool AlreadyLinkedToTransaction { get; set; }
  public bool IsFullyLinked { get; set; }
  public int MatchScore { get; set; }
  public string MatchStatus { get; set; } = "POSIBLE";
}
