/*
  Nuevos pasos de ordenes de trabajo y plantillas permiten foto por defecto.
  No se modifican filas existentes.
*/
DECLARE @ConstraintName sysname;
DECLARE @DropConstraintSql nvarchar(max);

SELECT @ConstraintName = defaultConstraint.name
FROM sys.default_constraints AS defaultConstraint
JOIN sys.columns AS columnDefinition
  ON columnDefinition.object_id = defaultConstraint.parent_object_id
 AND columnDefinition.column_id = defaultConstraint.parent_column_id
WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.OrdenTrabajoPaso')
  AND columnDefinition.name = N'PoliticaFoto';

IF @ConstraintName IS NOT NULL
BEGIN
    SET @DropConstraintSql = N'ALTER TABLE dbo.OrdenTrabajoPaso DROP CONSTRAINT ' + QUOTENAME(@ConstraintName) + N';';
    EXEC sys.sp_executesql @DropConstraintSql;
END

ALTER TABLE dbo.OrdenTrabajoPaso
    ADD CONSTRAINT DF_OrdenTrabajoPaso_PoliticaFoto DEFAULT ('OPCIONAL') FOR PoliticaFoto;

SET @ConstraintName = NULL;

SELECT @ConstraintName = defaultConstraint.name
FROM sys.default_constraints AS defaultConstraint
JOIN sys.columns AS columnDefinition
  ON columnDefinition.object_id = defaultConstraint.parent_object_id
 AND columnDefinition.column_id = defaultConstraint.parent_column_id
WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.OrdenTrabajoPlantillaPaso')
  AND columnDefinition.name = N'PoliticaFoto';

IF @ConstraintName IS NOT NULL
BEGIN
    SET @DropConstraintSql = N'ALTER TABLE dbo.OrdenTrabajoPlantillaPaso DROP CONSTRAINT ' + QUOTENAME(@ConstraintName) + N';';
    EXEC sys.sp_executesql @DropConstraintSql;
END

ALTER TABLE dbo.OrdenTrabajoPlantillaPaso
    ADD CONSTRAINT DF_OrdenTrabajoPlantillaPaso_PoliticaFoto DEFAULT ('OPCIONAL') FOR PoliticaFoto;
