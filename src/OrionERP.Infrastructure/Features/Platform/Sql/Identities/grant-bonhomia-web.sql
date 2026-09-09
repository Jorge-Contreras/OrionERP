/*
  E2: permisos mínimos para la identidad SQL de OrionERP.Bonhomia.Web.

  SE ENTREGA APAGADO. Este guion no lo corre el migrador y no está en el manifiesto:
  vive aparte a propósito, porque otorga permisos, no cambia esquema. Lo aplicas tú y
  después cambias la cadena de la instancia.

  NO CREA EL LOGIN. Créalo tú con su contraseña, fuera de este archivo: ningún secreto
  entra al repositorio, ni a un recibo, ni a la salida de consola. El guion falla si el
  login no existe todavía.

  La matriz se derivó del código, no de lo que se supone: los servicios que
  OrionERP.Bonhomia.Web registra son HospitalityWebsiteScopeAccessor,
  BonhomiaScopedPublicDataReader, BonhomiaPublicBookingService, el envío de
  confirmación y el PDF de reservación. Sus sentencias son SQL en línea; no invoca
  ningún procedimiento almacenado, así que no se otorga EXECUTE.

  Sin db_owner, sin db_datareader ni db_datawriter, sin DDL, sin permisos de
  administración, y sin un solo objeto de la otra instancia. La identidad del migrador
  es otra y no aparece en la configuración pública.

  RLS sigue mandando por encima de esto: los GRANT dicen a qué tablas llega, y la
  política dice qué filas ve. NO se deshabilita RLS para que algo funcione.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Usuario sysname = N'orion_bonhomia_web';

IF DB_NAME() NOT IN (N'Orion_Sandbox', N'grupocarpio')
  THROW 53000, 'Este guion solo se aplica a Orion_Sandbox o grupocarpio.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @Usuario AND type IN ('S','U'))
  THROW 53001, 'Crea primero el login orion_bonhomia_web con su contrasena, fuera de este archivo.', 1;

DECLARE @Sql nvarchar(max);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @Usuario)
BEGIN
  SET @Sql = N'CREATE USER ' + QUOTENAME(@Usuario) + N' FOR LOGIN ' + QUOTENAME(@Usuario) + N';';
  EXEC sys.sp_executesql @Sql;
END;

/* Ni un rol de base: los permisos son por objeto y nada mas. */
IF EXISTS
(
  SELECT 1 FROM sys.database_role_members rm
  WHERE USER_NAME(rm.member_principal_id) = @Usuario
)
  THROW 53002, 'La identidad publica no debe pertenecer a ningun rol de base.', 1;

DECLARE @Permisos TABLE (Objeto nvarchar(300) NOT NULL, Accion varchar(20) NOT NULL, PRIMARY KEY (Objeto, Accion));

/* Lectura: el recorrido publico de cotizacion, reserva, documentos y readiness. */
INSERT @Permisos (Objeto, Accion) VALUES
  (N'dbo.Clientes','SELECT'), (N'dbo.Experience','SELECT'), (N'dbo.ExperienceAddOn','SELECT'),
  (N'dbo.ExperiencePackage','SELECT'), (N'dbo.ExperienceProvider','SELECT'), (N'dbo.Extra','SELECT'),
  (N'dbo.RESERVATION','SELECT'), (N'dbo.RESERVATION_ATTACHMENT','SELECT'), (N'dbo.RESERVATION_DETAIL','SELECT'),
  (N'dbo.ROOM','SELECT'), (N'dbo.ROOM_CALENDAR','SELECT'), (N'dbo.ReservationAirbnbBreakdown','SELECT'),
  (N'dbo.Reservation_Experience','SELECT'), (N'dbo.Reservation_ExperienceAddOn','SELECT'),
  (N'dbo.Reservation_Extra','SELECT'), (N'dbo.Reservation_Transacciones','SELECT'),
  (N'dbo.Transacciones','SELECT'),
  (N'orion.Company','SELECT'), (N'orion.CompanyModule','SELECT'), (N'orion.Module','SELECT'),
  (N'orion.PublicSite','SELECT'), (N'orion.Site','SELECT'), (N'orion.SiteCapability','SELECT'),
  (N'orion.HospitalitySiteCustomer','SELECT'), (N'orion.SchemaMigration','SELECT');

