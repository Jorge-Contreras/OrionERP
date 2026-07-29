SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('restaurante.[Order]', 'U') IS NULL
  THROW 51128, 'Ejecuta primero 20260713_restaurant_operations.sql.', 1;

IF OBJECT_ID('restaurante.OrderEvent', 'U') IS NULL
BEGIN
  CREATE TABLE restaurante.OrderEvent
  (
    Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_RestaurantOrderEvent PRIMARY KEY,
    Rfc varchar(50) NOT NULL,
    SiteId int NOT NULL,
    OrderId uniqueidentifier NOT NULL,
    EventType varchar(80) NOT NULL,
    Category varchar(30) NOT NULL,
    Title nvarchar(180) NOT NULL,
    [Description] nvarchar(1200) NULL,
    Actor varchar(256) NULL,
    SourceKey varchar(180) NULL,
    OccurredAt datetime2(3) NOT NULL CONSTRAINT DF_RestaurantOrderEvent_Occurred DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT FK_RestaurantOrderEvent_Order_Rfc FOREIGN KEY (Rfc,OrderId)
      REFERENCES restaurante.[Order] (Rfc,Id),
    CONSTRAINT FK_RestaurantOrderEvent_Site_Rfc FOREIGN KEY (Rfc,SiteId)
      REFERENCES restaurante.Site (Rfc,Id)
  );
END;

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id=OBJECT_ID('restaurante.OrderEvent')
    AND [name]='IX_RestaurantOrderEvent_Timeline'
)
  CREATE INDEX IX_RestaurantOrderEvent_Timeline
    ON restaurante.OrderEvent (Rfc,OrderId,OccurredAt,Id)
    INCLUDE (SiteId,EventType,Category,Title,Actor);

IF NOT EXISTS
(
  SELECT 1 FROM sys.indexes
  WHERE object_id=OBJECT_ID('restaurante.OrderEvent')
    AND [name]='UX_RestaurantOrderEvent_Source'
)
  CREATE UNIQUE INDEX UX_RestaurantOrderEvent_Source
    ON restaurante.OrderEvent (Rfc,OrderId,SourceKey)
    WHERE SourceKey IS NOT NULL;

-- Give existing orders a useful audit trail from their durable operational timestamps.
INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT orderInfo.Rfc,orderInfo.SiteId,orderInfo.Id,'OrderCreated','Order',N'Orden generada',
       CONCAT(
         CASE orderInfo.OrderType WHEN 'Table' THEN N'Mesa' WHEN 'Delivery' THEN N'Domicilio' ELSE N'Para recoger' END,
         N' · Total ',CONVERT(varchar(40),CAST(orderInfo.Total AS money),1),
         N' · ',lineTotals.LineCount,N' partida(s)'
       ),
       orderInfo.CreatedBy,CONCAT('order:',CONVERT(varchar(36),orderInfo.Id),':created'),orderInfo.CreatedAt
FROM restaurante.[Order] orderInfo
OUTER APPLY
(
  SELECT COUNT(*) AS LineCount
  FROM restaurante.OrderLine lineInfo
  WHERE lineInfo.Rfc=orderInfo.Rfc AND lineInfo.OrderId=orderInfo.Id
) lineTotals
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=orderInfo.Rfc AND eventInfo.OrderId=orderInfo.Id
    AND eventInfo.SourceKey=CONCAT('order:',CONVERT(varchar(36),orderInfo.Id),':created')
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT reservation.Rfc,orderInfo.SiteId,orderInfo.Id,eventSource.EventType,'Inventory',
       eventSource.Title,eventSource.[Description],eventSource.Actor,
       CONCAT('reservation:',reservation.Id,':',eventSource.SourceSuffix),eventSource.OccurredAt
FROM logistica.InventoryReservation reservation
JOIN restaurante.[Order] orderInfo
  ON orderInfo.Rfc=reservation.Rfc AND orderInfo.InventoryReservationId=reservation.Id
