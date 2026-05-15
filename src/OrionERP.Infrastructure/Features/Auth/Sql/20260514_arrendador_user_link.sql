SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
GO

IF OBJECT_ID('auth.AspNetUsers', 'U') IS NOT NULL
BEGIN
  IF COL_LENGTH('auth.AspNetUsers', 'ArrendadorProveedorId') IS NULL
  BEGIN
    ALTER TABLE auth.AspNetUsers
      ADD ArrendadorProveedorId int NULL;
  END;
END;
GO

IF OBJECT_ID('auth.AspNetUsers', 'U') IS NOT NULL
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'IX_AspNetUsers_ArrendadorProveedorId'
      AND object_id = OBJECT_ID(N'auth.AspNetUsers')
  )
  BEGIN
    CREATE UNIQUE INDEX IX_AspNetUsers_ArrendadorProveedorId
      ON auth.AspNetUsers (ArrendadorProveedorId)
      WHERE ArrendadorProveedorId IS NOT NULL;
  END;

  IF OBJECT_ID('dbo.Proveedores', 'U') IS NOT NULL
     AND NOT EXISTS (
       SELECT 1
       FROM sys.foreign_keys
       WHERE name = N'FK_AspNetUsers_Proveedores_ArrendadorProveedorId'
         AND parent_object_id = OBJECT_ID(N'auth.AspNetUsers')
     )
  BEGIN
    ALTER TABLE auth.AspNetUsers WITH CHECK
      ADD CONSTRAINT FK_AspNetUsers_Proveedores_ArrendadorProveedorId
      FOREIGN KEY (ArrendadorProveedorId)
      REFERENCES dbo.Proveedores (id);
  END;
END;
GO
