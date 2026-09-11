/* Repair the Sandbox owner so EXECUTE AS OWNER modules and compatibility bridges are executable. */
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @ExpectedDatabase sysname=N'$(ExpectedDatabase)';
DECLARE @ApplyInput nvarchar(20)=N'$(ApplyChanges)';
DECLARE @ApplyChanges bit=0;
DECLARE @MigrationId nvarchar(200)=N'$(MigrationId)';
DECLARE @MigrationChecksum varchar(128)='$(MigrationChecksum)';
DECLARE @AppVersionInput nvarchar(64)=N'$(AppVersion)';
DECLARE @AppVersion nvarchar(64)=CASE WHEN @AppVersionInput LIKE N'$'+N'(%' THEN NULL ELSE NULLIF(LTRIM(RTRIM(@AppVersionInput)),N'') END;
IF @ApplyInput NOT LIKE N'$'+N'(%'
BEGIN
  IF @ApplyInput NOT IN(N'0',N'1') THROW 52850,'ApplyChanges debe ser 0 o 1.',1;
  SET @ApplyChanges=CONVERT(bit,@ApplyInput);
END;
IF @ExpectedDatabase LIKE N'$'+N'(%' OR @ExpectedDatabase<>N'Orion_Sandbox' OR DB_NAME()<>N'Orion_Sandbox'
  THROW 52851,'Esta reparación solo puede ejecutarse en Orion_Sandbox.',1;
IF @MigrationId<>N'20260911_sandbox_database_owner' THROW 52852,'MigrationId incorrecto.',1;
IF @MigrationChecksum LIKE '$'+'(%' OR LEN(@MigrationChecksum)<>64
   OR @MigrationChecksum LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2
  THROW 52853,'MigrationChecksum inválido.',1;
IF IS_SRVROLEMEMBER(N'sysadmin')<>1 THROW 52854,'La reparación del owner requiere sysadmin.',1;
DECLARE @ExistingChecksum char(64)=(SELECT Checksum FROM orion.SchemaMigration WHERE MigrationId=@MigrationId);
IF @ExistingChecksum IS NOT NULL AND UPPER(@ExistingChecksum)<>UPPER(@MigrationChecksum)
  THROW 52855,'El MigrationId ya existe con otro checksum.',1;
IF @ExistingChecksum IS NOT NULL
BEGIN SELECT N'YA_APLICADO' Estado,@MigrationId MigrationId,@ExistingChecksum Checksum; RETURN; END;

BEGIN TRANSACTION;
ALTER AUTHORIZATION ON DATABASE::[Orion_Sandbox] TO [sa];
BEGIN TRY
  EXECUTE AS USER=N'dbo';
  IF USER_NAME()<>N'dbo' THROW 52856,'EXECUTE AS OWNER continúa indisponible.',1;
  REVERT;
END TRY
BEGIN CATCH
  IF USER_NAME()<>N'dbo' REVERT;
  IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
  THROW;
END CATCH;

IF @ApplyChanges=1
BEGIN
  INSERT orion.SchemaMigration(MigrationId,Checksum,AppliedBy,AppVersion,DatabaseName)
  VALUES(@MigrationId,@MigrationChecksum,CONVERT(nvarchar(256),ORIGINAL_LOGIN()),@AppVersion,DB_NAME());
  COMMIT TRANSACTION;
  SELECT N'APLICADO' Estado,@MigrationId MigrationId,N'sa' DatabaseOwner;
END
ELSE
BEGIN
  SELECT N'PREVIEW' Estado,@MigrationId MigrationId,N'sa' DatabaseOwner;
  ROLLBACK TRANSACTION;
END;