OUTER APPLY
(
  SELECT COUNT(*) AS MaterialCount,
         SUM(CASE WHEN reservationLine.IsDeficit=1 THEN 1 ELSE 0 END) AS DeficitCount
  FROM logistica.InventoryReservationLine reservationLine
  WHERE reservationLine.Rfc=reservation.Rfc AND reservationLine.ReservationId=reservation.Id
) reservationTotals
CROSS APPLY
(
  SELECT
    CASE WHEN ISNULL(reservationTotals.DeficitCount,0)>0 THEN 'InventoryDeficitReserved' ELSE 'InventoryReserved' END,
    CASE WHEN ISNULL(reservationTotals.DeficitCount,0)>0 THEN N'Inventario reservado con déficit' ELSE N'Inventario reservado' END,
    CONCAT(
      reservationTotals.MaterialCount,N' insumo(s)',
      CASE WHEN ISNULL(reservationTotals.DeficitCount,0)>0
        THEN CONCAT(N' · ',reservationTotals.DeficitCount,N' con déficit') ELSE N'' END
    ),
    reservation.CreatedBy,'reserved',reservation.CreatedAt
  UNION ALL
  SELECT 'InventoryConsumed',N'Inventario consumido',
         N'La reserva de insumos se descontó al iniciar la preparación.',
         NULL,'consumed',reservation.ConsumedAt
    WHERE reservation.ConsumedAt IS NOT NULL
  UNION ALL
  SELECT 'InventoryReleased',N'Inventario liberado',
         N'Los insumos apartados regresaron a disponibilidad.',
         NULL,'released',reservation.ReleasedAt
    WHERE reservation.ReleasedAt IS NOT NULL
) eventSource(EventType,Title,[Description],Actor,SourceSuffix,OccurredAt)
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=reservation.Rfc AND eventInfo.OrderId=orderInfo.Id
    AND eventInfo.SourceKey=CONCAT('reservation:',reservation.Id,':',eventSource.SourceSuffix)
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT paymentInfo.Rfc,orderInfo.SiteId,paymentInfo.OrderId,'PaymentReceived','Payment',N'Pago recibido',
       CONCAT(
         CASE paymentInfo.PaymentMethod
           WHEN 'Cash' THEN N'Efectivo' WHEN 'ExternalCard' THEN N'Tarjeta'
           WHEN 'Transfer' THEN N'Transferencia' WHEN 'Platform' THEN N'Plataforma'
           ELSE paymentInfo.PaymentMethod END,
         N' · ',CONVERT(varchar(40),CAST(paymentInfo.Amount AS money),1),
         CASE WHEN paymentInfo.TipAmount>0
           THEN CONCAT(N' · Propina ',CONVERT(varchar(40),CAST(paymentInfo.TipAmount AS money),1))
           ELSE N'' END
       ),
       paymentInfo.ReceivedBy,CONCAT('payment:',CONVERT(varchar(36),paymentInfo.Id)),paymentInfo.PaidAt
FROM restaurante.Payment paymentInfo
JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=paymentInfo.Rfc AND eventInfo.OrderId=paymentInfo.OrderId
    AND eventInfo.SourceKey=CONCAT('payment:',CONVERT(varchar(36),paymentInfo.Id))
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT refundInfo.Rfc,orderInfo.SiteId,paymentInfo.OrderId,'PaymentRefunded','Payment',N'Pago reembolsado',
       CONCAT(CONVERT(varchar(40),CAST(refundInfo.Amount AS money),1),N' · ',refundInfo.Reason),
       refundInfo.RequestedBy,CONCAT('refund:',CONVERT(varchar(36),refundInfo.Id)),refundInfo.RefundedAt
FROM restaurante.PaymentRefund refundInfo
JOIN restaurante.Payment paymentInfo ON paymentInfo.Rfc=refundInfo.Rfc AND paymentInfo.Id=refundInfo.PaymentId
JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=refundInfo.Rfc AND eventInfo.OrderId=paymentInfo.OrderId
    AND eventInfo.SourceKey=CONCAT('refund:',CONVERT(varchar(36),refundInfo.Id))
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],SourceKey,OccurredAt)
SELECT lineInfo.Rfc,orderInfo.SiteId,lineInfo.OrderId,eventSource.EventType,'Kitchen',
       eventSource.Title,lineInfo.ProductNameSnapshot,
       CONCAT('line:',lineInfo.Id,':',eventSource.EventType),eventSource.OccurredAt
