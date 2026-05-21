using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CuentasPorPagar.Recurrentes;

namespace OrionERP.Infrastructure.Features.CuentasPorPagar.Recurrentes;

public sealed class RecurrentApService : IRecurrentApService
{
  private const int DefaultRollingMonths = 18;
  private readonly IDbConnectionFactory _connectionFactory;

  public RecurrentApService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<RecurrentApWorkspaceDto> GetWorkspaceAsync(RecurrentApFilter filter, CancellationToken ct = default)
  {
    filter ??= new RecurrentApFilter();
    var rfc = NormalizeRequiredRfc(filter.Rfc);
    await GenerateMissingOccurrencesAsync(rfc, DateTime.Today.AddMonths(DefaultRollingMonths), ct);

    using var conn = CreateConnection();
    var payables = await LoadPayablesAsync(conn, rfc, activeOnly: false, ct);
    var vendors = await LoadVendorsAsync(conn, ct);
    var occurrences = await LoadOccurrencesAsync(conn, filter, ct);

    return new RecurrentApWorkspaceDto
    {
      Dashboard = BuildDashboard(occurrences, filter.DueSoonDays),
      Occurrences = occurrences,
      Payables = payables,
      Vendors = vendors,
      Statuses = RecurrentApStatuses.All,
      FrequencyUnits = RecurrentApFrequencyUnits.All
    };
  }

  public async Task<RecurrentApPayableSummaryDto?> GetPayableAsync(int payableId, string rfc, bool includePassword = false, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          Id,
          Rfc,
          [Name],
          BusinessPartnerId,
          PayeeNameSnapshot,
          PayeeRfcSnapshot,
          Category,
          [Description],
          Website,
          UserName,
          PasswordEnc,
          FrequencyUnit,
          IntervalCount,
          StartDate,
          EndDate,
          DueDayOfMonth,
          DueMonth,
          ExpectedAmount,
          Currency,
          IsActive
      FROM AP.RecurringPayable
      WHERE Id = @PayableId
        AND Rfc = @Rfc;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<RecurringPayableRow>(
      new CommandDefinition(sql, new { PayableId = payableId, Rfc = NormalizeRequiredRfc(rfc) }, cancellationToken: ct));
    return row is null ? null : MapPayable(row, includePassword);
  }

