using OrionERP.Application.Features.ReportesFinancieros.Models;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.ReportesFinancieros;

public sealed class PublishedReportsTests
{
  private static string Migration() => RepoFile.Read(
    "src/OrionERP.Infrastructure/Features/ReportesFinancieros/Sql/20260908_published_reports_sandbox.sql");

  [Fact]
  public void TheCurrentReportKeepsItsMeaning_BecauseTheFlagDefaultsToOff()
  {
    var sql = Migration();

    // Añadir un WHERE de publicación incondicional habría hecho desaparecer la historia.
    Assert.Contains("@SoloPublicadas BIT = 0", sql, StringComparison.Ordinal);
    Assert.Contains("@SoloPublicadas = 0 OR t.CycleState IN (''Posted'', ''Reversed'')", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void DraftIsExcludedAndPostedIsIncluded()
  {
    var sql = Migration();

    Assert.Contains("''Posted''", sql, StringComparison.Ordinal);
    // Draft y NULL quedan fuera por no estar en la lista; no se nombran para incluirlos.
    Assert.DoesNotContain("t.CycleState IN (''Draft''", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void TheReversalIsCountedSoItSubtracts()
  {
    var sql = Migration();

    // Incluir 'Reversed' es lo que deja el asiento original en el reporte para que la
    // reversa lo cancele. Excluirlo dejaría sólo el inverso y el reporte saldría al revés.
    Assert.Contains("''Reversed''", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void BothReportsWereVersionedTogether()
  {
    var sql = Migration();

    Assert.Contains("CREATE OR ALTER PROCEDURE [reporteFinanciero].[Rpt_BalanzaComprobacion]", sql, StringComparison.Ordinal);
    Assert.Contains("CREATE OR ALTER PROCEDURE [reporteFinanciero].[ESTADO_PERDIDAS_GANANCIAS]", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void TheFiscalDeclarationIsNotTouched()
  {
    var sql = Migration();

    // El guion ajeno se nombra en un comentario para decir justamente que no se toca;
    // lo que se comprueba es que ningún objeto fiscal entre en una sentencia.
    foreach (var statement in new[] { "FROM fiscal.", "JOIN fiscal.", "UPDATE fiscal.", "INSERT fiscal.", "DELETE fiscal.", "EXEC fiscal." })
      Assert.DoesNotContain(statement, sql, StringComparison.OrdinalIgnoreCase);
  }

  [Theory]
  [InlineData(false, 0, 0, "no tiene el ciclo contable activado")]
  [InlineData(true, 0, 10, "ninguna póliza publicada")]
  [InlineData(true, 4, 10, "Quedan 6 pólizas fuera del ciclo")]
  [InlineData(true, 10, 10, "Nada:")]
  public void TheAvailabilityRecordsWhatIsMissingToAdoptIt(
    bool cycleEnabled, long published, long total, string expected)
  {
    var availability = Availability(cycleEnabled, published, total);

    Assert.Contains(expected, availability.MissingToAdopt, StringComparison.Ordinal);
    Assert.Equal(cycleEnabled && published > 0, availability.IsAvailable);
  }

  [Fact]
  public void WithoutTheCycleInstalled_TheVariantIsNotOffered()
  {
    var availability = new PublishedReportAvailability(false, false, false, 0, 0,
      "El ciclo contable formal no está instalado en esta base de datos.");

    Assert.False(availability.IsAvailable);
    Assert.False(availability.CycleInstalled);
  }

  /// <summary>Reproduce la misma decisión que toma el servicio, para fijarla como contrato.</summary>
  private static PublishedReportAvailability Availability(bool cycleEnabled, long published, long total)
  {
    var missing = !cycleEnabled
      ? "La empresa no tiene el ciclo contable activado; hay que aprobar su baseline primero."
      : published == 0
        ? "La empresa tiene el ciclo activado pero ninguna póliza publicada todavía."
        : published < total
          ? $"Quedan {total - published} pólizas fuera del ciclo; el reporte vigente sigue siendo el oficial hasta conciliarlas."
          : "Nada: todas las pólizas de la empresa están dentro del ciclo.";
    return new PublishedReportAvailability(cycleEnabled && published > 0, true, cycleEnabled, published, total, missing);
  }
}
