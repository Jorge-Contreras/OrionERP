using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantQuickPinService : IRestaurantQuickPinService
{
  private const int HashIterations = 120_000;
  private const int MaxFailedAttempts = 5;
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantQuickPinService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<RestaurantCommandResult> SetOwnPinAsync(
    RestaurantQuickPinSetupRequest request,
    string userId,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    ValidatePin(request.Pin);
    if (string.IsNullOrWhiteSpace(userId))
    {
      return RestaurantCommandResult.Fail("No se pudo identificar al usuario autenticado.");
    }

    var salt = RandomNumberGenerator.GetBytes(32);
    var hash = HashPin(request.Pin, salt);
    using var conn = CreateConnection();
    const string sql =
      """
      IF NOT EXISTS
      (
        SELECT 1
        FROM restaurante.CashRegister registerInfo
        WHERE registerInfo.Rfc=@Rfc AND registerInfo.SiteId=@SiteId
          AND registerInfo.Id=@CashRegisterId AND registerInfo.IsActive=1
      )
        THROW 51050, 'La caja no pertenece a la sede y RFC seleccionados.', 1;

      IF NOT EXISTS
      (
        SELECT 1
        FROM auth.AspNetUsers userInfo
        WHERE userInfo.Id=@UserId
          AND EXISTS
          (
            SELECT 1 FROM auth.AspNetUserClaims claimInfo
            WHERE claimInfo.UserId=userInfo.Id AND claimInfo.ClaimType='rfc' AND UPPER(claimInfo.ClaimValue)=@Rfc
          )
          AND EXISTS
          (
            SELECT 1
            FROM auth.AspNetUserRoles userRole
            JOIN auth.AspNetRoles roleInfo ON roleInfo.Id=userRole.RoleId
            WHERE userRole.UserId=userInfo.Id
              AND roleInfo.NormalizedName IN ('ADMINISTRADOR','RESTAURANTEADMIN','RESTAURANTESUPERVISOR','RESTAURANTECAJA')
          )
      )
        THROW 51051, 'El usuario no tiene acceso operativo a este RFC.', 1;

      MERGE restaurante.QuickPin WITH (HOLDLOCK) AS target
      USING (SELECT @Rfc AS Rfc,@CashRegisterId AS CashRegisterId,@UserId AS UserId) AS source
        ON target.Rfc=source.Rfc AND target.CashRegisterId=source.CashRegisterId AND target.UserId=source.UserId
      WHEN MATCHED THEN UPDATE SET PinHash=@Hash,PinSalt=@Salt,FailedAttempts=0,LockedUntil=NULL,UpdatedAt=SYSUTCDATETIME()
      WHEN NOT MATCHED THEN INSERT (Rfc,CashRegisterId,UserId,PinHash,PinSalt)
        VALUES (@Rfc,@CashRegisterId,@UserId,@Hash,@Salt);

      INSERT INTO restaurante.SupervisorAuthorization
        (Rfc,SiteId,ActionType,AggregateId,Reason,RequestedBy,AuthorizedBy)
      VALUES
        (@Rfc,@SiteId,'QuickPinConfigured',CONVERT(varchar(20),@CashRegisterId),'Configuración de PIN rápido en caja registrada.',@UserName,@UserName);
      """;
    await conn.ExecuteAsync(new CommandDefinition(sql, new
    {
      Rfc = rfc,
      request.SiteId,
      request.CashRegisterId,
      UserId = userId,
      UserName = userName,
      Hash = hash,
      Salt = salt
    }, cancellationToken: ct));
    return RestaurantCommandResult.Ok("El PIN rápido quedó configurado para esta caja.");
  }

  public async Task<RestaurantQuickPinResult> VerifySupervisorPinAsync(
    RestaurantQuickPinVerifyRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    ValidatePin(request.Pin);
    var normalizedIdentity = request.UserNameOrEmail.Trim().ToUpperInvariant();

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var row = await conn.QuerySingleOrDefaultAsync<QuickPinRow>(new CommandDefinition(
        """
        SELECT pinInfo.UserId,userInfo.UserName,pinInfo.PinHash,pinInfo.PinSalt,
               pinInfo.FailedAttempts,pinInfo.LockedUntil
        FROM restaurante.CashRegister registerInfo WITH (UPDLOCK,HOLDLOCK)
        JOIN restaurante.QuickPin pinInfo WITH (UPDLOCK,HOLDLOCK)
          ON pinInfo.Rfc=registerInfo.Rfc AND pinInfo.CashRegisterId=registerInfo.Id
        JOIN auth.AspNetUsers userInfo ON userInfo.Id=pinInfo.UserId
        WHERE registerInfo.Rfc=@Rfc AND registerInfo.Id=@CashRegisterId AND registerInfo.IsActive=1
          AND (userInfo.NormalizedUserName=@Identity OR userInfo.NormalizedEmail=@Identity)
          AND EXISTS
          (
            SELECT 1 FROM auth.AspNetUserClaims claimInfo
            WHERE claimInfo.UserId=userInfo.Id AND claimInfo.ClaimType='rfc' AND UPPER(claimInfo.ClaimValue)=@Rfc
          )
          AND EXISTS
          (
            SELECT 1
            FROM auth.AspNetUserRoles userRole
            JOIN auth.AspNetRoles roleInfo ON roleInfo.Id=userRole.RoleId
            WHERE userRole.UserId=userInfo.Id
              AND roleInfo.NormalizedName IN ('ADMINISTRADOR','RESTAURANTEADMIN','RESTAURANTESUPERVISOR')
          );
        """, new { Rfc = rfc, request.CashRegisterId, Identity = normalizedIdentity }, tx, cancellationToken: ct));

      if (row is null)
      {
        await AuditAttemptAsync(conn, tx, rfc, request.CashRegisterId, null, false, "InvalidCredentials", ct);
        await tx.CommitAsync(ct);
        return Failure();
      }
      if (row.LockedUntil.HasValue && row.LockedUntil.Value > DateTime.UtcNow)
      {
        await AuditAttemptAsync(conn, tx, rfc, request.CashRegisterId, row.UserId, false, "Locked", ct);
        await tx.CommitAsync(ct);
        return new RestaurantQuickPinResult { Message = "El PIN está bloqueado temporalmente. Intente más tarde." };
      }

      var suppliedHash = HashPin(request.Pin, row.PinSalt);
      if (!CryptographicOperations.FixedTimeEquals(suppliedHash, row.PinHash))
      {
        var attempts = row.FailedAttempts + 1;
        await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE restaurante.QuickPin
          SET FailedAttempts=@Attempts,
              LockedUntil=CASE WHEN @Attempts>=@MaxAttempts THEN DATEADD(minute,5,SYSUTCDATETIME()) ELSE NULL END,
              UpdatedAt=SYSUTCDATETIME()
          WHERE Rfc=@Rfc AND CashRegisterId=@CashRegisterId AND UserId=@UserId;
          """, new { Rfc = rfc, request.CashRegisterId, row.UserId, Attempts = attempts, MaxAttempts = MaxFailedAttempts }, tx, cancellationToken: ct));
        await AuditAttemptAsync(conn, tx, rfc, request.CashRegisterId, row.UserId, false, "InvalidPin", ct);
        await tx.CommitAsync(ct);
        return Failure();
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.QuickPin SET FailedAttempts=0,LockedUntil=NULL,UpdatedAt=SYSUTCDATETIME()
        WHERE Rfc=@Rfc AND CashRegisterId=@CashRegisterId AND UserId=@UserId;
        """, new { Rfc = rfc, request.CashRegisterId, row.UserId }, tx, cancellationToken: ct));
      await AuditAttemptAsync(conn, tx, rfc, request.CashRegisterId, row.UserId, true, null, ct);
      await tx.CommitAsync(ct);
      return new RestaurantQuickPinResult
      {
        Success = true,
        Message = "Autorización de supervisor confirmada.",
        UserId = row.UserId,
        UserName = row.UserName
      };
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static async Task AuditAttemptAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    int cashRegisterId,
    string? userId,
    bool succeeded,
    string? failureReason,
    CancellationToken ct)
  {
    await conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO restaurante.QuickPinAttempt (Rfc,CashRegisterId,UserId,Succeeded,FailureReason)
      SELECT @Rfc,registerInfo.Id,@UserId,@Succeeded,@FailureReason
      FROM restaurante.CashRegister registerInfo
      WHERE registerInfo.Rfc=@Rfc AND registerInfo.Id=@CashRegisterId;
      """, new { Rfc = rfc, CashRegisterId = cashRegisterId, UserId = userId, Succeeded = succeeded, FailureReason = failureReason }, tx, cancellationToken: ct));
  }

  private static RestaurantQuickPinResult Failure()
    => new() { Message = "Usuario, caja o PIN no válidos." };

  private static void ValidatePin(string pin)
  {
    if (string.IsNullOrWhiteSpace(pin) || pin.Length is < 4 or > 8 || pin.Any(character => !char.IsAsciiDigit(character)))
    {
      throw new InvalidOperationException("El PIN debe contener entre 4 y 8 dígitos.");
    }
  }

  internal static byte[] HashPin(string pin, byte[] salt)
    => Rfc2898DeriveBytes.Pbkdf2(pin, salt, HashIterations, HashAlgorithmName.SHA256, 64);

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed class QuickPinRow
  {
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public byte[] PinHash { get; set; } = [];
    public byte[] PinSalt { get; set; } = [];
    public int FailedAttempts { get; set; }
    public DateTime? LockedUntil { get; set; }
  }
}
