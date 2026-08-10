using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantCashService : IRestaurantCashService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantCashService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<RestaurantCashRegisterDto>> GetRegistersAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    const string sql = "SELECT Id, RegisterCode AS Code, [Name], IsActive FROM restaurante.CashRegister WHERE Rfc=@Rfc AND SiteId=@SiteId ORDER BY [Name], Id;";
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RestaurantCashRegisterDto>(new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), SiteId = siteId }, cancellationToken: ct))).AsList();
  }

  public async Task<RestaurantCommandResult> SaveRegisterAsync(RestaurantCashRegisterUpsertRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    const string sql =
      """
      INSERT INTO restaurante.CashRegister (Rfc, SiteId, RegisterCode, [Name])
      SELECT @Rfc, site.Id, @Code, @Name FROM restaurante.Site site WHERE site.Rfc=@Rfc AND site.Id=@SiteId;
      SELECT CAST(SCOPE_IDENTITY() AS int);
      """;
    using var conn = CreateConnection();
    try
    {
      var id = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(sql, new { Rfc = rfc, request.SiteId, Code = request.Code.Trim().ToUpperInvariant(), Name = request.Name.Trim() }, cancellationToken: ct));
      return id.HasValue ? RestaurantCommandResult.Ok("La caja fue creada.", id) : RestaurantCommandResult.Fail("La sede no pertenece al RFC seleccionado.");
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return RestaurantCommandResult.Fail("Ya existe una caja con ese código en la sede.");
    }
  }

  public async Task<IReadOnlyList<RestaurantCashShiftDto>> GetShiftsAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT shiftInfo.Id, shiftInfo.SiteId, shiftInfo.CashRegisterId, registerInfo.[Name] AS RegisterName, shiftInfo.[Status],
             shiftInfo.OpeningFloat, shiftInfo.OpenedAt, shiftInfo.OpenedBy, shiftInfo.ClosedAt, shiftInfo.ClosedBy,
             CAST(0 AS decimal(18,2)) AS GrossSales, shiftInfo.ExpectedCash, shiftInfo.CountedCash, shiftInfo.Difference,
             shiftInfo.ApprovedAt, shiftInfo.ApprovedBy, shiftInfo.ReopenedAt, shiftInfo.ReopenedBy
      FROM restaurante.CashShift shiftInfo
      JOIN restaurante.CashRegister registerInfo ON registerInfo.Rfc=shiftInfo.Rfc AND registerInfo.Id=shiftInfo.CashRegisterId
      WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.SiteId=@SiteId
      ORDER BY CASE shiftInfo.[Status] WHEN 'Open' THEN 0 WHEN 'PendingApproval' THEN 1 ELSE 2 END, shiftInfo.OpenedAt DESC;

      SELECT shiftInfo.Id AS ShiftId, paymentInfo.PaymentMethod,
             COUNT(*) AS PaymentCount, CAST(0 AS int) AS RefundCount,
             CAST(ISNULL(SUM(paymentInfo.Amount),0) AS decimal(18,2)) AS Sales,
             CAST(ISNULL(SUM(paymentInfo.TipAmount),0) AS decimal(18,2)) AS Tips,
             CAST(0 AS decimal(18,2)) AS Refunds
      FROM restaurante.CashShift shiftInfo
      JOIN restaurante.Payment paymentInfo ON paymentInfo.Rfc=shiftInfo.Rfc
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.SiteId=@SiteId
        AND (orderInfo.CashShiftId=shiftInfo.Id OR orderInfo.CashRegisterId=shiftInfo.CashRegisterId)
        AND paymentInfo.PaidAt>=shiftInfo.OpenedAt
        AND paymentInfo.PaidAt<=ISNULL(shiftInfo.ClosedAt,SYSUTCDATETIME())
      GROUP BY shiftInfo.Id,paymentInfo.PaymentMethod;

      SELECT shiftInfo.Id AS ShiftId, paymentInfo.PaymentMethod,
             CAST(0 AS int) AS PaymentCount, COUNT(*) AS RefundCount,
             CAST(0 AS decimal(18,2)) AS Sales, CAST(0 AS decimal(18,2)) AS Tips,
             CAST(ISNULL(SUM(refundInfo.Amount),0) AS decimal(18,2)) AS Refunds
      FROM restaurante.CashShift shiftInfo
      JOIN restaurante.PaymentRefund refundInfo ON refundInfo.Rfc=shiftInfo.Rfc
      JOIN restaurante.Payment paymentInfo
        ON paymentInfo.Rfc=refundInfo.Rfc AND paymentInfo.Id=refundInfo.PaymentId
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.SiteId=@SiteId
        AND (orderInfo.CashShiftId=shiftInfo.Id OR orderInfo.CashRegisterId=shiftInfo.CashRegisterId)
        AND refundInfo.RefundedAt>=shiftInfo.OpenedAt
        AND refundInfo.RefundedAt<=ISNULL(shiftInfo.ClosedAt,SYSUTCDATETIME())
      GROUP BY shiftInfo.Id,paymentInfo.PaymentMethod;
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { Rfc = LogisticsRfc.Require(rfc), SiteId = siteId },
      cancellationToken: ct));
    var shifts = (await multi.ReadAsync<RestaurantCashShiftDto>()).AsList();
    var summaryRows = (await multi.ReadAsync<ShiftPaymentSummaryRow>())
      .Concat(await multi.ReadAsync<ShiftPaymentSummaryRow>())
      .ToLookup(row => row.ShiftId);

    foreach (var shift in shifts)
    {
      shift.PaymentMethods = RestaurantCashShiftPaymentSummaryRules.Combine(summaryRows[shift.Id].Select(row => new RestaurantCashShiftPaymentSummaryDto
      {
        PaymentMethod = row.PaymentMethod,
        PaymentCount = row.PaymentCount,
        RefundCount = row.RefundCount,
        Sales = row.Sales,
        Tips = row.Tips,
        Refunds = row.Refunds
      }));
      shift.GrossSales = shift.PaymentMethods.Sum(method => method.Sales);
    }

    return shifts;
  }

  public async Task<RestaurantCashShiftLogDto?> GetShiftLogAsync(string rfc, Guid shiftId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    var shift = await conn.QuerySingleOrDefaultAsync<RestaurantCashShiftDto>(new CommandDefinition(
      """
      SELECT shiftInfo.Id, shiftInfo.SiteId, shiftInfo.CashRegisterId, registerInfo.[Name] AS RegisterName, shiftInfo.[Status],
             shiftInfo.OpeningFloat, shiftInfo.OpenedAt, shiftInfo.OpenedBy, shiftInfo.ClosedAt, shiftInfo.ClosedBy,
             shiftInfo.ExpectedCash, shiftInfo.CountedCash, shiftInfo.Difference,
             shiftInfo.ApprovedAt, shiftInfo.ApprovedBy, shiftInfo.ReopenedAt, shiftInfo.ReopenedBy
      FROM restaurante.CashShift shiftInfo
      JOIN restaurante.CashRegister registerInfo
        ON registerInfo.Rfc=shiftInfo.Rfc AND registerInfo.Id=shiftInfo.CashRegisterId
      WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.Id=@ShiftId;
      """, new { Rfc = normalizedRfc, ShiftId = shiftId }, cancellationToken: ct));
    if (shift is null)
    {
      return null;
    }

    var endedAt = shift.ClosedAt ?? DateTime.UtcNow;
    var paymentRows = (await conn.QueryAsync<ShiftPaymentRow>(new CommandDefinition(
      """
      SELECT paymentInfo.Id, paymentInfo.OrderId, orderInfo.Folio AS OrderFolio, orderInfo.CustomerName,
             paymentInfo.PaymentMethod, paymentInfo.Amount, paymentInfo.TipAmount,
             paymentInfo.ExternalReference, paymentInfo.PaidAt, paymentInfo.ReceivedBy
      FROM restaurante.Payment paymentInfo
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE paymentInfo.Rfc=@Rfc
        AND (orderInfo.CashShiftId=@ShiftId OR orderInfo.CashRegisterId=@CashRegisterId)
        AND paymentInfo.PaidAt>=@OpenedAt AND paymentInfo.PaidAt<=@EndedAt
      ORDER BY paymentInfo.PaidAt, paymentInfo.Id;
      """, new
      {
        Rfc = normalizedRfc,
        ShiftId = shift.Id,
        shift.CashRegisterId,
        shift.OpenedAt,
        EndedAt = endedAt
      }, cancellationToken: ct))).AsList();

    var refundRows = (await conn.QueryAsync<ShiftRefundRow>(new CommandDefinition(
      """
      SELECT refundInfo.Id, paymentInfo.OrderId, orderInfo.Folio AS OrderFolio, orderInfo.CustomerName,
             paymentInfo.PaymentMethod, refundInfo.Amount, refundInfo.Reason,
             refundInfo.RefundedAt, refundInfo.RequestedBy, refundInfo.AuthorizedBy
      FROM restaurante.PaymentRefund refundInfo
      JOIN restaurante.Payment paymentInfo
        ON paymentInfo.Rfc=refundInfo.Rfc AND paymentInfo.Id=refundInfo.PaymentId
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE refundInfo.Rfc=@Rfc
        AND (orderInfo.CashShiftId=@ShiftId OR orderInfo.CashRegisterId=@CashRegisterId)
        AND refundInfo.RefundedAt>=@OpenedAt AND refundInfo.RefundedAt<=@EndedAt
      ORDER BY refundInfo.RefundedAt, refundInfo.Id;
      """, new
      {
        Rfc = normalizedRfc,
        ShiftId = shift.Id,
        shift.CashRegisterId,
        shift.OpenedAt,
        EndedAt = endedAt
      }, cancellationToken: ct))).AsList();

    var movementRows = (await conn.QueryAsync<ShiftMovementRow>(new CommandDefinition(
      """
      SELECT movement.Id, movement.MovementType, movement.PaymentMethod, movement.Amount,
             movement.OrderId, movement.Reason, movement.CreatedAt, movement.CreatedBy,
             orderInfo.Folio AS OrderFolio, orderInfo.CustomerName
      FROM restaurante.CashMovement movement
      LEFT JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=movement.Rfc AND orderInfo.Id=movement.OrderId
      WHERE movement.Rfc=@Rfc AND movement.CashShiftId=@ShiftId
        AND movement.MovementType NOT IN ('Sale','Refund')
      ORDER BY movement.CreatedAt, movement.Id;
      """, new { Rfc = normalizedRfc, ShiftId = shift.Id }, cancellationToken: ct))).AsList();

    var orderEvents = (await conn.QueryAsync<ShiftOrderEventRow>(new CommandDefinition(
      """
      SELECT eventInfo.Id, eventInfo.OrderId, orderInfo.Folio AS OrderFolio, orderInfo.CustomerName,
             eventInfo.EventType, eventInfo.Category, eventInfo.Title, eventInfo.[Description],
             eventInfo.Actor, eventInfo.OccurredAt
      FROM restaurante.OrderEvent eventInfo
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=eventInfo.Rfc AND orderInfo.Id=eventInfo.OrderId
      WHERE eventInfo.Rfc=@Rfc
        AND (orderInfo.CashShiftId=@ShiftId OR orderInfo.CashRegisterId=@CashRegisterId)
        AND eventInfo.OccurredAt>=@OpenedAt AND eventInfo.OccurredAt<=@EndedAt
        AND eventInfo.Category<>'Payment'
      ORDER BY eventInfo.OccurredAt, eventInfo.Id;
      """, new
      {
        Rfc = normalizedRfc,
        ShiftId = shift.Id,
        shift.CashRegisterId,
        shift.OpenedAt,
        EndedAt = endedAt
      }, cancellationToken: ct))).AsList();

    var entries = new List<RestaurantCashShiftLogEntryDto>
    {
      new()
      {
        Id = $"shift:{shift.Id}:opened",
        OccurredAt = shift.OpenedAt,
        EventType = "ShiftOpened",
        Category = "Shift",
        Title = "Turno abierto",
        Description = $"Fondo inicial: {shift.OpeningFloat:C}.",
        Actor = shift.OpenedBy,
        Amount = shift.OpeningFloat,
        PaymentMethod = "Cash"
      }
    };
    entries.AddRange(paymentRows.Select(PaymentEntry));
    entries.AddRange(refundRows.Select(RefundEntry));
    entries.AddRange(movementRows
      .Where(item => item.MovementType != "OpeningFloat")
      .Select(MovementEntry));
    entries.AddRange(orderEvents.Select(item => new RestaurantCashShiftLogEntryDto
    {
      Id = $"order-event:{item.Id}",
      OccurredAt = item.OccurredAt,
      EventType = item.EventType,
      Category = item.Category,
      Title = NormalizeLegacyText(item.Title) ?? item.Title,
      Description = NormalizeLegacyText(item.Description),
      Actor = item.Actor,
      OrderId = item.OrderId,
      OrderFolio = item.OrderFolio,
      CustomerName = item.CustomerName
    }));

    if (shift.ClosedAt.HasValue)
    {
      entries.Add(new()
      {
        Id = $"shift:{shift.Id}:closed",
        OccurredAt = shift.ClosedAt.Value,
        EventType = "ShiftCounted",
        Category = "Shift",
        Title = "Conteo ciego confirmado",
        Description = $"Contado: {shift.CountedCash:C} · Esperado: {shift.ExpectedCash:C} · Diferencia: {shift.Difference:C}.",
        Actor = shift.ClosedBy,
        Amount = shift.CountedCash
      });
    }
    if (shift.ApprovedAt.HasValue)
    {
      entries.Add(new()
      {
        Id = $"shift:{shift.Id}:approved",
        OccurredAt = shift.ApprovedAt.Value,
        EventType = "ShiftDifferenceApproved",
        Category = "Authorization",
        Title = "Diferencia autorizada",
        Description = $"Se autorizó la diferencia de {shift.Difference:C} y el turno quedó cerrado.",
        Actor = shift.ApprovedBy,
        AuthorizedBy = shift.ApprovedBy,
        Amount = shift.Difference,
        IsNegative = shift.Difference < 0
      });
    }
    if (shift.ReopenedAt.HasValue)
    {
      entries.Add(new()
      {
        Id = $"shift:{shift.Id}:reopened",
        OccurredAt = shift.ReopenedAt.Value,
        EventType = "ShiftReopened",
        Category = "Authorization",
        Title = "Turno reabierto",
        Description = "El corte fue reabierto después del conteo.",
        Actor = shift.ReopenedBy,
        AuthorizedBy = shift.ReopenedBy
      });
    }

    var paymentMethods = RestaurantCashShiftPaymentSummaryRules.Combine(paymentRows
      .GroupBy(item => item.PaymentMethod, StringComparer.OrdinalIgnoreCase)
      .Select(group => new RestaurantCashShiftPaymentSummaryDto
      {
        PaymentMethod = group.Key,
        PaymentCount = group.Count(),
        Sales = group.Sum(item => item.Amount),
        Tips = group.Sum(item => item.TipAmount)
      })
      .Concat(refundRows
        .GroupBy(item => item.PaymentMethod, StringComparer.OrdinalIgnoreCase)
        .Select(group => new RestaurantCashShiftPaymentSummaryDto
        {
          PaymentMethod = group.Key,
          RefundCount = group.Count(),
          Refunds = group.Sum(item => item.Amount)
        })));

    var orderCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
      """
      SELECT COUNT(*)
      FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.CashShiftId=@ShiftId;
      """, new { Rfc = normalizedRfc, ShiftId = shift.Id }, cancellationToken: ct));

    shift.GrossSales = paymentRows.Sum(item => item.Amount);
    shift.PaymentMethods = paymentMethods;

    return new()
    {
      Shift = shift,
      OrderCount = orderCount,
      PaymentCount = paymentRows.Count,
      RefundCount = refundRows.Count,
      GrossSales = shift.GrossSales,
      TipTotal = paymentRows.Sum(item => item.TipAmount),
      RefundTotal = refundRows.Sum(item => item.Amount),
      PaymentMethods = paymentMethods,
      Entries = entries
        .OrderBy(item => item.OccurredAt)
        .ThenBy(item => EntryOrder(item.EventType))
        .ThenBy(item => item.Id, StringComparer.Ordinal)
        .ToList()
    };
  }

  public async Task<RestaurantCashShiftDto> OpenShiftAsync(RestaurantCashShiftOpenRequest request, string userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    var id = Guid.NewGuid();
    using var conn = CreateConnection();
    try
    {
      const string sql =
        """
        INSERT INTO restaurante.CashShift
          (Id, Rfc, SiteId, CashRegisterId, [Status], OpeningFloat, OpenedBy)
        SELECT @Id, @Rfc, @SiteId, registerInfo.Id, 'Open', @OpeningFloat, @OpenedBy
        FROM restaurante.CashRegister registerInfo
        WHERE registerInfo.Rfc=@Rfc AND registerInfo.SiteId=@SiteId AND registerInfo.Id=@CashRegisterId AND registerInfo.IsActive=1;
        IF @@ROWCOUNT = 0 THROW 51020, 'La caja no pertenece a la sede y RFC seleccionados.', 1;
        INSERT INTO restaurante.CashMovement (Rfc, CashShiftId, MovementType, PaymentMethod, Amount, Reason, CreatedBy)
        VALUES (@Rfc, @Id, 'OpeningFloat', 'Cash', @OpeningFloat, 'Fondo inicial', @OpenedBy);
        """;
      await conn.ExecuteAsync(new CommandDefinition(sql, new
      {
        Id = id,
        Rfc = rfc,
        request.SiteId,
        request.CashRegisterId,
        request.OpeningFloat,
        OpenedBy = userName
      }, cancellationToken: ct));
      return (await GetShiftsAsync(rfc, request.SiteId, ct)).Single(item => item.Id == id);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      throw new InvalidOperationException("La caja ya tiene un turno abierto.", ex);
    }
  }

  public async Task<RestaurantCashShiftDto> CloseShiftAsync(RestaurantCashShiftCloseRequest request, string userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var row = await conn.QuerySingleOrDefaultAsync<ShiftIdentityRow>(new CommandDefinition(
        "SELECT Id, SiteId, [Status], OpeningFloat FROM restaurante.CashShift WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;",
        new { Rfc = rfc, Id = request.ShiftId }, tx, cancellationToken: ct))
        ?? throw new InvalidOperationException("El turno no existe en el RFC seleccionado.");
      if (row.Status != "Open")
      {
        throw new InvalidOperationException("El turno ya no está abierto.");
      }
      var expectedCash = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
        """
        SELECT CAST(ISNULL(SUM(CASE
          WHEN MovementType IN ('OpeningFloat','Sale','CashIn') AND PaymentMethod='Cash' THEN Amount
          WHEN MovementType IN ('Refund','CashOut') AND PaymentMethod='Cash' THEN -Amount
          ELSE 0 END), 0) AS decimal(18,2))
        FROM restaurante.CashMovement WHERE Rfc=@Rfc AND CashShiftId=@Id;
        """, new { Rfc = rfc, Id = request.ShiftId }, tx, cancellationToken: ct));
      var difference = request.CountedCash - expectedCash;
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.CashShift
        SET [Status]=CASE WHEN ABS(@Difference) < 0.01 THEN 'Closed' ELSE 'PendingApproval' END,
            ClosedAt=SYSUTCDATETIME(), ClosedBy=@ClosedBy,
            ExpectedCash=@ExpectedCash, CountedCash=@CountedCash, Difference=@Difference
        WHERE Rfc=@Rfc AND Id=@Id;
        """, new
        {
          Rfc = rfc,
          Id = request.ShiftId,
          ClosedBy = userName,
          ExpectedCash = expectedCash,
          request.CountedCash,
          Difference = difference
        }, tx, cancellationToken: ct));
      await tx.CommitAsync(ct);
      return (await GetShiftsAsync(rfc, row.SiteId, ct)).Single(item => item.Id == request.ShiftId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> ApproveShiftAsync(string rfc, Guid shiftId, string supervisorUserName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (string.IsNullOrWhiteSpace(supervisorUserName))
    {
      return RestaurantCommandResult.Fail("Se requiere un supervisor.");
    }
    const string sql =
      """
      UPDATE restaurante.CashShift
      SET [Status]='Closed', ApprovedAt=SYSUTCDATETIME(), ApprovedBy=@Supervisor
      WHERE Rfc=@Rfc AND Id=@Id AND [Status]='PendingApproval';
      """;
    using var conn = CreateConnection();
    var affected = await conn.ExecuteAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc, Id = shiftId, Supervisor = supervisorUserName }, cancellationToken: ct));
    return affected == 1
      ? RestaurantCommandResult.Ok("El corte y su diferencia fueron aprobados.")
      : RestaurantCommandResult.Fail("El turno no existe en el RFC o no espera aprobación.");
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static RestaurantCashShiftLogEntryDto PaymentEntry(ShiftPaymentRow payment)
  {
    var detail = $"{PaymentMethodLabel(payment.PaymentMethod)} · Venta {payment.Amount:C}";
    if (payment.TipAmount > 0)
    {
      detail += $" · Propina {payment.TipAmount:C}";
    }
    if (!string.IsNullOrWhiteSpace(payment.ExternalReference))
    {
      detail += $" · Referencia {payment.ExternalReference}";
    }
    return new()
    {
      Id = $"payment:{payment.Id}",
      OccurredAt = payment.PaidAt,
      EventType = "PaymentReceived",
      Category = "Payment",
      Title = "Pago recibido",
      Description = detail,
      Actor = payment.ReceivedBy,
      Amount = payment.Amount + payment.TipAmount,
      PaymentMethod = payment.PaymentMethod,
      OrderId = payment.OrderId,
      OrderFolio = payment.OrderFolio,
      CustomerName = payment.CustomerName
    };
  }

  private static RestaurantCashShiftLogEntryDto RefundEntry(ShiftRefundRow refund)
    => new()
    {
      Id = $"refund:{refund.Id}",
      OccurredAt = refund.RefundedAt,
      EventType = "PaymentRefunded",
      Category = "Refund",
      Title = "Devolución registrada",
      Description = $"{PaymentMethodLabel(refund.PaymentMethod)} · {refund.Reason}",
      Actor = refund.RequestedBy,
      AuthorizedBy = refund.AuthorizedBy,
      Amount = refund.Amount,
      IsNegative = true,
      PaymentMethod = refund.PaymentMethod,
      OrderId = refund.OrderId,
      OrderFolio = refund.OrderFolio,
      CustomerName = refund.CustomerName
    };

  private static RestaurantCashShiftLogEntryDto MovementEntry(ShiftMovementRow movement)
  {
    var isNegative = movement.MovementType is "CashOut";
    return new()
    {
      Id = $"movement:{movement.Id}",
      OccurredAt = movement.CreatedAt,
      EventType = movement.MovementType,
      Category = "Cash",
      Title = movement.MovementType switch
      {
        "CashIn" => "Entrada de efectivo",
        "CashOut" => "Retiro de efectivo",
        _ => "Movimiento de caja"
      },
      Description = movement.Reason,
      Actor = movement.CreatedBy,
      Amount = movement.Amount,
      IsNegative = isNegative,
      PaymentMethod = movement.PaymentMethod,
      OrderId = movement.OrderId,
      OrderFolio = movement.OrderFolio,
      CustomerName = movement.CustomerName
    };
  }

  private static int EntryOrder(string eventType)
    => eventType switch
    {
      "ShiftOpened" => 0,
      "PaymentReceived" => 20,
      "PaymentRefunded" => 21,
      "ShiftCounted" => 90,
      "ShiftDifferenceApproved" => 91,
      "ShiftReopened" => 92,
      _ => 50
    };

  private static string? NormalizeLegacyText(string? value)
  {
    if (string.IsNullOrEmpty(value) || (!value.Contains('Ã') && !value.Contains('Â')))
    {
      return value;
    }
    for (var pass = 0; pass < 2 && (value.Contains('Ã') || value.Contains('Â')); pass++)
    {
      value = value
        .Replace("Ãƒ", "Ã", StringComparison.Ordinal)
        .Replace("Ã‚", "Â", StringComparison.Ordinal)
        .Replace("Ã¡", "á", StringComparison.Ordinal)
        .Replace("Ã©", "é", StringComparison.Ordinal)
        .Replace("Ã­", "í", StringComparison.Ordinal)
        .Replace("Ã³", "ó", StringComparison.Ordinal)
        .Replace("Ãº", "ú", StringComparison.Ordinal)
        .Replace("Ã±", "ñ", StringComparison.Ordinal)
        .Replace("Ã", "Á", StringComparison.Ordinal)
        .Replace("Ã‰", "É", StringComparison.Ordinal)
        .Replace("Ã", "Í", StringComparison.Ordinal)
        .Replace("Ã“", "Ó", StringComparison.Ordinal)
        .Replace("Ãš", "Ú", StringComparison.Ordinal)
        .Replace("Ã‘", "Ñ", StringComparison.Ordinal)
        .Replace("Â·", "·", StringComparison.Ordinal)
        .Replace("Â", string.Empty, StringComparison.Ordinal);
    }
    return value;
  }

  private static string PaymentMethodLabel(string value)
    => value switch
    {
      "Cash" => "Efectivo",
      "Card" or "ExternalCard" => "Tarjeta",
      "Transfer" => "Transferencia",
      "DeliveryProvider" or "Platform" => "Plataforma",
      _ => value
    };

  private sealed class ShiftIdentityRow
  {
    public Guid Id { get; set; }
    public int SiteId { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal OpeningFloat { get; set; }
  }

  private sealed class ShiftPaymentRow
  {
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int OrderFolio { get; set; }
    public string? CustomerName { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal TipAmount { get; set; }
    public string? ExternalReference { get; set; }
    public DateTime PaidAt { get; set; }
    public string? ReceivedBy { get; set; }
  }

  private sealed class ShiftPaymentSummaryRow
  {
    public Guid ShiftId { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public int PaymentCount { get; set; }
    public int RefundCount { get; set; }
    public decimal Sales { get; set; }
    public decimal Tips { get; set; }
    public decimal Refunds { get; set; }
  }

  private sealed class ShiftRefundRow
  {
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public int OrderFolio { get; set; }
    public string? CustomerName { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime RefundedAt { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string AuthorizedBy { get; set; } = string.Empty;
  }

  private sealed class ShiftMovementRow
  {
    public long Id { get; set; }
    public string MovementType { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public decimal Amount { get; set; }
    public Guid? OrderId { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public int? OrderFolio { get; set; }
    public string? CustomerName { get; set; }
  }

  private sealed class ShiftOrderEventRow
  {
    public long Id { get; set; }
    public Guid OrderId { get; set; }
    public int OrderFolio { get; set; }
    public string? CustomerName { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Actor { get; set; }
    public DateTime OccurredAt { get; set; }
  }
}