FROM restaurante.OrderLine lineInfo
JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=lineInfo.Rfc AND orderInfo.Id=lineInfo.OrderId
CROSS APPLY
(
  SELECT 'LinePreparing',N'Preparación iniciada',lineInfo.StartedAt WHERE lineInfo.StartedAt IS NOT NULL
  UNION ALL
  SELECT 'LineReady',N'Partida lista',lineInfo.ReadyAt WHERE lineInfo.ReadyAt IS NOT NULL
  UNION ALL
  SELECT 'LineDelivered',N'Partida entregada',lineInfo.DeliveredAt WHERE lineInfo.DeliveredAt IS NOT NULL
  UNION ALL
  SELECT 'LineCancelled',N'Partida cancelada',lineInfo.CancelledAt WHERE lineInfo.CancelledAt IS NOT NULL
) eventSource(EventType,Title,OccurredAt)
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=lineInfo.Rfc AND eventInfo.OrderId=lineInfo.OrderId
    AND eventInfo.SourceKey=CONCAT('line:',lineInfo.Id,':',eventSource.EventType)
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT orderInfo.Rfc,orderInfo.SiteId,orderInfo.Id,eventSource.EventType,eventSource.Category,
       eventSource.Title,eventSource.[Description],eventSource.Actor,
       CONCAT('order:',CONVERT(varchar(36),orderInfo.Id),':',eventSource.EventType),eventSource.OccurredAt
FROM restaurante.[Order] orderInfo
CROSS APPLY
(
  SELECT 'SentToKitchen','Kitchen',N'Orden enviada a cocina',NULL,NULL,orderInfo.SentToKitchenAt
    WHERE orderInfo.SentToKitchenAt IS NOT NULL
  UNION ALL
  SELECT 'OrderReady','Kitchen',N'Orden lista',NULL,NULL,orderInfo.ReadyAt
    WHERE orderInfo.ReadyAt IS NOT NULL
  UNION ALL
  SELECT 'OrderPaid','Payment',N'Orden pagada',NULL,NULL,orderInfo.PaidAt
    WHERE orderInfo.PaidAt IS NOT NULL
  UNION ALL
  SELECT 'OrderCompleted','Order',N'Orden completada',NULL,NULL,orderInfo.CompletedAt
    WHERE orderInfo.CompletedAt IS NOT NULL
  UNION ALL
  SELECT 'OrderCancelled','Order',N'Orden cancelada',orderInfo.CancellationReason,orderInfo.CancelledBy,orderInfo.CancelledAt
    WHERE orderInfo.CancelledAt IS NOT NULL
  UNION ALL
  SELECT CASE WHEN orderInfo.Priority>0 THEN 'PrioritySet' ELSE 'PriorityRemoved' END,
         'Authorization',
         CASE WHEN orderInfo.Priority>0 THEN N'Orden priorizada' ELSE N'Prioridad retirada' END,
         orderInfo.PriorityReason,orderInfo.PrioritizedBy,orderInfo.PrioritizedAt
    WHERE orderInfo.PrioritizedAt IS NOT NULL
) eventSource(EventType,Category,Title,[Description],Actor,OccurredAt)
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=orderInfo.Rfc AND eventInfo.OrderId=orderInfo.Id
    AND eventInfo.SourceKey=CONCAT('order:',CONVERT(varchar(36),orderInfo.Id),':',eventSource.EventType)
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT delivery.Rfc,orderInfo.SiteId,delivery.OrderId,eventSource.EventType,'Delivery',
       eventSource.Title,eventSource.[Description],eventSource.Actor,
       CONCAT('delivery:',CONVERT(varchar(36),delivery.OrderId),':',eventSource.EventType),eventSource.OccurredAt
