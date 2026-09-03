/*
  Capital Humano - seguridad a nivel de fila del esquema rh.

  Ejecutar con SQLCMD variables:
    ExpectedDatabase = Orion_Sandbox | grupocarpio
    ApplyChanges     = 0 | 1

  El modo ApplyChanges=0 ejecuta todas las validaciones y revierte la transaccion.
  Idempotente: la funcion y la politica solo se crean si no existen.

  Que cubre y que no
  ------------------
  Las 22 tablas de rh que llevan columna Rfc quedan filtradas y bloqueadas por
  empresa, salvo rh.KioskDevice. Las 8 tablas restantes no tienen columna Rfc:
  cuelgan de un padre que si la tiene, y ese padre es el que queda protegido.

  Por que rh.KioskDevice se queda fuera
  ------------------------------------
  El kiosco es anonimo por diseno: la tableta no inicia sesion. Al vincularse y al
  registrar, el dispositivo se localiza por el hash SHA-256 de su token, es decir
  que el secreto es lo que da acceso, no la empresa. En ese momento todavia no se
  sabe que RFC corresponde, asi que si la tabla estuviera en la politica la
  consulta devolveria cero filas y el kiosco no funcionaria nunca en produccion.
  Una vez resuelto el dispositivo, KioskAttendanceService fija el RFC en la
  conexion (WorkforceServiceBase.PinRfcScopeAsync) y de ahi en adelante todo lo
  demas -credenciales, eventos y bitacora- si pasa por la politica.
  rh.KioskPairingCode tampoco aparece porque no tiene columna Rfc.

  Alcance real de esta proteccion
  -------------------------------
  La aplicacion fija SESSION_CONTEXT con @read_only=0, asi que el propio codigo
  puede reescribirlo. Esto es defensa en profundidad contra una consulta que
  olvide filtrar por Rfc, no una frontera dura contra codigo malicioso dentro del
  servidor. La verificacion de RFC y de alcance de equipo sigue viviendo tambien
  en WorkforceServiceBase.

  Para revertir:
    DROP SECURITY POLICY rh.RfcSecurityPolicy;
    DROP FUNCTION rh.fn_RfcAccessPredicate;
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');

IF @ApplyChanges IS NULL
  THROW 51000, 'ApplyChanges debe ser 0 o 1.', 1;

IF DB_NAME() <> @ExpectedDatabase
  THROW 51001, 'La base conectada no coincide con ExpectedDatabase.', 1;

IF SCHEMA_ID('rh') IS NULL
  THROW 51003, 'El esquema rh no existe. Aplica primero 20260805_workforce_attendance_mvp.sql.', 1;

BEGIN TRANSACTION;

-- CREATE FUNCTION y CREATE SECURITY POLICY tienen que ser el primer enunciado de
-- su lote; EXEC abre un lote anidado y deja que ambos vivan dentro de esta
-- transaccion, que es lo que permite revisar el script sin dejar rastro.
IF OBJECT_ID('rh.fn_RfcAccessPredicate', 'IF') IS NULL
  EXEC
  (
    'CREATE FUNCTION rh.fn_RfcAccessPredicate(@Rfc varchar(50))
     RETURNS TABLE
     WITH SCHEMABINDING
     AS
     RETURN SELECT 1 AS IsAllowed
     WHERE SESSION_CONTEXT(N''OrionRfc'') IS NULL
        OR @Rfc = CONVERT(varchar(50), SESSION_CONTEXT(N''OrionRfc''));'
  );

IF NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE [name] = 'RfcSecurityPolicy' AND schema_id = SCHEMA_ID('rh'))
  EXEC
  (
    'CREATE SECURITY POLICY rh.RfcSecurityPolicy
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceCorrectionRequest,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceCorrectionRequest AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceCorrectionRequest AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceDay,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceDay AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceDay AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceException,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceException AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendanceException AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendancePolicy,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendancePolicy AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AttendancePolicy AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AuditEvent,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AuditEvent AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.AuditEvent AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeKioskCredential,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeKioskCredential AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeKioskCredential AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeWorkAssignment,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeWorkAssignment AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.EmployeeWorkAssignment AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.Holiday,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.Holiday AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.Holiday AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveBalanceLedger,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveBalanceLedger AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveBalanceLedger AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveEnrollment,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveEnrollment AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveEnrollment AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeavePolicy,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeavePolicy AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeavePolicy AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveRequest,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveRequest AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveRequest AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveType,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveType AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.LeaveType AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.OvertimeDecision,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.OvertimeDecision AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.OvertimeDecision AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.OvertimePolicy,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.OvertimePolicy AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.OvertimePolicy AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PayGroup,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PayGroup AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PayGroup AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PrenominaPeriod,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PrenominaPeriod AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PrenominaPeriod AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PrivacyNotice,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PrivacyNotice AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.PrivacyNotice AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.ScheduleTemplate,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.ScheduleTemplate AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.ScheduleTemplate AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.SupervisorAssignment,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.SupervisorAssignment AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.SupervisorAssignment AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.TimeEvent,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.TimeEvent AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.TimeEvent AFTER UPDATE,
       ADD FILTER PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.WorkSite,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.WorkSite AFTER INSERT,
       ADD BLOCK PREDICATE rh.fn_RfcAccessPredicate(Rfc) ON rh.WorkSite AFTER UPDATE
     WITH (STATE = ON);'
  );

IF OBJECT_ID('rh.fn_RfcAccessPredicate', 'IF') IS NULL
   OR NOT EXISTS (SELECT 1 FROM sys.security_policies WHERE [name] = 'RfcSecurityPolicy' AND schema_id = SCHEMA_ID('rh'))
  THROW 51002, 'La validacion de la seguridad a nivel de fila de rh no fue satisfactoria.', 1;

IF EXISTS
(
  SELECT 1
  FROM sys.security_predicates predicate
  INNER JOIN sys.security_policies policy ON policy.object_id = predicate.object_id
  WHERE policy.[name] = 'RfcSecurityPolicy'
    AND policy.schema_id = SCHEMA_ID('rh')
    AND predicate.target_object_id = OBJECT_ID('rh.KioskDevice')
)
  THROW 51004, 'rh.KioskDevice no puede entrar en la politica: dejaria al kiosco sin poder vincularse.', 1;

IF @ApplyChanges = 1
BEGIN
  COMMIT TRANSACTION;
  SELECT N'APLICADO' AS Estado, DB_NAME() AS BaseDatos;
END
ELSE
BEGIN
  ROLLBACK TRANSACTION;
  SELECT N'VALIDADO_SIN_CAMBIOS' AS Estado, DB_NAME() AS BaseDatos;
END;
GO
