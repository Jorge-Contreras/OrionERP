using OrionERP.Application.Features.CapitalHumano.Workforce;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.CapitalHumano;

/// <summary>
/// El estado <c>INACCURATE</c> mezcla dos situaciones muy distintas: la persona
/// estuvo lejos del sitio, o el equipo no supo ubicarla con la precisión que exige
/// la política. La política se evalúa antes que el radio, así que un registro hecho
/// a 39 m del sitio se marca igual que uno hecho a 12 km si el GPS es impreciso.
///
/// Por eso el supervisor necesita ver distancia y precisión, no sólo la etiqueta.
/// </summary>
public class AttendanceLocationEvidenceTests
{
  [Theory]
  [InlineData(39.4, 150, true)]   // dentro del perímetro aunque la precisión falle
  [InlineData(151.0, 150, false)] // realmente fuera
  [InlineData(150.0, 150, true)]  // el borde cuenta como dentro
  public void MeasuredInsideRadius_SeparaLejaniaDeImprecision(decimal distance, int radius, bool expected)
  {
    var exception = new AttendanceExceptionDto
    {
      DistanceMeters = distance,
      SiteRadiusMeters = radius,
      AccuracyMeters = 381m,
      SiteMaxAccuracyMeters = 100
    };

    Assert.Equal(expected, exception.MeasuredInsideRadius);
  }

  [Fact]
  public void SinLectura_NoAfirmaNadaSobreLaUbicacion()
  {
    var exception = new AttendanceExceptionDto { SiteRadiusMeters = 150 };

    Assert.Null(exception.MeasuredInsideRadius);
  }

  [Fact]
  public void ConsultasDeExcepciones_TraenLaEvidenciaDeUbicacion()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/CapitalHumano/Workforce/AttendanceService.cs");

    // La del colaborador y la del equipo: ambas.
    Assert.Equal(2, service.Split("loc.LocationStatus,loc.DistanceMeters,loc.AccuracyMeters").Length - 1);
    Assert.Equal(2, service.Split("evidence.DistanceMeters IS NOT NULL").Length - 1);
  }

  [Fact]
  public void PanelDelSupervisor_MuestraDistanciaPrecisionYVeredicto()
  {
    var panel = RepoFile.Read(
      "src/OrionERP.Web/Features/CapitalHumano/Workforce/TeamAttendancePanel.razor");

    Assert.Contains("<th>Ubicación del día</th>", panel, StringComparison.Ordinal);
    Assert.Contains("m del sitio", panel, StringComparison.Ordinal);
    Assert.Contains("Dentro del radio", panel, StringComparison.Ordinal);
    Assert.Contains("Fuera del radio", panel, StringComparison.Ordinal);
    Assert.Contains("la política pide", panel, StringComparison.Ordinal);
  }
}
