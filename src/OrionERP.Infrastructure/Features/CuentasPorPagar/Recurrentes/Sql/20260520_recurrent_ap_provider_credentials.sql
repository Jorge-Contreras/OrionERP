SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO
SET XACT_ABORT ON;
GO

IF SCHEMA_ID('AP') IS NOT NULL
   AND OBJECT_ID('AP.RecurringPayable', 'U') IS NOT NULL
BEGIN
  IF COL_LENGTH('AP.RecurringPayable', 'Website') IS NULL
  BEGIN
    ALTER TABLE AP.RecurringPayable
      ADD Website nvarchar(500) NULL;
  END;

  IF COL_LENGTH('AP.RecurringPayable', 'UserName') IS NULL
  BEGIN
    ALTER TABLE AP.RecurringPayable
      ADD UserName nvarchar(200) NULL;
  END;

  IF COL_LENGTH('AP.RecurringPayable', 'PasswordEnc') IS NULL
  BEGIN
    ALTER TABLE AP.RecurringPayable
      ADD PasswordEnc varbinary(max) NULL;
  END;
END;
GO
