using System.Collections.Generic;
using OrionERP.Application.Features.Contabilidad.Transacciones;

namespace OrionERP.UnitTests.Contabilidad;

public sealed class MovimientosCuadreValidatorTests
{
  private static TransaccionMovimientoUpdateItem Row(decimal debe, decimal haber)
    => new() { Debe = debe, Haber = haber };

  [Fact]
  public void Validate_WhenDebeEqualsHaber_IsAccepted()
  {
    var movimientos = new List<TransaccionMovimientoUpdateItem>
    {
      Row(1160m, 0m),
      Row(0m, 1000m),
      Row(0m, 160m)
    };

    Assert.Null(MovimientosCuadreValidator.Validate(movimientos));
  }

  [Fact]
  public void Validate_WhenPolizaDoesNotBalance_IsRejectedWithAmounts()
  {
    var movimientos = new List<TransaccionMovimientoUpdateItem>
    {
      Row(1000m, 0m),
      Row(0m, 900m)
    };

    var error = MovimientosCuadreValidator.Validate(movimientos);

    Assert.NotNull(error);
    Assert.Contains("no cuadra", error);
  }

  [Fact]
  public void Validate_ToleratesSubCentRoundingDrift()
  {
    var movimientos = new List<TransaccionMovimientoUpdateItem>
    {
      Row(100.00m, 0m),
      Row(0m, 99.997m)
    };

    Assert.Null(MovimientosCuadreValidator.Validate(movimientos));
  }

  [Fact]
  public void Validate_RejectsNegativeAmounts()
  {
    var movimientos = new List<TransaccionMovimientoUpdateItem> { Row(-50m, 0m), Row(0m, -50m) };

    Assert.Contains("negativo", MovimientosCuadreValidator.Validate(movimientos)!);
  }

  [Fact]
  public void Validate_RejectsRowWithBothSides()
  {
    var movimientos = new List<TransaccionMovimientoUpdateItem> { Row(50m, 50m) };

    Assert.Contains("un solo lado", MovimientosCuadreValidator.Validate(movimientos)!);
  }

  [Fact]
  public void Validate_EmptyOrNull_IsAccepted()
  {
    Assert.Null(MovimientosCuadreValidator.Validate(new List<TransaccionMovimientoUpdateItem>()));
    Assert.Null(MovimientosCuadreValidator.Validate(null!));
  }
}
