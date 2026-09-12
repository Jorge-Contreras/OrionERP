/*
  E2: permisos mínimos para la identidad SQL de OrionERP.Bruno.Web.

  SE ENTREGA APAGADO, igual que el de Bonhomía: no lo corre el migrador, no está en el
  manifiesto, y NO crea el login. Créalo tú con su contraseña fuera de este archivo.

  La matriz se derivó del código y corrigió dos suposiciones:
    - La membresía NO vive en `restaurante`, sino en un esquema `fidelidad`
      (MemberAccount, MemberQrToken, PointLedger, MemberClosureRequest, MemberConsent,
      ProgramSettings). Un barrido superficial no lo veía porque el SQL de
      LoyaltyService es multilínea.
    - Identity vive en `brunos_auth`, con siete tablas y un trigger sobre AspNetUsers.

  Bruno lee el catálogo publicado y escribe SOLO su membresía. En particular:
    - `restaurante.PublicSiteSettings` se otorga de LECTURA nada más. El servicio
      compartido que Bruno registra expone un guardado de esa tabla, pero eso es una
      operación de consola y un sitio público no debe reescribir su propia
      presentación. Si alguna pantalla de Bruno lo intenta, fallará con un error de
      permisos visible, que es preferible a concederlo por si acaso.
    - `restaurante.[Order]` se otorga UPDATE porque la acumulación de puntos marca la
      orden. Ni INSERT ni DELETE.

  Sin db_owner, sin db_datareader ni db_datawriter, sin DDL, sin permisos de
  administración, y sin un solo objeto de Hospedaje. No invoca procedimientos
  almacenados, así que no se otorga EXECUTE. NO se deshabilita RLS para que algo
  funcione.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Usuario sysname = N'orion_bruno_web';

IF DB_NAME() NOT IN (N'Orion_Sandbox', N'grupocarpio')
  THROW 53010, 'Este guion solo se aplica a Orion_Sandbox o grupocarpio.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @Usuario AND type IN ('S','U'))
  THROW 53011, 'Crea primero el login orion_bruno_web con su contrasena, fuera de este archivo.', 1;
IF SCHEMA_ID(N'brunos_auth') IS NULL OR SCHEMA_ID(N'fidelidad') IS NULL
  THROW 53012, 'Faltan los esquemas brunos_auth o fidelidad que esta identidad necesita.', 1;

DECLARE @Sql nvarchar(max);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @Usuario)
BEGIN
  SET @Sql = N'CREATE USER ' + QUOTENAME(@Usuario) + N' FOR LOGIN ' + QUOTENAME(@Usuario) + N';';
  EXEC sys.sp_executesql @Sql;
END;

IF EXISTS
(
  SELECT 1 FROM sys.database_role_members rm
  WHERE USER_NAME(rm.member_principal_id) = @Usuario
)
  THROW 53013, 'La identidad publica no debe pertenecer a ningun rol de base.', 1;

DECLARE @Permisos TABLE (Objeto nvarchar(300) NOT NULL, Accion varchar(20) NOT NULL, PRIMARY KEY (Objeto, Accion));

/* Binding y readiness. */
INSERT @Permisos (Objeto, Accion) VALUES
  (N'orion.Company','SELECT'), (N'orion.PublicSite','SELECT'), (N'orion.Site','SELECT'),
  (N'orion.CompanyModule','SELECT'), (N'orion.Module','SELECT'), (N'orion.SiteCapability','SELECT'),
  (N'orion.SchemaMigration','SELECT'), (N'orion.PublicIdentityCompatibilityState','SELECT');

/* Catalogo publicado: lectura. */
INSERT @Permisos (Objeto, Accion) VALUES
  (N'restaurante.Site','SELECT'), (N'restaurante.PublicSiteSettings','SELECT'),
  (N'restaurante.Menu','SELECT'), (N'restaurante.MenuItem','SELECT'), (N'restaurante.MenuSchedule','SELECT'),
  (N'restaurante.MenuSection','SELECT'), (N'restaurante.Product','SELECT'), (N'restaurante.ProductCard','SELECT'),
  (N'restaurante.ProductDietaryTag','SELECT'), (N'restaurante.ProductModifierGroup','SELECT'),
  (N'restaurante.ModifierGroup','SELECT'), (N'restaurante.ModifierOption','SELECT'),
  (N'restaurante.ModifierIngredientDelta','SELECT'),
  (N'restaurante.ComboSlot','SELECT'), (N'restaurante.ComboSlotOption','SELECT'),
  (N'restaurante.ComboSlotOptionRoute','SELECT'),
  (N'restaurante.Promotion','SELECT'), (N'restaurante.PromotionCode','SELECT'),
  (N'restaurante.PromotionProduct','SELECT'), (N'restaurante.PromotionSchedule','SELECT'),
  (N'restaurante.PromotionMaterialCategory','SELECT'), (N'restaurante.PromotionRedemption','SELECT'),
  (N'restaurante.OrderPromotion','SELECT'), (N'restaurante.[Order]','SELECT'),
  (N'restaurante.Payment','SELECT'), (N'restaurante.PaymentRefund','SELECT'),
  (N'logistica.Material','SELECT'), (N'logistica.MaterialAllergen','SELECT'), (N'logistica.Allergen','SELECT'),
  (N'logistica.UnitOfMeasure','SELECT'), (N'logistica.BomHeader','SELECT'),
  (N'logistica.BomVersion','SELECT'), (N'logistica.BomComponent','SELECT');

