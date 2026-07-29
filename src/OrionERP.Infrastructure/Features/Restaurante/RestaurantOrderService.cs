using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantOrderService : IRestaurantOrderService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantOrderService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<RestaurantOrderResult> CreateOrderAsync(RestaurantOrderCreateRequest request, string userName, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.Lines.Count == 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
      throw new InvalidOperationException("La orden requiere productos y una clave de idempotencia.");
    }
    if (string.IsNullOrWhiteSpace(request.CustomerName))
    {
      throw new InvalidOperationException("El nombre del cliente es obligatorio.");
    }
    if (request.CustomerName.Trim().Length > 150)
    {
      throw new InvalidOperationException("El nombre del cliente no puede exceder 150 caracteres.");
    }
    foreach (var line in request.Lines)
    {
      if (line.IsCustom)
      {
        _ = RestaurantCustomItemRules.CreateSnapshot(line);
      }
      else
      {
        RestaurantCustomItemRules.ValidateCatalogLine(line);
      }
    }
    if (request.OrderDiscountAmount > 0 || request.Lines.Any(line => line.DiscountAmount > 0))
    {
      if (string.IsNullOrWhiteSpace(request.DiscountReason) || string.IsNullOrWhiteSpace(request.SupervisorAuthorizedBy))
      {
        throw new InvalidOperationException("Los descuentos requieren motivo y autorización de supervisor.");
      }
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var duplicate = await FindDuplicateAsync(conn, tx, rfc, request.SiteId, request.IdempotencyKey.Trim(), ct);
      if (duplicate is not null)
      {
        duplicate.WasDuplicate = true;
        await tx.CommitAsync(ct);
        return duplicate;
      }

      var site = await conn.QuerySingleOrDefaultAsync<SiteRow>(new CommandDefinition(
        """
        SELECT Id, Rfc, TimeZoneId, OperationalDayCutoff, TaxRate, PricesIncludeTax,
               IsEnabled, AllowSupervisorDeficit
        FROM restaurante.Site WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc = @Rfc AND Id = @SiteId;
        """, new { Rfc = rfc, request.SiteId }, tx, cancellationToken: ct))
        ?? throw new InvalidOperationException("La sede no existe en el RFC seleccionado.");
      if (!site.IsEnabled)
      {
        throw new InvalidOperationException("Restaurante está deshabilitado para esta sede.");
      }
      if (request.AllowInventoryDeficit && (!site.AllowSupervisorDeficit || string.IsNullOrWhiteSpace(request.SupervisorAuthorizedBy)))
      {
        throw new InvalidOperationException("El déficit de inventario requiere que la sede lo permita y autorización de supervisor.");
      }
      if (request.OrderDiscountAmount > 0 || request.Lines.Any(line => line.DiscountAmount > 0) || request.AllowInventoryDeficit)
      {
        var hasRecentSupervisorPin = request.CashRegisterId.HasValue && await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          """
          SELECT CAST(CASE WHEN EXISTS
          (
            SELECT 1
            FROM restaurante.QuickPinAttempt attemptInfo
            JOIN auth.AspNetUsers userInfo ON userInfo.Id=attemptInfo.UserId
            WHERE attemptInfo.Rfc=@Rfc AND attemptInfo.CashRegisterId=@CashRegisterId
              AND attemptInfo.Succeeded=1 AND attemptInfo.AttemptedAt>=DATEADD(minute,-2,SYSUTCDATETIME())
              AND (userInfo.UserName=@Supervisor OR userInfo.Email=@Supervisor)
          ) THEN 1 ELSE 0 END AS bit);
          """, new { Rfc = rfc, request.CashRegisterId, Supervisor = request.SupervisorAuthorizedBy }, tx, cancellationToken: ct));
        if (!hasRecentSupervisorPin)
          throw new InvalidOperationException("La autorización de supervisor no tiene una verificación de PIN reciente en esta caja.");
      }

      await ValidateOperationalReferencesAsync(conn, tx, rfc, request, ct);
      var catalogLines = request.Lines.Where(line => !line.IsCustom).ToList();
      var productIds = catalogLines.Select(line => line.ProductId!.Value).Distinct().ToArray();
      var products = productIds.Length == 0
        ? []
        : (await conn.QueryAsync<ProductRow>(new CommandDefinition(
          """
          SELECT product.Id, product.MaterialId, product.Sku, card.[Name], product.VariantName,
                 product.Price, product.KitchenStationId, product.PreparationMinutes,
                 material.FulfillmentMode, material.BaseUnitId, material.TrackLots,
                 CAST(ISNULL(activeBom.FrozenTheoreticalCost, 0) AS decimal(18,6)) AS TheoreticalCost
          FROM restaurante.Product product
          JOIN restaurante.ProductCard card ON card.Rfc = product.Rfc AND card.Id = product.ProductCardId
          JOIN logistica.Material material ON material.Rfc = product.Rfc AND material.Id = product.MaterialId
          OUTER APPLY
          (
            SELECT TOP (1) bomVersion.FrozenTheoreticalCost
            FROM logistica.BomHeader bomHeader
            JOIN logistica.BomVersion bomVersion ON bomVersion.Rfc = bomHeader.Rfc AND bomVersion.BomHeaderId = bomHeader.Id
            WHERE bomHeader.Rfc = product.Rfc AND bomHeader.ProductMaterialId = product.MaterialId AND bomVersion.[Status] = 'Active'
          ) activeBom
          WHERE product.Rfc = @Rfc AND product.Id IN @ProductIds AND product.IsActive = 1 AND product.SoldOutOverride = 0;
          """, new { Rfc = rfc, ProductIds = productIds }, tx, cancellationToken: ct))).AsList();
      if (products.Count != productIds.Length)
      {
        throw new InvalidOperationException("Uno o más productos están inactivos, agotados o pertenecen a otro RFC.");
      }

      var requestedOptionIds = catalogLines.SelectMany(line => line.ModifierOptionIds).Distinct().ToArray();
      var modifierRows = requestedOptionIds.Length == 0
        ? []
        : (await conn.QueryAsync<ModifierRow>(new CommandDefinition(
          """
          SELECT productGroup.ProductId, groupInfo.Id AS ModifierGroupId, groupInfo.[Name] AS GroupName,
                 groupInfo.MinSelections, groupInfo.MaxSelections,
                 optionInfo.Id, optionInfo.[Name], optionInfo.PriceDelta
          FROM restaurante.ProductModifierGroup productGroup
          JOIN restaurante.ModifierGroup groupInfo ON groupInfo.Rfc = productGroup.Rfc AND groupInfo.Id = productGroup.ModifierGroupId
          JOIN restaurante.ModifierOption optionInfo ON optionInfo.Rfc = groupInfo.Rfc AND optionInfo.ModifierGroupId = groupInfo.Id
          WHERE productGroup.Rfc = @Rfc AND optionInfo.Id IN @OptionIds
            AND groupInfo.IsActive = 1 AND optionInfo.IsActive = 1;
          """, new { Rfc = rfc, OptionIds = requestedOptionIds }, tx, cancellationToken: ct))).AsList();
      ValidateModifiers(request, modifierRows);

      var pricedLines = new List<PricedLine>();
      decimal subtotalBeforeDiscount = 0;
      decimal lineDiscount = 0;
      foreach (var line in request.Lines)
      {
        if (line.Quantity <= 0)
        {
          throw new InvalidOperationException("La cantidad de cada producto debe ser mayor que cero.");
        }
        ProductRow? product = null;
        List<ModifierRow> modifiers;
        string productName;
        string sku;
        decimal unitPrice;
        decimal gross;
        if (line.IsCustom)
        {
          var custom = RestaurantCustomItemRules.CreateSnapshot(line);
          modifiers = [];
          productName = custom.Name;
          sku = RestaurantCustomItemRules.SkuSnapshot;
          unitPrice = custom.UnitPrice;
          gross = custom.Gross;
        }
        else
        {
          var productId = line.ProductId!.Value;
          product = products.Single(item => item.Id == productId);
          modifiers = modifierRows.Where(item => item.ProductId == productId && line.ModifierOptionIds.Contains(item.Id)).ToList();
          productName = string.IsNullOrWhiteSpace(product.VariantName)
            ? product.Name
            : $"{product.Name} · {product.VariantName}";
          sku = product.Sku;
          unitPrice = product.Price + modifiers.Sum(item => item.PriceDelta);
          gross = decimal.Round(unitPrice * line.Quantity, 2, MidpointRounding.AwayFromZero);
        }
        if (line.DiscountAmount < 0 || line.DiscountAmount > gross)
        {
          throw new InvalidOperationException("El descuento de una línea no puede exceder su importe.");
        }
        subtotalBeforeDiscount += gross;
        lineDiscount += line.DiscountAmount;
        pricedLines.Add(new PricedLine(line, product, modifiers, productName, sku, unitPrice, gross, line.IsCustom));
      }
      if (request.OrderDiscountAmount < 0 || request.OrderDiscountAmount > subtotalBeforeDiscount - lineDiscount)
      {
        throw new InvalidOperationException("El descuento de orden no puede exceder el subtotal disponible.");
      }

      var discountTotal = decimal.Round(lineDiscount + request.OrderDiscountAmount, 2, MidpointRounding.AwayFromZero);
      var discountedMerchandise = decimal.Round(subtotalBeforeDiscount - discountTotal, 2, MidpointRounding.AwayFromZero);
      decimal subtotalSnapshot;
      decimal taxTotal;
      decimal total;
      if (site.PricesIncludeTax)
      {
        taxTotal = site.TaxRate == 0 ? 0 : decimal.Round(discountedMerchandise - discountedMerchandise / (1 + site.TaxRate), 2, MidpointRounding.AwayFromZero);
        subtotalSnapshot = decimal.Round(subtotalBeforeDiscount - taxTotal, 2, MidpointRounding.AwayFromZero);
        total = decimal.Round(discountedMerchandise + request.DeliveryCost, 2, MidpointRounding.AwayFromZero);
      }
      else
      {
        subtotalSnapshot = subtotalBeforeDiscount;
        taxTotal = decimal.Round(discountedMerchandise * site.TaxRate, 2, MidpointRounding.AwayFromZero);
        total = decimal.Round(discountedMerchandise + taxTotal + request.DeliveryCost, 2, MidpointRounding.AwayFromZero);
      }

      var paymentAmount = request.Payments.Sum(payment => payment.Amount);
      if (request.Payments.Any(payment => payment.Amount <= 0 || string.IsNullOrWhiteSpace(payment.IdempotencyKey)))
      {
        throw new InvalidOperationException("Cada pago requiere importe positivo y clave de idempotencia.");
      }
      if (paymentAmount > total + 0.01m)
      {
        throw new InvalidOperationException("Los pagos no pueden exceder el total de la orden.");
      }
      var orderType = NormalizeOrderType(request.OrderType);
      var externalCod = orderType == "Delivery" && request.ExternalProviderId.HasValue && paymentAmount < total;
      if (orderType is "Pickup" or "Table" && paymentAmount < total)
      {
        throw new InvalidOperationException("Las órdenes para recoger o mesa deben pagarse antes de enviarse a cocina.");
      }

      var timeZone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZoneId);
      var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
      var operationalDate = DateOnly.FromDateTime(localNow.TimeOfDay < site.OperationalDayCutoff ? localNow.AddDays(-1).Date : localNow.Date);
      var orderId = Guid.NewGuid();
      var requirements = await BuildRequirementsAsync(conn, tx, rfc, pricedLines, modifierRows, ct);
      var reservation = requirements.Count == 0
        ? new ReservationResult(null, false)
        : await ReserveInventoryAsync(conn, tx, rfc, request.SiteId, orderId, request.IdempotencyKey.Trim(), requirements,
          request.AllowInventoryDeficit, userName, ct);

      var folioParameters = new DynamicParameters();
      folioParameters.Add("@Rfc", rfc);
      folioParameters.Add("@SiteId", request.SiteId);
      folioParameters.Add("@OperationalDate", operationalDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
      folioParameters.Add("@Folio", dbType: DbType.Int32, direction: ParameterDirection.Output);
      await conn.ExecuteAsync(new CommandDefinition("restaurante.NextDailyFolio", folioParameters, tx, commandType: CommandType.StoredProcedure, cancellationToken: ct));
      var folio = folioParameters.Get<int>("@Folio");

      var paymentStatus = externalCod
        ? RestaurantPaymentStatuses.PendingSettlement
        : paymentAmount >= total ? RestaurantPaymentStatuses.Paid
        : paymentAmount > 0 ? RestaurantPaymentStatuses.Partial : RestaurantPaymentStatuses.Pending;
      var hasProductionLines = pricedLines.Any(line => !line.IsCustom);
      var status = paymentAmount >= total || externalCod
        ? hasProductionLines ? RestaurantOrderStatuses.Sent : RestaurantOrderStatuses.Ready
        : RestaurantOrderStatuses.AwaitingPayment;
      var balanceDue = decimal.Round(total - paymentAmount, 2, MidpointRounding.AwayFromZero);
      var tips = request.Payments.Sum(payment => payment.TipAmount);
      var theoreticalCost = pricedLines.Sum(line => (line.Product?.TheoreticalCost ?? 0) * line.Request.Quantity);

      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.[Order]
          (Id, Rfc, SiteId, Folio, OperationalDate, OrderType, [Status], PaymentStatus,
           CustomerName, CustomerPhone, DiningTableId, CashRegisterId, CashShiftId,
           Subtotal, DiscountTotal, TaxTotal, TipTotal, Total, BalanceDue, TaxRateSnapshot,
           PricesIncludeTaxSnapshot, InventoryReservationId, TheoreticalCost, IdempotencyKey,
           Notes, CreatedBy, PaidAt, SentToKitchenAt)
        VALUES
          (@Id, @Rfc, @SiteId, @Folio, @OperationalDate, @OrderType, @Status, @PaymentStatus,
           @CustomerName, @CustomerPhone, @DiningTableId, @CashRegisterId, @CashShiftId,
           @Subtotal, @DiscountTotal, @TaxTotal, @TipTotal, @Total, @BalanceDue, @TaxRate,
           @PricesIncludeTax, @ReservationId, @TheoreticalCost, @IdempotencyKey,
           @Notes, @CreatedBy, CASE WHEN @PaymentStatus = 'Paid' THEN SYSUTCDATETIME() END,
           CASE WHEN @Status = 'Sent' THEN SYSUTCDATETIME() END);
        """, new
        {
          Id = orderId,
          Rfc = rfc,
          request.SiteId,
          Folio = folio,
          OperationalDate = operationalDate.ToDateTime(TimeOnly.MinValue),
          OrderType = orderType,
          Status = status,
          PaymentStatus = paymentStatus,
          CustomerName = NullIfWhiteSpace(request.CustomerName),
          CustomerPhone = NullIfWhiteSpace(request.CustomerPhone),
          request.DiningTableId,
          request.CashRegisterId,
          request.CashShiftId,
          Subtotal = subtotalSnapshot,
          DiscountTotal = discountTotal,
          TaxTotal = taxTotal,
          TipTotal = tips,
          Total = total,
          BalanceDue = balanceDue,
          TaxRate = site.TaxRate,
          PricesIncludeTax = site.PricesIncludeTax,
          ReservationId = reservation.ReservationId,
          TheoreticalCost = theoreticalCost,
          IdempotencyKey = request.IdempotencyKey.Trim(),
          Notes = NullIfWhiteSpace(request.Notes),
          CreatedBy = userName
        }, tx, cancellationToken: ct));

      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, rfc, request.SiteId, orderId,
        "OrderCreated", "Order", "Orden generada",
        $"{OrderTypeLabel(orderType)} · {pricedLines.Count} partida(s) · Total {total:C}",
        userName, ct, $"order:{orderId}:created");

      if (reservation.ReservationId.HasValue)
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, request.SiteId, orderId,
          reservation.HasDeficit ? "InventoryDeficitReserved" : "InventoryReserved",
          "Inventory",
          reservation.HasDeficit ? "Inventario reservado con déficit" : "Inventario reservado",
          reservation.HasDeficit
            ? "La orden se autorizó aunque una o más existencias resultaron insuficientes."
            : "Los insumos requeridos quedaron apartados para la preparación.",
          userName, ct, $"reservation:{reservation.ReservationId.Value}:reserved");
      }

      foreach (var pricedLine in pricedLines)
      {
        var allocatedOrderDiscount = subtotalBeforeDiscount == 0 ? 0 : decimal.Round(request.OrderDiscountAmount * pricedLine.Gross / subtotalBeforeDiscount, 2, MidpointRounding.AwayFromZero);
        var totalLineDiscount = pricedLine.Request.DiscountAmount + allocatedOrderDiscount;
        var discountedLine = pricedLine.Gross - totalLineDiscount;
        var lineTax = site.PricesIncludeTax
          ? (site.TaxRate == 0 ? 0 : decimal.Round(discountedLine - discountedLine / (1 + site.TaxRate), 2, MidpointRounding.AwayFromZero))
          : decimal.Round(discountedLine * site.TaxRate, 2, MidpointRounding.AwayFromZero);
        var lineTotal = site.PricesIncludeTax ? discountedLine : discountedLine + lineTax;
        var lineStatus = pricedLine.IsCustom ? RestaurantOrderStatuses.Ready : "Pending";
        var lineId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT INTO restaurante.OrderLine
            (Rfc, OrderId, ProductId, IsCustom, ProductNameSnapshot, SkuSnapshot, Quantity, UnitPrice,
             DiscountAmount, TaxAmount, LineTotal, [Status], KitchenStationId, Notes, ReadyAt)
          VALUES
            (@Rfc, @OrderId, @ProductId, @IsCustom, @ProductName, @Sku, @Quantity, @UnitPrice,
             @DiscountAmount, @TaxAmount, @LineTotal, @Status, @KitchenStationId, @Notes,
             CASE WHEN @IsCustom = 1 THEN SYSUTCDATETIME() END);
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new
          {
            Rfc = rfc,
            OrderId = orderId,
            ProductId = pricedLine.Product?.Id,
            pricedLine.IsCustom,
            ProductName = pricedLine.ProductName,
            Sku = pricedLine.Sku,
            pricedLine.Request.Quantity,
            UnitPrice = pricedLine.UnitPrice,
            DiscountAmount = totalLineDiscount,
            TaxAmount = lineTax,
            LineTotal = lineTotal,
            Status = lineStatus,
            KitchenStationId = pricedLine.Product?.KitchenStationId,
            Notes = NullIfWhiteSpace(pricedLine.Request.Notes)
          }, tx, cancellationToken: ct));
        foreach (var modifier in pricedLine.Modifiers)
        {
          await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO restaurante.OrderLineModifier (Rfc, OrderLineId, ModifierOptionId, [Name], PriceDelta)
            VALUES (@Rfc, @OrderLineId, @ModifierOptionId, @Name, @PriceDelta);
            """, new { Rfc = rfc, OrderLineId = lineId, ModifierOptionId = modifier.Id, modifier.Name, modifier.PriceDelta }, tx, cancellationToken: ct));
        }
      }

      foreach (var payment in request.Payments)
      {
        var paymentId = Guid.NewGuid();
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO restaurante.Payment
            (Id, Rfc, OrderId, PaymentMethod, Amount, TipAmount, [Status], ExternalReference, IdempotencyKey, ReceivedBy)
          VALUES
            (@Id, @Rfc, @OrderId, @PaymentMethod, @Amount, @TipAmount, 'Paid', @ExternalReference, @IdempotencyKey, @ReceivedBy);
          """, new
          {
            Id = paymentId,
            Rfc = rfc,
            OrderId = orderId,
            PaymentMethod = NormalizePaymentMethod(payment.PaymentMethod),
            payment.Amount,
            payment.TipAmount,
            ExternalReference = NullIfWhiteSpace(payment.ExternalReference),
            IdempotencyKey = payment.IdempotencyKey.Trim(),
            ReceivedBy = userName
          }, tx, cancellationToken: ct));
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, request.SiteId, orderId,
          "PaymentReceived", "Payment", "Pago recibido",
          $"{PaymentMethodLabel(NormalizePaymentMethod(payment.PaymentMethod))} · {payment.Amount:C}" +
          (payment.TipAmount > 0 ? $" · Propina {payment.TipAmount:C}" : string.Empty),
          userName, ct, $"payment:{paymentId}");
        if (request.CashShiftId.HasValue)
        {
          await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO restaurante.CashMovement
              (Rfc, CashShiftId, MovementType, PaymentMethod, Amount, OrderId, CreatedBy)
            VALUES
              (@Rfc, @CashShiftId, 'Sale', @PaymentMethod, @Amount, @OrderId, @CreatedBy);
            """, new
            {
              Rfc = rfc,
              request.CashShiftId,
              PaymentMethod = NormalizePaymentMethod(payment.PaymentMethod),
              Amount = payment.Amount + payment.TipAmount,
              OrderId = orderId,
              CreatedBy = userName
            }, tx, cancellationToken: ct));
        }
      }

      if (orderType == "Delivery")
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO restaurante.Delivery
            (Rfc, OrderId, ExternalProviderId, ExternalReference, AddressLine, AddressReferences,
             DeliveryCost, CommissionAmount, [Status])
          VALUES
            (@Rfc, @OrderId, @ExternalProviderId, @ExternalReference, @AddressLine, @AddressReferences,
             @DeliveryCost, @CommissionAmount, 'PendingDispatch');
          """, new
          {
            Rfc = rfc,
            OrderId = orderId,
            request.ExternalProviderId,
            ExternalReference = NullIfWhiteSpace(request.ExternalReference),
            AddressLine = request.DeliveryAddress!.Trim(),
            AddressReferences = NullIfWhiteSpace(request.DeliveryReferences),
            request.DeliveryCost,
            request.CommissionAmount
          }, tx, cancellationToken: ct));
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, request.SiteId, orderId,
          "DeliveryRequested", "Delivery", "Entrega a domicilio registrada",
          $"{request.DeliveryAddress!.Trim()}" +
          (!string.IsNullOrWhiteSpace(request.ExternalReference)
            ? $" · Referencia {request.ExternalReference.Trim()}"
            : string.Empty),
          userName, ct, $"delivery:{orderId}:DeliveryRequested");
      }

      if (!string.IsNullOrWhiteSpace(request.SupervisorAuthorizedBy))
      {
        if (discountTotal > 0)
        {
          await AddSupervisorAuthorizationAsync(
            conn, tx, rfc, request.SiteId, orderId, "Discount",
            NullIfWhiteSpace(request.DiscountReason) ?? "Descuento aplicado a la orden.",
            userName, request.SupervisorAuthorizedBy, ct);
          await RestaurantOrderEventWriter.AddAsync(
            conn, tx, rfc, request.SiteId, orderId,
            "DiscountAuthorized", "Authorization", "Descuento autorizado",
            $"{discountTotal:C} · {NullIfWhiteSpace(request.DiscountReason) ?? "Sin motivo capturado"}",
            request.SupervisorAuthorizedBy, ct);
        }

        if (request.AllowInventoryDeficit)
        {
          await AddSupervisorAuthorizationAsync(
            conn, tx, rfc, request.SiteId, orderId, "InventoryDeficit",
            "Venta autorizada con déficit de inventario.",
            userName, request.SupervisorAuthorizedBy, ct);
          await RestaurantOrderEventWriter.AddAsync(
            conn, tx, rfc, request.SiteId, orderId,
            "InventoryDeficitAuthorized", "Authorization", "Déficit de inventario autorizado",
            "Se autorizó continuar la venta con existencias insuficientes.",
            request.SupervisorAuthorizedBy, ct);
        }
      }

      var initialStateEvent = status switch
      {
        RestaurantOrderStatuses.Sent => ("SentToKitchen", "Kitchen", "Orden enviada a cocina", "La preparación puede comenzar."),
        RestaurantOrderStatuses.Ready => ("OrderReady", "Kitchen", "Orden lista", "La orden no requiere preparación de cocina."),
        _ => ("AwaitingPayment", "Payment", "Orden pendiente de pago", $"Saldo pendiente {balanceDue:C}.")
      };
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, rfc, request.SiteId, orderId,
        initialStateEvent.Item1, initialStateEvent.Item2, initialStateEvent.Item3, initialStateEvent.Item4,
        userName, ct, $"order:{orderId}:{initialStateEvent.Item1}");
      if (paymentStatus == RestaurantPaymentStatuses.Paid)
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, request.SiteId, orderId,
          "OrderPaid", "Payment", "Orden pagada", "El saldo de la orden quedó cubierto.",
          userName, ct, $"order:{orderId}:OrderPaid");
      }

      await AddOutboxEventAsync(conn, tx, rfc, request.SiteId, "OrderCreated", orderId.ToString(), new { orderId, folio, status, paymentStatus }, ct);
      if (reservation.HasDeficit)
      {
        await AddOutboxEventAsync(conn, tx, rfc, request.SiteId, "InventoryDeficit", orderId.ToString(), new { orderId, folio }, ct);
      }
      await tx.CommitAsync(ct);
      return new RestaurantOrderResult
      {
        OrderId = orderId,
        Folio = folio,
        CustomerName = NullIfWhiteSpace(request.CustomerName),
        OperationalDate = operationalDate.ToDateTime(TimeOnly.MinValue),
        Status = status,
        PaymentStatus = paymentStatus,
        Total = total,
        BalanceDue = balanceDue
      };
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantOrderDto?> GetOrderAsync(string rfc, Guid orderId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    return await LoadOrderAsync(conn, null, normalizedRfc, orderId, ct);
  }

  public async Task<RestaurantKitchenBoardDto> GetKitchenBoardAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    const string sql =
      """
      SELECT orderInfo.Id FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.[Status] IN ('Sent', 'Preparing', 'Ready')
        AND orderInfo.CreatedAt >= DATEADD(day, -1, SYSUTCDATETIME())
        AND EXISTS
        (
          SELECT 1 FROM restaurante.OrderLine lineInfo
          WHERE lineInfo.Rfc = orderInfo.Rfc
            AND lineInfo.OrderId = orderInfo.Id
            AND lineInfo.IsCustom = 0
        )
      ORDER BY orderInfo.OperationalDate, orderInfo.Folio, orderInfo.CreatedAt, orderInfo.Id;
      """;
    var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(sql, new { Rfc = normalizedRfc, SiteId = siteId }, cancellationToken: ct))).AsList();
    var orders = new List<RestaurantOrderDto>();
    foreach (var id in ids)
    {
      var order = await LoadOrderAsync(conn, null, normalizedRfc, id, ct);
      if (order is not null)
      {
        orders.Add(order);
      }
    }
    return new RestaurantKitchenBoardDto { ServerTimeUtc = DateTime.UtcNow, Orders = orders };
  }

  public async Task<IReadOnlyList<RestaurantPublicOrderDto>> GetPublicBoardAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT orderInfo.Id, orderInfo.Folio, orderInfo.CustomerName, orderInfo.OrderType,
             diningTable.[Name] AS TableName, orderInfo.[Status]
      FROM restaurante.[Order] orderInfo
      LEFT JOIN restaurante.DiningTable diningTable ON diningTable.Rfc = orderInfo.Rfc AND diningTable.Id = orderInfo.DiningTableId
      WHERE orderInfo.Rfc = @Rfc AND orderInfo.SiteId = @SiteId
        AND orderInfo.[Status] IN ('Sent', 'Preparing', 'Ready', 'Dispatched')
        AND orderInfo.CreatedAt >= DATEADD(day, -1, SYSUTCDATETIME())
      ORDER BY CASE orderInfo.[Status] WHEN 'Ready' THEN 0 WHEN 'Dispatched' THEN 1 ELSE 2 END, orderInfo.Folio;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RestaurantPublicOrderDto>(new CommandDefinition(sql, new
    {
      Rfc = LogisticsRfc.Require(rfc),
      SiteId = siteId
    }, cancellationToken: ct))).AsList();
  }

  public async Task<IReadOnlyList<RestaurantOrderDto>> GetOperationalOrdersAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(
      """
      SELECT orderInfo.Id FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId
        AND orderInfo.CreatedAt>=DATEADD(day,-2,SYSUTCDATETIME())
        AND orderInfo.[Status]<>'Draft'
      ORDER BY orderInfo.OperationalDate, orderInfo.Folio, orderInfo.CreatedAt, orderInfo.Id;
      """, new { Rfc = normalizedRfc, SiteId = siteId }, cancellationToken: ct))).AsList();
    var result = new List<RestaurantOrderDto>();
    foreach (var id in ids)
    {
      var order = await LoadOrderAsync(conn, null, normalizedRfc, id, ct);
      if (order is not null) result.Add(order);
    }
    return result;
  }

  public async Task<IReadOnlyList<RestaurantOrderEventDto>> GetOrderEventsAsync(
    string rfc,
    Guid orderId,
    CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT eventInfo.Id,eventInfo.EventType,eventInfo.Category,eventInfo.Title,
             eventInfo.[Description],eventInfo.Actor,eventInfo.OccurredAt
      FROM restaurante.OrderEvent eventInfo
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=eventInfo.Rfc AND orderInfo.Id=eventInfo.OrderId
      WHERE eventInfo.Rfc=@Rfc AND eventInfo.OrderId=@OrderId
      ORDER BY eventInfo.OccurredAt,eventInfo.Id;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RestaurantOrderEventDto>(new CommandDefinition(
      sql,
      new { Rfc = LogisticsRfc.Require(rfc), OrderId = orderId },
      cancellationToken: ct))).AsList();
  }

  public async Task<RestaurantCommandResult> UpdateOrderStatusAsync(string rfc, Guid orderId, string status, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var next = status?.Trim() switch
    {
      "Dispatched" => "Dispatched",
      "Delivered" => "Delivered",
      "Completed" => "Completed",
      _ => throw new InvalidOperationException("Estado de entrega no válido.")
    };
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var current = await conn.QuerySingleOrDefaultAsync<OrderFulfillmentRow>(new CommandDefinition(
        "SELECT Id,SiteId,OrderType,[Status],PaymentStatus FROM restaurante.[Order] WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;",
        new { Rfc = normalizedRfc, Id = orderId }, tx, cancellationToken: ct));
      if (current is null) { await tx.RollbackAsync(ct); return RestaurantCommandResult.Fail("La orden no pertenece al RFC activo."); }
      var allowed = (current.Status, next, current.OrderType) switch
      {
        ("Ready", "Dispatched", "Delivery") => true,
        ("Ready", "Delivered", "Pickup") => true,
        ("Ready", "Delivered", "Table") => true,
        ("Dispatched", "Delivered", "Delivery") => true,
        ("Delivered", "Completed", _) => current.PaymentStatus == "Paid",
        _ => false
      };
      if (!allowed) { await tx.RollbackAsync(ct); return RestaurantCommandResult.Fail($"No se puede pasar de {current.Status} a {next} para esta modalidad o pago."); }
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.[Order] SET [Status]=@Status,
          CompletedAt=CASE WHEN @Status='Completed' THEN SYSUTCDATETIME() ELSE CompletedAt END
        WHERE Rfc=@Rfc AND Id=@Id;
        UPDATE restaurante.OrderLine SET [Status]='Delivered',DeliveredAt=COALESCE(DeliveredAt,SYSUTCDATETIME())
        WHERE Rfc=@Rfc AND OrderId=@Id AND @Status IN ('Delivered','Completed') AND [Status]='Ready';
        UPDATE restaurante.Delivery SET [Status]=@Status,
          DispatchedAt=CASE WHEN @Status='Dispatched' THEN COALESCE(DispatchedAt,SYSUTCDATETIME()) ELSE DispatchedAt END,
          DeliveredAt=CASE WHEN @Status IN ('Delivered','Completed') THEN COALESCE(DeliveredAt,SYSUTCDATETIME()) ELSE DeliveredAt END
        WHERE Rfc=@Rfc AND OrderId=@Id;
        """, new { Rfc = normalizedRfc, Id = orderId, Status = next }, tx, cancellationToken: ct));
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, normalizedRfc, current.SiteId, orderId,
        next switch
        {
          "Dispatched" => "OrderDispatched",
          "Delivered" => "OrderDelivered",
          _ => "OrderCompleted"
        },
        next == "Completed" ? "Order" : "Delivery",
        next switch
        {
          "Dispatched" => "Orden despachada",
          "Delivered" => "Entrega confirmada",
          _ => "Orden completada"
        },
        $"{OrderStatusLabel(current.Status)} → {OrderStatusLabel(next)}",
        userName, ct);
      await AddOutboxEventAsync(conn, tx, normalizedRfc, current.SiteId, "OrderStatusChanged", orderId.ToString(), new { orderId, status = next, userName }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El estado de entrega fue actualizado.");
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  public async Task<RestaurantCommandResult> UpdateLineStatusAsync(string rfc, long lineId, string status, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var normalizedStatus = status?.Trim() switch
    {
      "Preparing" => "Preparing",
      "Ready" => "Ready",
      "Delivered" => "Delivered",
      _ => throw new InvalidOperationException("Transición de cocina no válida.")
    };
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var line = await conn.QuerySingleOrDefaultAsync<LineIdentityRow>(new CommandDefinition(
        """
        SELECT lineInfo.Id, lineInfo.OrderId, lineInfo.ProductNameSnapshot, lineInfo.IsCustom,
               lineInfo.[Status], orderInfo.SiteId, orderInfo.InventoryReservationId
        FROM restaurante.OrderLine lineInfo WITH (UPDLOCK, HOLDLOCK)
        JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc = lineInfo.Rfc AND orderInfo.Id = lineInfo.OrderId
        WHERE lineInfo.Rfc = @Rfc AND lineInfo.Id = @LineId;
        """, new { Rfc = normalizedRfc, LineId = lineId }, tx, cancellationToken: ct));
      if (line is null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La partida no existe en el RFC seleccionado.");
      }
      if (line.IsCustom)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Un cargo personalizado no requiere transición de cocina.");
      }
      if (!IsLineTransitionAllowed(line.Status, normalizedStatus))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail($"No se puede cambiar la partida de {line.Status} a {normalizedStatus}.");
      }

      var inventoryConsumed = false;
      if (normalizedStatus == "Preparing" && line.InventoryReservationId.HasValue)
      {
        inventoryConsumed = await ConsumeReservationAsync(
          conn, tx, normalizedRfc, line.InventoryReservationId.Value, userName, ct);
      }
      var affected = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.OrderLine
        SET [Status] = @Status,
            StartedAt = CASE WHEN @Status = 'Preparing' AND StartedAt IS NULL THEN SYSUTCDATETIME() ELSE StartedAt END,
            ReadyAt = CASE WHEN @Status = 'Ready' AND ReadyAt IS NULL THEN SYSUTCDATETIME() ELSE ReadyAt END,
            DeliveredAt = CASE WHEN @Status = 'Delivered' AND DeliveredAt IS NULL THEN SYSUTCDATETIME() ELSE DeliveredAt END
        WHERE Rfc = @Rfc AND Id = @LineId AND [Status] = @PreviousStatus;
        """, new { Rfc = normalizedRfc, LineId = lineId, Status = normalizedStatus, PreviousStatus = line.Status }, tx, cancellationToken: ct));
      if (affected != 1)
      {
        throw new InvalidOperationException("La partida cambió mientras se procesaba la acción; recargue el tablero.");
      }
      var orderTransition = await RefreshOrderStatusAsync(conn, tx, normalizedRfc, line.OrderId, ct);
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, normalizedRfc, line.SiteId, line.OrderId,
        normalizedStatus switch
        {
          "Preparing" => "LinePreparing",
          "Ready" => "LineReady",
          _ => "LineDelivered"
        },
        "Kitchen",
        normalizedStatus switch
        {
          "Preparing" => "Preparación iniciada",
          "Ready" => "Partida lista",
          _ => "Partida entregada"
        },
        line.ProductNameSnapshot,
        userName, ct);
      if (inventoryConsumed)
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, normalizedRfc, line.SiteId, line.OrderId,
          "InventoryConsumed", "Inventory", "Inventario consumido",
          "La reserva de insumos se descontó al iniciar la preparación.",
          userName, ct, $"reservation:{line.InventoryReservationId!.Value}:consumed");
      }
      if (orderTransition is not null)
      {
        await AddOrderTransitionEventAsync(
          conn, tx, normalizedRfc, line.SiteId, line.OrderId, orderTransition, userName, ct);
      }
      await AddOutboxEventAsync(conn, tx, normalizedRfc, line.SiteId, "OrderLineStatusChanged", line.OrderId.ToString(), new { line.OrderId, lineId, status = normalizedStatus }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El estado de la partida fue actualizado.", lineId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> RevertLineStatusAsync(string rfc, long lineId, string userName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var line = await conn.QuerySingleOrDefaultAsync<LineIdentityRow>(new CommandDefinition(
        """
        SELECT lineInfo.Id,lineInfo.OrderId,lineInfo.ProductNameSnapshot,lineInfo.IsCustom,
               lineInfo.[Status],orderInfo.SiteId,orderInfo.InventoryReservationId
        FROM restaurante.OrderLine lineInfo WITH (UPDLOCK,HOLDLOCK)
        JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=lineInfo.Rfc AND orderInfo.Id=lineInfo.OrderId
        WHERE lineInfo.Rfc=@Rfc AND lineInfo.Id=@LineId;
        """, new { Rfc = normalizedRfc, LineId = lineId }, tx, cancellationToken: ct));
      if (line is null || line.IsCustom || line.Status != "Ready")
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Sólo una partida marcada lista puede regresar a preparación.");
      }
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE restaurante.OrderLine SET [Status]='Preparing',ReadyAt=NULL WHERE Rfc=@Rfc AND Id=@LineId AND [Status]='Ready';",
        new { Rfc = normalizedRfc, LineId = lineId }, tx, cancellationToken: ct));
      var orderTransition = await RefreshOrderStatusAsync(conn, tx, normalizedRfc, line.OrderId, ct);
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, normalizedRfc, line.SiteId, line.OrderId,
        "LineReopened", "Kitchen", "Partida regresada a preparación",
        line.ProductNameSnapshot, userName, ct);
      if (orderTransition is not null)
      {
        await AddOrderTransitionEventAsync(
          conn, tx, normalizedRfc, line.SiteId, line.OrderId, orderTransition, userName, ct);
      }
      await AddOutboxEventAsync(conn, tx, normalizedRfc, line.SiteId, "OrderLineStatusReverted", line.OrderId.ToString(), new { line.OrderId, lineId, status = "Preparing", userName }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La partida regresó a preparación.", lineId);
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  public async Task<RestaurantCommandResult> SetOrderPriorityAsync(
    string rfc,
    Guid orderId,
    byte priority,
    string reason,
    string supervisorUserName,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (priority > 1 || (priority > 0 && string.IsNullOrWhiteSpace(reason)) || string.IsNullOrWhiteSpace(supervisorUserName))
      return RestaurantCommandResult.Fail("La prioridad requiere motivo y supervisor.");
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var siteId = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
        "SELECT SiteId FROM restaurante.[Order] WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@OrderId AND [Status] IN ('Sent','Preparing','Ready');",
        new { Rfc = normalizedRfc, OrderId = orderId }, tx, cancellationToken: ct));
      if (!siteId.HasValue)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La orden no está activa o no pertenece al RFC seleccionado.");
      }
      var normalizedReason = priority == 0 ? "Prioridad retirada" : reason.Trim();
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.[Order]
        SET Priority=@Priority,PriorityReason=@Reason,PrioritizedBy=@Supervisor,PrioritizedAt=SYSUTCDATETIME()
        WHERE Rfc=@Rfc AND Id=@OrderId;
        INSERT INTO restaurante.SupervisorAuthorization (Rfc,SiteId,ActionType,AggregateId,Reason,RequestedBy,AuthorizedBy)
        VALUES (@Rfc,@SiteId,'KitchenPriority',CONVERT(varchar(36),@OrderId),@Reason,@Supervisor,@Supervisor);
        """, new { Rfc = normalizedRfc, OrderId = orderId, Priority = priority, Reason = normalizedReason, Supervisor = supervisorUserName.Trim(), SiteId = siteId.Value }, tx, cancellationToken: ct));
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, normalizedRfc, siteId.Value, orderId,
        priority > 0 ? "PrioritySet" : "PriorityRemoved",
        "Authorization",
        priority > 0 ? "Orden priorizada" : "Prioridad retirada",
        normalizedReason, supervisorUserName, ct);
      await AddOutboxEventAsync(conn, tx, normalizedRfc, siteId.Value, "OrderPriorityChanged", orderId.ToString(), new { orderId, priority, reason = normalizedReason }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok(priority > 0 ? "Orden priorizada." : "Prioridad retirada.");
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  public async Task<RestaurantCommandResult> CancelOrderAsync(string rfc, Guid orderId, string reason, string supervisorUserName, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    if (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(supervisorUserName))
    {
      return RestaurantCommandResult.Fail("La cancelación requiere motivo y supervisor.");
    }
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var order = await conn.QuerySingleOrDefaultAsync<CancelOrderRow>(new CommandDefinition(
        """
        SELECT Id, SiteId, [Status], InventoryReservationId
        FROM restaurante.[Order] WITH (UPDLOCK, HOLDLOCK)
        WHERE Rfc = @Rfc AND Id = @OrderId;
        """, new { Rfc = normalizedRfc, OrderId = orderId }, tx, cancellationToken: ct));
      if (order is null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La orden no existe en el RFC seleccionado.");
      }
      if (order.Status is "Completed" or "Cancelled")
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La orden ya está cerrada.");
      }
      var inventoryReleased = false;
      if (order.InventoryReservationId.HasValue)
      {
        inventoryReleased = await ReleaseReservationAsync(
          conn, tx, normalizedRfc, order.InventoryReservationId.Value, ct);
      }
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.[Order]
        SET [Status] = 'Cancelled', CancelledAt = SYSUTCDATETIME(), CancelledBy = @Supervisor,
            CancellationReason = @Reason
        WHERE Rfc = @Rfc AND Id = @OrderId;
        UPDATE restaurante.OrderLine
        SET [Status] = 'Cancelled', CancelledAt = SYSUTCDATETIME()
        WHERE Rfc = @Rfc AND OrderId = @OrderId AND [Status] <> 'Delivered';
        INSERT INTO restaurante.SupervisorAuthorization
          (Rfc, SiteId, ActionType, AggregateId, Reason, RequestedBy, AuthorizedBy)
        VALUES
          (@Rfc, @SiteId, 'CancelOrder', CONVERT(varchar(36), @OrderId), @Reason, @Supervisor, @Supervisor);
        """, new { Rfc = normalizedRfc, OrderId = orderId, SiteId = order.SiteId, Supervisor = supervisorUserName, Reason = reason.Trim() }, tx, cancellationToken: ct));
      if (inventoryReleased)
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, normalizedRfc, order.SiteId, orderId,
          "InventoryReleased", "Inventory", "Inventario liberado",
          "Los insumos apartados regresaron a disponibilidad por la cancelación.",
          supervisorUserName, ct, $"reservation:{order.InventoryReservationId!.Value}:released");
      }
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, normalizedRfc, order.SiteId, orderId,
        "OrderCancelled", "Order", "Orden cancelada",
        reason, supervisorUserName, ct);
      await AddOutboxEventAsync(conn, tx, normalizedRfc, order.SiteId, "OrderCancelled", orderId.ToString(), new { orderId, reason }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La orden fue cancelada. Los cobros existentes requieren reembolso supervisado por separado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<IReadOnlyList<RestaurantPaymentDto>> GetPaymentsAsync(string rfc, Guid orderId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT paymentInfo.Id,paymentInfo.PaymentMethod,paymentInfo.Amount,paymentInfo.TipAmount,
             paymentInfo.RefundedAmount,paymentInfo.[Status],paymentInfo.PaidAt
      FROM restaurante.Payment paymentInfo
      JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
      WHERE paymentInfo.Rfc=@Rfc AND paymentInfo.OrderId=@OrderId
      ORDER BY paymentInfo.PaidAt,paymentInfo.Id;
      """;
    using var conn = CreateConnection();
    return (await conn.QueryAsync<RestaurantPaymentDto>(new CommandDefinition(
      sql, new { Rfc = LogisticsRfc.Require(rfc), OrderId = orderId }, cancellationToken: ct))).AsList();
  }

  public async Task<RestaurantCommandResult> AddPaymentAsync(
    RestaurantAdditionalPaymentRequest request,
    string requestedBy,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
      return RestaurantCommandResult.Fail("El cargo requiere importe y clave de idempotencia.");
    }
    if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.SupervisorUserName))
    {
      return RestaurantCommandResult.Fail("El cargo posterior requiere motivo y autorización de supervisor.");
    }
    var paymentMethod = NormalizePaymentMethod(request.PaymentMethod);
    if (paymentMethod == "Cash" && !request.CashShiftId.HasValue)
    {
      return RestaurantCommandResult.Fail("Los cargos en efectivo requieren un turno de caja abierto.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      var duplicate = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
        "SELECT Id FROM restaurante.Payment WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND IdempotencyKey=@Key;",
        new { Rfc = rfc, Key = request.IdempotencyKey.Trim() }, tx, cancellationToken: ct));
      if (duplicate.HasValue)
      {
        await tx.CommitAsync(ct);
        return RestaurantCommandResult.Ok("El cargo ya había sido registrado.");
      }

      var order = await conn.QuerySingleOrDefaultAsync<PaymentOrderRow>(new CommandDefinition(
        """
        SELECT Id,SiteId,CashRegisterId,CashShiftId,[Status],PaymentStatus,Total,BalanceDue
        FROM restaurante.[Order] WITH (UPDLOCK,HOLDLOCK)
        WHERE Rfc=@Rfc AND Id=@OrderId;
        """, new { Rfc = rfc, request.OrderId }, tx, cancellationToken: ct));
      if (order is null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La orden no pertenece al RFC seleccionado.");
      }
      if (order.Status is "Cancelled" or "Completed")
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("No se pueden agregar cargos a una orden cerrada o cancelada.");
      }
      if (request.Amount > order.BalanceDue + 0.01m)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El cargo no puede exceder el saldo pendiente.");
      }
      if (request.CashShiftId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS
        (
          SELECT 1 FROM restaurante.CashShift shiftInfo
          WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.Id=@CashShiftId AND shiftInfo.SiteId=@SiteId
            AND shiftInfo.[Status]='Open'
            AND (@CashRegisterId IS NULL OR shiftInfo.CashRegisterId=@CashRegisterId)
        ) THEN 1 ELSE 0 END AS bit);
        """, new { Rfc = rfc, request.CashShiftId, order.SiteId, order.CashRegisterId }, tx, cancellationToken: ct)))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El turno de caja no está abierto para esta orden.");
      }

      var paymentId = Guid.NewGuid();
      var newBalance = Math.Max(0, decimal.Round(order.BalanceDue - request.Amount, 2, MidpointRounding.AwayFromZero));
      var newPaymentStatus = newBalance <= 0.01m ? RestaurantPaymentStatuses.Paid : RestaurantPaymentStatuses.Partial;
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.Payment
          (Id,Rfc,OrderId,PaymentMethod,Amount,TipAmount,[Status],ExternalReference,IdempotencyKey,ReceivedBy)
        VALUES
          (@PaymentId,@Rfc,@OrderId,@PaymentMethod,@Amount,@TipAmount,'Paid',@ExternalReference,@IdempotencyKey,@RequestedBy);

        UPDATE restaurante.[Order]
        SET BalanceDue=@BalanceDue,PaymentStatus=@PaymentStatus,TipTotal=TipTotal+@TipAmount,
            PaidAt=CASE WHEN @PaymentStatus='Paid' THEN COALESCE(PaidAt,SYSUTCDATETIME()) ELSE PaidAt END,
            [Status]=CASE WHEN [Status]='AwaitingPayment' AND @PaymentStatus='Paid' THEN 'Sent' ELSE [Status] END,
            SentToKitchenAt=CASE WHEN [Status]='AwaitingPayment' AND @PaymentStatus='Paid' THEN COALESCE(SentToKitchenAt,SYSUTCDATETIME()) ELSE SentToKitchenAt END
        WHERE Rfc=@Rfc AND Id=@OrderId;

        INSERT INTO restaurante.SupervisorAuthorization
          (Rfc,SiteId,ActionType,AggregateId,Reason,RequestedBy,AuthorizedBy)
        VALUES
          (@Rfc,@SiteId,'AdditionalPayment',CONVERT(varchar(36),@OrderId),@Reason,@RequestedBy,@Supervisor);
        """, new
        {
          PaymentId = paymentId,
          Rfc = rfc,
          request.OrderId,
          PaymentMethod = paymentMethod,
          request.Amount,
          request.TipAmount,
          ExternalReference = NullIfWhiteSpace(request.ExternalReference),
          IdempotencyKey = request.IdempotencyKey.Trim(),
          RequestedBy = requestedBy,
          BalanceDue = newBalance,
          PaymentStatus = newPaymentStatus,
          order.SiteId,
          Reason = request.Reason.Trim(),
          Supervisor = request.SupervisorUserName.Trim()
        }, tx, cancellationToken: ct));
      if (request.CashShiftId.HasValue)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO restaurante.CashMovement
            (Rfc,CashShiftId,MovementType,PaymentMethod,Amount,OrderId,Reason,CreatedBy)
          VALUES
            (@Rfc,@CashShiftId,'Sale',@PaymentMethod,@Amount,@OrderId,@Reason,@RequestedBy);
          """, new
          {
            Rfc = rfc,
            request.CashShiftId,
            PaymentMethod = paymentMethod,
            Amount = request.Amount + request.TipAmount,
            request.OrderId,
            Reason = request.Reason.Trim(),
            RequestedBy = requestedBy
          }, tx, cancellationToken: ct));
      }
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, rfc, order.SiteId, request.OrderId,
        "PaymentReceived", "Payment", "Pago adicional recibido",
        $"{PaymentMethodLabel(paymentMethod)} · {request.Amount:C}" +
        (request.TipAmount > 0 ? $" · Propina {request.TipAmount:C}" : string.Empty) +
         $" · {request.Reason.Trim()} · Autorizó: {request.SupervisorUserName.Trim()}",
        requestedBy, ct, $"payment:{paymentId}");
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, rfc, order.SiteId, request.OrderId,
        "AdditionalPaymentAuthorized", "Authorization", "Cargo adicional autorizado",
        $"{request.Reason.Trim()} · Autorizó: {request.SupervisorUserName.Trim()}",
        requestedBy, ct, $"payment:{paymentId}:authorization");
      if (newPaymentStatus == RestaurantPaymentStatuses.Paid &&
          order.PaymentStatus != RestaurantPaymentStatuses.Paid)
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, order.SiteId, request.OrderId,
          "OrderPaid", "Payment", "Orden pagada", "El saldo de la orden quedó cubierto.",
          requestedBy, ct, $"order:{request.OrderId}:OrderPaid");
      }
      if (newPaymentStatus == RestaurantPaymentStatuses.Paid &&
          order.Status == RestaurantOrderStatuses.AwaitingPayment)
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, order.SiteId, request.OrderId,
          "SentToKitchen", "Kitchen", "Orden enviada a cocina",
          "El pago quedó cubierto y la preparación puede comenzar.",
          requestedBy, ct, $"order:{request.OrderId}:SentToKitchen");
      }
      await AddOutboxEventAsync(conn, tx, rfc, order.SiteId, "OrderPaymentAdded", request.OrderId.ToString(), new { request.OrderId, paymentId, request.Amount, paymentStatus = newPaymentStatus }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El cargo adicional fue registrado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> RefundPaymentAsync(
    RestaurantPaymentRefundRequest request,
    string requestedBy,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.Amount <= 0 || string.IsNullOrWhiteSpace(request.IdempotencyKey))
    {
      return RestaurantCommandResult.Fail("El reembolso requiere importe y clave de idempotencia.");
    }
    if (string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.SupervisorUserName))
    {
      return RestaurantCommandResult.Fail("El reembolso requiere motivo y autorización de supervisor.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      if (await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM restaurante.PaymentRefund WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND IdempotencyKey=@Key) THEN 1 ELSE 0 END AS bit);",
        new { Rfc = rfc, Key = request.IdempotencyKey.Trim() }, tx, cancellationToken: ct)))
      {
        await tx.CommitAsync(ct);
        return RestaurantCommandResult.Ok("El reembolso ya había sido registrado.");
      }

      var payment = await conn.QuerySingleOrDefaultAsync<RefundPaymentRow>(new CommandDefinition(
        """
        SELECT paymentInfo.Id,paymentInfo.OrderId,paymentInfo.PaymentMethod,paymentInfo.Amount,paymentInfo.RefundedAmount,
               orderInfo.SiteId,orderInfo.CashRegisterId,orderInfo.Total,orderInfo.[Status] AS OrderStatus
        FROM restaurante.Payment paymentInfo WITH (UPDLOCK,HOLDLOCK)
        JOIN restaurante.[Order] orderInfo WITH (UPDLOCK,HOLDLOCK)
          ON orderInfo.Rfc=paymentInfo.Rfc AND orderInfo.Id=paymentInfo.OrderId
        WHERE paymentInfo.Rfc=@Rfc AND paymentInfo.Id=@PaymentId;
        """, new { Rfc = rfc, request.PaymentId }, tx, cancellationToken: ct));
      if (payment is null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El pago no pertenece al RFC seleccionado.");
      }
      if (request.Amount > payment.Amount - payment.RefundedAmount + 0.01m)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El reembolso excede el saldo reembolsable del pago.");
      }

      Guid? cashShiftId = null;
      if (payment.PaymentMethod == "Cash")
      {
        cashShiftId = await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
          """
          SELECT TOP (1) shiftInfo.Id
          FROM restaurante.CashShift shiftInfo WITH (UPDLOCK,HOLDLOCK)
          WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.SiteId=@SiteId AND shiftInfo.[Status]='Open'
            AND (@CashRegisterId IS NULL OR shiftInfo.CashRegisterId=@CashRegisterId)
          ORDER BY shiftInfo.OpenedAt DESC;
          """, new { Rfc = rfc, payment.SiteId, payment.CashRegisterId }, tx, cancellationToken: ct));
        if (!cashShiftId.HasValue)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("Abra un turno en la caja original antes de reembolsar efectivo.");
        }
      }

      var refundId = Guid.NewGuid();
      var refundedAmount = decimal.Round(payment.RefundedAmount + request.Amount, 2, MidpointRounding.AwayFromZero);
      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.PaymentRefund
          (Id,Rfc,PaymentId,Amount,Reason,IdempotencyKey,RequestedBy,AuthorizedBy)
        VALUES
          (@RefundId,@Rfc,@PaymentId,@Amount,@Reason,@IdempotencyKey,@RequestedBy,@Supervisor);
        UPDATE restaurante.Payment
        SET RefundedAmount=@RefundedAmount,
            [Status]=CASE WHEN @RefundedAmount>=Amount THEN 'Refunded' ELSE 'PartiallyRefunded' END
        WHERE Rfc=@Rfc AND Id=@PaymentId;
        """, new
        {
          RefundId = refundId,
          Rfc = rfc,
          request.PaymentId,
          request.Amount,
          Reason = request.Reason.Trim(),
          IdempotencyKey = request.IdempotencyKey.Trim(),
          RequestedBy = requestedBy,
          Supervisor = request.SupervisorUserName.Trim(),
          RefundedAmount = refundedAmount
        }, tx, cancellationToken: ct));

      var netPaid = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
        "SELECT CAST(ISNULL(SUM(Amount-RefundedAmount),0) AS decimal(18,2)) FROM restaurante.Payment WHERE Rfc=@Rfc AND OrderId=@OrderId;",
        new { Rfc = rfc, payment.OrderId }, tx, cancellationToken: ct));
      var balanceDue = Math.Max(0, decimal.Round(payment.Total - netPaid, 2, MidpointRounding.AwayFromZero));
      var paymentStatus = netPaid <= 0.01m ? RestaurantPaymentStatuses.Refunded : RestaurantPaymentStatuses.PartiallyRefunded;
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.[Order] SET BalanceDue=@BalanceDue,PaymentStatus=@PaymentStatus
        WHERE Rfc=@Rfc AND Id=@OrderId;
        INSERT INTO restaurante.SupervisorAuthorization
          (Rfc,SiteId,ActionType,AggregateId,Reason,RequestedBy,AuthorizedBy)
        VALUES
          (@Rfc,@SiteId,'PaymentRefund',CONVERT(varchar(36),@OrderId),@Reason,@RequestedBy,@Supervisor);
        """, new
        {
          Rfc = rfc,
          payment.OrderId,
          payment.SiteId,
          BalanceDue = balanceDue,
          PaymentStatus = paymentStatus,
          Reason = request.Reason.Trim(),
          RequestedBy = requestedBy,
          Supervisor = request.SupervisorUserName.Trim()
        }, tx, cancellationToken: ct));
      if (cashShiftId.HasValue)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT INTO restaurante.CashMovement
            (Rfc,CashShiftId,MovementType,PaymentMethod,Amount,OrderId,Reason,CreatedBy)
          VALUES
            (@Rfc,@CashShiftId,'Refund','Cash',@Amount,@OrderId,@Reason,@RequestedBy);
          """, new
          {
            Rfc = rfc,
            CashShiftId = cashShiftId,
            request.Amount,
            payment.OrderId,
            Reason = request.Reason.Trim(),
            RequestedBy = requestedBy
          }, tx, cancellationToken: ct));
      }
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, rfc, payment.SiteId, payment.OrderId,
        "PaymentRefunded", "Payment", "Pago reembolsado",
        $"{request.Amount:C} · {request.Reason.Trim()} · Autorizó: {request.SupervisorUserName.Trim()}",
        requestedBy, ct, $"refund:{refundId}");
      await RestaurantOrderEventWriter.AddAsync(
        conn, tx, rfc, payment.SiteId, payment.OrderId,
        "RefundAuthorized", "Authorization", "Reembolso autorizado",
        $"{request.Reason.Trim()} · Autorizó: {request.SupervisorUserName.Trim()}",
        requestedBy, ct, $"refund:{refundId}:authorization");
      await AddOutboxEventAsync(conn, tx, rfc, payment.SiteId, "OrderPaymentRefunded", payment.OrderId.ToString(), new { payment.OrderId, request.PaymentId, refundId, request.Amount, paymentStatus }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El reembolso fue registrado y auditado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static async Task ValidateOperationalReferencesAsync(DbConnection conn, DbTransaction tx, string rfc, RestaurantOrderCreateRequest request, CancellationToken ct)
  {
    var orderType = NormalizeOrderType(request.OrderType);
    if (orderType == "Table" && !request.DiningTableId.HasValue)
    {
      throw new InvalidOperationException("La modalidad mesa requiere seleccionar una mesa.");
    }
    if (orderType == "Delivery" && string.IsNullOrWhiteSpace(request.DeliveryAddress))
    {
      throw new InvalidOperationException("La modalidad domicilio requiere dirección.");
    }
    if (request.DiningTableId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM restaurante.DiningTable WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id AND IsActive=1) THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, request.SiteId, Id = request.DiningTableId }, tx, cancellationToken: ct)))
    {
      throw new InvalidOperationException("La mesa no pertenece a la sede y RFC seleccionados.");
    }
    if (request.CashRegisterId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM restaurante.CashRegister WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id AND IsActive=1) THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, request.SiteId, Id = request.CashRegisterId }, tx, cancellationToken: ct)))
    {
      throw new InvalidOperationException("La caja no pertenece a la sede y RFC seleccionados.");
    }
    if (request.CashShiftId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM restaurante.CashShift WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id AND [Status]='Open') THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, request.SiteId, Id = request.CashShiftId }, tx, cancellationToken: ct)))
    {
      throw new InvalidOperationException("El turno de caja no está abierto en la sede y RFC seleccionados.");
    }
    if (request.ExternalProviderId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM restaurante.ExternalProvider WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id AND IsActive=1) THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, request.SiteId, Id = request.ExternalProviderId }, tx, cancellationToken: ct)))
    {
      throw new InvalidOperationException("El proveedor externo no pertenece a la sede y RFC seleccionados.");
    }
  }

  private static void ValidateModifiers(RestaurantOrderCreateRequest request, IReadOnlyList<ModifierRow> modifierRows)
  {
    foreach (var line in request.Lines.Where(line => !line.IsCustom))
    {
      if (line.ModifierOptionIds.Count != line.ModifierOptionIds.Distinct().Count())
      {
        throw new InvalidOperationException("No se puede repetir el mismo modificador en una partida.");
      }
      foreach (var optionId in line.ModifierOptionIds)
      {
        if (!modifierRows.Any(row => row.ProductId == line.ProductId!.Value && row.Id == optionId))
        {
          throw new InvalidOperationException("Un modificador no corresponde al producto o al RFC seleccionado.");
        }
      }
      foreach (var group in modifierRows.Where(row => row.ProductId == line.ProductId!.Value).GroupBy(row => row.ModifierGroupId))
      {
        var selected = group.Count(row => line.ModifierOptionIds.Contains(row.Id));
        var definition = group.First();
        if (selected < definition.MinSelections || selected > definition.MaxSelections)
        {
          throw new InvalidOperationException($"El grupo {definition.GroupName} requiere entre {definition.MinSelections} y {definition.MaxSelections} opciones.");
        }
      }
    }
  }

  private static async Task<Dictionary<int, decimal>> BuildRequirementsAsync(DbConnection conn, DbTransaction tx, string rfc,
    IReadOnlyList<PricedLine> lines, IReadOnlyList<ModifierRow> modifiers, CancellationToken ct)
  {
    var requirements = new Dictionary<int, decimal>();
    foreach (var line in lines)
    {
      if (line.IsCustom || line.Product is null)
      {
        continue;
      }
      if (line.Product.FulfillmentMode == "MakeToOrder")
      {
        await ExpandMaterialAsync(conn, tx, rfc, line.Product.MaterialId, line.Request.Quantity, requirements, new HashSet<int>(), 0, ct);
      }
      else
      {
        AddRequirement(requirements, line.Product.MaterialId, line.Request.Quantity);
      }
      if (line.Modifiers.Count > 0)
      {
        var deltas = (await conn.QueryAsync<ModifierDeltaRow>(new CommandDefinition(
          """
          SELECT delta.MaterialId, delta.QuantityDelta,
                 COALESCE(materialConversion.Factor, globalConversion.Factor, CASE WHEN delta.UnitId = material.BaseUnitId THEN 1 END) AS Factor
          FROM restaurante.ModifierIngredientDelta delta
          JOIN logistica.Material material ON material.Rfc = delta.Rfc AND material.Id = delta.MaterialId
          OUTER APPLY (SELECT TOP (1) Factor FROM logistica.MaterialUnitConversion conversionInfo
                       WHERE conversionInfo.Rfc=delta.Rfc AND conversionInfo.MaterialId=delta.MaterialId
                         AND conversionInfo.FromUnitId=delta.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1) materialConversion
          OUTER APPLY (SELECT TOP (1) Factor FROM logistica.UnitConversion conversionInfo
                       WHERE conversionInfo.FromUnitId=delta.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1) globalConversion
          WHERE delta.Rfc = @Rfc AND delta.ModifierOptionId IN @OptionIds;
          """, new { Rfc = rfc, OptionIds = line.Modifiers.Select(item => item.Id).ToArray() }, tx, cancellationToken: ct))).AsList();
        if (deltas.Any(delta => !delta.Factor.HasValue))
        {
          throw new InvalidOperationException("Falta una conversión para los ingredientes de un modificador.");
        }
        foreach (var delta in deltas)
        {
          AddRequirement(requirements, delta.MaterialId, delta.QuantityDelta * delta.Factor!.Value * line.Request.Quantity);
        }
      }
    }
    return requirements.Where(item => item.Value > 0).ToDictionary(item => item.Key, item => item.Value);
  }

  private static async Task ExpandMaterialAsync(DbConnection conn, DbTransaction tx, string rfc, int materialId, decimal multiplier,
    IDictionary<int, decimal> requirements, ISet<int> path, int depth, CancellationToken ct)
  {
    if (depth >= 32 || !path.Add(materialId))
    {
      throw new InvalidOperationException("El BOM contiene un ciclo o excede 32 niveles.");
    }
    var components = (await conn.QueryAsync<BomRequirementRow>(new CommandDefinition(
      """
      SELECT component.ComponentMaterialId AS MaterialId,
             component.Quantity * (1 + component.ExpectedWastePercent / 100.0)
               * COALESCE(materialConversion.Factor, globalConversion.Factor, CASE WHEN component.UnitId = material.BaseUnitId THEN 1 END)
               / NULLIF(versionInfo.YieldQuantity, 0) AS QuantityPerYield,
             material.FulfillmentMode
      FROM logistica.BomHeader headerInfo
      JOIN logistica.BomVersion versionInfo ON versionInfo.Rfc = headerInfo.Rfc AND versionInfo.BomHeaderId = headerInfo.Id AND versionInfo.[Status] = 'Active'
      JOIN logistica.BomComponent component ON component.Rfc = versionInfo.Rfc AND component.BomVersionId = versionInfo.Id
      JOIN logistica.Material material ON material.Rfc = component.Rfc AND material.Id = component.ComponentMaterialId
      OUTER APPLY (SELECT TOP (1) Factor FROM logistica.MaterialUnitConversion conversionInfo
                   WHERE conversionInfo.Rfc=component.Rfc AND conversionInfo.MaterialId=component.ComponentMaterialId
                     AND conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1) materialConversion
      OUTER APPLY (SELECT TOP (1) Factor FROM logistica.UnitConversion conversionInfo
                   WHERE conversionInfo.FromUnitId=component.UnitId AND conversionInfo.ToUnitId=material.BaseUnitId AND conversionInfo.IsActive=1) globalConversion
      WHERE headerInfo.Rfc = @Rfc AND headerInfo.ProductMaterialId = @MaterialId;
      """, new { Rfc = rfc, MaterialId = materialId }, tx, cancellationToken: ct))).AsList();
    if (components.Count == 0)
    {
      throw new InvalidOperationException($"El producto {materialId} no tiene un BOM activo.");
    }
    foreach (var component in components)
    {
      if (!component.QuantityPerYield.HasValue)
      {
        throw new InvalidOperationException($"Falta una conversión de unidad para el material {component.MaterialId}.");
      }
      var required = component.QuantityPerYield.Value * multiplier;
      if (component.FulfillmentMode == "MakeToOrder")
      {
        await ExpandMaterialAsync(conn, tx, rfc, component.MaterialId, required, requirements, new HashSet<int>(path), depth + 1, ct);
      }
      else
      {
        AddRequirement(requirements, component.MaterialId, required);
      }
    }
  }

  private static async Task<ReservationResult> ReserveInventoryAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId,
    Guid orderId, string orderIdempotencyKey, IReadOnlyDictionary<int, decimal> requirements, bool allowDeficit, string userName, CancellationToken ct)
  {
    var reservationId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      INSERT INTO logistica.InventoryReservation
        (Rfc, SiteId, ReferenceType, ReferenceId, IdempotencyKey, [Status], CreatedBy)
      VALUES
        (@Rfc, @SiteId, 'RestaurantOrder', @OrderId, @IdempotencyKey, 'Reserved', @CreatedBy);
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { Rfc = rfc, SiteId = siteId, OrderId = orderId, IdempotencyKey = $"ORDER:{orderIdempotencyKey}", CreatedBy = userName }, tx, cancellationToken: ct));
    var hasDeficit = false;
    foreach (var requirement in requirements)
    {
      var needed = decimal.Round(requirement.Value, 4, MidpointRounding.AwayFromZero);
      var remaining = needed;
      var material = await conn.QuerySingleAsync<MaterialInventoryRow>(new CommandDefinition(
        "SELECT Id, TrackLots FROM logistica.Material WHERE Rfc=@Rfc AND Id=@MaterialId;",
        new { Rfc = rfc, MaterialId = requirement.Key }, tx, cancellationToken: ct));
      if (material.TrackLots)
      {
        var lots = (await conn.QueryAsync<LotAvailabilityRow>(new CommandDefinition(
          """
          SELECT lotBalance.MaterialLotId, lotBalance.LocationId,
                 lotBalance.Quantity - lotBalance.ReservedQuantity AS AvailableQuantity,
                 lot.UnitCost
          FROM logistica.LotBalance lotBalance WITH (UPDLOCK, HOLDLOCK)
          JOIN logistica.MaterialLot lot ON lot.Rfc = lotBalance.Rfc AND lot.Id = lotBalance.MaterialLotId
          LEFT JOIN restaurante.SiteLocationPriority priorityInfo
            ON priorityInfo.Rfc = lotBalance.Rfc AND priorityInfo.SiteId = @SiteId AND priorityInfo.LocationId = lotBalance.LocationId
          WHERE lotBalance.Rfc=@Rfc AND lotBalance.MaterialId=@MaterialId
            AND lotBalance.Quantity > lotBalance.ReservedQuantity AND lot.IsBlocked=0
            AND (lot.ExpiresAt IS NULL OR lot.ExpiresAt >= CONVERT(date, SYSUTCDATETIME()))
          ORDER BY ISNULL(priorityInfo.Priority, 2147483647), CASE WHEN lot.ExpiresAt IS NULL THEN 1 ELSE 0 END, lot.ExpiresAt, lot.Id;
          """, new { Rfc = rfc, SiteId = siteId, MaterialId = requirement.Key }, tx, cancellationToken: ct))).AsList();
        foreach (var lot in lots.Where(_ => remaining > 0))
        {
          var reserve = Math.Min(remaining, lot.AvailableQuantity);
          if (reserve <= 0) continue;
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE logistica.LotBalance SET ReservedQuantity = ReservedQuantity + @Quantity, UpdatedAt = SYSUTCDATETIME()
            WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;
            UPDATE logistica.StockBalance SET ReservedQuantity = ReservedQuantity + @Quantity, UpdatedAt = SYSUTCDATETIME()
            WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;
            """, new { Rfc = rfc, LotId = lot.MaterialLotId, lot.LocationId, MaterialId = requirement.Key, Quantity = reserve }, tx, cancellationToken: ct));
          await InsertReservationLineAsync(conn, tx, rfc, reservationId, requirement.Key, lot.LocationId, lot.MaterialLotId, needed, reserve, false, lot.UnitCost, ct);
          remaining -= reserve;
        }
      }
      else
      {
        var balances = (await conn.QueryAsync<BalanceAvailabilityRow>(new CommandDefinition(
          """
          SELECT balance.LocationId, balance.Quantity - balance.ReservedQuantity AS AvailableQuantity, balance.AverageUnitCost
          FROM logistica.StockBalance balance WITH (UPDLOCK, HOLDLOCK)
          LEFT JOIN restaurante.SiteLocationPriority priorityInfo
            ON priorityInfo.Rfc=balance.Rfc AND priorityInfo.SiteId=@SiteId AND priorityInfo.LocationId=balance.LocationId
          WHERE balance.Rfc=@Rfc AND balance.MaterialId=@MaterialId AND balance.IsRemoved=0
            AND balance.Quantity > balance.ReservedQuantity
          ORDER BY ISNULL(priorityInfo.Priority, 2147483647), balance.LocationId;
          """, new { Rfc = rfc, SiteId = siteId, MaterialId = requirement.Key }, tx, cancellationToken: ct))).AsList();
        foreach (var balance in balances.Where(_ => remaining > 0))
        {
          var reserve = Math.Min(remaining, balance.AvailableQuantity);
          if (reserve <= 0) continue;
          await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE logistica.StockBalance SET ReservedQuantity=ReservedQuantity+@Quantity, UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;",
            new { Rfc = rfc, MaterialId = requirement.Key, balance.LocationId, Quantity = reserve }, tx, cancellationToken: ct));
          await InsertReservationLineAsync(conn, tx, rfc, reservationId, requirement.Key, balance.LocationId, null, needed, reserve, false, balance.AverageUnitCost, ct);
          remaining -= reserve;
        }
      }
      if (remaining > 0)
      {
        if (!allowDeficit)
        {
          throw new InvalidOperationException($"Inventario insuficiente para el material {requirement.Key}. Faltan {remaining:N4}.");
        }
        var fallbackLocation = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
          """
          SELECT TOP (1) locationInfo.Id
          FROM logistica.Location locationInfo
          LEFT JOIN restaurante.SiteLocationPriority priorityInfo
            ON priorityInfo.Rfc=locationInfo.Rfc AND priorityInfo.SiteId=@SiteId AND priorityInfo.LocationId=locationInfo.Id
          WHERE locationInfo.Rfc=@Rfc AND locationInfo.IsActive=1 AND locationInfo.IsInventoryEnabled=1
          ORDER BY ISNULL(priorityInfo.Priority, 2147483647), locationInfo.Id;
          """, new { Rfc = rfc, SiteId = siteId }, tx, cancellationToken: ct))
          ?? throw new InvalidOperationException("No hay una ubicación de inventario configurada para registrar el déficit.");
        await InsertReservationLineAsync(conn, tx, rfc, reservationId, requirement.Key, fallbackLocation, null, needed, 0, true, 0, ct);
        hasDeficit = true;
      }
    }
    return new ReservationResult(reservationId, hasDeficit);
  }

  private static Task InsertReservationLineAsync(DbConnection conn, DbTransaction tx, string rfc, long reservationId, int materialId,
    int locationId, long? lotId, decimal required, decimal reserved, bool isDeficit, decimal cost, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO logistica.InventoryReservationLine
        (Rfc, ReservationId, MaterialId, LocationId, MaterialLotId, RequiredQuantity, ReservedQuantity, IsDeficit, FrozenUnitCost)
      VALUES
        (@Rfc, @ReservationId, @MaterialId, @LocationId, @LotId, @Required, @Reserved, @IsDeficit, @Cost);
      """, new { Rfc = rfc, ReservationId = reservationId, MaterialId = materialId, LocationId = locationId, LotId = lotId, Required = required, Reserved = reserved, IsDeficit = isDeficit, Cost = cost }, tx, cancellationToken: ct));

  private static async Task<bool> ConsumeReservationAsync(DbConnection conn, DbTransaction tx, string rfc, long reservationId, string userName, CancellationToken ct)
  {
    var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
      "SELECT [Status] FROM logistica.InventoryReservation WITH (UPDLOCK, HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
    if (status == "Consumed") return false;
    if (status != "Reserved") throw new InvalidOperationException("La reserva de inventario ya no está disponible.");
    var lines = (await conn.QueryAsync<ReservationLineRow>(new CommandDefinition(
      "SELECT Id, MaterialId, LocationId, MaterialLotId, ReservedQuantity, FrozenUnitCost FROM logistica.InventoryReservationLine WHERE Rfc=@Rfc AND ReservationId=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct))).AsList();
    foreach (var line in lines.Where(item => item.ReservedQuantity > 0))
    {
      var balance = await conn.QuerySingleAsync<StockBalanceRow>(new CommandDefinition(
        "SELECT Id, Quantity, ReservedQuantity FROM logistica.StockBalance WITH (UPDLOCK, HOLDLOCK) WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;",
        new { Rfc = rfc, line.MaterialId, line.LocationId }, tx, cancellationToken: ct));
      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.StockBalance
        SET Quantity=Quantity-@Quantity, ReservedQuantity=ReservedQuantity-@Quantity, UpdatedAt=SYSUTCDATETIME()
        WHERE Rfc=@Rfc AND Id=@BalanceId;
        INSERT INTO logistica.StockTransaction
          (Rfc, StockBalanceId, LocationId, MaterialId, TransactionType, QuantityDelta, QuantityAfter,
           ReferenceType, ReferenceId, Notes, PerformedBy)
        VALUES
          (@Rfc, @BalanceId, @LocationId, @MaterialId, 'RestaurantConsumption', -@Quantity, @QuantityAfter,
           'InventoryReservation', @ReservationId, 'Consumo por inicio de preparación', @PerformedBy);
        UPDATE logistica.InventoryReservationLine SET ConsumedQuantity=@Quantity WHERE Rfc=@Rfc AND Id=@LineId;
        """, new
        {
          Rfc = rfc,
          BalanceId = balance.Id,
          line.LocationId,
          line.MaterialId,
          Quantity = line.ReservedQuantity,
          QuantityAfter = balance.Quantity - line.ReservedQuantity,
          ReservationId = reservationId,
          PerformedBy = userName,
          LineId = line.Id
        }, tx, cancellationToken: ct));
      if (line.MaterialLotId.HasValue)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.LotBalance SET Quantity=Quantity-@Quantity, ReservedQuantity=ReservedQuantity-@Quantity, UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;",
          new { Rfc = rfc, Quantity = line.ReservedQuantity, LotId = line.MaterialLotId.Value, line.LocationId }, tx, cancellationToken: ct));
      }
    }
    await conn.ExecuteAsync(new CommandDefinition(
      "UPDATE logistica.InventoryReservation SET [Status]='Consumed', ConsumedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
    return true;
  }

  private static async Task<bool> ReleaseReservationAsync(DbConnection conn, DbTransaction tx, string rfc, long reservationId, CancellationToken ct)
  {
    var status = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
      "SELECT [Status] FROM logistica.InventoryReservation WITH (UPDLOCK, HOLDLOCK) WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
    if (status != "Reserved") return false;
    var lines = (await conn.QueryAsync<ReservationLineRow>(new CommandDefinition(
      "SELECT Id, MaterialId, LocationId, MaterialLotId, ReservedQuantity, FrozenUnitCost FROM logistica.InventoryReservationLine WHERE Rfc=@Rfc AND ReservationId=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct))).AsList();
    foreach (var line in lines.Where(item => item.ReservedQuantity > 0))
    {
      await conn.ExecuteAsync(new CommandDefinition(
        "UPDATE logistica.StockBalance SET ReservedQuantity=ReservedQuantity-@Quantity, UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialId=@MaterialId AND LocationId=@LocationId;",
        new { Rfc = rfc, Quantity = line.ReservedQuantity, line.MaterialId, line.LocationId }, tx, cancellationToken: ct));
      if (line.MaterialLotId.HasValue)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE logistica.LotBalance SET ReservedQuantity=ReservedQuantity-@Quantity, UpdatedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND MaterialLotId=@LotId AND LocationId=@LocationId;",
          new { Rfc = rfc, Quantity = line.ReservedQuantity, LotId = line.MaterialLotId.Value, line.LocationId }, tx, cancellationToken: ct));
      }
    }
    await conn.ExecuteAsync(new CommandDefinition(
      "UPDATE logistica.InventoryReservation SET [Status]='Released', ReleasedAt=SYSUTCDATETIME() WHERE Rfc=@Rfc AND Id=@Id;",
      new { Rfc = rfc, Id = reservationId }, tx, cancellationToken: ct));
    return true;
  }

  private static async Task<OrderStatusTransition?> RefreshOrderStatusAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid orderId,
    CancellationToken ct)
  {
    var current = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
      "SELECT [Status] FROM restaurante.[Order] WITH (UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Id=@OrderId;",
      new { Rfc = rfc, OrderId = orderId }, tx, cancellationToken: ct));
    if (current is null || current is "Cancelled" or "Completed") return null;
    var statuses = (await conn.QueryAsync<string>(new CommandDefinition(
      "SELECT [Status] FROM restaurante.OrderLine WHERE Rfc=@Rfc AND OrderId=@OrderId;",
      new { Rfc = rfc, OrderId = orderId }, tx, cancellationToken: ct))).AsList();
    var next = statuses.All(status => status is "Ready" or "Delivered" or "Cancelled")
      ? "Ready"
      : statuses.Any(status => status == "Preparing") ? "Preparing" : "Sent";
    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE restaurante.[Order]
      SET [Status]=@Status,
          ReadyAt=CASE WHEN @Status='Ready' AND ReadyAt IS NULL THEN SYSUTCDATETIME() ELSE ReadyAt END
      WHERE Rfc=@Rfc AND Id=@OrderId AND [Status] NOT IN ('Cancelled','Completed');
      """, new { Rfc = rfc, OrderId = orderId, Status = next }, tx, cancellationToken: ct));
    return string.Equals(current, next, StringComparison.Ordinal)
      ? null
      : new OrderStatusTransition(current, next);
  }

  private static async Task<RestaurantOrderDto?> LoadOrderAsync(DbConnection conn, DbTransaction? tx, string rfc, Guid orderId, CancellationToken ct)
  {
    const string sql =
      """
      SELECT orderInfo.Id, orderInfo.Folio, orderInfo.OperationalDate, orderInfo.OrderType, orderInfo.[Status],
             orderInfo.PaymentStatus, orderInfo.CustomerName, diningTable.[Name] AS TableName, orderInfo.Notes,
              orderInfo.Total, orderInfo.BalanceDue, orderInfo.CashRegisterId, orderInfo.CashShiftId,
              orderInfo.Priority,orderInfo.PriorityReason,orderInfo.PrioritizedBy,orderInfo.CreatedAt
      FROM restaurante.[Order] orderInfo
      LEFT JOIN restaurante.DiningTable diningTable ON diningTable.Rfc=orderInfo.Rfc AND diningTable.Id=orderInfo.DiningTableId
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.Id=@OrderId;
      SELECT lineInfo.Id, lineInfo.ProductNameSnapshot AS ProductName, lineInfo.IsCustom, lineInfo.Quantity, lineInfo.[Status],
             lineInfo.Notes, product.PreparationMinutes, lineInfo.StartedAt, lineInfo.ReadyAt
      FROM restaurante.OrderLine lineInfo
      LEFT JOIN restaurante.Product product ON product.Rfc=lineInfo.Rfc AND product.Id=lineInfo.ProductId
      WHERE lineInfo.Rfc=@Rfc AND lineInfo.OrderId=@OrderId ORDER BY lineInfo.Id;
      SELECT modifier.OrderLineId, modifier.[Name]
      FROM restaurante.OrderLineModifier modifier
      JOIN restaurante.OrderLine lineInfo ON lineInfo.Rfc=modifier.Rfc AND lineInfo.Id=modifier.OrderLineId
      WHERE modifier.Rfc=@Rfc AND lineInfo.OrderId=@OrderId ORDER BY modifier.Id;
      """;
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = rfc, OrderId = orderId }, tx, cancellationToken: ct));
    var order = await multi.ReadSingleOrDefaultAsync<RestaurantOrderDto>();
    var lines = (await multi.ReadAsync<RestaurantOrderLineDto>()).AsList();
    var modifiers = (await multi.ReadAsync<OrderLineModifierRow>()).AsList();
    if (order is null) return null;
    foreach (var line in lines)
    {
      line.Modifiers = modifiers.Where(item => item.OrderLineId == line.Id).Select(item => item.Name).ToList();
    }
    order.Lines = lines;
    return order;
  }

  private static Task AddOutboxEventAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId, string eventType,
    string aggregateId, object payload, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      "INSERT INTO restaurante.EventOutbox (Rfc, SiteId, EventType, AggregateId, Payload) VALUES (@Rfc,@SiteId,@EventType,@AggregateId,@Payload);",
      new { Rfc = rfc, SiteId = siteId, EventType = eventType, AggregateId = aggregateId, Payload = JsonSerializer.Serialize(payload) }, tx, cancellationToken: ct));

  private static Task AddSupervisorAuthorizationAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    int siteId,
    Guid orderId,
    string actionType,
    string reason,
    string requestedBy,
    string authorizedBy,
    CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO restaurante.SupervisorAuthorization
        (Rfc,SiteId,ActionType,AggregateId,Reason,RequestedBy,AuthorizedBy)
      VALUES
        (@Rfc,@SiteId,@ActionType,CONVERT(varchar(36),@OrderId),@Reason,@RequestedBy,@AuthorizedBy);
      """,
      new
      {
        Rfc = rfc,
        SiteId = siteId,
        OrderId = orderId,
        ActionType = actionType,
        Reason = reason,
        RequestedBy = requestedBy,
        AuthorizedBy = authorizedBy
      },
      tx,
      cancellationToken: ct));

  private static Task AddOrderTransitionEventAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    int siteId,
    Guid orderId,
    OrderStatusTransition transition,
    string actor,
    CancellationToken ct)
    => RestaurantOrderEventWriter.AddAsync(
      conn, tx, rfc, siteId, orderId,
      transition.CurrentStatus == "Ready" && transition.NextStatus == "Preparing"
        ? "OrderReopened"
        : $"Order{transition.NextStatus}",
      "Kitchen",
      transition.NextStatus switch
      {
        "Preparing" when transition.CurrentStatus == "Ready" => "Orden regresada a preparación",
        "Preparing" => "Preparación de la orden iniciada",
        "Ready" => "Orden lista",
        _ => "Estado de cocina actualizado"
      },
      $"{OrderStatusLabel(transition.CurrentStatus)} → {OrderStatusLabel(transition.NextStatus)}",
      actor, ct);

  private static async Task<RestaurantOrderResult?> FindDuplicateAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId, string key, CancellationToken ct)
    => await conn.QuerySingleOrDefaultAsync<RestaurantOrderResult>(new CommandDefinition(
      """
      SELECT Id AS OrderId, Folio, CustomerName, OperationalDate, [Status], PaymentStatus, Total, BalanceDue
      FROM restaurante.[Order] WITH (UPDLOCK, HOLDLOCK)
      WHERE Rfc=@Rfc AND SiteId=@SiteId AND IdempotencyKey=@Key;
      """, new { Rfc = rfc, SiteId = siteId, Key = key }, tx, cancellationToken: ct));

  private static void AddRequirement(IDictionary<int, decimal> requirements, int materialId, decimal quantity)
  {
    requirements.TryGetValue(materialId, out var current);
    requirements[materialId] = current + quantity;
  }

  private static bool IsLineTransitionAllowed(string current, string next)
    => (current, next) switch
    {
      ("Pending", "Preparing") => true,
      ("Preparing", "Ready") => true,
      ("Ready", "Delivered") => true,
      _ when current == next => true,
      _ => false
    };

  private static string NormalizeOrderType(string? value)
    => value?.Trim().ToLowerInvariant() switch
    {
      "pickup" or "recoger" => "Pickup",
      "table" or "mesa" => "Table",
      "delivery" or "domicilio" => "Delivery",
      _ => throw new InvalidOperationException("Modalidad de orden no válida.")
    };

  private static string NormalizePaymentMethod(string? value)
    => value?.Trim().ToLowerInvariant() switch
    {
      "cash" or "efectivo" => "Cash",
      "card" or "tarjeta" => "ExternalCard",
      "externalcard" => "ExternalCard",
      "transfer" or "transferencia" => "Transfer",
      "platform" or "plataforma" => "Platform",
      _ => throw new InvalidOperationException("Forma de pago no válida.")
    };

  private static string OrderTypeLabel(string value)
    => value switch
    {
      "Table" => "Mesa",
      "Delivery" => "Domicilio",
      _ => "Para recoger"
    };

  private static string PaymentMethodLabel(string value)
    => value switch
    {
      "Cash" => "Efectivo",
      "ExternalCard" => "Tarjeta",
      "Transfer" => "Transferencia",
      "Platform" => "Plataforma",
      _ => value
    };

  private static string OrderStatusLabel(string value)
    => value switch
    {
      "AwaitingPayment" => "Pendiente de pago",
      "Sent" => "Enviada a cocina",
      "Preparing" => "Preparando",
      "Ready" => "Lista",
      "Dispatched" => "Despachada",
      "Delivered" => "Entregada",
      "Completed" => "Completada",
      "Cancelled" => "Cancelada",
      _ => value
    };

  private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed record PricedLine(
    RestaurantOrderLineCreateRequest Request,
    ProductRow? Product,
    List<ModifierRow> Modifiers,
    string ProductName,
    string Sku,
    decimal UnitPrice,
    decimal Gross,
    bool IsCustom);
  private sealed record ReservationResult(long? ReservationId, bool HasDeficit);
  private sealed record OrderStatusTransition(string CurrentStatus, string NextStatus);

  private sealed class SiteRow
  {
    public int Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;
    public TimeSpan OperationalDayCutoff { get; set; }
    public decimal TaxRate { get; set; }
    public bool PricesIncludeTax { get; set; }
    public bool IsEnabled { get; set; }
    public bool AllowSupervisorDeficit { get; set; }
  }
  private sealed class ProductRow
  {
    public long Id { get; set; }
    public int MaterialId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? VariantName { get; set; }
    public decimal Price { get; set; }
    public int? KitchenStationId { get; set; }
    public int? PreparationMinutes { get; set; }
    public string FulfillmentMode { get; set; } = string.Empty;
    public int BaseUnitId { get; set; }
    public bool TrackLots { get; set; }
    public decimal TheoreticalCost { get; set; }
  }
  private sealed class ModifierRow
  {
    public long ProductId { get; set; }
    public long ModifierGroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceDelta { get; set; }
  }
  private sealed class ModifierDeltaRow { public int MaterialId { get; set; } public decimal QuantityDelta { get; set; } public decimal? Factor { get; set; } }
  private sealed class BomRequirementRow { public int MaterialId { get; set; } public decimal? QuantityPerYield { get; set; } public string FulfillmentMode { get; set; } = string.Empty; }
  private sealed class MaterialInventoryRow { public int Id { get; set; } public bool TrackLots { get; set; } }
  private sealed class LotAvailabilityRow { public long MaterialLotId { get; set; } public int LocationId { get; set; } public decimal AvailableQuantity { get; set; } public decimal UnitCost { get; set; } }
  private sealed class BalanceAvailabilityRow { public int LocationId { get; set; } public decimal AvailableQuantity { get; set; } public decimal AverageUnitCost { get; set; } }
  private sealed class ReservationLineRow { public long Id { get; set; } public int MaterialId { get; set; } public int LocationId { get; set; } public long? MaterialLotId { get; set; } public decimal ReservedQuantity { get; set; } public decimal FrozenUnitCost { get; set; } }
  private sealed class StockBalanceRow { public int Id { get; set; } public decimal Quantity { get; set; } public decimal ReservedQuantity { get; set; } }
  private sealed class LineIdentityRow { public long Id { get; set; } public Guid OrderId { get; set; } public string ProductNameSnapshot { get; set; } = string.Empty; public bool IsCustom { get; set; } public string Status { get; set; } = string.Empty; public int SiteId { get; set; } public long? InventoryReservationId { get; set; } }
  private sealed class CancelOrderRow { public Guid Id { get; set; } public int SiteId { get; set; } public string Status { get; set; } = string.Empty; public long? InventoryReservationId { get; set; } }
  private sealed class OrderFulfillmentRow { public Guid Id { get; set; } public int SiteId { get; set; } public string OrderType { get; set; } = string.Empty; public string Status { get; set; } = string.Empty; public string PaymentStatus { get; set; } = string.Empty; }
  private sealed class PaymentOrderRow
  {
    public Guid Id { get; set; }
    public int SiteId { get; set; }
    public int? CashRegisterId { get; set; }
    public Guid? CashShiftId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PaymentStatus { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public decimal BalanceDue { get; set; }
  }
  private sealed class RefundPaymentRow
  {
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string PaymentMethod { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal RefundedAmount { get; set; }
    public int SiteId { get; set; }
    public int? CashRegisterId { get; set; }
    public decimal Total { get; set; }
    public string OrderStatus { get; set; } = string.Empty;
  }
  private sealed class OrderLineModifierRow { public long OrderLineId { get; set; } public string Name { get; set; } = string.Empty; }
}
