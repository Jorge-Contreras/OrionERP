IF COL_LENGTH('dbo.RESERVATION', 'TAXABLE') IS NOT NULL
BEGIN
    UPDATE dbo.RESERVATION
    SET TAXABLE = 0
    WHERE TAXABLE IS NULL;

    DECLARE @Description nvarchar(3750) = N'Indicates whether the customer requires CFDI for this reservation. It does not control IVA or ISH calculation.';

    IF EXISTS
    (
        SELECT 1
        FROM sys.extended_properties ep
        WHERE ep.major_id = OBJECT_ID(N'dbo.RESERVATION')
          AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.RESERVATION'), N'TAXABLE', 'ColumnId')
          AND ep.name = N'MS_Description'
    )
    BEGIN
        EXEC sys.sp_updateextendedproperty
            @name = N'MS_Description',
            @value = @Description,
            @level0type = N'SCHEMA',
            @level0name = N'dbo',
            @level1type = N'TABLE',
            @level1name = N'RESERVATION',
            @level2type = N'COLUMN',
            @level2name = N'TAXABLE';
    END
    ELSE
    BEGIN
        EXEC sys.sp_addextendedproperty
            @name = N'MS_Description',
            @value = @Description,
            @level0type = N'SCHEMA',
            @level0name = N'dbo',
            @level1type = N'TABLE',
            @level1name = N'RESERVATION',
            @level2type = N'COLUMN',
            @level2name = N'TAXABLE';
    END
END;
