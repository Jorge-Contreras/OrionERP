/*
  Impide crear cuentas contables con codigo agrupador duplicado
  (mismo RFC + Nivel1 + Nivel2 + Nivel3).

    sqlcmd ... -v ExpectedDatabase="grupocarpio" -i 20260907_cuentas_codigo_unico.sql

  Requiere que 20260907_cuentas_duplicadas_consolidar.sql ya se haya aplicado y
  que las colisiones restantes (216-01-00: "ISR retenido por sueldos" vs "IVA
  retenido", en los RFC que las tengan) ya se hayan recodificado a mano.

  Mientras exista cualquier duplicado el script NO crea el indice: avisa cuales
  faltan y termina sin error, para poder re-correrlo despues sin editar nada.
  La capa de aplicacion (CatalogoService.SaveCuentaAsync y
  CuentasContablesRepository.CreateNivel3Async) ya valida el duplicado antes de
  insertar; este indice es la red de seguridad para imports masivos y ediciones
  directas a la base.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51100, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51101, 'La base conectada no coincide con ExpectedDatabase.', 1;

IF EXISTS (SELECT name FROM sys.indexes WHERE name = 'UX_CuentasContables_RFC_Clave'
           AND object_id = OBJECT_ID('dbo.CuentasContables'))
BEGIN
  PRINT 'UX_CuentasContables_RFC_Clave ya existe. Nada que hacer.';
  RETURN;
END;

IF EXISTS (
  SELECT 1 FROM dbo.CuentasContables
  GROUP BY RFC, Nivel1, Nivel2, Nivel3
  HAVING COUNT(*) > 1)
BEGIN
  PRINT 'Todavia hay claves duplicadas. No se crea el indice. Pendientes:';
  SELECT RFC, Clave = CONCAT(Nivel1,'-',Nivel2,'-',Nivel3), Filas = COUNT(*)
  FROM dbo.CuentasContables
  GROUP BY RFC, Nivel1, Nivel2, Nivel3
  HAVING COUNT(*) > 1
  ORDER BY RFC, Clave;
  RETURN;
END;

CREATE UNIQUE NONCLUSTERED INDEX UX_CuentasContables_RFC_Clave
  ON dbo.CuentasContables (RFC, Nivel1, Nivel2, Nivel3);
PRINT 'UX_CuentasContables_RFC_Clave creado.';