/* Membresia y fidelidad: aqui si escribe. */
INSERT @Permisos (Objeto, Accion) VALUES
  (N'fidelidad.MemberAccount','SELECT'), (N'fidelidad.MemberAccount','INSERT'), (N'fidelidad.MemberAccount','UPDATE'),
  (N'fidelidad.MemberQrToken','SELECT'), (N'fidelidad.MemberQrToken','INSERT'), (N'fidelidad.MemberQrToken','DELETE'),
  (N'fidelidad.PointLedger','SELECT'), (N'fidelidad.PointLedger','INSERT'),
  (N'fidelidad.MemberClosureRequest','SELECT'), (N'fidelidad.MemberClosureRequest','INSERT'),
  (N'fidelidad.MemberConsent','SELECT'), (N'fidelidad.MemberConsent','INSERT'),
  (N'fidelidad.ProgramSettings','SELECT'), (N'fidelidad.ProgramSettings','UPDATE'),
  (N'restaurante.[Order]','UPDATE');

IF EXISTS (SELECT 1 FROM @Permisos WHERE OBJECT_ID(Objeto) IS NULL)
BEGIN
  SELECT Objeto AS ObjetoInexistente FROM @Permisos WHERE OBJECT_ID(Objeto) IS NULL;
  THROW 53014, 'Un objeto de la matriz no existe en esta base; revisa antes de otorgar.', 1;
END;

DECLARE @Objeto nvarchar(300), @Accion varchar(20);
DECLARE permiso_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT Objeto, Accion FROM @Permisos ORDER BY Objeto, Accion;
OPEN permiso_cursor;
FETCH NEXT FROM permiso_cursor INTO @Objeto, @Accion;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Sql = N'GRANT ' + @Accion + N' ON ' + QUOTENAME(PARSENAME(REPLACE(REPLACE(@Objeto,'[',''),']',''), 2))
           + N'.' + QUOTENAME(PARSENAME(REPLACE(REPLACE(@Objeto,'[',''),']',''), 1))
           + N' TO ' + QUOTENAME(@Usuario) + N';';
  EXEC sys.sp_executesql @Sql;
  FETCH NEXT FROM permiso_cursor INTO @Objeto, @Accion;
END;
CLOSE permiso_cursor;
DEALLOCATE permiso_cursor;

/* Readiness verifies that the scoped Identity policy remains enabled and complete. */
SET @Sql = N'GRANT VIEW DEFINITION ON OBJECT::[orion].[PublicIdentityScopePolicy] TO ' + QUOTENAME(@Usuario) + N';';
EXEC sys.sp_executesql @Sql;

/* Identity, membresia, cookies y recuperacion: CRUD sobre su propio esquema, con sus
   indices, claves foraneas y el trigger de AspNetUsers, que corre con el dueno de la
   tabla y no exige permiso aparte. */
SET @Sql = N'GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[brunos_auth] TO ' + QUOTENAME(@Usuario) + N';';
EXEC sys.sp_executesql @Sql;

/* Nada de Hospedaje, nada administrativo, nada de la otra instancia. */
DECLARE @Vedados TABLE (Esquema sysname NOT NULL PRIMARY KEY);
INSERT @Vedados VALUES
  (N'auth'), (N'rh'), (N'contabilidad'), (N'bancos'), (N'fiscal'), (N'reporteFinanciero'), (N'cfdi');
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

/* Hospedaje vive en dbo junto a tablas compartidas, asi que el veto es por objeto. */
DECLARE @HospedajeVedado TABLE (Objeto nvarchar(300) NOT NULL PRIMARY KEY);
INSERT @HospedajeVedado VALUES
  (N'dbo.RESERVATION'), (N'dbo.RESERVATION_DETAIL'), (N'dbo.RESERVATION_ATTACHMENT'),
  (N'dbo.ROOM'), (N'dbo.ROOM_CALENDAR'), (N'dbo.Extra'), (N'dbo.Reservation_Extra'),
  (N'dbo.Reservation_Transacciones'), (N'dbo.Transacciones'), (N'dbo.Clientes'),
  (N'orion.HospitalitySiteCustomer');
DECLARE veto_objeto_cursor CURSOR LOCAL FAST_FORWARD FOR
  SELECT Objeto FROM @HospedajeVedado WHERE OBJECT_ID(Objeto) IS NOT NULL ORDER BY Objeto;
OPEN veto_objeto_cursor;
FETCH NEXT FROM veto_objeto_cursor INTO @Objeto;
WHILE @@FETCH_STATUS = 0
BEGIN
  SET @Sql = N'DENY SELECT, INSERT, UPDATE, DELETE ON ' + QUOTENAME(PARSENAME(@Objeto, 2)) + N'.'
           + QUOTENAME(PARSENAME(@Objeto, 1)) + N' TO ' + QUOTENAME(@Usuario) + N';';
  EXEC sys.sp_executesql @Sql;
  FETCH NEXT FROM veto_objeto_cursor INTO @Objeto;
END;
CLOSE veto_objeto_cursor;
DEALLOCATE veto_objeto_cursor;

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
