using System.Data;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.Infrastructure.Features.CapitalHumano.Workforce;

public abstract class WorkforceServiceBase
{
  protected readonly IDbConnectionFactory ConnectionFactory;
  private readonly ICurrentEmployeeAccessor _currentEmployeeAccessor;

  protected WorkforceServiceBase(
    IDbConnectionFactory connectionFactory,
    ICurrentEmployeeAccessor currentEmployeeAccessor)
  {
    ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    _currentEmployeeAccessor = currentEmployeeAccessor ?? throw new ArgumentNullException(nameof(currentEmployeeAccessor));
  }

  protected async Task<CurrentEmployeeContext> RequireActorAsync(
    string rfc,
    bool requireEmployee,
    CancellationToken ct,
    params string[] roles)
  {
    var normalizedRfc = NormalizeRfc(rfc);
    var actor = await _currentEmployeeAccessor.GetCurrentAsync(ct)
      ?? throw new UnauthorizedAccessException("La sesion no esta autenticada.");

    if (!actor.CanAccessRfc(normalizedRfc))
    {
      throw new UnauthorizedAccessException("El usuario no tiene acceso al RFC seleccionado.");
    }

    if (requireEmployee && !actor.EmployeeId.HasValue)
    {
      throw new UnauthorizedAccessException("El usuario no esta ligado a un empleado de Capital Humano.");
    }

    if (requireEmployee)
    {
      using var connection = CreateOpenConnection();
      var employeeMatchesRfc = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(1) FROM dbo.Capital_Humano WHERE ID=@EmployeeId AND RFC=@Rfc;",
        new { EmployeeId = actor.EmployeeId!.Value, Rfc = normalizedRfc }, cancellationToken: ct));
      if (employeeMatchesRfc == 0)
        throw new UnauthorizedAccessException("El empleado ligado al usuario no pertenece al RFC seleccionado.");
    }

    if (roles.Length > 0 && !actor.IsInRole("Administrador") && !actor.IsInRole(roles))
    {
      throw new UnauthorizedAccessException("El usuario no tiene permisos para esta operacion.");
    }

    return actor;
  }

  protected static string NormalizeRfc(string? rfc)
  {
    var value = rfc?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(value) || value.Length > 50)
    {
      throw new ArgumentException("El RFC seleccionado no es valido.", nameof(rfc));
    }

    return value;
  }

  protected static string NormalizeActor(string? actor)
    => string.IsNullOrWhiteSpace(actor) ? "OrionERP" : actor.Trim()[..Math.Min(actor.Trim().Length, 256)];

  protected IDbConnection CreateOpenConnection()
  {
    var connection = ConnectionFactory.Create();
    if (connection.State != ConnectionState.Open)
    {
      connection.Open();
    }
    return connection;
  }

  /// <summary>
  /// Fija el RFC de la conexion para la seguridad a nivel de fila del esquema rh.
  ///
  /// La fabrica de conexiones escribe la literal '__UNSCOPED__' en
  /// SESSION_CONTEXT('OrionRfc') cuando no hay RFC de sesion, y el predicado de rh
  /// solo deja pasar la fila cuando ese contexto es NULL o coincide con su Rfc. El
  /// kiosco es anonimo por diseno: la tableta no inicia sesion. Sin este ajuste el
  /// registro veria cero filas y el evento quedaria bloqueado justo en produccion,
  /// aunque todo funcione con una sesion iniciada.
  ///
  /// El RFC nunca lo elige quien usa el kiosco: sale del dispositivo ya vinculado
  /// o del comando que la propia aplicacion construyo.
  /// </summary>
  public static Task PinRfcScopeAsync(
    IDbConnection connection,
    IDbTransaction? transaction,
    string rfc,
    CancellationToken ct)
    => connection.ExecuteAsync(new CommandDefinition(
      "EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=@Rfc, @read_only=0;",
      new { Rfc = NormalizeRfc(rfc) },
      transaction,
      cancellationToken: ct));

  /// <summary>
  /// Deja la conexion sin RFC para las tareas que recorren todas las empresas.
  ///
  /// La unica que lo necesita es la retencion programada, que anonimiza evidencia
  /// de ubicacion agrupando por Rfc. Corre en un BackgroundService, fuera de toda
  /// peticion HTTP, asi que la fabrica la dejaria en '__UNSCOPED__' y el predicado
  /// la filtraria por completo: purgaria cero filas sin reportar ningun error.
  /// </summary>
  public static Task ClearRfcScopeAsync(
    IDbConnection connection,
    IDbTransaction? transaction,
    CancellationToken ct)
    => connection.ExecuteAsync(new CommandDefinition(
      "EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=NULL, @read_only=0;",
      transaction: transaction,
      cancellationToken: ct));

  public static async Task WriteAuditAsync(
    IDbConnection connection,
    IDbTransaction? transaction,
    string rfc,
    int? employeeId,
    string entityType,
    object entityId,
    string eventType,
    string? detail,
    string actor,
    CancellationToken ct)
  {
    const string sql =
      """
      INSERT INTO rh.AuditEvent
        (Rfc, EmployeeId, EntityType, EntityId, EventType, Detail, CreatedBy)
      VALUES
        (@Rfc, @EmployeeId, @EntityType, @EntityId, @EventType, @Detail, @CreatedBy);
      """;
    await connection.ExecuteAsync(new CommandDefinition(
      sql,
      new
      {
        Rfc = rfc,
        EmployeeId = employeeId,
        EntityType = entityType,
        EntityId = Convert.ToString(entityId, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        EventType = eventType,
        Detail = detail,
        CreatedBy = NormalizeActor(actor)
      },
      transaction,
      cancellationToken: ct));
  }

  protected static async Task<bool> CanManageEmployeeAsync(
    IDbConnection connection,
    IDbTransaction? transaction,
    CurrentEmployeeContext actor,
    string rfc,
    int employeeId,
    DateOnly effectiveDate,
    CancellationToken ct)
  {
    if (actor.IsInRole("Administrador", "CapitalHumanoAdmin", "CapitalHumanoNomina"))
    {
      return true;
    }

    if (!actor.EmployeeId.HasValue || !actor.IsInRole("CapitalHumanoSupervisor"))
    {
      return false;
    }

    const string sql =
      """
      SELECT COUNT(1)
      FROM rh.SupervisorAssignment
      WHERE Rfc = @Rfc
        AND EmployeeId = @EmployeeId
        AND SupervisorEmployeeId = @SupervisorEmployeeId
        AND EffectiveFrom <= @EffectiveDate
        AND (EffectiveTo IS NULL OR EffectiveTo >= @EffectiveDate);
      """;
    return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
      sql,
      new
      {
        Rfc = rfc,
        EmployeeId = employeeId,
        SupervisorEmployeeId = actor.EmployeeId.Value,
        EffectiveDate = effectiveDate
      },
      transaction,
      cancellationToken: ct)) > 0;
  }
}
