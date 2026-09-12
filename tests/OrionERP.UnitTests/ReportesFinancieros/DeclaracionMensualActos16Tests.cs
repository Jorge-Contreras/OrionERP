using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.ReportesFinancieros;

public class DeclaracionMensualActos16Tests
{
  private const string FiscalObjects =
    "src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260907_fiscal_declaracion_objetos.sql";

  private const string DeclaracionPreviaObjects =
    "src/OrionERP.Infrastructure/Features/Cfdi/DeclaracionPrevia/Sql/20260911_declaracion_previa_show_excluded.sql";

  private const string DeclaracionPreviaService =
    "src/OrionERP.Infrastructure/Features/Cfdi/DeclaracionPrevia/DeclaracionPreviaService.cs";

  private const string DeclaracionPreviaToggleUi =
    "src/OrionERP.Web/Features/Cfdi/DeclaracionPrevia/Pages/DeclaracionPrevia.SelectionAndToggle.cs";

  [Fact]
  public void MonthlyReport_ExposesIncludedActos16ComponentsAndSatEntryTotal()
  {
    var sql = RepoFile.Read(FiscalObjects);

    Assert.Contains("@AcredActos16Incluidos + @AcredBase16C", sql, StringComparison.Ordinal);
    Assert.Contains("Total de actos o actividades pagados a la tasa del 16% (captura SAT)", sql, StringComparison.Ordinal);
    Assert.Contains("f.IncluirEnDeclaracion = 1", sql, StringComparison.Ordinal);
    Assert.Contains("cd.Actos_16", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void PriorDeclarationQuery_ReturnsIncludedAndExcludedCurrentCfdis()
  {
    var sql = RepoFile.Read(DeclaracionPreviaObjects);
    var cancelledProcedureStart = sql.IndexOf(
      "CREATE OR ALTER PROCEDURE [cfdi].[Declaracion_Canceladas_Omitidas]",
      StringComparison.Ordinal);

    Assert.Contains("ELSE 'X' END AS D", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("AND cd.Incluir_En_Declaracion = 1", sql, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("NOT IN ('Cancelado', 'Cancelada')", sql[..cancelledProcedureStart], StringComparison.Ordinal);
    Assert.Contains("cd.FechaCancelacion IS NULL", sql[..cancelledProcedureStart], StringComparison.Ordinal);
  }

  [Fact]
  public void PriorDeclarationQuery_OnlySendsCancelledCfdisToCancelledSection()
  {
    var sql = RepoFile.Read(DeclaracionPreviaObjects);
    var cancelledProcedureStart = sql.IndexOf(
      "CREATE OR ALTER PROCEDURE [cfdi].[Declaracion_Canceladas_Omitidas]",
      StringComparison.Ordinal);
    var cancelledProcedure = sql[cancelledProcedureStart..];

    Assert.Contains("IN ('Cancelado', 'Cancelada')", cancelledProcedure, StringComparison.Ordinal);
    Assert.Contains("OR cd.FechaCancelacion IS NOT NULL", cancelledProcedure, StringComparison.Ordinal);
    Assert.DoesNotContain("cd.Incluir_En_Declaracion = 0", cancelledProcedure, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void PriorDeclarationToggle_RejectsCancelledCfdis()
  {
    var service = RepoFile.Read(DeclaracionPreviaService);
    var sql = RepoFile.Read(DeclaracionPreviaObjects);

    Assert.Contains("NOT IN ('Cancelado', 'Cancelada')", service, StringComparison.Ordinal);
    Assert.Contains("AND FechaCancelacion IS NULL", service, StringComparison.Ordinal);
    Assert.Contains("if (affected == 0)", service, StringComparison.Ordinal);
    Assert.Contains("No se puede cambiar la inclusión de un CFDI cancelado", service, StringComparison.Ordinal);
    Assert.Contains("CK_Comprobante_Cancelado_No_Declaracion", sql, StringComparison.Ordinal);
    Assert.Contains("OR ISNULL(Incluir_En_Declaracion, 1) = 0", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void PriorDeclarationToggle_ReportsTheNewInclusionState()
  {
    var ui = RepoFile.Read(DeclaracionPreviaToggleUi);

    Assert.Contains("!string.Equals(selectedEmitida.D, \"X\"", ui, StringComparison.Ordinal);
    Assert.Contains("!string.Equals(selectedRecibida.D, \"X\"", ui, StringComparison.Ordinal);
    Assert.DoesNotContain(".D == \"✓\"", ui, StringComparison.Ordinal);
  }
}
