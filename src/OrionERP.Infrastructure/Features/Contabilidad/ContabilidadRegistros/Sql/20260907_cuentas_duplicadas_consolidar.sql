/*
  Consolida las cuentas contables con codigo agrupador duplicado
  (mismo RFC + Nivel1 + Nivel2 + Nivel3, distinta Descripcion).

    sqlcmd ... -v ExpectedDatabase="Orion_Sandbox" ApplyChanges="0" -i 20260907_cuentas_duplicadas_consolidar.sql
    sqlcmd ... -v ExpectedDatabase="grupocarpio"   ApplyChanges="1" -i 20260907_cuentas_duplicadas_consolidar.sql

  ApplyChanges=0 corre todo dentro de una transaccion que se revierte al final y
  deja ver los conteos. ApplyChanges=1 confirma.

  Origen del problema: un import historico del catalogo SAT pego dos veces varias
  claves con descripciones distintas. dbo.Registro_Contable cuelga de la clave de
  tres niveles, no del id de la cuenta, asi que los movimientos no se ven
  afectados; reporteFinanciero.Rpt_BalanzaComprobacion resuelve el duplicado con
  MAX(Descripcion) y por eso una cuenta puede leerse con el nombre de otra.

  Regla de consolidacion, por grupo (RFC, Nivel1, Nivel2, Nivel3):
    - "uso" de una fila = movimientos que la referencian por
      Registro_Contable.CuentaContableID, o movimientos de esa clave cuyo
      Nombre_Cuenta coincide con su Descripcion, o referencias en
      PlantillaContableLinea / CfdiPolizaCuentaDefault / bancos.Cuentas_Banco.
    - si 2 o mas filas tienen uso -> COLISION: son dos cuentas distintas metidas
      en la misma clave (p. ej. 216-01-00 = "ISR retenido por sueldos" e "IVA
      retenido"). No se toca; se reporta para recodificar a mano.
    - si exactamente 1 fila tiene uso -> esa es la canonica.
    - si ninguna fila tiene uso -> canonica = la de menor id (en todos los casos
      observados es la que trae el nombre estandar del codigo agrupador SAT).
    - las demas filas del grupo son "perdedoras": se re-apuntan sus referencias
      a la canonica y se eliminan.

  Idempotente: si ya no hay duplicados no consolidables, no hace nada.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
SET NOCOUNT ON;

DECLARE @ExpectedDatabase sysname = N'$(ExpectedDatabase)';
DECLARE @ApplyChanges bit = TRY_CONVERT(bit, N'$(ApplyChanges)');

IF @ExpectedDatabase NOT IN (N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio')
  THROW 51100, 'ExpectedDatabase debe ser Orion_Sandbox o grupocarpio.', 1;
IF DB_NAME() <> @ExpectedDatabase
  THROW 51101, 'La base conectada no coincide con ExpectedDatabase.', 1;
IF @ApplyChanges IS NULL
  THROW 51102, 'ApplyChanges debe ser 0 o 1.', 1;

BEGIN TRANSACTION;

-- 1) Filas de cada grupo duplicado, con su "uso".
;WITH grupos AS (
  SELECT RFC, Nivel1, Nivel2, Nivel3
  FROM dbo.CuentasContables
  GROUP BY RFC, Nivel1, Nivel2, Nivel3
  HAVING COUNT(*) > 1
)
SELECT
  cc.id, cc.RFC, cc.Nivel1, cc.Nivel2, cc.Nivel3, cc.Descripcion,
  Uso =
      (SELECT COUNT(*) FROM dbo.Registro_Contable rc WHERE rc.CuentaContableID = cc.id)
    + (SELECT COUNT(*) FROM dbo.Registro_Contable rc
         JOIN dbo.Transacciones t ON t.ID = rc.TransaccionID
        WHERE t.RFC = cc.RFC AND rc.Nivel1 = cc.Nivel1 AND rc.Nivel2 = cc.Nivel2
          AND rc.Nivel3 = cc.Nivel3 AND rc.Nombre_Cuenta = cc.Descripcion)
    + (SELECT COUNT(*) FROM dbo.PlantillaContableLinea l WHERE l.CuentaContableID = cc.id)
    + (SELECT COUNT(*) FROM dbo.CfdiPolizaCuentaDefault f WHERE f.CuentaContableId = cc.id)
    + (SELECT COUNT(*) FROM bancos.Cuentas_Banco k WHERE k.Cuenta_Contable_ID = cc.id)
INTO #Filas
FROM dbo.CuentasContables cc
JOIN grupos g
  ON g.RFC = cc.RFC AND g.Nivel1 = cc.Nivel1 AND g.Nivel2 = cc.Nivel2 AND g.Nivel3 = cc.Nivel3;

-- 2) Resumen por grupo.
SELECT
  RFC, Nivel1, Nivel2, Nivel3,
  Filas       = COUNT(*),
  FilasConUso = SUM(CASE WHEN Uso > 0 THEN 1 ELSE 0 END),
  IdCanonica  = CASE
                  WHEN SUM(CASE WHEN Uso > 0 THEN 1 ELSE 0 END) = 1
                    THEN MIN(CASE WHEN Uso > 0 THEN id END)
                  ELSE MIN(id)
                END
INTO #Grupos
FROM #Filas
GROUP BY RFC, Nivel1, Nivel2, Nivel3;

-- 3) Colisiones: se reportan y se excluyen.
DECLARE @colisiones int = (SELECT COUNT(*) FROM #Grupos WHERE FilasConUso >= 2);
SELECT g.RFC, Clave = CONCAT(g.Nivel1,'-',g.Nivel2,'-',g.Nivel3),
       Descripciones = STRING_AGG(CONVERT(nvarchar(200), f.Descripcion), '  |  ')
FROM #Grupos g
JOIN #Filas f ON f.RFC = g.RFC AND f.Nivel1 = g.Nivel1 AND f.Nivel2 = g.Nivel2 AND f.Nivel3 = g.Nivel3
WHERE g.FilasConUso >= 2
GROUP BY g.RFC, g.Nivel1, g.Nivel2, g.Nivel3;
PRINT CONCAT('Colisiones no consolidables (se dejan intactas): ', @colisiones);

-- 4) Plan de consolidacion (solo grupos consolidables).
SELECT
  f.id AS LoserId, g.IdCanonica AS CanonId,
  f.RFC, Clave = CONCAT(f.Nivel1,'-',f.Nivel2,'-',f.Nivel3),
  LoserDesc = f.Descripcion,
  CanonDesc = (SELECT Descripcion FROM #Filas c WHERE c.id = g.IdCanonica),
  f.Uso AS LoserUso
INTO #Plan
FROM #Filas f
JOIN #Grupos g ON g.RFC = f.RFC AND g.Nivel1 = f.Nivel1 AND g.Nivel2 = f.Nivel2 AND g.Nivel3 = f.Nivel3
WHERE g.FilasConUso < 2
  AND f.id <> g.IdCanonica;

SELECT PlanFilas = COUNT(*), Grupos = COUNT(DISTINCT CONCAT(RFC,'|',Clave)),
       Rfcs = COUNT(DISTINCT RFC), ConUso = SUM(CASE WHEN LoserUso > 0 THEN 1 ELSE 0 END)
FROM #Plan;
SELECT * FROM #Plan ORDER BY RFC, Clave;

-- 5) Re-apuntar referencias de las perdedoras a la canonica.
DECLARE @rc int, @pl int, @cf int, @bk int;

UPDATE rc SET rc.CuentaContableID = p.CanonId
FROM dbo.Registro_Contable rc JOIN #Plan p ON p.LoserId = rc.CuentaContableID;
SET @rc = @@ROWCOUNT;

UPDATE l SET l.CuentaContableID = p.CanonId
FROM dbo.PlantillaContableLinea l JOIN #Plan p ON p.LoserId = l.CuentaContableID;
SET @pl = @@ROWCOUNT;

UPDATE f SET f.CuentaContableId = p.CanonId
FROM dbo.CfdiPolizaCuentaDefault f JOIN #Plan p ON p.LoserId = f.CuentaContableId;
SET @cf = @@ROWCOUNT;

UPDATE k SET k.Cuenta_Contable_ID = p.CanonId
FROM bancos.Cuentas_Banco k JOIN #Plan p ON p.LoserId = k.Cuenta_Contable_ID;
SET @bk = @@ROWCOUNT;

PRINT CONCAT('Referencias re-apuntadas -> Registro_Contable: ', @rc,
             ' | PlantillaContableLinea: ', @pl,
             ' | CfdiPolizaCuentaDefault: ', @cf,
             ' | Cuentas_Banco: ', @bk);

-- 6) Eliminar las perdedoras.
DECLARE @del int;
DELETE cc FROM dbo.CuentasContables cc JOIN #Plan p ON p.LoserId = cc.id;
SET @del = @@ROWCOUNT;
PRINT CONCAT('Cuentas perdedoras eliminadas: ', @del);

-- 7) Verificacion: solo deben quedar las colisiones.
DECLARE @restantes int = (
  SELECT COUNT(*) FROM (
    SELECT RFC, Nivel1, Nivel2, Nivel3
    FROM dbo.CuentasContables
    GROUP BY RFC, Nivel1, Nivel2, Nivel3
    HAVING COUNT(*) > 1
  ) x);
PRINT CONCAT('Grupos duplicados restantes: ', @restantes, ' (esperado = colisiones = ', @colisiones, ')');
IF @restantes <> @colisiones
  THROW 51103, 'Quedaron duplicados que no son colisiones. Se revierte.', 1;

DROP TABLE #Plan; DROP TABLE #Grupos; DROP TABLE #Filas;

IF @ApplyChanges = 1
BEGIN
  COMMIT TRANSACTION;
  PRINT 'ApplyChanges=1: cambios confirmados.';
END
ELSE
BEGIN
  ROLLBACK TRANSACTION;
  PRINT 'ApplyChanges=0: transaccion revertida (ensayo).';
END