FROM restaurante.Delivery delivery
JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=delivery.Rfc AND orderInfo.Id=delivery.OrderId
CROSS APPLY
(
  SELECT 'DeliveryRequested',N'Entrega a domicilio registrada',
         CONCAT(delivery.AddressLine,
           CASE WHEN delivery.ExternalReference IS NOT NULL
             THEN CONCAT(N' · Referencia ',delivery.ExternalReference) ELSE N'' END),
         orderInfo.CreatedBy,orderInfo.CreatedAt
  UNION ALL
  SELECT 'OrderDispatched',N'Orden despachada',delivery.ExternalReference,NULL,delivery.DispatchedAt
    WHERE delivery.DispatchedAt IS NOT NULL
  UNION ALL
  SELECT 'OrderDelivered',N'Entrega confirmada',delivery.ExternalReference,NULL,delivery.DeliveredAt
    WHERE delivery.DeliveredAt IS NOT NULL
  UNION ALL
  SELECT 'OrderSettled',N'Liquidación de plataforma completada',delivery.ExternalReference,NULL,delivery.SettledAt
    WHERE delivery.SettledAt IS NOT NULL
) eventSource(EventType,Title,[Description],Actor,OccurredAt)
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=delivery.Rfc AND eventInfo.OrderId=delivery.OrderId
    AND eventInfo.SourceKey=CONCAT('delivery:',CONVERT(varchar(36),delivery.OrderId),':',eventSource.EventType)
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],Actor,SourceKey,OccurredAt)
SELECT authInfo.Rfc,authInfo.SiteId,orderInfo.Id,'SupervisorAuthorization','Authorization',
       N'Acción autorizada por supervisor',
       CONCAT(
         CASE authInfo.ActionType
           WHEN 'Discount' THEN N'Descuento' WHEN 'InventoryDeficit' THEN N'Déficit de inventario'
           WHEN 'KitchenPriority' THEN N'Prioridad de cocina' WHEN 'CancelOrder' THEN N'Cancelación'
           WHEN 'AdditionalPayment' THEN N'Cargo adicional' WHEN 'PaymentRefund' THEN N'Reembolso'
           ELSE authInfo.ActionType END,
         N' · ',authInfo.Reason,N' · Autorizó: ',authInfo.AuthorizedBy
       ),
       authInfo.RequestedBy,CONCAT('authorization:',authInfo.Id),authInfo.AuthorizedAt
FROM restaurante.SupervisorAuthorization authInfo
JOIN restaurante.[Order] orderInfo
  ON orderInfo.Rfc=authInfo.Rfc
 AND CONVERT(varchar(36),orderInfo.Id)=authInfo.AggregateId
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=authInfo.Rfc AND eventInfo.OrderId=orderInfo.Id
    AND eventInfo.SourceKey=CONCAT('authorization:',authInfo.Id)
);

INSERT INTO restaurante.OrderEvent
  (Rfc,SiteId,OrderId,EventType,Category,Title,[Description],SourceKey,OccurredAt)
SELECT linkInfo.Rfc,linkInfo.SiteId,linkInfo.OrderId,'AccountingLinked','Accounting',
       CASE linkInfo.LinkType
         WHEN 'IndividualCfdi' THEN N'CFDI ligado a póliza individual'
         ELSE N'Orden incluida en póliza diaria' END,
       CONCAT(N'Póliza ',linkInfo.TransactionId,
         CASE WHEN linkInfo.CfdiId IS NOT NULL THEN CONCAT(N' · CFDI ',linkInfo.CfdiId) ELSE N'' END),
       CONCAT('accounting:',CONVERT(varchar(36),linkInfo.OrderId),':',linkInfo.LinkType,':',linkInfo.TransactionId),
       linkInfo.CreatedAt
FROM restaurante.AccountingOrderLink linkInfo
WHERE NOT EXISTS
(
  SELECT 1 FROM restaurante.OrderEvent eventInfo
  WHERE eventInfo.Rfc=linkInfo.Rfc AND eventInfo.OrderId=linkInfo.OrderId
    AND eventInfo.SourceKey=CONCAT('accounting:',CONVERT(varchar(36),linkInfo.OrderId),':',linkInfo.LinkType,':',linkInfo.TransactionId)
);

COMMIT TRANSACTION;
GO
