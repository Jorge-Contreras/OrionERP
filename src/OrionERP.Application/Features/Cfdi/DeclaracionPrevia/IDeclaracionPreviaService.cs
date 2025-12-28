using System.Security.Claims;

namespace OrionERP.Application.Features.Cfdi.DeclaracionPrevia;

public interface IDeclaracionPreviaService
{
  Task<IReadOnlyList<string>> GetAvailableRfcsAsync(ClaimsPrincipal user);
  Task<DeclaracionPreviaData> GetDeclaracionAsync(DeclaracionPreviaRequest request);
  Task ToggleInclusionAsync(int comprobanteId);
  Task<int> ExcludePagosYDevolucionesAsync(string rfc, int year, int? month);
  Task<IReadOnlyList<PagoComplementoResumen>> GetComplementosAsync(Guid uuid);
  Task<int> GenerarPolizaDesdeComprobanteAsync(int comprobanteId, string rfc);
  Task CancelEmitidaAsync(string uuid, int comprobanteId);
  Task<IReadOnlyList<string>> GenerateDiotAsync(string rfc, int year, int month);
  Task<long?> GetLinkedTransactionIdAsync(int comprobanteId);
  Task<ComprobanteDetalleDto?> GetComprobanteDetalleAsync(int comprobanteId);
}

public record DeclaracionPreviaRequest(string Rfc, int Year, int? Month, bool IsAnnual)
{
  public DateTime StartDate => IsAnnual ? new DateTime(Year, 1, 1) : new DateTime(Year, Month ?? 1, 1);
  public DateTime EndDate => IsAnnual
    ? new DateTime(Year, 12, 31)
    : new DateTime(Year, Month ?? 1, DateTime.DaysInMonth(Year, Month ?? 1));
}

public class DeclaracionPreviaData
{
  public IReadOnlyList<string> DisponiblesRfc { get; init; } = Array.Empty<string>();
  public IReadOnlyList<int> DisponibleYears { get; init; } = Array.Empty<int>();
  public IReadOnlyList<(int, string)> DisponibleMonths { get; init; } = Array.Empty<(int, string)>();
  public IReadOnlyList<DeclaracionCfdiBase> AllCfdiBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionCfdiBase> EmitidasBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionCfdiBase> RecibidasBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionCfdiBase> EmitidasNominaBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionCfdiBase> RecibidasNominaBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionCfdiBase> TipoEEmitidasBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionCfdiBase> TipoERecibidasBase { get; init; } = Array.Empty<DeclaracionCfdiBase>();
  public IReadOnlyList<DeclaracionComplementoBase> ComplementosBase { get; init; } = Array.Empty<DeclaracionComplementoBase>();
  public IReadOnlyList<DeclaracionComplementoBase> ComplementosEmitidosBase { get; init; } = Array.Empty<DeclaracionComplementoBase>();
  public IReadOnlyList<DeclaracionComplementoBase> ComplementosRecibidosBase { get; init; } = Array.Empty<DeclaracionComplementoBase>();
  public IReadOnlyList<DeclaracionEmitida> Emitidas { get; init; } = Array.Empty<DeclaracionEmitida>();
  public IReadOnlyList<DeclaracionRecibida> Recibidas { get; init; } = Array.Empty<DeclaracionRecibida>();
  public IReadOnlyList<DeclaracionEmitida> EmitidasNomina { get; init; } = Array.Empty<DeclaracionEmitida>();
  public IReadOnlyList<DeclaracionRecibida> RecibidasNomina { get; init; } = Array.Empty<DeclaracionRecibida>();
  public IReadOnlyList<DeclaracionEmitida> TipoEEmitidas { get; init; } = Array.Empty<DeclaracionEmitida>();
  public IReadOnlyList<DeclaracionRecibida> TipoERecibidas { get; init; } = Array.Empty<DeclaracionRecibida>();
  public IReadOnlyList<DeclaracionComplementoEmitido> ComplementosEmitidos { get; init; } = Array.Empty<DeclaracionComplementoEmitido>();
  public IReadOnlyList<DeclaracionComplementoRecibido> ComplementosRecibidos { get; init; } = Array.Empty<DeclaracionComplementoRecibido>();
  public IReadOnlyList<DesfaseItem> Desfase { get; init; } = Array.Empty<DesfaseItem>();
  public IReadOnlyList<PolizaNoConsolidada> PolizasNoConsolidadas { get; init; } = Array.Empty<PolizaNoConsolidada>();
  public DeclaracionTotales? EmitidasTotals { get; init; }
  public DeclaracionTotales? EmitidasNominaTotals { get; init; }
  public DeclaracionTotales? RecibidasTotals { get; init; }
  public DeclaracionTotales? RecibidasNominaTotals { get; init; }
  public DeclaracionTotales? TipoEEmitidasTotals { get; init; }
  public DeclaracionTotales? TipoERecibidasTotals { get; init; }
  public DesfaseTotales? DesfaseTotals { get; init; }
  public string? ImpuestosSummary { get; init; }
  public string? BancosCajaSummary { get; init; }
}