/* Escritura: exactamente las tablas donde el checkout publico inserta o actualiza. */
INSERT @Permisos (Objeto, Accion) VALUES
  (N'dbo.Clientes','INSERT'), (N'dbo.RESERVATION','INSERT'),
  (N'dbo.Reservation_Experience','INSERT'), (N'dbo.Reservation_ExperienceAddOn','INSERT'),
  (N'dbo.Reservation_Extra','INSERT'), (N'dbo.Reservation_Transacciones','INSERT'),
  (N'dbo.Transacciones','INSERT'), (N'orion.HospitalitySiteCustomer','INSERT'),
  (N'dbo.Clientes','UPDATE'), (N'dbo.ROOM_CALENDAR','UPDATE');

IF EXISTS (SELECT 1 FROM @Permisos WHERE OBJECT_ID(Objeto) IS NULL)
BEGIN
  SELECT Objeto AS ObjetoInexistente FROM @Permisos WHERE OBJECT_ID(Objeto) IS NULL;
  THROW 53003, 'Un objeto de la matriz no existe en esta base; revisa antes de otorgar.', 1;
END;

DECLARE @Objeto nvarchar(300), @Accion varchar(20);
DECLARE permiso_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Objeto, Accion FROM @Permisos ORDER BY Objeto, Accion;
OPEN permiso_cursor;
FETCH NEXT FROM permiso_cursor INTO @Objeto, @Accion;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Sql = N'GRANT ' + @Accion + N' ON ' + QUOTENAME(PARSENAME(@Objeto, 2)) + N'.' + QUOTENAME(PARSENAME(@Objeto, 1))
           + N' TO ' + QUOTENAME(@Usuario) + N';';
  EXEC sys.sp_executesql @Sql;
  FETCH NEXT FROM permiso_cursor INTO @Objeto, @Accion;
END;
CLOSE permiso_cursor;
DEALLOCATE permiso_cursor;

/* Y ni un objeto de la otra instancia, ni de lo administrativo. El DENY es explicito
   para que nadie lo conceda mas tarde por descuido. */
DECLARE @Vedados TABLE (Esquema sysname NOT NULL PRIMARY KEY);
INSERT @Vedados VALUES
  (N'brunos_auth'), (N'fidelidad'), (N'restaurante'), (N'auth'),
  (N'rh'), (N'contabilidad'), (N'bancos'), (N'fiscal'), (N'reporteFinanciero'), (N'cfdi'), (N'logistica');
DECLARE @Esquema sysname;
DECLARE veto_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT Esquema FROM @Vedados WHERE SCHEMA_ID(Esquema) IS NOT NULL ORDER BY Esquema;
OPEN veto_cursor;
FETCH NEXT FROM veto_cursor INTO @Esquema;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Sql = N'DENY SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::' + QUOTENAME(@Esquema)
           + N' TO ' + QUOTENAME(@Usuario) + N';';
  EXEC sys.sp_executesql @Sql;
  FETCH NEXT FROM veto_cursor INTO @Esquema;
END;
CLOSE veto_cursor;
DEALLOCATE veto_cursor;

PRINT 'Lo que quedo otorgado, para revisarlo antes de cambiar la cadena:';
SELECT
  pe.state_desc AS Estado, pe.permission_name AS Permiso,
  CASE pe.class WHEN 1 THEN OBJECT_SCHEMA_NAME(pe.major_id) + '.' + OBJECT_NAME(pe.major_id)
                WHEN 3 THEN 'SCHEMA::' + SCHEMA_NAME(pe.major_id) ELSE CONVERT(varchar(20), pe.class_desc) END AS Objeto
FROM sys.database_permissions pe
WHERE pe.grantee_principal_id = DATABASE_PRINCIPAL_ID(@Usuario)
ORDER BY Estado DESC, Objeto, Permiso;

SELECT CONVERT(int, COUNT(*)) AS RolesDeBase
FROM sys.database_role_members WHERE USER_NAME(member_principal_id) = @Usuario;
