SET XACT_ABORT ON;
GO

IF OBJECT_ID('auth.AspNetRoles', 'U') IS NOT NULL
BEGIN
  INSERT INTO auth.AspNetRoles (Id, [Name], NormalizedName, ConcurrencyStamp)
  SELECT CONVERT(nvarchar(450), NEWID()), N'Arrendadores', N'ARRENDADORES', CONVERT(nvarchar(max), NEWID())
  WHERE NOT EXISTS (
    SELECT 1
    FROM auth.AspNetRoles
    WHERE NormalizedName = N'ARRENDADORES'
  );
END
ELSE IF OBJECT_ID('dbo.AspNetRoles', 'U') IS NOT NULL
BEGIN
  INSERT INTO dbo.AspNetRoles (Id, [Name], NormalizedName, ConcurrencyStamp)
  SELECT CONVERT(nvarchar(450), NEWID()), N'Arrendadores', N'ARRENDADORES', CONVERT(nvarchar(max), NEWID())
  WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.AspNetRoles
    WHERE NormalizedName = N'ARRENDADORES'
  );
END;
GO
