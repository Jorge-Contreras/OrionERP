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
    if (request.PointsToRedeem < 0)
    {
      throw new InvalidOperationException("Los puntos a canjear no pueden ser negativos.");
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
      if (request.AllowInventoryDeficit && string.IsNullOrWhiteSpace(request.SupervisorAuthorizedBy))
      {
        throw new InvalidOperationException("El déficit de inventario requiere autorización de supervisor.");
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
      var requestedProductIds = catalogLines.Select(line => line.ProductId!.Value).Distinct().ToArray();
      var products = (await LoadProductsAsync(
        conn, tx, rfc, requestedProductIds, request.AllowInventoryDeficit, ct)).ToList();
      if (products.Count != requestedProductIds.Length)
      {
        throw new InvalidOperationException("Uno o más productos están inactivos o pertenecen a otro RFC.");
      }

      var timeZone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZoneId);
      var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
      var activeMenuId = await LoadActiveMenuIdAsync(conn, tx, rfc, request.SiteId, localNow, ct);
      var comboParentIds = products
        .Where(product => string.Equals(product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase))
        .Select(product => product.Id)
        .ToArray();
      var comboRows = await LoadComboOptionsAsync(conn, tx, rfc, comboParentIds, activeMenuId, ct);
      var selectedComboOptionIds = catalogLines
        .SelectMany(line => line.ComboSelections)
        .Select(selection => selection.ComboSlotOptionId)
        .Distinct()
        .ToHashSet();
      var componentProductIds = RestaurantComboOrderRules.ResolveSelectedComponentProductIds(
        comboRows
          .Where(row => row.ComboSlotOptionId.HasValue && row.ComponentProductId.HasValue)
          .Select(row => new RestaurantComboOrderOptionRule(
            row.ComboSlotId,
            row.ComboSlotOptionId!.Value,
            row.ComponentProductId!.Value))
          .ToList(),
        selectedComboOptionIds).ToArray();
      var components = await LoadProductsAsync(
        conn, tx, rfc, componentProductIds, request.AllowInventoryDeficit, ct);
      if (components.Count != componentProductIds.Length)
      {
        throw new InvalidOperationException("Uno o más componentes del combo están inactivos, agotados o pertenecen a otro RFC.");
      }
      products.AddRange(components.Where(component => products.All(product => product.Id != component.Id)));
      if (components.Any(component => string.Equals(component.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase)))
      {
        throw new InvalidOperationException("La versión actual no permite incluir un combo dentro de otro combo.");
      }

      var allOperationalProductIds = requestedProductIds
        .Concat(componentProductIds)
        .Distinct()
        .ToArray();
      var menuSections = await LoadMenuSectionSnapshotsAsync(
        conn, tx, rfc, activeMenuId, allOperationalProductIds, ct);
      var menuMemberships = menuSections.Select(item => new RestaurantMenuSectionMembershipRule(
        item.ProductId,
        item.MenuSectionId,
        item.MenuSectionName,
        item.MenuSectionSortOrder)).ToList();

      var modifierProductIds = products
        .Where(product => !string.Equals(product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase))
        .Select(product => product.Id)
        .Distinct()
        .ToArray();
      var modifierRows = modifierProductIds.Length == 0
        ? []
        : (await conn.QueryAsync<ModifierRow>(new CommandDefinition(
          """
          SELECT productGroup.ProductId, groupInfo.Id AS ModifierGroupId, groupInfo.[Name] AS GroupName,
                 groupInfo.MinSelections, groupInfo.MaxSelections,
                 optionInfo.Id, optionInfo.[Name], optionInfo.PriceDelta,
                 CASE
                   WHEN COUNT(deltaInfo.Id) = 0 THEN NULL
                   WHEN MIN(deltaInfo.EffectKind) = MAX(deltaInfo.EffectKind) THEN MIN(deltaInfo.EffectKind)
                   ELSE 'Mixed'
                 END AS EffectKind
          FROM restaurante.ProductModifierGroup productGroup
          JOIN restaurante.ModifierGroup groupInfo ON groupInfo.Rfc = productGroup.Rfc AND groupInfo.Id = productGroup.ModifierGroupId
          JOIN restaurante.ModifierOption optionInfo ON optionInfo.Rfc = groupInfo.Rfc AND optionInfo.ModifierGroupId = groupInfo.Id
          LEFT JOIN restaurante.ModifierIngredientDelta deltaInfo
            ON deltaInfo.Rfc=optionInfo.Rfc AND deltaInfo.ModifierOptionId=optionInfo.Id
          WHERE productGroup.Rfc = @Rfc AND productGroup.ProductId IN @ProductIds
            AND groupInfo.IsActive = 1 AND optionInfo.IsActive = 1
          GROUP BY productGroup.ProductId,groupInfo.Id,groupInfo.[Name],groupInfo.MinSelections,groupInfo.MaxSelections,
                   optionInfo.Id,optionInfo.[Name],optionInfo.PriceDelta;
          """, new { Rfc = rfc, ProductIds = modifierProductIds }, tx, cancellationToken: ct))).AsList();
      if (modifierRows.Count > 0)
      {
        var modifierEffects = (await conn.QueryAsync<ModifierEffectRow>(new CommandDefinition(
          """
          SELECT deltaInfo.ModifierOptionId,deltaInfo.EffectKind,
                 material.[Description] AS MaterialName
          FROM restaurante.ModifierIngredientDelta deltaInfo
          JOIN logistica.Material material
            ON material.Rfc=deltaInfo.Rfc AND material.Id=deltaInfo.MaterialId
          WHERE deltaInfo.Rfc=@Rfc AND deltaInfo.ModifierOptionId IN @ModifierOptionIds
          ORDER BY deltaInfo.ModifierOptionId,deltaInfo.Id;
          """,
          new { Rfc = rfc, ModifierOptionIds = modifierRows.Select(item => item.Id).Distinct().ToArray() },
          tx,
          cancellationToken: ct))).AsList();
        foreach (var modifier in modifierRows)
        {
          modifier.Effects = modifierEffects
            .Where(effect => effect.ModifierOptionId == modifier.Id)
            .Select(effect => new ModifierEffectSnapshot(effect.MaterialName, effect.EffectKind))
            .ToList();
        }
      }
      var comboPlans = BuildComboPlans(request, products, comboRows, menuSections, modifierRows);
      ValidateModifiers(request, products, modifierRows);

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
        MenuSectionSnapshotRow? menuSection = null;
        IReadOnlyList<PricedComboComponent> comboComponents = [];
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
          var membership = RestaurantComboOrderRules.RequireActiveMenuMembership(
            productId,
            line.MenuSectionId,
            menuMemberships);
          menuSection = new MenuSectionSnapshotRow
          {
            ProductId = productId,
            MenuSectionId = membership.MenuSectionId,
            MenuSectionName = membership.MenuSectionName,
            MenuSectionSortOrder = membership.MenuSectionSortOrder
          };
          var isCombo = string.Equals(product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase);
          modifiers = isCombo
            ? []
            : modifierRows.Where(item => item.ProductId == productId && line.ModifierOptionIds.Contains(item.Id)).ToList();
          productName = string.IsNullOrWhiteSpace(product.VariantName)
            ? product.Name
            : $"{product.Name} · {product.VariantName}";
          sku = product.Sku;
          if (isCombo)
          {
            var comboPlan = comboPlans[line];
            comboComponents = comboPlan.Components;
            unitPrice = RestaurantComboPricingRules.CalculateUnitPrice(
              product.Price,
              comboComponents.Select(ToPriceSelection));
          }
          else
          {
            unitPrice = product.Price + modifiers.Sum(item => item.PriceDelta);
          }
          gross = decimal.Round(unitPrice * line.Quantity, 2, MidpointRounding.AwayFromZero);
        }
        if (line.DiscountAmount < 0 || line.DiscountAmount > gross)
        {
          throw new InvalidOperationException("El descuento de una línea no puede exceder su importe.");
        }
        subtotalBeforeDiscount += gross;
        lineDiscount += line.DiscountAmount;
        pricedLines.Add(new PricedLine(
          pricedLines.Count.ToString(),
          line,
          product,
          modifiers,
          productName,
          sku,
          unitPrice,
          gross,
          line.IsCustom,
          menuSection?.MenuSectionId,
          menuSection?.MenuSectionName,
          menuSection?.MenuSectionSortOrder,
          comboComponents));
      }

      var member = await RestaurantLoyaltyTransaction.ValidateMemberAsync(conn, tx, rfc, request.MemberId, ct);
      var promotionsEnabled = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        """
        SELECT CAST(ISNULL
        (
          (SELECT IsPromotionsEnabled FROM restaurante.PublicSiteSettings
           WHERE Rfc=@Rfc AND SiteId=@SiteId),0
        ) AS bit);
        """,
        new { Rfc = rfc, request.SiteId },
        tx,
        cancellationToken: ct));
      var promotionRequest = new RestaurantPromotionQuoteRequest
      {
        Rfc = rfc,
        SiteId = request.SiteId,
        At = localNow,
        Channel = string.IsNullOrWhiteSpace(request.SalesChannel)
          ? RestaurantSalesChannels.Pos
          : request.SalesChannel.Trim(),
        OrderType = request.OrderType,
        MemberId = member?.Id,
        Code = request.PromotionCode,
        Lines = pricedLines.Select(pricedLine => new RestaurantPromotionQuoteLineRequest
        {
          LineKey = pricedLine.LineKey,
          ProductId = pricedLine.Product?.Id,
          MaterialCategoryId = pricedLine.Product?.MaterialCategoryId,
          Quantity = pricedLine.Request.Quantity,
          UnitPrice = pricedLine.UnitPrice,
          ManualDiscountAmount = pricedLine.Request.DiscountAmount,
          IsCustom = pricedLine.IsCustom
        }).ToList()
      };
      RestaurantPromotionQuoteDto promotionQuote;
      if (promotionsEnabled)
      {
        var definitions = await RestaurantPromotionService.LoadDefinitionsAsync(
          conn,
          tx,
          rfc,
          request.SiteId,
          member?.Id,
          request.PromotionCode,
          includeInactive: false,
          ct);
        promotionQuote = RestaurantPromotionEngine.Quote(promotionRequest, definitions, localNow);
      }
      else
      {
        promotionQuote = RestaurantPromotionEngine.Quote(promotionRequest, [], localNow);
      }
      if (!string.IsNullOrWhiteSpace(request.PromotionCode) && !promotionQuote.CodeAccepted)
      {
        throw new InvalidOperationException(promotionQuote.Message ?? "El código promocional no es elegible.");
      }

      var promotionDiscount = promotionQuote.PromotionDiscountTotal;
      var promotionDiscountByLine = promotionQuote.LineAdjustments
        .GroupBy(adjustment => adjustment.LineKey, StringComparer.Ordinal)
        .ToDictionary(
          group => group.Key,
          group => decimal.Round(group.Sum(adjustment => adjustment.DiscountAmount), 2, MidpointRounding.AwayFromZero),
          StringComparer.Ordinal);
      foreach (var pricedLine in pricedLines)
      {
        var availableAfterPromotion = pricedLine.Gross - promotionDiscountByLine.GetValueOrDefault(pricedLine.LineKey);
        if (pricedLine.Request.DiscountAmount > availableAfterPromotion)
        {
          throw new InvalidOperationException(
            $"El descuento manual de {pricedLine.ProductName} excede el importe restante después de promociones.");
        }
      }
      if (request.OrderDiscountAmount < 0 ||
          request.OrderDiscountAmount > subtotalBeforeDiscount - lineDiscount - promotionDiscount)
      {
        throw new InvalidOperationException("El descuento de orden no puede exceder el subtotal disponible.");
      }

      var nonLoyaltyDiscountTotal = decimal.Round(
        lineDiscount + request.OrderDiscountAmount + promotionDiscount,
        2,
        MidpointRounding.AwayFromZero);
      var merchandiseBeforeRedemption = decimal.Round(
        subtotalBeforeDiscount - nonLoyaltyDiscountTotal,
        2,
        MidpointRounding.AwayFromZero);
      var redemption = await RestaurantLoyaltyTransaction.PrepareOrderRedemptionAsync(
        conn,
        tx,
        rfc,
        member,
        request.PointsToRedeem,
        merchandiseBeforeRedemption,
        ct);
      var discountTotal = decimal.Round(
        nonLoyaltyDiscountTotal + redemption.ValueMxn,
        2,
        MidpointRounding.AwayFromZero);
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
      if (request.Payments.Any(payment =>
            payment.Amount < 0 || payment.TipAmount < 0 ||
            payment.Amount + payment.TipAmount <= 0 ||
            string.IsNullOrWhiteSpace(payment.IdempotencyKey)))
      {
        throw new InvalidOperationException("Cada pago requiere un importe o propina positivos y clave de idempotencia.");
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

      var operationalDate = DateOnly.FromDateTime(localNow.TimeOfDay < site.OperationalDayCutoff ? localNow.AddDays(-1).Date : localNow.Date);
      var orderId = Guid.NewGuid();
      var inventoryPlan = await BuildRequirementsAsync(
        conn, tx, rfc, pricedLines, request.AllowInventoryDeficit, ct);
      var reservation = inventoryPlan.Requirements.Count == 0
        ? new ReservationResult(null, inventoryPlan.OverrideReasons.Count > 0, inventoryPlan.OverrideReasons)
        : await ReserveInventoryAsync(
          conn, tx, rfc, request.SiteId, orderId, request.IdempotencyKey.Trim(), inventoryPlan.Requirements,
          request.AllowInventoryDeficit, inventoryPlan.OverrideReasons, userName, ct);

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
      var hasProductionLines = pricedLines.Count > 0;
      var status = paymentAmount >= total || externalCod
        ? hasProductionLines ? RestaurantOrderStatuses.Sent : RestaurantOrderStatuses.Ready
        : RestaurantOrderStatuses.AwaitingPayment;
      var balanceDue = decimal.Round(total - paymentAmount, 2, MidpointRounding.AwayFromZero);
      var tips = request.Payments.Sum(payment => payment.TipAmount);
      var theoreticalCost = await CalculateRequirementCostAsync(conn, tx, rfc, inventoryPlan.Requirements, ct);

      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO restaurante.[Order]
          (Id, Rfc, SiteId, Folio, OperationalDate, OrderType, [Status], PaymentStatus,
           CustomerName, CustomerPhone, DiningTableId, CashRegisterId, CashShiftId,
           Subtotal, DiscountTotal, TaxTotal, TipTotal, Total, BalanceDue, TaxRateSnapshot,
           PricesIncludeTaxSnapshot, InventoryReservationId, TheoreticalCost, IdempotencyKey,
           Notes, CreatedBy, PaidAt, SentToKitchenAt,MemberId,MembershipNumberSnapshot,
           PromotionDiscountTotal,EligibleMerchandiseTotal,PointsEarned,RedeemedPoints,RedemptionValue)
        VALUES
          (@Id, @Rfc, @SiteId, @Folio, @OperationalDate, @OrderType, @Status, @PaymentStatus,
           @CustomerName, @CustomerPhone, @DiningTableId, @CashRegisterId, @CashShiftId,
           @Subtotal, @DiscountTotal, @TaxTotal, @TipTotal, @Total, @BalanceDue, @TaxRate,
           @PricesIncludeTax, @ReservationId, @TheoreticalCost, @IdempotencyKey,
           @Notes, @CreatedBy, CASE WHEN @PaymentStatus = 'Paid' THEN SYSUTCDATETIME() END,
           CASE WHEN @Status = 'Sent' THEN SYSUTCDATETIME() END,@MemberId,@MembershipNumber,
           @PromotionDiscountTotal,@EligibleMerchandiseTotal,0,@RedeemedPoints,@RedemptionValue);
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
          CreatedBy = userName,
          MemberId = member?.Id,
          MembershipNumber = member?.MembershipNumber,
          PromotionDiscountTotal = promotionDiscount,
          EligibleMerchandiseTotal = discountedMerchandise,
          RedeemedPoints = redemption.Points,
          RedemptionValue = redemption.ValueMxn
        }, tx, cancellationToken: ct));

      if (redemption.Points > 0)
      {
        redemption = await RestaurantLoyaltyTransaction.ApplyOrderRedemptionAsync(
          conn,
          tx,
          rfc,
          orderId,
          member!.Id,
          redemption,
          merchandiseBeforeRedemption,
          userName,
          ct);
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, request.SiteId, orderId,
          "LoyaltyPointsRedeemed", "Loyalty", "Puntos canjeados",
          $"{redemption.Points} punto(s) · Descuento {redemption.ValueMxn:C} · Saldo {redemption.BalanceAfter}",
          userName, ct, $"order:{orderId}:LoyaltyPointsRedeemed");
      }

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

      var lineIds = new Dictionary<string, long>(StringComparer.Ordinal);
      var orderDiscountWeights = pricedLines.ToDictionary(
        line => line.LineKey,
        line => Math.Max(
          0,
          line.Gross -
          promotionDiscountByLine.GetValueOrDefault(line.LineKey) -
          line.Request.DiscountAmount),
        StringComparer.Ordinal);
      var orderDiscountAllocations = AllocateDiscount(
        request.OrderDiscountAmount + redemption.ValueMxn,
        pricedLines.Select(line => (line.LineKey, orderDiscountWeights[line.LineKey])).ToList());
      for (var lineIndex = 0; lineIndex < pricedLines.Count; lineIndex++)
      {
        var pricedLine = pricedLines[lineIndex];
        var promotionLineDiscount = promotionDiscountByLine.GetValueOrDefault(pricedLine.LineKey);
        var allocatedOrderDiscount = orderDiscountAllocations.GetValueOrDefault(pricedLine.LineKey);
        var totalLineDiscount = decimal.Round(
          promotionLineDiscount + pricedLine.Request.DiscountAmount + allocatedOrderDiscount,
          2,
          MidpointRounding.AwayFromZero);
        var discountedLine = pricedLine.Gross - totalLineDiscount;
        var lineTax = site.PricesIncludeTax
          ? (site.TaxRate == 0 ? 0 : decimal.Round(discountedLine - discountedLine / (1 + site.TaxRate), 2, MidpointRounding.AwayFromZero))
          : decimal.Round(discountedLine * site.TaxRate, 2, MidpointRounding.AwayFromZero);
        var lineTotal = site.PricesIncludeTax ? discountedLine : discountedLine + lineTax;
        const string lineStatus = "Pending";
        var lineKind = pricedLine.Product is not null &&
                       string.Equals(pricedLine.Product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase)
          ? RestaurantOrderLineKinds.Combo
          : RestaurantOrderLineKinds.Standard;
        var baseUnitPrice = pricedLine.IsCustom
          ? pricedLine.UnitPrice
          : pricedLine.Product?.Price ?? pricedLine.UnitPrice;
        var lineId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT INTO restaurante.OrderLine
            (Rfc, OrderId, ProductId, IsCustom, ProductNameSnapshot, SkuSnapshot, Quantity, UnitPrice,
             DiscountAmount, TaxAmount, LineTotal, [Status], KitchenStationId, Notes,
             MenuSectionIdSnapshot, MenuSectionNameSnapshot, MenuSectionSortOrderSnapshot,
             LineKind,ParentOrderLineId,ComboSlotId,ComboSlotOptionId,ParentProductNameSnapshot,
             ComboSlotNameSnapshot,BaseUnitPrice,ChoicePriceDelta)
          VALUES
            (@Rfc, @OrderId, @ProductId, @IsCustom, @ProductName, @Sku, @Quantity, @UnitPrice,
             @DiscountAmount, @TaxAmount, @LineTotal, @Status, @KitchenStationId, @Notes,
             @MenuSectionId, @MenuSectionName, @MenuSectionSortOrder,
             @LineKind,NULL,NULL,NULL,NULL,NULL,@BaseUnitPrice,@ChoicePriceDelta);
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
            Notes = NullIfWhiteSpace(pricedLine.Request.Notes),
            pricedLine.MenuSectionId,
            pricedLine.MenuSectionName,
            pricedLine.MenuSectionSortOrder,
            LineKind = lineKind,
            BaseUnitPrice = baseUnitPrice,
            ChoicePriceDelta = decimal.Round(pricedLine.UnitPrice - baseUnitPrice, 2, MidpointRounding.AwayFromZero)
          }, tx, cancellationToken: ct));
        lineIds[pricedLine.LineKey] = lineId;
        foreach (var modifier in pricedLine.Modifiers)
        {
          await InsertLineModifierAsync(conn, tx, rfc, lineId, modifier, ct);
        }

        foreach (var component in pricedLine.ComboComponents)
        {
          var componentName = string.IsNullOrWhiteSpace(component.Product.VariantName)
            ? component.Product.Name
            : $"{component.Product.Name} · {component.Product.VariantName}";
          var componentQuantity = component.TotalQuantity(pricedLine.Request.Quantity);
          var componentChoiceDelta = RestaurantComboPricingRules.CalculateUnitSupplement([ToPriceSelection(component)]);
          var componentLineId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO restaurante.OrderLine
              (Rfc,OrderId,ProductId,IsCustom,ProductNameSnapshot,SkuSnapshot,Quantity,UnitPrice,
               DiscountAmount,TaxAmount,LineTotal,[Status],KitchenStationId,Notes,
               MenuSectionIdSnapshot,MenuSectionNameSnapshot,MenuSectionSortOrderSnapshot,
               LineKind,ParentOrderLineId,ComboSlotId,ComboSlotOptionId,ParentProductNameSnapshot,
               ComboSlotNameSnapshot,BaseUnitPrice,ChoicePriceDelta)
            VALUES
              (@Rfc,@OrderId,@ProductId,0,@ProductName,@Sku,@Quantity,0,
               0,0,0,'Pending',@KitchenStationId,@Notes,
               @MenuSectionId,@MenuSectionName,@MenuSectionSortOrder,
               @LineKind,@ParentOrderLineId,@ComboSlotId,@ComboSlotOptionId,@ParentProductName,
               @ComboSlotName,0,@ChoicePriceDelta);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """, new
            {
              Rfc = rfc,
              OrderId = orderId,
              ProductId = component.Product.Id,
              ProductName = componentName,
              Sku = component.Product.Sku,
              Quantity = componentQuantity,
              component.Product.KitchenStationId,
              Notes = NullIfWhiteSpace(component.Notes),
              component.MenuSectionId,
              component.MenuSectionName,
              component.MenuSectionSortOrder,
              LineKind = RestaurantOrderLineKinds.ComboComponent,
              ParentOrderLineId = lineId,
              component.ComboSlotId,
              ComboSlotOptionId = component.ComboSlotOptionId,
              ParentProductName = pricedLine.ProductName,
              ComboSlotName = component.SlotName,
              ChoicePriceDelta = componentChoiceDelta
            }, tx, cancellationToken: ct));
          foreach (var modifier in component.Modifiers)
          {
            await InsertLineModifierAsync(conn, tx, rfc, componentLineId, modifier, ct);
          }
        }
      }

      await PersistPromotionSnapshotsAsync(
        conn,
        tx,
        rfc,
        orderId,
        member?.Id,
        promotionQuote,
        lineIds,
        ct);

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
          var inventoryAuthorizationReason = DescribeInventoryOverride(reservation.OverrideReasons);
          await AddSupervisorAuthorizationAsync(
            conn, tx, rfc, request.SiteId, orderId, "InventoryDeficit",
            inventoryAuthorizationReason,
            userName, request.SupervisorAuthorizedBy, ct);
          await RestaurantOrderEventWriter.AddAsync(
            conn, tx, rfc, request.SiteId, orderId,
            "InventoryDeficitAuthorized", "Authorization", "Déficit de inventario autorizado",
            inventoryAuthorizationReason,
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

      RestaurantLoyaltyTransaction.LoyaltyAwardResult? loyaltyAward = null;
      if (paymentStatus == RestaurantPaymentStatuses.Paid)
      {
        loyaltyAward = await RestaurantLoyaltyTransaction.AwardPaidOrderAsync(
          conn,
          tx,
          rfc,
          orderId,
          member?.Id,
          discountedMerchandise,
          userName,
          ct);
        if (loyaltyAward is { Points: > 0 })
        {
          await RestaurantOrderEventWriter.AddAsync(
            conn, tx, rfc, request.SiteId, orderId,
            "LoyaltyPointsEarned", "Loyalty", "Puntos acreditados",
            $"{loyaltyAward.Points} punto(s) · Saldo {loyaltyAward.BalanceAfter}",
            userName, ct, $"order:{orderId}:LoyaltyPointsEarned");
        }
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
        BalanceDue = balanceDue,
        PromotionDiscountTotal = promotionDiscount,
        AppliedPromotions = promotionQuote.Adjustments,
        MembershipNumber = member?.MembershipNumber,
        PointsEarned = loyaltyAward?.Points ?? 0,
        PointsRedeemed = redemption.Points,
        RedemptionValue = redemption.ValueMxn,
        PointsBalance = loyaltyAward?.BalanceAfter ?? (redemption.Points > 0 ? redemption.BalanceAfter : member?.PointsBalance)
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

  public async Task<RestaurantReceiptDto?> GetReceiptAsync(string rfc, Guid orderId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT orderInfo.Id AS OrderId,orderInfo.SiteId,siteInfo.[Name] AS SiteName,
             siteInfo.TimeZoneId AS SiteTimeZoneId,orderInfo.Folio,orderInfo.OrderType,
             orderInfo.[Status],orderInfo.PaymentStatus,orderInfo.CustomerName,
             diningTable.[Name] AS TableName,orderInfo.Notes,orderInfo.DiscountTotal,
             orderInfo.TaxTotal,orderInfo.TipTotal,orderInfo.Total,orderInfo.BalanceDue,
             orderInfo.TaxRateSnapshot AS TaxRate,
             orderInfo.PricesIncludeTaxSnapshot AS PricesIncludeTax,
             ISNULL(deliveryInfo.DeliveryCost,0) AS DeliveryCost,
             orderInfo.MembershipNumberSnapshot AS MembershipNumber,orderInfo.PointsEarned,
             orderInfo.RedeemedPoints AS PointsRedeemed,orderInfo.RedemptionValue,
             orderPoints.BalanceAfter AS PointsBalance,
             orderInfo.CreatedAt
      FROM restaurante.[Order] orderInfo
      JOIN restaurante.Site siteInfo
        ON siteInfo.Rfc=orderInfo.Rfc AND siteInfo.Id=orderInfo.SiteId
      LEFT JOIN restaurante.DiningTable diningTable
        ON diningTable.Rfc=orderInfo.Rfc AND diningTable.Id=orderInfo.DiningTableId
      LEFT JOIN restaurante.Delivery deliveryInfo
        ON deliveryInfo.Rfc=orderInfo.Rfc AND deliveryInfo.OrderId=orderInfo.Id
      OUTER APPLY
      (
        SELECT TOP(1) ledger.BalanceAfter
        FROM fidelidad.PointLedger ledger
        WHERE ledger.Rfc=orderInfo.Rfc AND ledger.OrderId=orderInfo.Id
          AND ledger.EntryType IN ('Redeem','Earn')
        ORDER BY ledger.Id DESC
      ) orderPoints
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.Id=@OrderId;

      SELECT lineInfo.Id,lineInfo.ProductId,lineInfo.ProductNameSnapshot AS ProductName,
             lineInfo.IsCustom,lineInfo.Quantity,lineInfo.UnitPrice,lineInfo.DiscountAmount,
             lineInfo.Notes,lineInfo.MenuSectionIdSnapshot AS MenuSectionId,
             lineInfo.MenuSectionNameSnapshot AS MenuSectionName,
             lineInfo.MenuSectionSortOrderSnapshot AS MenuSectionSortOrder,
             lineInfo.LineKind,lineInfo.ParentOrderLineId,lineInfo.ComboSlotId,lineInfo.ComboSlotOptionId,
             lineInfo.ParentProductNameSnapshot AS ParentProductName,
             lineInfo.ComboSlotNameSnapshot AS ComboSlotName,
             lineInfo.BaseUnitPrice,lineInfo.ChoicePriceDelta
      FROM restaurante.OrderLine lineInfo
      WHERE lineInfo.Rfc=@Rfc AND lineInfo.OrderId=@OrderId
      ORDER BY CASE WHEN lineInfo.ParentOrderLineId IS NULL THEN lineInfo.Id ELSE lineInfo.ParentOrderLineId END,
               CASE WHEN lineInfo.ParentOrderLineId IS NULL THEN 0 ELSE 1 END,lineInfo.Id;

      SELECT modifier.OrderLineId,modifier.ModifierOptionId,
             modifier.ModifierGroupNameSnapshot AS GroupName,
             COALESCE(effectInfo.MaterialNameSnapshot,modifier.[Name]) AS [Name],
             CASE
               WHEN effectInfo.Id IS NULL
                 OR effectInfo.Id=MIN(effectInfo.Id) OVER (PARTITION BY modifier.Id)
               THEN modifier.PriceDelta ELSE 0
             END AS PriceDelta,
             modifier.Quantity,COALESCE(effectInfo.EffectKind,modifier.EffectKind) AS EffectKind
      FROM restaurante.OrderLineModifier modifier
      JOIN restaurante.OrderLine lineInfo
        ON lineInfo.Rfc=modifier.Rfc AND lineInfo.Id=modifier.OrderLineId
      LEFT JOIN restaurante.OrderLineModifierIngredientEffect effectInfo
        ON effectInfo.Rfc=modifier.Rfc AND effectInfo.OrderLineModifierId=modifier.Id
      WHERE modifier.Rfc=@Rfc AND lineInfo.OrderId=@OrderId
      ORDER BY modifier.Id,effectInfo.Id;

      SELECT paymentInfo.Id,paymentInfo.PaymentMethod,paymentInfo.Amount,paymentInfo.TipAmount,
             paymentInfo.RefundedAmount,paymentInfo.[Status],paymentInfo.PaidAt
      FROM restaurante.Payment paymentInfo
      WHERE paymentInfo.Rfc=@Rfc AND paymentInfo.OrderId=@OrderId
      ORDER BY paymentInfo.PaidAt,paymentInfo.Id;

      SELECT PromotionId,PromotionNameSnapshot AS PromotionName,
             RuleTypeSnapshot AS RuleType,CodeSnapshot AS Code,DiscountAmount
      FROM restaurante.OrderPromotion
      WHERE Rfc=@Rfc AND OrderId=@OrderId
      ORDER BY Id;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(
      sql,
      new { Rfc = LogisticsRfc.Require(rfc), OrderId = orderId },
      cancellationToken: ct));
    var receipt = await multi.ReadSingleOrDefaultAsync<RestaurantReceiptDto>();
    if (receipt is null)
    {
      return null;
    }

    var lines = (await multi.ReadAsync<RestaurantReceiptLineDto>()).AsList();
    var modifiers = (await multi.ReadAsync<OrderLineModifierRow>()).AsList();
    foreach (var line in lines)
    {
      var lineModifiers = modifiers.Where(item => item.OrderLineId == line.Id).ToList();
      line.Modifiers = lineModifiers.Select(item => item.Name).ToList();
      line.StructuredModifiers = lineModifiers.Select(ToModifierDto).ToList();
    }

    receipt.Lines = lines;
    receipt.Payments = (await multi.ReadAsync<RestaurantPaymentDto>()).AsList();
    receipt.Promotions = (await multi.ReadAsync<RestaurantPromotionAdjustmentDto>()).AsList();
    return receipt;
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
            AND lineInfo.[Status] <> 'Cancelled'
            AND lineInfo.LineKind <> 'Combo'
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
        order.Lines = order.Lines
          .Where(line => line.LineKind != RestaurantOrderLineKinds.Combo)
          .ToList();
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

  public async Task<IReadOnlyList<RestaurantOrderDto>> GetOperationalOrdersAsync(
    string rfc,
    int siteId,
    DateOnly serviceDate,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    var timeZoneId = await conn.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
      "SELECT TimeZoneId FROM restaurante.Site WHERE Rfc=@Rfc AND Id=@SiteId;",
      new { Rfc = normalizedRfc, SiteId = siteId },
      cancellationToken: ct))
      ?? throw new InvalidOperationException("La sede no existe en el RFC seleccionado.");
    var window = RestaurantServiceDayPolicy.GetUtcWindow(
      serviceDate,
      TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
    var ids = (await conn.QueryAsync<Guid>(new CommandDefinition(
      """
      SELECT orderInfo.Id FROM restaurante.[Order] orderInfo
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.SiteId=@SiteId
        AND orderInfo.CreatedAt>=@StartUtc AND orderInfo.CreatedAt<@EndUtcExclusive
        AND orderInfo.[Status]<>'Draft'
      ORDER BY orderInfo.OperationalDate, orderInfo.Folio, orderInfo.CreatedAt, orderInfo.Id;
      """, new
      {
        Rfc = normalizedRfc,
        SiteId = siteId,
        window.StartUtc,
        window.EndUtcExclusive
      }, cancellationToken: ct))).AsList();
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
        WHERE Rfc=@Rfc AND OrderId=@Id AND @Status IN ('Delivered','Completed')
          AND [Status]='Ready' AND LineKind<>'Combo';
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
        SELECT lineInfo.Id, lineInfo.OrderId, lineInfo.ProductNameSnapshot, lineInfo.IsCustom,lineInfo.LineKind,
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
      if (string.Equals(line.LineKind, RestaurantOrderLineKinds.Combo, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El encabezado del combo no es accionable; actualice sus componentes.");
      }
      if (!IsLineTransitionAllowed(line.Status, normalizedStatus))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail($"No se puede cambiar la partida de {line.Status} a {normalizedStatus}.");
      }

      var inventoryConsumed = false;
      if (!line.IsCustom && normalizedStatus == "Preparing" && line.InventoryReservationId.HasValue)
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
        SELECT lineInfo.Id,lineInfo.OrderId,lineInfo.ProductNameSnapshot,lineInfo.IsCustom,lineInfo.LineKind,
               lineInfo.[Status],orderInfo.SiteId,orderInfo.InventoryReservationId
        FROM restaurante.OrderLine lineInfo WITH (UPDLOCK,HOLDLOCK)
        JOIN restaurante.[Order] orderInfo ON orderInfo.Rfc=lineInfo.Rfc AND orderInfo.Id=lineInfo.OrderId
        WHERE lineInfo.Rfc=@Rfc AND lineInfo.Id=@LineId;
        """, new { Rfc = normalizedRfc, LineId = lineId }, tx, cancellationToken: ct));
      if (line is null || line.Status != "Ready")
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Sólo una partida marcada lista puede regresar a preparación.");
      }
      if (string.Equals(line.LineKind, RestaurantOrderLineKinds.Combo, StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El encabezado del combo no es accionable; actualice sus componentes.");
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
      var loyaltyReversal = await RestaurantLoyaltyTransaction.ReverseCancelledOrderAsync(
        conn,
        tx,
        normalizedRfc,
        orderId,
        supervisorUserName,
        ct);
      var redemptionRestoration = await RestaurantLoyaltyTransaction.RestoreCancelledRedemptionAsync(
        conn,
        tx,
        normalizedRfc,
        orderId,
        supervisorUserName,
        ct);
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
      if (loyaltyReversal is { Points: < 0 })
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, normalizedRfc, order.SiteId, orderId,
          "LoyaltyPointsReversed", "Loyalty", "Puntos retirados por cancelación",
          $"{Math.Abs(loyaltyReversal.Points)} punto(s) · Saldo {loyaltyReversal.BalanceAfter}",
          supervisorUserName, ct, $"order:{orderId}:LoyaltyPointsCancelled");
      }
      if (redemptionRestoration is { Points: > 0 })
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, normalizedRfc, order.SiteId, orderId,
          "LoyaltyRedemptionRestored", "Loyalty", "Puntos canjeados restituidos",
          $"{redemptionRestoration.Points} punto(s) · Saldo {redemptionRestoration.BalanceAfter}",
          supervisorUserName, ct, $"order:{orderId}:LoyaltyRedemptionRestored");
      }
      var pointsReversed = Math.Abs(loyaltyReversal?.Points ?? 0);
      var pointsRestored = Math.Max(0, redemptionRestoration?.Points ?? 0);
      await AddOutboxEventAsync(
        conn,
        tx,
        normalizedRfc,
        order.SiteId,
        "OrderCancelled",
        orderId.ToString(),
        new { orderId, reason, pointsReversed, pointsRestored },
        ct);
      await tx.CommitAsync(ct);
      var loyaltyMessage = pointsReversed > 0
        ? $" Se retiraron {pointsReversed} punto(s) de la membresía vinculada."
        : string.Empty;
      var restorationMessage = pointsRestored > 0
        ? $" Se restituyeron {pointsRestored} punto(s) canjeados."
        : string.Empty;
      return RestaurantCommandResult.Ok($"La orden fue cancelada.{loyaltyMessage}{restorationMessage} Los cobros existentes requieren reembolso supervisado por separado.");
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
        var loyaltyAward = await RestaurantLoyaltyTransaction.AwardExistingPaidOrderAsync(
          conn,
          tx,
          rfc,
          request.OrderId,
          requestedBy,
          ct);
        if (loyaltyAward is { Points: > 0 })
        {
          await RestaurantOrderEventWriter.AddAsync(
            conn, tx, rfc, order.SiteId, request.OrderId,
            "LoyaltyPointsEarned", "Loyalty", "Puntos acreditados",
            $"{loyaltyAward.Points} punto(s) · Saldo {loyaltyAward.BalanceAfter}",
            requestedBy, ct, $"order:{request.OrderId}:LoyaltyPointsEarned");
        }
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
      var loyaltyReversal = await RestaurantLoyaltyTransaction.ReverseRefundAsync(
        conn,
        tx,
        rfc,
        payment.OrderId,
        refundId,
        requestedBy,
        ct);
      var redemptionRestoration = await RestaurantLoyaltyTransaction.RestoreRefundedRedemptionAsync(
        conn,
        tx,
        rfc,
        payment.OrderId,
        refundId,
        requestedBy,
        ct);
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
      if (loyaltyReversal is { Points: < 0 })
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, payment.SiteId, payment.OrderId,
          "LoyaltyPointsReversed", "Loyalty", "Puntos revertidos",
          $"{Math.Abs(loyaltyReversal.Points)} punto(s) · Saldo {loyaltyReversal.BalanceAfter}",
          requestedBy, ct, $"refund:{refundId}:LoyaltyPointsReversed");
      }
      if (redemptionRestoration is { Points: > 0 })
      {
        await RestaurantOrderEventWriter.AddAsync(
          conn, tx, rfc, payment.SiteId, payment.OrderId,
          "LoyaltyRedemptionRestored", "Loyalty", "Puntos canjeados restituidos",
          $"{redemptionRestoration.Points} punto(s) · Saldo {redemptionRestoration.BalanceAfter}",
          requestedBy, ct, $"refund:{refundId}:LoyaltyRedemptionRestored");
      }
      await AddOutboxEventAsync(conn, tx, rfc, payment.SiteId, "OrderPaymentRefunded", payment.OrderId.ToString(), new
      {
        payment.OrderId,
        request.PaymentId,
        refundId,
        request.Amount,
        paymentStatus,
        pointsRestored = Math.Max(0, redemptionRestoration?.Points ?? 0)
      }, ct);
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El reembolso fue registrado y auditado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  private static async Task<IReadOnlyList<ProductRow>> LoadProductsAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    IReadOnlyCollection<long> productIds,
    bool allowInventoryOverride,
    CancellationToken ct)
  {
    if (productIds.Count == 0)
    {
      return [];
    }

    const string sql =
      """
      SELECT product.Id,product.ProductKind,product.MaterialId,material.CategoryId AS MaterialCategoryId,
             product.Sku,card.[Name],product.VariantName,product.Price,product.KitchenStationId,
             product.PreparationMinutes,material.FulfillmentMode,material.BaseUnitId,material.TrackLots,
             CAST(COALESCE(activeBom.FrozenTheoreticalCost,
               CASE WHEN material.FulfillmentMode='StockItem' THEN material.BaseUnitPrice END,0) AS decimal(18,6)) AS TheoreticalCost
      FROM restaurante.Product product
      JOIN restaurante.ProductCard card
        ON card.Rfc=product.Rfc AND card.Id=product.ProductCardId
      LEFT JOIN logistica.Material material
        ON material.Rfc=product.Rfc AND material.Id=product.MaterialId
      OUTER APPLY
      (
        SELECT TOP (1) bomVersion.FrozenTheoreticalCost
        FROM logistica.BomHeader bomHeader
        JOIN logistica.BomVersion bomVersion
          ON bomVersion.Rfc=bomHeader.Rfc AND bomVersion.BomHeaderId=bomHeader.Id
        WHERE bomHeader.Rfc=product.Rfc
          AND bomHeader.ProductMaterialId=product.MaterialId
          AND bomVersion.[Status]='Active'
        ORDER BY bomVersion.Id DESC
      ) activeBom
      WHERE product.Rfc=@Rfc AND product.Id IN @ProductIds AND product.IsActive=1
        AND (@AllowInventoryOverride = 1 OR product.SoldOutOverride = 0);
      """;
    return (await conn.QueryAsync<ProductRow>(new CommandDefinition(
      sql,
      new { Rfc = rfc, ProductIds = productIds, AllowInventoryOverride = allowInventoryOverride },
      tx,
      cancellationToken: ct))).AsList();
  }

  private static async Task<long?> LoadActiveMenuIdAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    int siteId,
    DateTimeOffset localNow,
    CancellationToken ct)
    => await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
      """
      SELECT TOP (1) menuInfo.Id
      FROM restaurante.Menu menuInfo
      LEFT JOIN restaurante.MenuSchedule scheduleInfo
        ON scheduleInfo.Rfc=menuInfo.Rfc
       AND scheduleInfo.MenuId=menuInfo.Id
       AND scheduleInfo.SiteId=@SiteId
      WHERE menuInfo.Rfc=@Rfc
        AND menuInfo.IsActive=1
        AND menuInfo.IsPublished=1
        AND
        (
          scheduleInfo.Id IS NULL
          OR
          (
            scheduleInfo.DayOfWeek=@DayOfWeek
            AND
            (
              (scheduleInfo.StartsAt<scheduleInfo.EndsAt AND @LocalTime>=scheduleInfo.StartsAt AND @LocalTime<scheduleInfo.EndsAt)
              OR
              (scheduleInfo.StartsAt>scheduleInfo.EndsAt AND (@LocalTime>=scheduleInfo.StartsAt OR @LocalTime<scheduleInfo.EndsAt))
            )
          )
        )
      ORDER BY CASE WHEN scheduleInfo.Id IS NULL THEN 1 ELSE 0 END,menuInfo.Id;
      """,
      new
      {
        Rfc = rfc,
        SiteId = siteId,
        DayOfWeek = (byte)localNow.DayOfWeek,
        LocalTime = localNow.TimeOfDay
      },
      tx,
      cancellationToken: ct));

  private static async Task<IReadOnlyList<ComboOptionRow>> LoadComboOptionsAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    IReadOnlyCollection<long> comboProductIds,
    long? activeMenuId,
    CancellationToken ct)
  {
    if (comboProductIds.Count == 0)
    {
      return [];
    }
    const string sql =
      """
      SELECT slotInfo.ComboProductId,slotInfo.Id AS ComboSlotId,slotInfo.[Name] AS SlotName,
             slotInfo.MinSelections,slotInfo.MaxSelections,slotInfo.SortOrder AS SlotSortOrder,
             optionInfo.Id AS ComboSlotOptionId,optionInfo.ComponentProductId,optionInfo.Quantity AS OptionQuantity,
             optionInfo.PriceDelta AS OptionPriceDelta,optionInfo.IsDefault,optionInfo.SortOrder AS OptionSortOrder,
             routeInfo.MenuId AS RouteMenuId,routeInfo.MenuSectionId AS RouteMenuSectionId,
             routeSection.[Name] AS RouteMenuSectionName,routeSection.SortOrder AS RouteMenuSectionSortOrder
      FROM restaurante.ComboSlot slotInfo
      LEFT JOIN restaurante.ComboSlotOption optionInfo
        ON optionInfo.Rfc=slotInfo.Rfc AND optionInfo.ComboSlotId=slotInfo.Id AND optionInfo.IsActive=1
      LEFT JOIN restaurante.ComboSlotOptionRoute routeInfo
        ON routeInfo.Rfc=optionInfo.Rfc AND routeInfo.ComboSlotOptionId=optionInfo.Id AND routeInfo.MenuId=@MenuId
      LEFT JOIN restaurante.MenuSection routeSection
        ON routeSection.Rfc=routeInfo.Rfc AND routeSection.Id=routeInfo.MenuSectionId
      WHERE slotInfo.Rfc=@Rfc AND slotInfo.ComboProductId IN @ComboProductIds AND slotInfo.IsActive=1
      ORDER BY slotInfo.ComboProductId,slotInfo.SortOrder,slotInfo.Id,optionInfo.SortOrder,optionInfo.Id;
      """;
    return (await conn.QueryAsync<ComboOptionRow>(new CommandDefinition(
      sql,
      new { Rfc = rfc, ComboProductIds = comboProductIds, MenuId = activeMenuId },
      tx,
      cancellationToken: ct))).AsList();
  }

  private static IReadOnlyDictionary<RestaurantOrderLineCreateRequest, ComboPlan> BuildComboPlans(
    RestaurantOrderCreateRequest request,
    IReadOnlyList<ProductRow> products,
    IReadOnlyList<ComboOptionRow> comboRows,
    IReadOnlyList<MenuSectionSnapshotRow> menuSections,
    IReadOnlyList<ModifierRow> modifierRows)
  {
    var plans = new Dictionary<RestaurantOrderLineCreateRequest, ComboPlan>();
    foreach (var line in request.Lines.Where(line => !line.IsCustom))
    {
      var product = products.Single(item => item.Id == line.ProductId!.Value);
      if (!string.Equals(product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase))
      {
        if (line.ComboSelections.Count > 0)
        {
          throw new InvalidOperationException("Solo los productos tipo combo aceptan selecciones de combo.");
        }
        continue;
      }
      if (line.ModifierOptionIds.Count > 0)
      {
        throw new InvalidOperationException("Los modificadores de un combo deben capturarse en cada componente.");
      }
      var productSlots = comboRows
        .Where(row => row.ComboProductId == product.Id)
        .GroupBy(row => row.ComboSlotId)
        .OrderBy(group => group.First().SlotSortOrder)
        .ThenBy(group => group.Key)
        .ToList();
      var slotRules = productSlots.Select(slot =>
      {
        var definition = slot.First();
        return new RestaurantComboOrderSlotRule(
          slot.Key,
          definition.SlotName,
          definition.MinSelections,
          definition.MaxSelections,
          slot.Where(option => option.ComboSlotOptionId.HasValue && option.ComponentProductId.HasValue)
            .Select(option => new RestaurantComboOrderOptionRule(
              slot.Key,
              option.ComboSlotOptionId!.Value,
              option.ComponentProductId!.Value))
            .ToList());
      }).ToList();
      _ = RestaurantComboOrderRules.ValidateAndResolveSelections(product.Sku, slotRules, line.ComboSelections);

      var components = new List<PricedComboComponent>();
      foreach (var slot in productSlots)
      {
        var selections = line.ComboSelections.Where(selection => selection.ComboSlotId == slot.Key).ToList();
        foreach (var selection in selections)
        {
          if (selection.Notes?.Trim().Length > 500)
          {
            throw new InvalidOperationException("La nota de un componente no puede exceder 500 caracteres.");
          }
          var option = slot.SingleOrDefault(row => row.ComboSlotOptionId == selection.ComboSlotOptionId && row.ComponentProductId.HasValue)
            ?? throw new InvalidOperationException("Una opción está inactiva o no pertenece al combo y RFC seleccionados.");
          var component = products.Single(item => item.Id == option.ComponentProductId!.Value);
          var selectedModifiers = ValidateProductModifierSelection(
            component.Id,
            selection.ModifierOptionIds,
            modifierRows,
            $"el componente {component.Sku}");
          MenuSectionSnapshotRow route;
          if (option.RouteMenuSectionId.HasValue)
          {
            route = new MenuSectionSnapshotRow
            {
              ProductId = component.Id,
              MenuSectionId = option.RouteMenuSectionId.Value,
              MenuSectionName = option.RouteMenuSectionName
                ?? throw new InvalidOperationException("La ruta operacional del componente no tiene sección válida."),
              MenuSectionSortOrder = option.RouteMenuSectionSortOrder ?? int.MaxValue
            };
          }
          else
          {
            var inferredRoutes = menuSections.Where(item => item.ProductId == component.Id).ToList();
            if (inferredRoutes.Count != 1)
            {
              throw new InvalidOperationException(
                inferredRoutes.Count == 0
                  ? $"El componente {component.Sku} no tiene una ruta operacional en el menú activo."
                  : $"El componente {component.Sku} aparece en varias secciones; configura su ruta operacional en el combo.");
            }
            route = inferredRoutes[0];
          }
          components.Add(new PricedComboComponent(
            option.ComboSlotId,
            option.ComboSlotOptionId!.Value,
            option.SlotName,
            component,
            option.OptionQuantity,
            option.OptionPriceDelta,
            selectedModifiers,
            NullIfWhiteSpace(selection.Notes),
            route.MenuSectionId,
            route.MenuSectionName,
            route.MenuSectionSortOrder));
        }
      }
      plans[line] = new ComboPlan(components);
    }
    return plans;
  }

  private static async Task<IReadOnlyList<MenuSectionSnapshotRow>> LoadMenuSectionSnapshotsAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    long? activeMenuId,
    IReadOnlyCollection<long> productIds,
    CancellationToken ct)
  {
    if (productIds.Count == 0)
    {
      return [];
    }

    const string sql =
      """
      SELECT item.ProductId,
             sectionInfo.Id AS MenuSectionId,
             sectionInfo.[Name] AS MenuSectionName,
             sectionInfo.SortOrder AS MenuSectionSortOrder
      FROM restaurante.MenuItem item
      JOIN restaurante.MenuSection sectionInfo
        ON sectionInfo.Rfc=item.Rfc AND sectionInfo.Id=item.MenuSectionId
      WHERE item.Rfc=@Rfc
        AND sectionInfo.MenuId=@MenuId
        AND item.ProductId IN @ProductIds
      ORDER BY sectionInfo.SortOrder,sectionInfo.Id,item.SortOrder,item.ProductId;
      """;

    return (await conn.QueryAsync<MenuSectionSnapshotRow>(new CommandDefinition(
      sql,
      new
      {
        Rfc = rfc,
        MenuId = activeMenuId,
        ProductIds = productIds
      },
      tx,
      cancellationToken: ct))).AsList();
  }

  private static async Task ValidateOperationalReferencesAsync(DbConnection conn, DbTransaction tx, string rfc, RestaurantOrderCreateRequest request, CancellationToken ct)
  {
    var orderType = NormalizeOrderType(request.OrderType);
    var salesChannel = string.IsNullOrWhiteSpace(request.SalesChannel)
      ? RestaurantSalesChannels.Pos
      : request.SalesChannel.Trim();
    if (string.Equals(salesChannel, RestaurantSalesChannels.Pos, StringComparison.OrdinalIgnoreCase) &&
        (!request.CashRegisterId.HasValue || !request.CashShiftId.HasValue))
    {
      throw new InvalidOperationException("Las ventas de Punto de Venta requieren seleccionar un turno de caja abierto.");
    }
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
          """
          SELECT CAST(CASE WHEN EXISTS
          (
            SELECT 1
            FROM restaurante.CashShift shiftInfo
            JOIN restaurante.CashRegister registerInfo
              ON registerInfo.Rfc=shiftInfo.Rfc AND registerInfo.Id=shiftInfo.CashRegisterId
            WHERE shiftInfo.Rfc=@Rfc AND shiftInfo.SiteId=@SiteId AND shiftInfo.Id=@Id
              AND shiftInfo.[Status]='Open' AND registerInfo.IsActive=1
              AND (@CashRegisterId IS NULL OR shiftInfo.CashRegisterId=@CashRegisterId)
          ) THEN 1 ELSE 0 END AS bit);
          """,
          new { Rfc = rfc, request.SiteId, Id = request.CashShiftId, request.CashRegisterId }, tx, cancellationToken: ct)))
    {
      throw new InvalidOperationException("El turno y la caja seleccionados no coinciden o ya no están abiertos en la sede y RFC seleccionados.");
    }
    if (request.ExternalProviderId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
          "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM restaurante.ExternalProvider WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id AND IsActive=1) THEN 1 ELSE 0 END AS bit);",
          new { Rfc = rfc, request.SiteId, Id = request.ExternalProviderId }, tx, cancellationToken: ct)))
    {
      throw new InvalidOperationException("El proveedor externo no pertenece a la sede y RFC seleccionados.");
    }
  }

  private static void ValidateModifiers(
    RestaurantOrderCreateRequest request,
    IReadOnlyList<ProductRow> products,
    IReadOnlyList<ModifierRow> modifierRows)
  {
    foreach (var line in request.Lines.Where(line => !line.IsCustom))
    {
      if (line.Notes?.Trim().Length > 500)
      {
        throw new InvalidOperationException("La nota de una partida no puede exceder 500 caracteres.");
      }
      var product = products.Single(item => item.Id == line.ProductId!.Value);
      if (string.Equals(product.ProductKind, RestaurantProductKinds.Combo, StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }
      _ = ValidateProductModifierSelection(product.Id, line.ModifierOptionIds, modifierRows, $"el producto {product.Sku}");
    }
  }

  private static List<ModifierRow> ValidateProductModifierSelection(
    long productId,
    IReadOnlyList<long> selectedOptionIds,
    IReadOnlyList<ModifierRow> modifierRows,
    string context)
  {
    if (selectedOptionIds.Count != selectedOptionIds.Distinct().Count())
    {
      throw new InvalidOperationException($"No se puede repetir el mismo modificador en {context}.");
    }
    var productModifiers = modifierRows.Where(row => row.ProductId == productId).ToList();
    foreach (var optionId in selectedOptionIds)
    {
      if (productModifiers.All(row => row.Id != optionId))
      {
        throw new InvalidOperationException($"Un modificador no corresponde a {context} o al RFC seleccionado.");
      }
    }
    foreach (var group in productModifiers.GroupBy(row => row.ModifierGroupId))
    {
      var selected = group.Count(row => selectedOptionIds.Contains(row.Id));
      var definition = group.First();
      if (selected < definition.MinSelections || selected > definition.MaxSelections)
      {
        throw new InvalidOperationException(
          $"El grupo {definition.GroupName} requiere entre {definition.MinSelections} y {definition.MaxSelections} opciones para {context}.");
      }
    }
    return productModifiers.Where(row => selectedOptionIds.Contains(row.Id)).ToList();
  }

  private static async Task<InventoryRequirementPlan> BuildRequirementsAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    IReadOnlyList<PricedLine> lines,
    bool allowInventoryOverride,
    CancellationToken ct)
  {
    var selectedOptionIds = lines
      .SelectMany(line => line.Modifiers.Concat(line.ComboComponents.SelectMany(component => component.Modifiers)))
      .Select(modifier => modifier.Id)
      .Distinct()
      .ToArray();
    var graph = await RestaurantRequirementGraphLoader.LoadAsync(conn, tx, rfc, selectedOptionIds, ct);
    var requirements = new Dictionary<int, decimal>();
    var overrideReasons = new List<string>();
    foreach (var line in lines)
    {
      if (line.IsCustom || line.Product is null)
      {
        continue;
      }
      if (line.ComboComponents.Count > 0)
      {
        foreach (var component in line.ComboComponents)
        {
          AddProductRequirements(
            component.Product,
            component.TotalQuantity(line.Request.Quantity),
            component.Modifiers,
            graph,
            requirements,
            overrideReasons,
            allowInventoryOverride);
        }
      }
      else
      {
        AddProductRequirements(
          line.Product,
          line.Request.Quantity,
          line.Modifiers,
          graph,
          requirements,
          overrideReasons,
          allowInventoryOverride);
      }
    }
    return new InventoryRequirementPlan(
      requirements.Where(item => item.Value > 0).ToDictionary(item => item.Key, item => item.Value),
      overrideReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
  }

  private static void AddProductRequirements(
    ProductRow product,
    decimal quantity,
    IReadOnlyList<ModifierRow> modifiers,
    RestaurantSaleRequirementGraph graph,
    IDictionary<int, decimal> requirements,
    ICollection<string> overrideReasons,
    bool allowInventoryOverride)
  {
    if (!product.MaterialId.HasValue)
    {
      var message = $"El producto {product.Sku} no tiene material para calcular inventario.";
      if (!allowInventoryOverride)
      {
        throw new InvalidOperationException(message);
      }
      overrideReasons.Add(message);
      return;
    }
    var calculation = RestaurantSaleRequirementCalculator.Calculate(
      graph,
      product.MaterialId.Value,
      product.Sku,
      quantity,
      modifiers.Select(modifier => modifier.Id).ToArray());
    var issue = calculation.Issues.FirstOrDefault();
    if (issue is not null)
    {
      if (!allowInventoryOverride)
      {
        throw new InvalidOperationException(issue.Message);
      }
      overrideReasons.Add(issue.Message);
    }
    foreach (var requirement in calculation.Requirements)
    {
      AddRequirement(requirements, requirement.Key, requirement.Value);
    }
  }

  private static async Task<decimal> CalculateRequirementCostAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    IReadOnlyDictionary<int, decimal> requirements,
    CancellationToken ct)
  {
    if (requirements.Count == 0)
    {
      return 0;
    }
    var costs = (await conn.QueryAsync<MaterialCostRow>(new CommandDefinition(
      """
      SELECT Id,CAST(ISNULL(BaseUnitPrice,0) AS decimal(18,6)) AS BaseUnitPrice
      FROM logistica.Material
      WHERE Rfc=@Rfc AND Id IN @MaterialIds;
      """,
      new { Rfc = rfc, MaterialIds = requirements.Keys.ToArray() },
      tx,
      cancellationToken: ct))).ToDictionary(item => item.Id, item => item.BaseUnitPrice);
    return decimal.Round(requirements.Sum(requirement =>
      requirement.Value * costs.GetValueOrDefault(requirement.Key)), 6, MidpointRounding.AwayFromZero);
  }

  private static async Task<ReservationResult> ReserveInventoryAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId,
    Guid orderId, string orderIdempotencyKey, IReadOnlyDictionary<int, decimal> requirements, bool allowDeficit,
    IReadOnlyList<string> initialOverrideReasons, string userName, CancellationToken ct)
  {
    var reservationId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
      """
      INSERT INTO logistica.InventoryReservation
        (Rfc, SiteId, ReferenceType, ReferenceId, IdempotencyKey, [Status], CreatedBy)
      VALUES
        (@Rfc, @SiteId, 'RestaurantOrder', @OrderId, @IdempotencyKey, 'Reserved', @CreatedBy);
      SELECT CAST(SCOPE_IDENTITY() AS bigint);
      """, new { Rfc = rfc, SiteId = siteId, OrderId = orderId, IdempotencyKey = $"ORDER:{orderIdempotencyKey}", CreatedBy = userName }, tx, cancellationToken: ct));
    var hasDeficit = initialOverrideReasons.Count > 0;
    var overrideReasons = new List<string>(initialOverrideReasons);
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
          """, new { Rfc = rfc, SiteId = siteId }, tx, cancellationToken: ct));
        if (fallbackLocation.HasValue)
        {
          await InsertReservationLineAsync(
            conn, tx, rfc, reservationId, requirement.Key, fallbackLocation.Value, null, needed, 0, true, 0, ct);
        }
        else
        {
          overrideReasons.Add($"El material {requirement.Key} no tiene una ubicación de inventario configurada.");
        }
        hasDeficit = true;
      }
    }
    return new ReservationResult(
      reservationId,
      hasDeficit,
      overrideReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
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
      "SELECT [Status] FROM restaurante.OrderLine WHERE Rfc=@Rfc AND OrderId=@OrderId AND LineKind<>'Combo';",
      new { Rfc = rfc, OrderId = orderId }, tx, cancellationToken: ct))).AsList();
    var next = statuses.All(status => status is "Ready" or "Delivered" or "Cancelled")
      ? "Ready"
      : statuses.Any(status => status == "Preparing") ? "Preparing" : "Sent";
    await conn.ExecuteAsync(new CommandDefinition(
      """
      UPDATE comboParent
      SET [Status]=componentState.[Status],
          StartedAt=CASE WHEN componentState.[Status]='Preparing' AND comboParent.StartedAt IS NULL
                         THEN SYSUTCDATETIME() ELSE comboParent.StartedAt END,
          ReadyAt=CASE WHEN componentState.[Status]='Ready' AND comboParent.ReadyAt IS NULL
                       THEN SYSUTCDATETIME() ELSE comboParent.ReadyAt END
      FROM restaurante.OrderLine comboParent
      CROSS APPLY
      (
        SELECT CASE
          WHEN COUNT(*)=SUM(CASE WHEN child.[Status] IN ('Ready','Delivered','Cancelled') THEN 1 ELSE 0 END) THEN 'Ready'
          WHEN SUM(CASE WHEN child.[Status]='Preparing' THEN 1 ELSE 0 END)>0 THEN 'Preparing'
          ELSE 'Pending'
        END AS [Status]
        FROM restaurante.OrderLine child
        WHERE child.Rfc=comboParent.Rfc AND child.ParentOrderLineId=comboParent.Id
      ) componentState
      WHERE comboParent.Rfc=@Rfc AND comboParent.OrderId=@OrderId AND comboParent.LineKind='Combo';

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
              orderInfo.Total, orderInfo.BalanceDue,orderInfo.PromotionDiscountTotal,
              orderInfo.MemberId,orderInfo.MembershipNumberSnapshot AS MembershipNumber,orderInfo.PointsEarned,
              orderInfo.RedeemedPoints AS PointsRedeemed,orderInfo.RedemptionValue,
              orderInfo.CashRegisterId, orderInfo.CashShiftId,
              orderInfo.Priority,orderInfo.PriorityReason,orderInfo.PrioritizedBy,orderInfo.CreatedAt
      FROM restaurante.[Order] orderInfo
      LEFT JOIN restaurante.DiningTable diningTable ON diningTable.Rfc=orderInfo.Rfc AND diningTable.Id=orderInfo.DiningTableId
      WHERE orderInfo.Rfc=@Rfc AND orderInfo.Id=@OrderId;
      SELECT lineInfo.Id,lineInfo.ProductId,lineInfo.ProductNameSnapshot AS ProductName,
             lineInfo.IsCustom,lineInfo.Quantity,lineInfo.[Status],lineInfo.Notes,
             product.PreparationMinutes,lineInfo.StartedAt,lineInfo.ReadyAt,
             lineInfo.MenuSectionIdSnapshot AS MenuSectionId,
             lineInfo.MenuSectionNameSnapshot AS MenuSectionName,
             lineInfo.MenuSectionSortOrderSnapshot AS MenuSectionSortOrder,
             lineInfo.LineKind,lineInfo.ParentOrderLineId,lineInfo.ComboSlotId,lineInfo.ComboSlotOptionId,
             lineInfo.ParentProductNameSnapshot AS ParentProductName,
             lineInfo.ComboSlotNameSnapshot AS ComboSlotName,
             lineInfo.UnitPrice,lineInfo.BaseUnitPrice,lineInfo.ChoicePriceDelta
      FROM restaurante.OrderLine lineInfo
      LEFT JOIN restaurante.Product product ON product.Rfc=lineInfo.Rfc AND product.Id=lineInfo.ProductId
      WHERE lineInfo.Rfc=@Rfc AND lineInfo.OrderId=@OrderId
      ORDER BY CASE WHEN lineInfo.ParentOrderLineId IS NULL THEN lineInfo.Id ELSE lineInfo.ParentOrderLineId END,
               CASE WHEN lineInfo.ParentOrderLineId IS NULL THEN 0 ELSE 1 END,lineInfo.Id;
      SELECT modifier.OrderLineId,modifier.ModifierOptionId,
             modifier.ModifierGroupNameSnapshot AS GroupName,
             COALESCE(effectInfo.MaterialNameSnapshot,modifier.[Name]) AS [Name],
             CASE
               WHEN effectInfo.Id IS NULL
                 OR effectInfo.Id=MIN(effectInfo.Id) OVER (PARTITION BY modifier.Id)
               THEN modifier.PriceDelta ELSE 0
             END AS PriceDelta,
             modifier.Quantity,COALESCE(effectInfo.EffectKind,modifier.EffectKind) AS EffectKind
      FROM restaurante.OrderLineModifier modifier
      JOIN restaurante.OrderLine lineInfo ON lineInfo.Rfc=modifier.Rfc AND lineInfo.Id=modifier.OrderLineId
      LEFT JOIN restaurante.OrderLineModifierIngredientEffect effectInfo
        ON effectInfo.Rfc=modifier.Rfc AND effectInfo.OrderLineModifierId=modifier.Id
      WHERE modifier.Rfc=@Rfc AND lineInfo.OrderId=@OrderId ORDER BY modifier.Id,effectInfo.Id;
      """;
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = rfc, OrderId = orderId }, tx, cancellationToken: ct));
    var order = await multi.ReadSingleOrDefaultAsync<RestaurantOrderDto>();
    var lines = (await multi.ReadAsync<RestaurantOrderLineDto>()).AsList();
    var modifiers = (await multi.ReadAsync<OrderLineModifierRow>()).AsList();
    if (order is null) return null;
    foreach (var line in lines)
    {
      var lineModifiers = modifiers.Where(item => item.OrderLineId == line.Id).ToList();
      line.Modifiers = lineModifiers.Select(item => item.Name).ToList();
      line.StructuredModifiers = lineModifiers.Select(ToModifierDto).ToList();
    }
    order.Lines = lines;
    return order;
  }

  private static Task AddOutboxEventAsync(DbConnection conn, DbTransaction tx, string rfc, int siteId, string eventType,
    string aggregateId, object payload, CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      "INSERT INTO restaurante.EventOutbox (Rfc, SiteId, EventType, AggregateId, Payload) VALUES (@Rfc,@SiteId,@EventType,@AggregateId,@Payload);",
      new { Rfc = rfc, SiteId = siteId, EventType = eventType, AggregateId = aggregateId, Payload = JsonSerializer.Serialize(payload) }, tx, cancellationToken: ct));

  private static Task InsertLineModifierAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    long orderLineId,
    ModifierRow modifier,
    CancellationToken ct)
    => conn.ExecuteAsync(new CommandDefinition(
      """
      INSERT INTO restaurante.OrderLineModifier
        (Rfc,OrderLineId,ModifierOptionId,[Name],PriceDelta,Quantity,ModifierGroupNameSnapshot,EffectKind)
      VALUES
        (@Rfc,@OrderLineId,@ModifierOptionId,@Name,@PriceDelta,1,@GroupName,@EffectKind);
      """,
      new
      {
        Rfc = rfc,
        OrderLineId = orderLineId,
        ModifierOptionId = modifier.Id,
        modifier.Name,
        modifier.PriceDelta,
        GroupName = modifier.GroupName,
        EffectKind = string.IsNullOrWhiteSpace(modifier.EffectKind)
          ? RestaurantModifierEffectKinds.AdjustQuantity
          : modifier.EffectKind
      },
      tx,
      cancellationToken: ct));

  private static RestaurantOrderLineModifierDto ToModifierDto(OrderLineModifierRow modifier)
    => new()
    {
      ModifierOptionId = modifier.ModifierOptionId,
      GroupName = modifier.GroupName,
      Name = modifier.Name,
      PriceDelta = modifier.PriceDelta,
      Quantity = modifier.Quantity,
      EffectKind = string.IsNullOrWhiteSpace(modifier.EffectKind)
        ? RestaurantModifierEffectKinds.AdjustQuantity
        : modifier.EffectKind
    };

  private static RestaurantComboPriceSelection ToPriceSelection(PricedComboComponent component)
    => new(
      component.OptionPriceDelta,
      component.OptionQuantity,
      component.Modifiers.Select(modifier => modifier.PriceDelta).ToArray());

  private static async Task PersistPromotionSnapshotsAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    Guid orderId,
    Guid? memberId,
    RestaurantPromotionQuoteDto quote,
    IReadOnlyDictionary<string, long> lineIds,
    CancellationToken ct)
  {
    foreach (var adjustment in quote.Adjustments)
    {
      var promotionUpdated = await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.Promotion
        SET RedemptionCount=RedemptionCount+1
        WHERE Rfc=@Rfc AND Id=@PromotionId
          AND (GlobalLimit IS NULL OR RedemptionCount<GlobalLimit);
        """,
        new { Rfc = rfc, PromotionId = adjustment.PromotionId },
        tx,
        cancellationToken: ct));
      if (promotionUpdated != 1)
      {
        throw new InvalidOperationException(
          $"La promoción {adjustment.PromotionName} alcanzó su límite mientras se cobraba la orden.");
      }

      long? codeId = null;
      if (!string.IsNullOrWhiteSpace(adjustment.Code))
      {
        var code = await conn.QuerySingleOrDefaultAsync<PromotionCodeLimitRow>(new CommandDefinition(
          """
          SELECT Id,GlobalLimit,PerMemberLimit,RedemptionCount
          FROM restaurante.PromotionCode WITH(UPDLOCK,HOLDLOCK)
          WHERE Rfc=@Rfc AND PromotionId=@PromotionId AND Code=@Code AND IsActive=1;
          """,
          new
          {
            Rfc = rfc,
            PromotionId = adjustment.PromotionId,
            Code = adjustment.Code
          },
          tx,
          cancellationToken: ct))
          ?? throw new InvalidOperationException("El código promocional dejó de estar disponible.");
        if (code.GlobalLimit.HasValue && code.RedemptionCount >= code.GlobalLimit.Value)
        {
          throw new InvalidOperationException("El código promocional alcanzó su límite global.");
        }
        if (code.PerMemberLimit.HasValue)
        {
          if (!memberId.HasValue)
          {
            throw new InvalidOperationException("El código requiere una membresía verificada.");
          }
          var memberUses = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM restaurante.PromotionRedemption WITH(UPDLOCK,HOLDLOCK)
            WHERE Rfc=@Rfc AND CodeId=@CodeId AND MemberId=@MemberId;
            """,
            new { Rfc = rfc, CodeId = code.Id, MemberId = memberId.Value },
            tx,
            cancellationToken: ct));
          if (memberUses >= code.PerMemberLimit.Value)
          {
            throw new InvalidOperationException("La membresía ya alcanzó el límite de este código.");
          }
        }
        var codeUpdated = await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE restaurante.PromotionCode
          SET RedemptionCount=RedemptionCount+1
          WHERE Rfc=@Rfc AND Id=@Id
            AND (GlobalLimit IS NULL OR RedemptionCount<GlobalLimit);
          """,
          new { Rfc = rfc, code.Id },
          tx,
          cancellationToken: ct));
        if (codeUpdated != 1)
        {
          throw new InvalidOperationException("El código promocional alcanzó su límite mientras se cobraba la orden.");
        }
        codeId = code.Id;
      }

      var orderPromotionId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
        """
        INSERT restaurante.OrderPromotion
          (Rfc,OrderId,PromotionId,PromotionNameSnapshot,RuleTypeSnapshot,
           CodeId,CodeSnapshot,DiscountAmount)
        VALUES
          (@Rfc,@OrderId,@PromotionId,@PromotionName,@RuleType,
           @CodeId,@Code,@DiscountAmount);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """,
        new
        {
          Rfc = rfc,
          OrderId = orderId,
          PromotionId = adjustment.PromotionId,
          PromotionName = adjustment.PromotionName,
          RuleType = adjustment.RuleType,
          CodeId = codeId,
          Code = adjustment.Code,
          adjustment.DiscountAmount
        },
        tx,
        cancellationToken: ct));

      foreach (var lineAdjustment in quote.LineAdjustments.Where(item =>
                 item.PromotionId == adjustment.PromotionId &&
                 lineIds.ContainsKey(item.LineKey)))
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT restaurante.OrderLinePromotion
            (Rfc,OrderPromotionId,OrderLineId,AppliedQuantity,DiscountAmount)
          VALUES
            (@Rfc,@OrderPromotionId,@OrderLineId,@AppliedQuantity,@DiscountAmount);
          """,
          new
          {
            Rfc = rfc,
            OrderPromotionId = orderPromotionId,
            OrderLineId = lineIds[lineAdjustment.LineKey],
            lineAdjustment.AppliedQuantity,
            lineAdjustment.DiscountAmount
          },
          tx,
          cancellationToken: ct));
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        INSERT restaurante.PromotionRedemption
          (Rfc,PromotionId,CodeId,OrderId,MemberId,DiscountAmount)
        VALUES
          (@Rfc,@PromotionId,@CodeId,@OrderId,@MemberId,@DiscountAmount);
        """,
        new
        {
          Rfc = rfc,
          PromotionId = adjustment.PromotionId,
          CodeId = codeId,
          OrderId = orderId,
          MemberId = memberId,
          adjustment.DiscountAmount
        },
        tx,
        cancellationToken: ct));
    }
  }

  private static IReadOnlyDictionary<string, decimal> AllocateDiscount(
    decimal amount,
    IReadOnlyList<(string Key, decimal Weight)> weights)
  {
    var result = weights.ToDictionary(item => item.Key, _ => 0m, StringComparer.Ordinal);
    var remainingAmount = decimal.Round(Math.Max(0, amount), 2, MidpointRounding.AwayFromZero);
    var remainingWeight = weights.Sum(item => Math.Max(0, item.Weight));

    foreach (var (key, rawWeight) in weights)
    {
      var weight = Math.Max(0, rawWeight);
      if (remainingAmount <= 0 || remainingWeight <= 0 || weight <= 0)
      {
        remainingWeight -= weight;
        continue;
      }

      var allocation = decimal.Round(
        remainingAmount * weight / remainingWeight,
        2,
        MidpointRounding.AwayFromZero);
      allocation = Math.Clamp(allocation, 0, Math.Min(weight, remainingAmount));
      result[key] = allocation;
      remainingAmount = decimal.Round(remainingAmount - allocation, 2, MidpointRounding.AwayFromZero);
      remainingWeight -= weight;
    }

    if (remainingAmount > 0)
    {
      foreach (var (key, rawWeight) in weights.Reverse())
      {
        var capacity = Math.Max(0, rawWeight - result[key]);
        var extra = Math.Min(capacity, remainingAmount);
        result[key] += extra;
        remainingAmount = decimal.Round(remainingAmount - extra, 2, MidpointRounding.AwayFromZero);
        if (remainingAmount <= 0)
        {
          break;
        }
      }
    }

    return result;
  }

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
      SELECT Id AS OrderId,Folio,CustomerName,OperationalDate,[Status],PaymentStatus,Total,BalanceDue,
             PromotionDiscountTotal,MembershipNumberSnapshot AS MembershipNumber,PointsEarned,
             RedeemedPoints AS PointsRedeemed,RedemptionValue
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

  private static string DescribeInventoryOverride(IReadOnlyList<string> reasons)
  {
    const string fallback = "Venta autorizada por supervisor con excepción de inventario.";
    if (reasons.Count == 0)
    {
      return fallback;
    }

    var description = $"{fallback} {string.Join(" | ", reasons)}";
    return description.Length <= 480 ? description : $"{description[..477]}...";
  }

  private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private sealed record PricedLine(
    string LineKey,
    RestaurantOrderLineCreateRequest Request,
    ProductRow? Product,
    List<ModifierRow> Modifiers,
    string ProductName,
    string Sku,
    decimal UnitPrice,
    decimal Gross,
    bool IsCustom,
    long? MenuSectionId,
    string? MenuSectionName,
    int? MenuSectionSortOrder,
    IReadOnlyList<PricedComboComponent> ComboComponents);
  private sealed record ComboPlan(
    IReadOnlyList<PricedComboComponent> Components);
  private sealed record PricedComboComponent(
    long ComboSlotId,
    long ComboSlotOptionId,
    string SlotName,
    ProductRow Product,
    decimal OptionQuantity,
    decimal OptionPriceDelta,
    List<ModifierRow> Modifiers,
    string? Notes,
    long MenuSectionId,
    string MenuSectionName,
    int MenuSectionSortOrder)
  {
    public decimal TotalQuantity(decimal comboQuantity)
      => decimal.Round(OptionQuantity * comboQuantity, 4, MidpointRounding.AwayFromZero);
  }
  private sealed class MenuSectionSnapshotRow
  {
    public long ProductId { get; set; }
    public long MenuSectionId { get; set; }
    public string MenuSectionName { get; set; } = string.Empty;
    public int MenuSectionSortOrder { get; set; }
  }
  private sealed record InventoryRequirementPlan(
    IReadOnlyDictionary<int, decimal> Requirements,
    IReadOnlyList<string> OverrideReasons);
  private sealed record ReservationResult(
    long? ReservationId,
    bool HasDeficit,
    IReadOnlyList<string> OverrideReasons);
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
    public string ProductKind { get; set; } = RestaurantProductKinds.Standard;
    public int? MaterialId { get; set; }
    public int? MaterialCategoryId { get; set; }
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
    public string? EffectKind { get; set; }
    public List<ModifierEffectSnapshot> Effects { get; set; } = [];
  }
  private sealed record ModifierEffectSnapshot(string Name, string EffectKind);
  private sealed class ModifierEffectRow
  {
    public long ModifierOptionId { get; set; }
    public string EffectKind { get; set; } = RestaurantModifierEffectKinds.AdjustQuantity;
    public string MaterialName { get; set; } = string.Empty;
  }
  private sealed class ComboOptionRow
  {
    public long ComboProductId { get; set; }
    public long ComboSlotId { get; set; }
    public string SlotName { get; set; } = string.Empty;
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; }
    public int SlotSortOrder { get; set; }
    public long? ComboSlotOptionId { get; set; }
    public long? ComponentProductId { get; set; }
    public decimal OptionQuantity { get; set; }
    public decimal OptionPriceDelta { get; set; }
    public bool IsDefault { get; set; }
    public int OptionSortOrder { get; set; }
    public long? RouteMenuId { get; set; }
    public long? RouteMenuSectionId { get; set; }
    public string? RouteMenuSectionName { get; set; }
    public int? RouteMenuSectionSortOrder { get; set; }
  }
  private sealed class MaterialInventoryRow { public int Id { get; set; } public bool TrackLots { get; set; } }
  private sealed class MaterialCostRow { public int Id { get; set; } public decimal BaseUnitPrice { get; set; } }
  private sealed class LotAvailabilityRow { public long MaterialLotId { get; set; } public int LocationId { get; set; } public decimal AvailableQuantity { get; set; } public decimal UnitCost { get; set; } }
  private sealed class BalanceAvailabilityRow { public int LocationId { get; set; } public decimal AvailableQuantity { get; set; } public decimal AverageUnitCost { get; set; } }
  private sealed class ReservationLineRow { public long Id { get; set; } public int MaterialId { get; set; } public int LocationId { get; set; } public long? MaterialLotId { get; set; } public decimal ReservedQuantity { get; set; } public decimal FrozenUnitCost { get; set; } }
  private sealed class StockBalanceRow { public int Id { get; set; } public decimal Quantity { get; set; } public decimal ReservedQuantity { get; set; } }
  private sealed class LineIdentityRow { public long Id { get; set; } public Guid OrderId { get; set; } public string ProductNameSnapshot { get; set; } = string.Empty; public bool IsCustom { get; set; } public string LineKind { get; set; } = RestaurantOrderLineKinds.Standard; public string Status { get; set; } = string.Empty; public int SiteId { get; set; } public long? InventoryReservationId { get; set; } }
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
  private sealed class PromotionCodeLimitRow
  {
    public long Id { get; set; }
    public int? GlobalLimit { get; set; }
    public int? PerMemberLimit { get; set; }
    public int RedemptionCount { get; set; }
  }
  private sealed class OrderLineModifierRow
  {
    public long OrderLineId { get; set; }
    public long ModifierOptionId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal PriceDelta { get; set; }
    public int Quantity { get; set; } = 1;
    public string EffectKind { get; set; } = RestaurantModifierEffectKinds.AdjustQuantity;
  }
}
