using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Contabilidad;

public sealed class AccountingCycleTests
{
  private static AccountingCycleStatus Status(
    string? state = null,
    bool cycleEnabled = true,
    bool legacyCompatible = false,
    bool periodClosed = false,
    int? reversedBy = null)
    => new(
      TransaccionId: 7,
      CompanyId: 2,
      State: state,
      Fecha: new DateTime(2026, 9, 8),
      PostedAtUtc: state == AccountingCycleStates.Posted ? DateTime.UtcNow : null,
      PostedBy: null,
      ReversalOfTransaccionId: null,
      ReversedByTransaccionId: reversedBy,
      ReversalReason: null,
      CycleEnabled: cycleEnabled,
      LegacyCompatible: legacyCompatible,
      PeriodClosed: periodClosed);

  [Fact]
  public void CycleOff_LeavesTheExistingOperationAlone()
  {
    var status = Status(cycleEnabled: false);

    Assert.False(status.CycleApplies);
    Assert.False(status.CanPost);
    Assert.False(status.CanReverse);
  }

  [Fact]
  public void UnreconciledHistory_StaysOutOfTheCycle()
  {
    var status = Status(legacyCompatible: true);

    Assert.False(status.CycleApplies);
    Assert.False(status.CanPost);
  }

  [Fact]
  public void ClosedPeriod_RefusesThePublication()
  {
    Assert.False(Status(periodClosed: true).CanPost);
    Assert.True(Status(periodClosed: false).CanPost);
  }

  [Theory]
  [InlineData(null, true, false)]
  [InlineData(AccountingCycleStates.Draft, true, false)]
  [InlineData(AccountingCycleStates.Posted, false, true)]
  [InlineData(AccountingCycleStates.Reversed, false, false)]
  public void StateTransitions_AreTheOnlyOnesAllowed(string? state, bool canPost, bool canReverse)
  {
    var status = Status(state);

    Assert.Equal(canPost, status.CanPost);
    Assert.Equal(canReverse, status.CanReverse);
  }

  [Fact]
  public void PostedAndReversed_AreImmutable()
  {
    Assert.True(AccountingCycleStates.IsImmutable(AccountingCycleStates.Posted));
    Assert.True(AccountingCycleStates.IsImmutable(AccountingCycleStates.Reversed));
    Assert.False(AccountingCycleStates.IsImmutable(AccountingCycleStates.Draft));
    Assert.False(AccountingCycleStates.IsImmutable(null));
  }

  [Fact]
  public void AnAlreadyReversedPoliza_IsNotReversedTwice()
  {
    Assert.False(Status(AccountingCycleStates.Posted, reversedBy: 99).CanReverse);
    Assert.True(Status(AccountingCycleStates.Posted).CanReverse);
  }

  [Fact]
  public void Publication_ReusesTheExistingBalanceValidator()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/AccountingCycleService.cs");

    // Una segunda regla de cuadre sería una segunda contabilidad.
    Assert.Contains("MovimientosCuadreValidator.Validate", service, StringComparison.Ordinal);
    Assert.DoesNotContain("SUM(Debe)", service, StringComparison.Ordinal);
  }

  [Fact]
  public void TheWriteQuery_TakesTheLocksThatCoverAConcurrentClose()
  {
    var sql = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/AccountingCycleSql.cs");

    // El literal se construye con los hints, no por reemplazo de texto: un cambio de
    // fin de línea no puede dejar la escritura sin candados.
    Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.Ordinal);
    Assert.Contains("WITH (HOLDLOCK)", sql, StringComparison.Ordinal);
    Assert.DoesNotContain(".Replace(", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void TheInstalledProbe_NamesEveryPieceTheWritersDependOn()
  {
    var sql = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/AccountingCycleSql.cs");

    Assert.Contains("contabilidad.CompanyCycleActivation", sql, StringComparison.Ordinal);
    Assert.Contains("contabilidad.AccountingPeriod", sql, StringComparison.Ordinal);
    Assert.Contains("COL_LENGTH(N'dbo.Transacciones', N'CycleState')", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void EveryExistingWriter_ConsultsTheCycleBeforeWriting()
  {
    var service = RepoFile.Read(
      "src/OrionERP.Infrastructure/Features/Contabilidad/Transacciones/Services/TransaccionService.cs");

    // GuardarMovimientos, GuardarYCerrar, DeleteMovimiento y DeleteTransaccion.
    Assert.Equal(4, CountOccurrences(service, "if (await CycleWriteBlockAsync("));
  }

  private static int CountOccurrences(string source, string value)
  {
    var count = 0;
    for (var index = source.IndexOf(value, StringComparison.Ordinal); index >= 0;
         index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
      count++;
    return count;
  }
}
