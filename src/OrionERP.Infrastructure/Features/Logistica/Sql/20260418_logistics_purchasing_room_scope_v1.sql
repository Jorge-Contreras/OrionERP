SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

IF OBJECT_ID('logistica.PurchaseOrderRoomScope', 'U') IS NULL
BEGIN
    CREATE TABLE logistica.PurchaseOrderRoomScope
    (
        PurchaseOrderId int NOT NULL,
        RoomId int NOT NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_PurchaseOrderRoomScope_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PurchaseOrderRoomScope PRIMARY KEY (PurchaseOrderId, RoomId),
        CONSTRAINT FK_PurchaseOrderRoomScope_PurchaseOrder
            FOREIGN KEY (PurchaseOrderId) REFERENCES logistica.PurchaseOrder (Id),
        CONSTRAINT FK_PurchaseOrderRoomScope_Room
            FOREIGN KEY (RoomId) REFERENCES dbo.ROOM (ID)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_PurchaseOrderRoomScope_RoomId'
      AND object_id = OBJECT_ID('logistica.PurchaseOrderRoomScope')
)
BEGIN
    CREATE INDEX IX_PurchaseOrderRoomScope_RoomId
        ON logistica.PurchaseOrderRoomScope (RoomId, PurchaseOrderId);
END;
GO
