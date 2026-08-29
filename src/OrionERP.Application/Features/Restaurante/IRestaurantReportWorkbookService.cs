namespace OrionERP.Application.Features.Restaurante;

public sealed class RestaurantReportWorkbook
{
  public string FileName { get; set; } = "reporte-restaurante.xlsx";
  public byte[] Content { get; set; } = [];
  public string ContentType { get; } = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}

/// <summary>Exportación a Excel de los reportes contables del Restaurante.</summary>
public interface IRestaurantReportWorkbookService
{
  RestaurantReportWorkbook CreateAccountingWorkbook(
    RestaurantAccountingReportDto report,
    IReadOnlyList<RestaurantRecipeCostDto> recipeCosts,
    RestaurantDiagnosticRunDto? diagnostic,
    string rfc,
    string siteName);
}
