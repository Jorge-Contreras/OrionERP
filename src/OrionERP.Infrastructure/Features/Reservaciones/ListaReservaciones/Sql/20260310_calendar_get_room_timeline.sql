CREATE OR ALTER PROCEDURE dbo.Calendar_GetRoomTimeline
    @StartDate date,
    @EndDateExclusive date,
    @RoomType varchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @EndDateExclusive <= @StartDate
    BEGIN
        THROW 50001, 'EndDateExclusive must be after StartDate.', 1;
    END;

    CREATE TABLE #Resources
    (
        RoomId int NOT NULL,
        RoomCode varchar(50) NOT NULL,
        RoomName varchar(50) NOT NULL,
        RoomType varchar(50) NOT NULL,
        BasePrice decimal(18,2) NOT NULL,
        DisplayOrder int NOT NULL,
        CalendarEnabled bit NOT NULL
    );

    INSERT INTO #Resources (RoomId, RoomCode, RoomName, RoomType, BasePrice, DisplayOrder, CalendarEnabled)
    SELECT
        r.ID AS RoomId,
        r.ROOM_NAME AS RoomCode,
        r.ROOM_NAME AS RoomName,
        r.ROOM_TYPE AS RoomType,
        CAST(ISNULL(r.BASE_PRICE, 0) AS decimal(18,2)) AS BasePrice,
        ROW_NUMBER() OVER (ORDER BY r.ROOM_NAME) AS DisplayOrder,
        CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM dbo.ROOM_CALENDAR rc
            WHERE rc.ROOM = r.ROOM_NAME
        ) THEN 1 ELSE 0 END AS bit) AS CalendarEnabled
    FROM dbo.ROOM r
    WHERE (@RoomType IS NULL OR r.ROOM_TYPE = @RoomType)
      AND EXISTS (
          SELECT 1
          FROM dbo.ROOM_CALENDAR rc
          WHERE rc.ROOM = r.ROOM_NAME
      );

    CREATE TABLE #Dates
    (
        RoomDate date NOT NULL PRIMARY KEY
    );

    ;WITH date_series AS
    (
        SELECT CAST(@StartDate AS date) AS RoomDate
        UNION ALL
        SELECT DATEADD(day, 1, RoomDate)
        FROM date_series
        WHERE RoomDate < DATEADD(day, -1, CAST(@EndDateExclusive AS date))
    )
    INSERT INTO #Dates (RoomDate)
    SELECT RoomDate
    FROM date_series
    OPTION (MAXRECURSION 32767);

    CREATE TABLE #CalendarBase
    (
        RoomId int NOT NULL,
        RoomCode varchar(50) NOT NULL,
        RoomName varchar(50) NOT NULL,
        RoomDate date NOT NULL,
        RoomCalendarId int NULL,
        IsLocked bit NOT NULL,
        LockedBy varchar(50) NULL,
        LockDescription varchar(500) NULL,
        StateCode varchar(20) NOT NULL,
        ReservationId int NULL,
        ReservationStatus varchar(50) NULL,
        IsArrival bit NOT NULL,
        IsDeparture bit NOT NULL,
        HasExtras bit NOT NULL,
        HasDeepCleaning bit NOT NULL,
        HasDailyCheck bit NOT NULL,
        Price decimal(18,2) NOT NULL,
        Notes varchar(500) NULL,
        DataQualityFlag varchar(50) NULL
    );

    INSERT INTO #CalendarBase
    (
        RoomId,
        RoomCode,
        RoomName,
        RoomDate,
        RoomCalendarId,
        IsLocked,
        LockedBy,
        LockDescription,
        StateCode,
        ReservationId,
        ReservationStatus,
        IsArrival,
        IsDeparture,
        HasExtras,
        HasDeepCleaning,
        HasDailyCheck,
        Price,
        Notes,
        DataQualityFlag
    )
    SELECT
        rr.RoomId,
        rr.RoomCode,
        rr.RoomName,
        d.RoomDate,
        rc.ID AS RoomCalendarId,
        CAST(ISNULL(rc.IS_LOCKED, 0) AS bit) AS IsLocked,
        NULLIF(LTRIM(RTRIM(rc.LOCKED_BY)), '') AS LockedBy,
        NULLIF(LTRIM(RTRIM(rc.LOCK_DESCRIPTION)), '') AS LockDescription,
        CASE
            WHEN rc.ID IS NULL THEN 'missing'
            WHEN ISNULL(rc.IS_LOCKED, 0) = 0 THEN 'available'
            WHEN TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL AND r.ID IS NULL THEN 'orphan'
            WHEN TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL
              AND r.ID IS NOT NULL
              AND UPPER(LTRIM(RTRIM(ISNULL(r.STATUS, '')))) COLLATE Latin1_General_100_CI_AI = N'COTIZACION' THEN 'soft_hold'
            WHEN TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL AND r.ID IS NOT NULL THEN 'reserved'
            ELSE 'blocked'
        END AS StateCode,
        r.ID AS ReservationId,
        r.STATUS AS ReservationStatus,
        CAST(CASE WHEN r.ID IS NOT NULL AND d.RoomDate = r.CHECKIN THEN 1 ELSE 0 END AS bit) AS IsArrival,
        CAST(CASE WHEN r.ID IS NOT NULL AND d.RoomDate = DATEADD(day, -1, r.CHECKOUT) THEN 1 ELSE 0 END AS bit) AS IsDeparture,
        CAST(CASE WHEN r.ID IS NOT NULL AND EXISTS (
            SELECT 1
            FROM dbo.Reservation_Extra re
            WHERE re.ReservationID = r.ID
        ) THEN 1 ELSE 0 END AS bit) AS HasExtras,
        CAST(ISNULL(rc.LIMPIEZA_PROFUNDA, 0) AS bit) AS HasDeepCleaning,
        CAST(ISNULL(rc.CHECK_DIARIO, 0) AS bit) AS HasDailyCheck,
        CAST(ISNULL(rc.PRECIO, rr.BasePrice) AS decimal(18,2)) AS Price,
        rc.NOTES AS Notes,
        CASE
            WHEN rc.ID IS NULL THEN 'missing-room-calendar'
            WHEN ISNULL(rc.IS_LOCKED, 0) = 1
              AND TRY_CAST(rc.LOCK_DESCRIPTION AS int) IS NOT NULL
              AND r.ID IS NULL THEN 'missing-reservation'
            ELSE NULL
        END AS DataQualityFlag
    FROM #Resources rr
    CROSS JOIN #Dates d
    LEFT JOIN dbo.ROOM_CALENDAR rc
      ON rc.ROOM = rr.RoomCode
     AND rc.ROOM_DATE = d.RoomDate
    LEFT JOIN dbo.RESERVATION r
      ON r.ID = TRY_CAST(rc.LOCK_DESCRIPTION AS int);

    SELECT
        RoomId,
        RoomCode,
        RoomName,
        RoomType,
        BasePrice,
        DisplayOrder,
        CalendarEnabled
    FROM #Resources
    ORDER BY DisplayOrder, RoomName;

    SELECT
        RoomId,
        RoomCode,
        RoomName,
        RoomDate,
        RoomCalendarId,
        IsLocked,
        LockedBy,
        LockDescription,
        StateCode,
        ReservationId,
        ReservationStatus,
        IsArrival,
        IsDeparture,
        HasExtras,
        HasDeepCleaning,
        HasDailyCheck,
        Price,
        Notes,
        DataQualityFlag
    FROM #CalendarBase
    ORDER BY RoomName, RoomDate;

    ;WITH event_source AS
    (
        SELECT
            cb.*,
            DATEADD(day, -ROW_NUMBER() OVER (
                PARTITION BY cb.RoomId,
                             cb.StateCode,
                             ISNULL(CONVERT(varchar(50), cb.ReservationId), ''),
                             ISNULL(cb.LockDescription, ''),
                             ISNULL(cb.LockedBy, '')
                ORDER BY cb.RoomDate
            ), cb.RoomDate) AS GroupAnchor
        FROM #CalendarBase cb
        WHERE cb.IsLocked = 1
    )
    SELECT
        RoomId,
        RoomCode,
        RoomName,
        MIN(RoomDate) AS StartDate,
        DATEADD(day, 1, MAX(RoomDate)) AS EndDateExclusive,
        StateCode AS EventType,
        ReservationId,
        MAX(ReservationStatus) AS ReservationStatus,
        MAX(LockedBy) AS LockedBy,
        MAX(LockDescription) AS LockDescription,
        MAX(CASE
            WHEN ReservationId IS NOT NULL THEN CONCAT(RoomName, ' #', ReservationId)
            WHEN LockDescription IS NOT NULL THEN CONCAT(RoomName, ' ', LockDescription)
            ELSE RoomName
        END) AS Title,
        MAX(CASE
            WHEN LockedBy IS NOT NULL THEN LockedBy
            WHEN ReservationStatus IS NOT NULL THEN ReservationStatus
            ELSE NULL
        END) AS Subtitle,
        MAX(DataQualityFlag) AS DataQualityFlag
    FROM event_source
    GROUP BY
        RoomId,
        RoomCode,
        RoomName,
        StateCode,
        ReservationId,
        GroupAnchor
    ORDER BY StartDate, RoomName;
END;