  public async Task<int> SavePayableAsync(RecurrentApUpsertRequest request, string? savedBy, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    ValidatePayable(request);
    request.Rfc = NormalizeRequiredRfc(request.Rfc);
    request.Currency = NormalizeCurrency(request.Currency);
    request.FrequencyUnit = NormalizeFrequency(request.FrequencyUnit);
    request.PayeeNameSnapshot = NullIfWhiteSpace(request.PayeeNameSnapshot);
    request.PayeeRfcSnapshot = NullIfWhiteSpace(request.PayeeRfcSnapshot);
    request.Category = NullIfWhiteSpace(request.Category);
    request.Description = NullIfWhiteSpace(request.Description);
    request.Website = NullIfWhiteSpace(request.Website);
    request.UserName = NullIfWhiteSpace(request.UserName);
    request.Password = NullIfWhiteSpace(request.Password);
    var passwordEnc = RecurrentApCredentialProtector.ProtectUtf8OrNull(request.Password);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      if (request.BusinessPartnerId.HasValue)
      {
        var vendor = await LoadVendorSnapshotAsync(conn, tx, request.BusinessPartnerId.Value, ct);
        if (vendor is null)
        {
          throw new InvalidOperationException("El proveedor seleccionado no existe.");
        }

        request.PayeeNameSnapshot = vendor.Name;
        request.PayeeRfcSnapshot = vendor.Rfc;
      }

      var actor = NormalizeActor(savedBy);
      int payableId;
      if (request.Id.HasValue && request.Id.Value > 0)
      {
        var rows = await conn.ExecuteAsync(
          new CommandDefinition(
            """
            UPDATE AP.RecurringPayable
            SET [Name] = @Name,
                BusinessPartnerId = @BusinessPartnerId,
                PayeeNameSnapshot = @PayeeNameSnapshot,
                PayeeRfcSnapshot = @PayeeRfcSnapshot,
                Category = @Category,
                [Description] = @Description,
                Website = @Website,
                UserName = @UserName,
                PasswordEnc = @PasswordEnc,
                FrequencyUnit = @FrequencyUnit,
                IntervalCount = @IntervalCount,
                StartDate = @StartDate,
                EndDate = @EndDate,
                DueDayOfMonth = @DueDayOfMonth,
                DueMonth = @DueMonth,
                ExpectedAmount = @ExpectedAmount,
                Currency = @Currency,
                IsActive = @IsActive,
                UpdatedAt = SYSUTCDATETIME(),
                UpdatedBy = @UpdatedBy
            WHERE Id = @Id
              AND Rfc = @Rfc;
            """,
            new
            {
              request.Id,
              request.Rfc,
              request.Name,
              request.BusinessPartnerId,
              request.PayeeNameSnapshot,
              request.PayeeRfcSnapshot,
              request.Category,
              request.Description,
              request.Website,
              request.UserName,
              PasswordEnc = passwordEnc,
              request.FrequencyUnit,
              request.IntervalCount,
              StartDate = request.StartDate.Date,
              EndDate = request.EndDate?.Date,
              request.DueDayOfMonth,
              request.DueMonth,
              request.ExpectedAmount,
              request.Currency,
              request.IsActive,
              UpdatedBy = actor
            },
            tx,
            cancellationToken: ct));

        if (rows == 0)
        {
          throw new InvalidOperationException("La cuenta por pagar recurrente ya no existe o pertenece a otro RFC.");
        }

        payableId = request.Id.Value;
        await DeleteFuturePendingOccurrencesAsync(conn, tx, payableId, request.Rfc, ct);
        await AddAuditAsync(conn, tx, request.Rfc, "RecurringPayable", payableId, "Updated", request.Name, actor, ct);
      }
      else
      {
        payableId = await conn.ExecuteScalarAsync<int>(
          new CommandDefinition(
            """
            INSERT INTO AP.RecurringPayable
            (
                Rfc, [Name], BusinessPartnerId, PayeeNameSnapshot, PayeeRfcSnapshot,
                Category, [Description], Website, UserName, PasswordEnc, FrequencyUnit, IntervalCount, StartDate, EndDate,
                DueDayOfMonth, DueMonth, ExpectedAmount, Currency, IsActive, CreatedBy
            )
            OUTPUT INSERTED.Id
            VALUES
            (
                @Rfc, @Name, @BusinessPartnerId, @PayeeNameSnapshot, @PayeeRfcSnapshot,
                @Category, @Description, @Website, @UserName, @PasswordEnc, @FrequencyUnit, @IntervalCount, @StartDate, @EndDate,
                @DueDayOfMonth, @DueMonth, @ExpectedAmount, @Currency, @IsActive, @CreatedBy
            );
            """,
            new
            {
              request.Rfc,
              request.Name,
              request.BusinessPartnerId,
              request.PayeeNameSnapshot,
              request.PayeeRfcSnapshot,
              request.Category,
              request.Description,
              request.Website,
              request.UserName,
              PasswordEnc = passwordEnc,
              request.FrequencyUnit,
              request.IntervalCount,
              StartDate = request.StartDate.Date,
              EndDate = request.EndDate?.Date,
              request.DueDayOfMonth,
              request.DueMonth,
              request.ExpectedAmount,
              request.Currency,
              request.IsActive,
              CreatedBy = actor
            },
            tx,
            cancellationToken: ct));

        await AddAuditAsync(conn, tx, request.Rfc, "RecurringPayable", payableId, "Created", request.Name, actor, ct);
      }

      if (request.IsActive)
      {
        var payable = await LoadPayableForGenerationAsync(conn, tx, payableId, request.Rfc, ct)
          ?? throw new InvalidOperationException("No se pudo recargar la cuenta recurrente.");
        await GeneratePayableOccurrencesAsync(conn, tx, payable, DateTime.Today.AddMonths(DefaultRollingMonths), ct);
      }

      await tx.CommitAsync(ct);
      return payableId;
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task DeactivatePayableAsync(int payableId, string rfc, string? updatedBy, CancellationToken ct = default)
  {
    const string sql =
      """
      UPDATE AP.RecurringPayable
      SET IsActive = 0,
          UpdatedAt = SYSUTCDATETIME(),
          UpdatedBy = @UpdatedBy
      WHERE Id = @PayableId
        AND Rfc = @Rfc;
      """;

    using var conn = CreateConnection();
    var rows = await conn.ExecuteAsync(new CommandDefinition(
      sql,
      new { PayableId = payableId, Rfc = NormalizeRequiredRfc(rfc), UpdatedBy = NormalizeActor(updatedBy) },
      cancellationToken: ct));

    if (rows == 0)
    {
      throw new InvalidOperationException("La cuenta recurrente ya no existe o pertenece a otro RFC.");
    }
  }

  public async Task<int> GenerateMissingOccurrencesAsync(string rfc, DateTime throughDate, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRequiredRfc(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var payables = await LoadPayablesForGenerationAsync(conn, tx, normalizedRfc, ct);
      var count = 0;
      foreach (var payable in payables)
      {
        count += await GeneratePayableOccurrencesAsync(conn, tx, payable, throughDate, ct);
      }

      await tx.CommitAsync(ct);
      return count;
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RecurrentApReseedResult> ReseedPayableOccurrencesAsync(int payableId, string rfc, string? updatedBy, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRequiredRfc(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var payable = await LoadPayableForGenerationAsync(conn, tx, payableId, normalizedRfc, ct)
        ?? throw new InvalidOperationException("La cuenta recurrente debe existir, pertenecer al RFC y estar activa para resembrar.");

      var preserved = await CountFuturePreservedOccurrencesAsync(conn, tx, payableId, normalizedRfc, ct);
      var deleted = await DeleteFuturePendingOccurrencesAsync(conn, tx, payableId, normalizedRfc, ct);
      var created = await GeneratePayableOccurrencesAsync(conn, tx, payable, DateTime.Today.AddMonths(DefaultRollingMonths), ct);
      var actor = NormalizeActor(updatedBy);

      await AddAuditAsync(
        conn,
        tx,
        normalizedRfc,
        "RecurringPayable",
        payableId,
        "Reseeded",
        $"Deleted={deleted}; Created={created}; Preserved={preserved}",
        actor,
        ct);

      await tx.CommitAsync(ct);
      return new RecurrentApReseedResult
      {
        RecurringPayableId = payableId,
        DeletedCount = deleted,
        CreatedCount = created,
        PreservedCount = preserved
      };
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RecurrentApOccurrenceDetailDto?> GetOccurrenceDetailAsync(int occurrenceId, string rfc, bool includePassword = false, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          o.Id,
          o.RecurringPayableId,
          o.Rfc,
          rp.[Name] AS PayableName,
          COALESCE(rp.PayeeNameSnapshot, bp.PartnerName) AS PayeeName,
          COALESCE(rp.PayeeRfcSnapshot, bp.Rfc) AS PayeeRfc,
          rp.Category,
          rp.[Description],
          rp.Website,
          rp.UserName,
          rp.PasswordEnc,
          rp.FrequencyUnit,
          rp.IntervalCount,
          rp.StartDate,
          rp.EndDate,
          rp.DueDayOfMonth,
          rp.DueMonth,
          rp.IsActive,
          o.PeriodStartDate,
          o.DueDate,
          o.ExpectedAmount,
          o.ActualPaidAmount,
          o.[Status],
          o.PaymentDate,
          o.Notes
      FROM AP.PayableOccurrence o
      JOIN AP.RecurringPayable rp
        ON rp.Id = o.RecurringPayableId
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.Id = rp.BusinessPartnerId
      WHERE o.Id = @OccurrenceId
        AND o.Rfc = @Rfc;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<OccurrenceDetailRow>(
      new CommandDefinition(sql, new { OccurrenceId = occurrenceId, Rfc = NormalizeRequiredRfc(rfc) }, cancellationToken: ct));

    return row is null ? null : MapOccurrenceDetail(row, includePassword);
  }

  public async Task SetOccurrenceStatusAsync(RecurrentApOccurrenceStatusRequest request, string? updatedBy, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var rfc = NormalizeRequiredRfc(request.Rfc);
    var status = NormalizeStatus(request.Status);
    var expectedAmount = request.ExpectedAmount.HasValue ? Math.Max(request.ExpectedAmount.Value, 0m) : (decimal?)null;
    var actualAmount = Math.Max(request.ActualAmount ?? 0m, 0m);
    var actor = NormalizeActor(updatedBy);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var paymentSummary = await LoadPaymentSummaryAsync(conn, tx, request.OccurrenceId, rfc, ct);
      if (paymentSummary.PaymentCount > 0)
      {
        actualAmount = paymentSummary.TotalAmount;
        status = ResolvePaymentStatus(paymentSummary.TotalAmount, expectedAmount);
      }

      const string sql =
        """
        UPDATE AP.PayableOccurrence
        SET [Status] = @Status,
            ExpectedAmount = @ExpectedAmount,
            ActualPaidAmount = @ActualPaidAmount,
            PaymentDate = @PaymentDate,
            Notes = @Notes,
            UpdatedAt = SYSUTCDATETIME(),
            UpdatedBy = @UpdatedBy
        WHERE Id = @OccurrenceId
          AND Rfc = @Rfc;
        """;

      var rows = await conn.ExecuteAsync(new CommandDefinition(
        sql,
        new
        {
          request.OccurrenceId,
          Rfc = rfc,
          Status = status,
          ExpectedAmount = expectedAmount,
          ActualPaidAmount = actualAmount,
          PaymentDate = paymentSummary.PaymentCount > 0 ? paymentSummary.PaymentDate : request.PaymentDate?.Date,
          Notes = NullIfWhiteSpace(request.Notes),
          UpdatedBy = actor
        },
        tx,
        cancellationToken: ct));

      if (rows == 0)
      {
        throw new InvalidOperationException("El vencimiento AP ya no existe o pertenece a otro RFC.");
      }

      await AddAuditAsync(conn, tx, rfc, "PayableOccurrence", request.OccurrenceId, "StatusChanged", status, actor, ct);
      await tx.CommitAsync(ct);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task CancelOccurrenceAsync(int occurrenceId, string rfc, string? updatedBy, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRequiredRfc(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var paymentCount = await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
          """
          SELECT COUNT(*)
          FROM AP.OccurrencePayment
          WHERE OccurrenceId = @OccurrenceId
            AND Rfc = @Rfc;
          """,
          new { OccurrenceId = occurrenceId, Rfc = normalizedRfc },
          tx,
          cancellationToken: ct));

      if (paymentCount > 0)
      {
        throw new InvalidOperationException("No se puede cancelar un vencimiento con pólizas ligadas. Primero desliga la póliza.");
      }

      var actor = NormalizeActor(updatedBy);
      var rows = await conn.ExecuteAsync(
        new CommandDefinition(
          """
          UPDATE AP.PayableOccurrence
          SET [Status] = 'Cancelled',
              ActualPaidAmount = 0,
              PaymentDate = NULL,
              UpdatedAt = SYSUTCDATETIME(),
              UpdatedBy = @UpdatedBy
          WHERE Id = @OccurrenceId
            AND Rfc = @Rfc;
          """,
          new { OccurrenceId = occurrenceId, Rfc = normalizedRfc, UpdatedBy = actor },
          tx,
          cancellationToken: ct));

      if (rows == 0)
      {
        throw new InvalidOperationException("El vencimiento AP ya no existe o pertenece a otro RFC.");
      }

      await AddAuditAsync(conn, tx, normalizedRfc, "PayableOccurrence", occurrenceId, "Cancelled", "Cancelado manualmente", actor, ct);
      await tx.CommitAsync(ct);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task LinkTransactionAsync(RecurrentApTransactionLinkRequest request, string? linkedBy, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var rfc = NormalizeRequiredRfc(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var occurrence = await LoadOccurrenceStateAsync(conn, tx, request.OccurrenceId, rfc, ct)
        ?? throw new InvalidOperationException("El vencimiento AP ya no existe o pertenece a otro RFC.");
      var transaccion = await LoadTransactionStateAsync(conn, tx, request.TransaccionId, ct)
        ?? throw new InvalidOperationException("La póliza seleccionada no existe.");

      if (!string.Equals(transaccion.Rfc, rfc, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException("La póliza seleccionada pertenece a otro RFC.");
      }

      var amount = Math.Abs(request.Amount ?? transaccion.Amount);
      var paymentDate = request.PaymentDate?.Date ?? transaccion.Fecha.Date;
      var actor = NormalizeActor(linkedBy);

      await conn.ExecuteAsync(
        new CommandDefinition(
          """
          INSERT INTO AP.OccurrencePayment (OccurrenceId, Rfc, TransaccionId, Amount, PaymentDate, Notes, CreatedBy)
          SELECT @OccurrenceId, @Rfc, @TransaccionId, @Amount, @PaymentDate, @Notes, @CreatedBy
          WHERE NOT EXISTS (
              SELECT 1
              FROM AP.OccurrencePayment existing
              WHERE existing.OccurrenceId = @OccurrenceId
                AND existing.TransaccionId = @TransaccionId
          );
          """,
          new
          {
            request.OccurrenceId,
            Rfc = rfc,
            request.TransaccionId,
            Amount = amount,
            PaymentDate = paymentDate,
            Notes = NullIfWhiteSpace(request.Notes),
            CreatedBy = actor
          },
          tx,
          cancellationToken: ct));

      await RecalculateOccurrenceFromPaymentsAsync(conn, tx, request.OccurrenceId, rfc, occurrence.ExpectedAmount, actor, ct);
      await AddAuditAsync(conn, tx, rfc, "PayableOccurrence", request.OccurrenceId, "TransactionLinked", request.TransaccionId.ToString(), actor, ct);
      await tx.CommitAsync(ct);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task UnlinkTransactionAsync(int paymentId, string rfc, string? unlinkedBy, CancellationToken ct = default)
  {
    var normalizedRfc = NormalizeRequiredRfc(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);

    try
    {
      var payment = await conn.QueryFirstOrDefaultAsync<PaymentStateRow>(
        new CommandDefinition(
          """
          SELECT p.Id, p.OccurrenceId, o.ExpectedAmount
          FROM AP.OccurrencePayment p
          JOIN AP.PayableOccurrence o
            ON o.Id = p.OccurrenceId
          WHERE p.Id = @PaymentId
            AND p.Rfc = @Rfc;
          """,
          new { PaymentId = paymentId, Rfc = normalizedRfc },
          tx,
          cancellationToken: ct));

      if (payment is null)
      {
        throw new InvalidOperationException("El pago AP ya no existe o pertenece a otro RFC.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM AP.OccurrencePayment WHERE Id = @PaymentId AND Rfc = @Rfc;",
        new { PaymentId = paymentId, Rfc = normalizedRfc },
        tx,
        cancellationToken: ct));

      var actor = NormalizeActor(unlinkedBy);
      await RecalculateOccurrenceFromPaymentsAsync(conn, tx, payment.OccurrenceId, normalizedRfc, payment.ExpectedAmount, actor, ct);
      await AddAuditAsync(conn, tx, normalizedRfc, "PayableOccurrence", payment.OccurrenceId, "TransactionUnlinked", paymentId.ToString(), actor, ct);
      await tx.CommitAsync(ct);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<RecurrentApTransactionCandidateDto>> SearchTransactionsAsync(string rfc, string? search, int top = 25, CancellationToken ct = default)
  {
    var sql =
      """
      SELECT TOP (@Top)
          t.ID AS Id,
          t.Fecha,
          t.Concepto,
          CAST(t.Monto AS decimal(18,2)) AS Monto,
          t.Tipo_Poliza AS TipoPoliza,
          t.Forma_Pago AS FormaPago,
          CAST(CASE WHEN EXISTS (
              SELECT 1
              FROM AP.OccurrencePayment p
              WHERE p.TransaccionId = t.ID
          ) THEN 1 ELSE 0 END AS bit) AS IsLinkedToAp
      FROM dbo.Transacciones t
      WHERE t.RFC = @Rfc
      """;

    var parameters = new DynamicParameters();
    parameters.Add("@Rfc", NormalizeRequiredRfc(rfc), DbType.String);
    parameters.Add("@Top", Math.Clamp(top, 1, 100), DbType.Int32);

    if (!string.IsNullOrWhiteSpace(search))
    {
      sql += """

        AND (
            CONVERT(varchar(30), t.ID) = @ExactId
            OR t.Concepto LIKE @Search
            OR t.Referencia LIKE @Search
            OR t.Memo LIKE @Search
        )
      """;
      parameters.Add("@ExactId", search.Trim(), DbType.String);
      parameters.Add("@Search", $"%{search.Trim()}%", DbType.String);
    }

    sql += "\nORDER BY t.Fecha DESC, t.ID DESC;";

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<RecurrentApTransactionCandidateDto>(
      new CommandDefinition(sql, parameters, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<RecurrentApTransactionLinkDto>> GetTransactionLinksAsync(int transaccionId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          p.Id AS PaymentId,
          p.OccurrenceId,
          rp.Id AS RecurringPayableId,
          p.Rfc,
          rp.[Name] AS PayableName,
          o.DueDate,
          p.TransaccionId,
          p.Amount,
          p.PaymentDate,
          o.[Status]
      FROM AP.OccurrencePayment p
      JOIN AP.PayableOccurrence o
        ON o.Id = p.OccurrenceId
      JOIN AP.RecurringPayable rp
        ON rp.Id = o.RecurringPayableId
      WHERE p.TransaccionId = @TransaccionId
      ORDER BY o.DueDate DESC, p.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<RecurrentApTransactionLinkDto>(
      new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<RecurrentApTransactionLinkDto>> GetOccurrenceTransactionLinksAsync(int occurrenceId, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
          p.Id AS PaymentId,
          p.OccurrenceId,
          rp.Id AS RecurringPayableId,
          p.Rfc,
          rp.[Name] AS PayableName,
          o.DueDate,
          p.TransaccionId,
          p.Amount,
          p.PaymentDate,
          o.[Status]
      FROM AP.OccurrencePayment p
      JOIN AP.PayableOccurrence o
        ON o.Id = p.OccurrenceId
      JOIN AP.RecurringPayable rp
        ON rp.Id = o.RecurringPayableId
      WHERE p.OccurrenceId = @OccurrenceId
        AND p.Rfc = @Rfc
        AND o.Rfc = @Rfc
      ORDER BY p.PaymentDate DESC, p.Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<RecurrentApTransactionLinkDto>(
      new CommandDefinition(sql, new { OccurrenceId = occurrenceId, Rfc = NormalizeRequiredRfc(rfc) }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<RecurrentApOccurrenceListItemDto>> SearchOpenOccurrencesAsync(string rfc, string? search, int top = 25, CancellationToken ct = default)
  {
    var filter = new RecurrentApFilter
    {
      Rfc = rfc,
      SearchText = search,
      Status = null,
      FromDate = DateTime.Today.AddMonths(-6),
      ToDate = DateTime.Today.AddMonths(DefaultRollingMonths),
      Take = Math.Clamp(top, 1, 100)
    };

    using var conn = CreateConnection();
    var rows = await LoadOccurrencesAsync(conn, filter, ct);
    return rows
      .Where(row => string.Equals(row.Status, RecurrentApStatuses.Pending, StringComparison.OrdinalIgnoreCase)
        || string.Equals(row.Status, RecurrentApStatuses.PartiallyPaid, StringComparison.OrdinalIgnoreCase))
      .Take(filter.Take)
      .ToList();
  }

  public async Task<RecurrentApAttachmentDto> AddAttachmentAsync(RecurrentApAttachmentCreateRequest request, CancellationToken ct = default)
  {
    if (request is null)
    {
      throw new ArgumentNullException(nameof(request));
    }

    var rfc = NormalizeRequiredRfc(request.Rfc);
    var content = request.Content ?? [];
    if (content.Length > RecurrentApAttachmentCreateRequest.MaxFileSizeBytes)
    {
      throw new InvalidOperationException("El archivo excede el tamaño máximo permitido.");
    }

    var fileName = Path.GetFileName(request.FileName);
    if (string.IsNullOrWhiteSpace(fileName))
    {
      throw new InvalidOperationException("El archivo debe tener nombre.");
    }

    using var conn = CreateConnection();
    await EnsureOccurrenceBelongsToRfcAsync(conn, request.OccurrenceId, rfc, ct);

    const string sql =
      """
      INSERT INTO AP.OccurrenceAttachment
      (
          OccurrenceId, Rfc, FileName, ContentType, Content, SizeBytes, UploadedBy
      )
      OUTPUT
          INSERTED.Id,
          INSERTED.OccurrenceId,
          INSERTED.FileName,
          INSERTED.ContentType,
          INSERTED.SizeBytes,
          INSERTED.UploadedAt,
          INSERTED.UploadedBy
      VALUES
      (
          @OccurrenceId, @Rfc, @FileName, @ContentType, @Content, @SizeBytes, @UploadedBy
      );
      """;

    return await conn.QuerySingleAsync<RecurrentApAttachmentDto>(
      new CommandDefinition(
        sql,
        new
        {
          request.OccurrenceId,
          Rfc = rfc,
          FileName = fileName,
          ContentType = NullIfWhiteSpace(request.ContentType) ?? "application/octet-stream",
          Content = content,
          SizeBytes = content.LongLength,
          UploadedBy = NullIfWhiteSpace(request.UploadedBy)
        },
        cancellationToken: ct));
  }

  public async Task<IReadOnlyList<RecurrentApAttachmentDto>> GetAttachmentsAsync(int occurrenceId, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT Id, OccurrenceId, FileName, ContentType, SizeBytes, UploadedAt, UploadedBy
      FROM AP.OccurrenceAttachment
      WHERE OccurrenceId = @OccurrenceId
        AND Rfc = @Rfc
        AND DeletedAt IS NULL
      ORDER BY UploadedAt DESC, Id DESC;
      """;

    using var conn = CreateConnection();
    var rows = await conn.QueryAsync<RecurrentApAttachmentDto>(
      new CommandDefinition(sql, new { OccurrenceId = occurrenceId, Rfc = NormalizeRequiredRfc(rfc) }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<RecurrentApAttachmentContent?> GetAttachmentContentAsync(int attachmentId, string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT FileName, ContentType, Content
      FROM AP.OccurrenceAttachment
      WHERE Id = @AttachmentId
        AND Rfc = @Rfc
        AND DeletedAt IS NULL;
      """;

    using var conn = CreateConnection();
    return await conn.QueryFirstOrDefaultAsync<RecurrentApAttachmentContent>(
      new CommandDefinition(sql, new { AttachmentId = attachmentId, Rfc = NormalizeRequiredRfc(rfc) }, cancellationToken: ct));
  }

  public async Task DeleteAttachmentAsync(int attachmentId, string rfc, string? deletedBy, CancellationToken ct = default)
  {
    const string sql =
      """
      UPDATE AP.OccurrenceAttachment
      SET DeletedAt = SYSUTCDATETIME(),
          DeletedBy = @DeletedBy
      WHERE Id = @AttachmentId
        AND Rfc = @Rfc
        AND DeletedAt IS NULL;
      """;

    using var conn = CreateConnection();
    await conn.ExecuteAsync(new CommandDefinition(
      sql,
      new { AttachmentId = attachmentId, Rfc = NormalizeRequiredRfc(rfc), DeletedBy = NormalizeActor(deletedBy) },
      cancellationToken: ct));
  }

  private async Task<IReadOnlyList<RecurrentApOccurrenceListItemDto>> LoadOccurrencesAsync(DbConnection conn, RecurrentApFilter filter, CancellationToken ct)
  {
    var sql =
      """
      SELECT TOP (@Take)
          o.Id,
          o.RecurringPayableId,
          o.Rfc,
          rp.[Name] AS PayableName,
          COALESCE(rp.PayeeNameSnapshot, bp.PartnerName) AS PayeeName,
          rp.Category,
          o.PeriodStartDate,
          o.DueDate,
          o.ExpectedAmount,
          o.ActualPaidAmount,
          o.[Status],
          o.PaymentDate,
          o.Notes,
          ISNULL(paymentCounts.PaymentLinkCount, 0) AS PaymentLinkCount,
          ISNULL(attachmentCounts.AttachmentCount, 0) AS AttachmentCount,
          CAST(CASE
              WHEN o.[Status] IN ('Pending','PartiallyPaid')
               AND o.DueDate >= CONVERT(date, SYSUTCDATETIME())
               AND o.DueDate <= DATEADD(DAY, @DueSoonDays, CONVERT(date, SYSUTCDATETIME()))
              THEN 1 ELSE 0 END AS bit) AS IsDueSoon,
          CAST(CASE
              WHEN o.[Status] IN ('Pending','PartiallyPaid')
               AND o.DueDate < CONVERT(date, SYSUTCDATETIME())
              THEN 1 ELSE 0 END AS bit) AS IsOverdue
      FROM AP.PayableOccurrence o
      JOIN AP.RecurringPayable rp
        ON rp.Id = o.RecurringPayableId
      LEFT JOIN dbo.BusinessPartner bp
        ON bp.Id = rp.BusinessPartnerId
      OUTER APPLY (
          SELECT COUNT(*) AS PaymentLinkCount
          FROM AP.OccurrencePayment p
          WHERE p.OccurrenceId = o.Id
      ) paymentCounts
      OUTER APPLY (
          SELECT COUNT(*) AS AttachmentCount
          FROM AP.OccurrenceAttachment a
          WHERE a.OccurrenceId = o.Id
            AND a.DeletedAt IS NULL
      ) attachmentCounts
      WHERE o.Rfc = @Rfc
      """;

    var parameters = new DynamicParameters();
    parameters.Add("@Rfc", NormalizeRequiredRfc(filter.Rfc), DbType.String);
    parameters.Add("@DueSoonDays", Math.Clamp(filter.DueSoonDays, 0, 365), DbType.Int32);
    parameters.Add("@Take", Math.Clamp(filter.Take, 1, 5000), DbType.Int32);

    if (filter.FromDate.HasValue)
    {
      sql += "\n  AND o.DueDate >= @FromDate";
      parameters.Add("@FromDate", filter.FromDate.Value.Date, DbType.Date);
    }

    if (filter.ToDate.HasValue)
    {
      sql += "\n  AND o.DueDate <= @ToDate";
      parameters.Add("@ToDate", filter.ToDate.Value.Date, DbType.Date);
    }

    if (filter.OccurrenceId.HasValue)
    {
      sql += "\n  AND o.Id = @OccurrenceId";
      parameters.Add("@OccurrenceId", filter.OccurrenceId.Value, DbType.Int32);
    }

    if (!string.IsNullOrWhiteSpace(filter.Status))
    {
      sql += "\n  AND o.[Status] = @Status";
      parameters.Add("@Status", NormalizeStatus(filter.Status), DbType.String);
    }
    else if (filter.OpenOnly)
    {
      sql += "\n  AND o.[Status] IN ('Pending','PartiallyPaid')";
    }

    if (!string.IsNullOrWhiteSpace(filter.SearchText))
    {
      sql += """

        AND (
            rp.[Name] LIKE @Search
            OR rp.Category LIKE @Search
            OR rp.PayeeNameSnapshot LIKE @Search
            OR bp.PartnerName LIKE @Search
            OR o.Notes LIKE @Search
        )
      """;
      parameters.Add("@Search", $"%{filter.SearchText.Trim()}%", DbType.String);
    }

    sql += "\nORDER BY o.DueDate ASC, o.Id ASC;";

    var rows = await conn.QueryAsync<RecurrentApOccurrenceListItemDto>(
      new CommandDefinition(sql, parameters, cancellationToken: ct));
    return rows.AsList();
  }

  private static RecurrentApDashboardDto BuildDashboard(IReadOnlyList<RecurrentApOccurrenceListItemDto> occurrences, int dueSoonDays)
  {
    var today = DateTime.Today;
    var monthStart = new DateTime(today.Year, today.Month, 1);
    var nextMonth = monthStart.AddMonths(1);
    var open = occurrences
      .Where(item => string.Equals(item.Status, RecurrentApStatuses.Pending, StringComparison.OrdinalIgnoreCase)
        || string.Equals(item.Status, RecurrentApStatuses.PartiallyPaid, StringComparison.OrdinalIgnoreCase))
      .ToList();

    return new RecurrentApDashboardDto
    {
      TotalOpen = open.Count,
      DueSoon = open.Count(item => item.DueDate.Date >= today && item.DueDate.Date <= today.AddDays(Math.Clamp(dueSoonDays, 0, 365))),
      Overdue = open.Count(item => item.DueDate.Date < today),
      PaidThisMonth = occurrences.Count(item => string.Equals(item.Status, RecurrentApStatuses.Paid, StringComparison.OrdinalIgnoreCase)
        && item.PaymentDate.HasValue
        && item.PaymentDate.Value.Date >= monthStart
        && item.PaymentDate.Value.Date < nextMonth),
      ExpectedOpenAmount = open.Sum(item => Math.Max((item.ExpectedAmount ?? 0m) - item.ActualPaidAmount, 0m)),
      PaidThisMonthAmount = occurrences
        .Where(item => item.PaymentDate.HasValue && item.PaymentDate.Value.Date >= monthStart && item.PaymentDate.Value.Date < nextMonth)
        .Sum(item => item.ActualPaidAmount)
    };
  }

  private static async Task<IReadOnlyList<RecurrentApPayableSummaryDto>> LoadPayablesAsync(DbConnection conn, string rfc, bool activeOnly, CancellationToken ct)
  {
    var sql =
      """
      SELECT
          Id,
          Rfc,
          [Name],
          BusinessPartnerId,
          PayeeNameSnapshot,
          PayeeRfcSnapshot,
          Category,
          [Description],
          Website,
          UserName,
          FrequencyUnit,
          IntervalCount,
          StartDate,
          EndDate,
          DueDayOfMonth,
          DueMonth,
          ExpectedAmount,
          Currency,
          IsActive
      FROM AP.RecurringPayable
      WHERE Rfc = @Rfc
      """;

    if (activeOnly)
    {
      sql += "\n  AND IsActive = 1";
    }

    sql += "\nORDER BY IsActive DESC, [Name], Id;";

    var rows = await conn.QueryAsync<RecurringPayableRow>(
      new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.Select(row => MapPayable(row, includePassword: false)).ToList();
  }

  private static async Task<IReadOnlyList<RecurrentApVendorOptionDto>> LoadVendorsAsync(DbConnection conn, CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          bp.Id,
          bp.PartnerName AS Name,
          bp.Rfc
      FROM dbo.BusinessPartner bp
      WHERE bp.IsActive = 1
        AND (
            EXISTS (SELECT 1 FROM dbo.BusinessPartnerRole roleMap WHERE roleMap.BusinessPartnerId = bp.Id AND roleMap.RoleCode = 'Vendor')
            OR EXISTS (SELECT 1 FROM logistica.VendorProfile vendor WHERE vendor.BusinessPartnerId = bp.Id)
        )
      ORDER BY bp.PartnerName, bp.Id;
      """;

    var rows = await conn.QueryAsync<RecurrentApVendorOptionDto>(new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
  }

  private static async Task<IReadOnlyList<RecurrentApPayableSummaryDto>> LoadPayablesForGenerationAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          Id,
          Rfc,
          [Name],
          BusinessPartnerId,
          PayeeNameSnapshot,
          PayeeRfcSnapshot,
          Category,
          [Description],
          Website,
          UserName,
          FrequencyUnit,
          IntervalCount,
          StartDate,
          EndDate,
          DueDayOfMonth,
          DueMonth,
          ExpectedAmount,
          Currency,
          IsActive
      FROM AP.RecurringPayable
      WHERE Rfc = @Rfc
        AND IsActive = 1;
      """;

    var rows = await conn.QueryAsync<RecurringPayableRow>(
      new CommandDefinition(sql, new { Rfc = rfc }, tx, cancellationToken: ct));
    return rows.Select(row => MapPayable(row, includePassword: false)).ToList();
  }

  private static async Task<RecurrentApPayableSummaryDto?> LoadPayableForGenerationAsync(
    DbConnection conn,
    DbTransaction tx,
    int payableId,
    string rfc,
    CancellationToken ct)
  {
    var rows = await LoadPayablesForGenerationAsync(conn, tx, rfc, ct);
    return rows.FirstOrDefault(item => item.Id == payableId);
  }

  private static async Task<int> GeneratePayableOccurrencesAsync(
    DbConnection conn,
    DbTransaction tx,
    RecurrentApPayableSummaryDto payable,
    DateTime throughDate,
    CancellationToken ct)
  {
    var seeds = RecurrentApOccurrenceGenerator.Generate(payable, throughDate);
    var created = 0;

    foreach (var seed in seeds)
    {
      created += await conn.ExecuteAsync(
        new CommandDefinition(
          """
          INSERT INTO AP.PayableOccurrence (RecurringPayableId, Rfc, PeriodStartDate, DueDate, ExpectedAmount)
          SELECT @RecurringPayableId, @Rfc, @PeriodStartDate, @DueDate, @ExpectedAmount
          WHERE NOT EXISTS (
              SELECT 1
              FROM AP.PayableOccurrence existing
              WHERE existing.RecurringPayableId = @RecurringPayableId
                AND existing.DueDate = @DueDate
          );
          """,
          new
          {
            RecurringPayableId = payable.Id,
            payable.Rfc,
            seed.PeriodStartDate,
            seed.DueDate,
            seed.ExpectedAmount
          },
          tx,
          cancellationToken: ct));
    }

    return created;
  }

  private static async Task<int> DeleteFuturePendingOccurrencesAsync(DbConnection conn, DbTransaction tx, int payableId, string rfc, CancellationToken ct)
  {
    return await conn.ExecuteAsync(
      new CommandDefinition(
        """
        DELETE o
        FROM AP.PayableOccurrence o
        WHERE o.RecurringPayableId = @PayableId
          AND o.Rfc = @Rfc
          AND o.DueDate >= CONVERT(date, SYSUTCDATETIME())
          AND o.[Status] = 'Pending'
          AND o.ActualPaidAmount = 0
          AND o.PaymentDate IS NULL
          AND o.Notes IS NULL
          AND o.UpdatedAt IS NULL
          AND NOT EXISTS (SELECT 1 FROM AP.OccurrencePayment p WHERE p.OccurrenceId = o.Id)
          AND NOT EXISTS (SELECT 1 FROM AP.OccurrenceAttachment a WHERE a.OccurrenceId = o.Id AND a.DeletedAt IS NULL);
        """,
        new { PayableId = payableId, Rfc = rfc },
        tx,
        cancellationToken: ct));
  }

  private static async Task<int> CountFuturePreservedOccurrencesAsync(DbConnection conn, DbTransaction tx, int payableId, string rfc, CancellationToken ct)
  {
    return await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(
        """
        SELECT COUNT(*)
        FROM AP.PayableOccurrence o
        WHERE o.RecurringPayableId = @PayableId
          AND o.Rfc = @Rfc
          AND o.DueDate >= CONVERT(date, SYSUTCDATETIME())
          AND (
              o.[Status] <> 'Pending'
              OR o.ActualPaidAmount <> 0
              OR o.PaymentDate IS NOT NULL
              OR o.Notes IS NOT NULL
              OR o.UpdatedAt IS NOT NULL
              OR EXISTS (SELECT 1 FROM AP.OccurrencePayment p WHERE p.OccurrenceId = o.Id)
              OR EXISTS (SELECT 1 FROM AP.OccurrenceAttachment a WHERE a.OccurrenceId = o.Id AND a.DeletedAt IS NULL)
          );
        """,
        new { PayableId = payableId, Rfc = rfc },
        tx,
        cancellationToken: ct));
  }

  private static async Task<RecurrentApVendorOptionDto?> LoadVendorSnapshotAsync(DbConnection conn, DbTransaction tx, int businessPartnerId, CancellationToken ct)
  {
    const string sql =
      """
      SELECT Id, PartnerName AS Name, Rfc
      FROM dbo.BusinessPartner
      WHERE Id = @BusinessPartnerId
        AND IsActive = 1;
      """;

    return await conn.QueryFirstOrDefaultAsync<RecurrentApVendorOptionDto>(
      new CommandDefinition(sql, new { BusinessPartnerId = businessPartnerId }, tx, cancellationToken: ct));
  }

  private static async Task<OccurrenceStateRow?> LoadOccurrenceStateAsync(DbConnection conn, DbTransaction tx, int occurrenceId, string rfc, CancellationToken ct)
  {
    const string sql =
      """
      SELECT Id, Rfc, ExpectedAmount, [Status]
      FROM AP.PayableOccurrence
      WHERE Id = @OccurrenceId
        AND Rfc = @Rfc;
      """;

    return await conn.QueryFirstOrDefaultAsync<OccurrenceStateRow>(
      new CommandDefinition(sql, new { OccurrenceId = occurrenceId, Rfc = rfc }, tx, cancellationToken: ct));
  }

  private static async Task<TransactionStateRow?> LoadTransactionStateAsync(DbConnection conn, DbTransaction tx, int transaccionId, CancellationToken ct)
  {
    const string sql =
      """
      SELECT ID AS Id, RFC AS Rfc, Fecha, CAST(Monto AS decimal(18,2)) AS Amount
      FROM dbo.Transacciones
      WHERE ID = @TransaccionId;
      """;

    return await conn.QueryFirstOrDefaultAsync<TransactionStateRow>(
      new CommandDefinition(sql, new { TransaccionId = transaccionId }, tx, cancellationToken: ct));
  }

  private static async Task<PaymentSummaryRow> LoadPaymentSummaryAsync(DbConnection conn, DbTransaction tx, int occurrenceId, string rfc, CancellationToken ct)
  {
    const string sql =
      """
      SELECT
          COUNT(*) AS PaymentCount,
          CAST(ISNULL(SUM(Amount), 0) AS decimal(18,2)) AS TotalAmount,
          MAX(PaymentDate) AS PaymentDate
      FROM AP.OccurrencePayment
      WHERE OccurrenceId = @OccurrenceId
        AND Rfc = @Rfc;
      """;

    return await conn.QuerySingleAsync<PaymentSummaryRow>(
      new CommandDefinition(sql, new { OccurrenceId = occurrenceId, Rfc = rfc }, tx, cancellationToken: ct));
  }

  private static async Task RecalculateOccurrenceFromPaymentsAsync(
    DbConnection conn,
    DbTransaction tx,
    int occurrenceId,
    string rfc,
    decimal? expectedAmount,
    string actor,
    CancellationToken ct)
  {
    var total = await conn.ExecuteScalarAsync<decimal>(
      new CommandDefinition(
        """
        SELECT CAST(ISNULL(SUM(Amount), 0) AS decimal(18,2))
        FROM AP.OccurrencePayment
        WHERE OccurrenceId = @OccurrenceId
          AND Rfc = @Rfc;
        """,
        new { OccurrenceId = occurrenceId, Rfc = rfc },
        tx,
        cancellationToken: ct));

    var paymentDate = await conn.ExecuteScalarAsync<DateTime?>(
      new CommandDefinition(
        """
        SELECT MAX(PaymentDate)
        FROM AP.OccurrencePayment
        WHERE OccurrenceId = @OccurrenceId
          AND Rfc = @Rfc;
        """,
        new { OccurrenceId = occurrenceId, Rfc = rfc },
        tx,
        cancellationToken: ct));

    var status = ResolvePaymentStatus(total, expectedAmount);
    await conn.ExecuteAsync(
      new CommandDefinition(
        """
        UPDATE AP.PayableOccurrence
        SET ActualPaidAmount = @ActualPaidAmount,
            PaymentDate = @PaymentDate,
            [Status] = @Status,
            UpdatedAt = SYSUTCDATETIME(),
            UpdatedBy = @UpdatedBy
        WHERE Id = @OccurrenceId
          AND Rfc = @Rfc;
        """,
        new
        {
          OccurrenceId = occurrenceId,
          Rfc = rfc,
          ActualPaidAmount = total,
          PaymentDate = paymentDate,
          Status = status,
          UpdatedBy = actor
        },
        tx,
        cancellationToken: ct));
  }

  private static string ResolvePaymentStatus(decimal total, decimal? expectedAmount)
  {
    if (total <= 0m)
    {
      return RecurrentApStatuses.Pending;
    }

    if (!expectedAmount.HasValue || expectedAmount.Value <= 0m)
    {
      return RecurrentApStatuses.Paid;
    }

    return total + 0.005m >= expectedAmount.Value
      ? RecurrentApStatuses.Paid
      : RecurrentApStatuses.PartiallyPaid;
  }

  private async Task EnsureOccurrenceBelongsToRfcAsync(DbConnection conn, int occurrenceId, string rfc, CancellationToken ct)
  {
    var exists = await conn.ExecuteScalarAsync<bool>(
      new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM AP.PayableOccurrence
            WHERE Id = @OccurrenceId
              AND Rfc = @Rfc
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { OccurrenceId = occurrenceId, Rfc = rfc },
        cancellationToken: ct));

    if (!exists)
    {
      throw new InvalidOperationException("El vencimiento AP ya no existe o pertenece a otro RFC.");
    }
  }

  private static async Task AddAuditAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    string entityType,
    int entityId,
    string eventName,
    string? detail,
    string actor,
    CancellationToken ct)
  {
    await conn.ExecuteAsync(
      new CommandDefinition(
        """
        INSERT INTO AP.AuditLog (Rfc, EntityType, EntityId, EventName, Detail, CreatedBy)
        VALUES (@Rfc, @EntityType, @EntityId, @EventName, @Detail, @CreatedBy);
        """,
        new
        {
          Rfc = rfc,
          EntityType = entityType,
          EntityId = entityId,
          EventName = eventName,
          Detail = NullIfWhiteSpace(detail),
          CreatedBy = actor
        },
        tx,
        cancellationToken: ct));
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static void ValidatePayable(RecurrentApUpsertRequest request)
  {
    var results = new List<ValidationResult>();
    if (!Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
    {
      throw new InvalidOperationException(results[0].ErrorMessage ?? "La cuenta recurrente no es válida.");
    }

    if (request.EndDate.HasValue && request.EndDate.Value.Date < request.StartDate.Date)
    {
      throw new InvalidOperationException("La fecha final no puede ser anterior a la fecha inicial.");
    }

    _ = NormalizeFrequency(request.FrequencyUnit);
  }

  private static string NormalizeRequiredRfc(string? rfc)
  {
    var normalized = rfc?.Trim();
    if (string.IsNullOrWhiteSpace(normalized))
    {
      throw new InvalidOperationException("Selecciona un RFC antes de continuar.");
    }

    return normalized.ToUpperInvariant();
  }

  private static string NormalizeFrequency(string? frequencyUnit)
  {
    var normalized = frequencyUnit?.Trim();
    var match = RecurrentApFrequencyUnits.All.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
      throw new InvalidOperationException("La frecuencia AP no es válida.");
    }

    return match;
  }

  private static string NormalizeStatus(string? status)
  {
    var normalized = status?.Trim();
    var match = RecurrentApStatuses.All.FirstOrDefault(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
      throw new InvalidOperationException("El estatus AP no es válido.");
    }

    return match;
  }

  private static string NormalizeCurrency(string? currency)
  {
    var normalized = currency?.Trim().ToUpperInvariant();
    return string.IsNullOrWhiteSpace(normalized) ? "MXN" : normalized[..Math.Min(normalized.Length, 3)].PadRight(3, 'X');
  }

  private static string NormalizeActor(string? actor)
    => string.IsNullOrWhiteSpace(actor) ? "OrionERP" : actor.Trim();

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static RecurrentApPayableSummaryDto MapPayable(RecurringPayableRow row, bool includePassword)
    => new()
    {
      Id = row.Id,
      Rfc = row.Rfc,
      Name = row.Name,
      BusinessPartnerId = row.BusinessPartnerId,
      PayeeNameSnapshot = row.PayeeNameSnapshot,
      PayeeRfcSnapshot = row.PayeeRfcSnapshot,
      Category = row.Category,
      Description = row.Description,
      Website = row.Website,
      UserName = row.UserName,
      Password = includePassword ? RecurrentApCredentialProtector.UnprotectUtf8OrNull(row.PasswordEnc) : null,
      FrequencyUnit = row.FrequencyUnit,
      IntervalCount = row.IntervalCount,
      StartDate = row.StartDate,
      EndDate = row.EndDate,
      DueDayOfMonth = row.DueDayOfMonth,
      DueMonth = row.DueMonth,
      ExpectedAmount = row.ExpectedAmount,
      Currency = row.Currency,
      IsActive = row.IsActive
    };

  private static RecurrentApOccurrenceDetailDto MapOccurrenceDetail(OccurrenceDetailRow row, bool includePassword)
    => new()
    {
      Id = row.Id,
      RecurringPayableId = row.RecurringPayableId,
      Rfc = row.Rfc,
      PayableName = row.PayableName,
      PayeeName = row.PayeeName,
      PayeeRfc = row.PayeeRfc,
      Category = row.Category,
      Description = row.Description,
      Website = row.Website,
      UserName = row.UserName,
      Password = includePassword ? RecurrentApCredentialProtector.UnprotectUtf8OrNull(row.PasswordEnc) : null,
      FrequencyUnit = row.FrequencyUnit,
      IntervalCount = row.IntervalCount,
      StartDate = row.StartDate,
      EndDate = row.EndDate,
      DueDayOfMonth = row.DueDayOfMonth,
      DueMonth = row.DueMonth,
      IsActive = row.IsActive,
      PeriodStartDate = row.PeriodStartDate,
      DueDate = row.DueDate,
      ExpectedAmount = row.ExpectedAmount,
      ActualPaidAmount = row.ActualPaidAmount,
      Status = row.Status,
      PaymentDate = row.PaymentDate,
      Notes = row.Notes
    };

  private sealed class RecurringPayableRow
  {
    public int Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int? BusinessPartnerId { get; set; }
    public string? PayeeNameSnapshot { get; set; }
    public string? PayeeRfcSnapshot { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public string? UserName { get; set; }
    public byte[]? PasswordEnc { get; set; }
    public string FrequencyUnit { get; set; } = RecurrentApFrequencyUnits.Months;
    public int IntervalCount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? DueDayOfMonth { get; set; }
    public int? DueMonth { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public string Currency { get; set; } = "MXN";
    public bool IsActive { get; set; }
  }

  private sealed class OccurrenceDetailRow
  {
    public int Id { get; set; }
    public int RecurringPayableId { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public string PayableName { get; set; } = string.Empty;
    public string? PayeeName { get; set; }
    public string? PayeeRfc { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public string? UserName { get; set; }
    public byte[]? PasswordEnc { get; set; }
    public string FrequencyUnit { get; set; } = RecurrentApFrequencyUnits.Months;
    public int IntervalCount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? DueDayOfMonth { get; set; }
    public int? DueMonth { get; set; }
    public bool IsActive { get; set; }
    public DateTime PeriodStartDate { get; set; }
    public DateTime DueDate { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public decimal ActualPaidAmount { get; set; }
    public string Status { get; set; } = RecurrentApStatuses.Pending;
    public DateTime? PaymentDate { get; set; }
    public string? Notes { get; set; }
  }

  private sealed class OccurrenceStateRow
  {
    public int Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public decimal? ExpectedAmount { get; set; }
    public string Status { get; set; } = string.Empty;
  }

  private sealed class TransactionStateRow
  {
    public int Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public DateTime Fecha { get; set; }
    public decimal Amount { get; set; }
  }

  private sealed class PaymentStateRow
  {
    public int Id { get; set; }
    public int OccurrenceId { get; set; }
    public decimal? ExpectedAmount { get; set; }
  }

  private sealed class PaymentSummaryRow
  {
    public int PaymentCount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime? PaymentDate { get; set; }
  }
}

internal static class RecurrentApCredentialProtector
{
  private const string EncryptionKeyFileName = "rfc-register.aes.key";
  private const int NonceSize = 12;
  private const int TagSize = 16;
  private static readonly Lazy<byte[]> EncryptionKey = new(LoadKey, isThreadSafe: true);

  public static byte[]? ProtectUtf8OrNull(string? plaintext)
  {
    if (string.IsNullOrEmpty(plaintext))
    {
      return null;
    }

    var bytes = Encoding.UTF8.GetBytes(plaintext);
    var nonce = RandomNumberGenerator.GetBytes(NonceSize);
    var ciphertext = new byte[bytes.Length];
    var tag = new byte[TagSize];

    using var aesGcm = new AesGcm(EncryptionKey.Value, TagSize);
    aesGcm.Encrypt(nonce, bytes, ciphertext, tag);

    var payload = new byte[NonceSize + TagSize + ciphertext.Length];
    Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
    Buffer.BlockCopy(tag, 0, payload, NonceSize, TagSize);
    Buffer.BlockCopy(ciphertext, 0, payload, NonceSize + TagSize, ciphertext.Length);
    return payload;
  }

  public static string? UnprotectUtf8OrNull(byte[]? ciphertext)
  {
    if (ciphertext is not { Length: > NonceSize + TagSize })
    {
      return null;
    }

    try
    {
      var data = ciphertext.AsSpan();
      var nonce = data[..NonceSize];
      var tag = data.Slice(NonceSize, TagSize);
      var encryptedData = data[(NonceSize + TagSize)..];
      var plaintext = new byte[encryptedData.Length];

      using var aesGcm = new AesGcm(EncryptionKey.Value, TagSize);
      aesGcm.Decrypt(nonce, encryptedData, tag, plaintext);
      return Encoding.UTF8.GetString(plaintext);
    }
    catch (CryptographicException)
    {
      return null;
    }
  }

  private static byte[] LoadKey()
  {
    foreach (var keyPath in GetCandidateKeyPaths())
    {
      if (!File.Exists(keyPath))
      {
        continue;
      }

      var key = File.ReadAllBytes(keyPath);
      if (key.Length != 32)
      {
        throw new InvalidDataException($"Encryption key must be 32 bytes. Found {key.Length} bytes at '{keyPath}'.");
      }

      return key;
    }

    throw new FileNotFoundException(
      $"Encryption key '{EncryptionKeyFileName}' was not found in App_Data or the repository Web App_Data folder.");
  }

  private static IEnumerable<string> GetCandidateKeyPaths()
  {
    yield return Path.Combine(AppContext.BaseDirectory, "App_Data", EncryptionKeyFileName);

    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
      yield return Path.Combine(directory.FullName, "src", "OrionERP.Web", "App_Data", EncryptionKeyFileName);
      yield return Path.Combine(directory.FullName, "App_Data", EncryptionKeyFileName);
      directory = directory.Parent;
    }
  }
}
