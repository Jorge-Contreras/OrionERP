using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Features.Reservaciones.ListaReservaciones;

namespace OrionERP.Infrastructure.Features.Reservaciones;

internal static class ReservationStoredTotalSynchronizer
{
  public static async Task<decimal> RecalculateAsync(
    SqlConnection connection,
    SqlTransaction transaction,
    int reservationId,
    CancellationToken cancellationToken)
  {
    const string sql = """
SELECT ID,CAST(CHECKIN AS datetime2) CheckIn,CAST(CHECKOUT AS datetime2) CheckOut,
       CAST(ISNULL(SUITE_DISCOUNT_PERCENT,0) AS decimal(9,2)) SuiteDiscountPercent
FROM dbo.RESERVATION WITH (UPDLOCK,ROWLOCK)
WHERE ID=@ReservationId;

SELECT CAST(ISNULL(PRECIO,0) AS decimal(18,2)) Amount
FROM dbo.ROOM_CALENDAR WITH (UPDLOCK)
WHERE TRY_CONVERT(int,NULLIF(LTRIM(RTRIM(LOCK_DESCRIPTION)),''))=@ReservationId;

SELECT CAST(ISNULL(UnitPriceSnapshot,0)*ISNULL(Quantity,1) AS decimal(18,2)) Amount,
       ISNULL(TaxMode,'TaxableExclusive') TaxMode
FROM dbo.Reservation_Extra WITH (UPDLOCK)
WHERE ReservationID=@ReservationId;

SELECT CAST(ISNULL(TotalSnapshot,0) AS decimal(18,2)) Amount,
       ISNULL(TaxMode,'TaxableExclusive') TaxMode
FROM dbo.Reservation_Experience WITH (UPDLOCK)
WHERE ReservationID=@ReservationId;
""";

    using var multi = await connection.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { ReservationId = reservationId },
      transaction,
      cancellationToken: cancellationToken));

    var reservation = await multi.ReadSingleOrDefaultAsync<ReservationHeader>();
    if (reservation is null)
      throw new InvalidOperationException($"No existe la reservacion {reservationId}.");

    var suites = (await multi.ReadAsync<ChargeRow>()).Select(row => row.Amount);
    var extras = (await multi.ReadAsync<ChargeRow>()).Select(ToChargeLine);
    var experiences = (await multi.ReadAsync<ChargeRow>()).Select(ToChargeLine);
    var totals = ReservacionTotalsCalculator.Calculate(
      reservation.CheckIn,
      reservation.CheckOut,
      suites,
      extras,
      experiences,
      0m,
      reservation.SuiteDiscountPercent);

    await connection.ExecuteAsync(new CommandDefinition(
      "UPDATE dbo.RESERVATION SET TOTAL_PRICE=@Total,DATE_UPDATED=CAST(GETDATE() AS date) WHERE ID=@ReservationId;",
      new { Total = totals.TotalReservacion, ReservationId = reservationId },
      transaction,
      cancellationToken: cancellationToken));

    return totals.TotalReservacion;
  }

  private static ReservationChargeLine ToChargeLine(ChargeRow row)
    => new(row.Amount, row.TaxMode switch
    {
      "TaxIncluded" => ReservationChargeTaxMode.TaxIncluded,
      "NonTaxable" => ReservationChargeTaxMode.NonTaxable,
      _ => ReservationChargeTaxMode.TaxableExclusive
    });

  private sealed class ReservationHeader
  {
    public DateTime CheckIn { get; init; }
    public DateTime CheckOut { get; init; }
    public decimal SuiteDiscountPercent { get; init; }
  }

  private sealed class ChargeRow
  {
    public decimal Amount { get; init; }
    public string TaxMode { get; init; } = "TaxableExclusive";
  }
}
