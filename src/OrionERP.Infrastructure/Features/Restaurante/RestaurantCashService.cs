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
      SELECT shiftInfo.Id, shiftInfo.CashRegisterId, registerInfo.[Name] AS RegisterName, shiftInfo.[Status],
             shiftInfo.OpeningFloat, shiftInfo.OpenedAt, shiftInfo.ExpectedCash, shiftInfo.CountedCash, shiftInfo.Difference
      FROM restaurante.CashShift shiftInfo
      JOIN restaurante.CashRegister registerInfo ON registerInfo.Rfc=shiftInfo.Rfc AND registerInfo.Id=shiftInfo.CashRegisterId
      WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.SiteId=@SiteId
      ORDER BY CASE shiftInfo.[Status] WHEN 'Open' THEN 0 WHEN 'PendingApproval' THEN 1 ELSE 2 END, shiftInfo.OpenedAt DESC;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RestaurantCashShiftDto>(new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), SiteId = siteId }, cancellationToken: ct))).AsList();
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

  private sealed class ShiftIdentityRow
  {
    public Guid Id { get; set; }
    public int SiteId { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal OpeningFloat { get; set; }
  }
}
