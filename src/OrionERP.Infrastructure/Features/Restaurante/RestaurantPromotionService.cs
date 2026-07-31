using System.Data;
using System.Data.Common;
using Dapper;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantPromotionService : IRestaurantPromotionService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantPromotionService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<RestaurantPromotionQuoteDto> QuoteAsync(
    RestaurantPromotionQuoteRequest request,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    var site = await conn.QuerySingleOrDefaultAsync<SiteTimeZoneRow>(new CommandDefinition(
      """
      SELECT siteInfo.TimeZoneId,
             CAST(ISNULL(settings.IsPromotionsEnabled,0) AS bit) AS IsPromotionsEnabled
      FROM restaurante.Site siteInfo
      LEFT JOIN restaurante.PublicSiteSettings settings
        ON settings.Rfc=siteInfo.Rfc AND settings.SiteId=siteInfo.Id
      WHERE siteInfo.Rfc=@Rfc AND siteInfo.Id=@SiteId AND siteInfo.IsEnabled=1;
      """,
      new { Rfc = rfc, request.SiteId },
      cancellationToken: ct))
      ?? throw new InvalidOperationException("La sede no existe o está deshabilitada.");
    var localAt = ConvertToSiteTime(request.At, site.TimeZoneId);
    if (!site.IsPromotionsEnabled)
    {
      return EmptyQuote(request, localAt, "Las promociones están deshabilitadas para esta sede.");
    }

    var definitions = await LoadDefinitionsAsync(
      conn,
      null,
      rfc,
      request.SiteId,
      request.MemberId,
      request.Code,
      includeInactive: false,
      ct);
    return RestaurantPromotionEngine.Quote(request, definitions, localAt);
  }

  public async Task<IReadOnlyList<RestaurantPromotionDto>> GetPromotionsAsync(
    string rfc,
    int? siteId = null,
    bool includeInactive = true,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    var rows = (await conn.QueryAsync<RestaurantPromotionDto>(new CommandDefinition(
      """
      SELECT promotion.Id,promotion.Rfc,promotion.SiteId,siteInfo.[Name] AS SiteName,
             promotion.[Name],promotion.PublicDescription,promotion.PublicTerms,promotion.[Status],
             promotion.RuleType,promotion.Priority,promotion.ValidFromLocal,promotion.ValidToLocal,
             promotion.PosEnabled,promotion.WebEnabled,promotion.MemberOnly,promotion.CodeRequired,
             promotion.IsCombinable,promotion.IsPublic,promotion.BuyQuantity,promotion.PayQuantity,
             promotion.PercentOff,promotion.FixedAmount,promotion.BundlePrice,promotion.MinimumQuantity,
             promotion.MinimumSubtotal,promotion.GlobalLimit,promotion.RedemptionCount,
             promotion.CreatedAt,promotion.UpdatedAt
      FROM restaurante.Promotion promotion
      LEFT JOIN restaurante.Site siteInfo ON siteInfo.Rfc=promotion.Rfc AND siteInfo.Id=promotion.SiteId
      WHERE promotion.Rfc=@Rfc
        AND (@SiteId IS NULL OR promotion.SiteId IS NULL OR promotion.SiteId=@SiteId)
        AND (@IncludeInactive=1 OR promotion.[Status] IN('Active','Scheduled'))
      ORDER BY promotion.Priority DESC,promotion.Id;
      """,
      new { Rfc = normalizedRfc, SiteId = siteId, IncludeInactive = includeInactive },
      cancellationToken: ct))).AsList();
    await HydratePromotionsAsync(conn, null, normalizedRfc, rows, null, ct);
    return rows;
  }

  public async Task<RestaurantCommandResult> SavePromotionAsync(
    RestaurantPromotionSaveRequest request,
    string userName,
    CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    ValidateSaveRequest(request);
    var codes = request.Codes
      .Select(code => new RestaurantPromotionCodeSaveRequest
      {
        Code = RestaurantPromotionEngine.NormalizeCode(code.Code) ?? string.Empty,
        GlobalLimit = code.GlobalLimit,
        PerMemberLimit = code.PerMemberLimit,
        IsActive = code.IsActive
      })
      .ToList();
    if (codes.Select(code => code.Code).Distinct(StringComparer.Ordinal).Count() != codes.Count)
    {
      return RestaurantCommandResult.Fail("No se puede repetir un código dentro de la promoción.");
    }

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
      await ValidateReferencesAsync(conn, tx, rfc, request, ct);
      long promotionId;
      if (request.Id.HasValue)
      {
        var updated = await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE restaurante.Promotion
          SET SiteId=@SiteId,[Name]=@Name,PublicDescription=@PublicDescription,PublicTerms=@PublicTerms,
              [Status]=@Status,RuleType=@RuleType,Priority=@Priority,ValidFromLocal=@ValidFromLocal,
              ValidToLocal=@ValidToLocal,PosEnabled=@PosEnabled,WebEnabled=@WebEnabled,
              MemberOnly=@MemberOnly,CodeRequired=@CodeRequired,IsCombinable=@IsCombinable,
              IsPublic=@IsPublic,BuyQuantity=@BuyQuantity,PayQuantity=@PayQuantity,
              PercentOff=@PercentOff,FixedAmount=@FixedAmount,BundlePrice=@BundlePrice,
              MinimumQuantity=@MinimumQuantity,MinimumSubtotal=@MinimumSubtotal,
              GlobalLimit=@GlobalLimit,UpdatedAt=SYSUTCDATETIME(),UpdatedBy=@UserName
          WHERE Rfc=@Rfc AND Id=@Id;
          """,
          new
          {
            Rfc = rfc,
            Id = request.Id.Value,
            request.SiteId,
            Name = request.Name.Trim(),
            PublicDescription = request.PublicDescription.Trim(),
            PublicTerms = request.PublicTerms.Trim(),
            request.Status,
            request.RuleType,
            request.Priority,
            request.ValidFromLocal,
            request.ValidToLocal,
            request.PosEnabled,
            request.WebEnabled,
            request.MemberOnly,
            request.CodeRequired,
            request.IsCombinable,
            request.IsPublic,
            request.BuyQuantity,
            request.PayQuantity,
            request.PercentOff,
            request.FixedAmount,
            request.BundlePrice,
            request.MinimumQuantity,
            request.MinimumSubtotal,
            request.GlobalLimit,
            UserName = userName
          },
          tx,
          cancellationToken: ct));
        if (updated == 0)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("La promoción no pertenece al RFC seleccionado.");
        }
        promotionId = request.Id.Value;
        await conn.ExecuteAsync(new CommandDefinition(
          """
          DELETE FROM restaurante.PromotionSchedule WHERE Rfc=@Rfc AND PromotionId=@PromotionId;
          DELETE FROM restaurante.PromotionProduct WHERE Rfc=@Rfc AND PromotionId=@PromotionId;
          DELETE FROM restaurante.PromotionMaterialCategory WHERE Rfc=@Rfc AND PromotionId=@PromotionId;
          DELETE FROM restaurante.PromotionCode
          WHERE Rfc=@Rfc AND PromotionId=@PromotionId
            AND NOT EXISTS
            (
              SELECT 1 FROM restaurante.PromotionRedemption redemption
              WHERE redemption.Rfc=restaurante.PromotionCode.Rfc
                AND redemption.CodeId=restaurante.PromotionCode.Id
            );
          UPDATE restaurante.PromotionCode SET IsActive=0
          WHERE Rfc=@Rfc AND PromotionId=@PromotionId;
          """,
          new { Rfc = rfc, PromotionId = promotionId },
          tx,
          cancellationToken: ct));
      }
      else
      {
        promotionId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT restaurante.Promotion
          (
            Rfc,SiteId,[Name],PublicDescription,PublicTerms,[Status],RuleType,Priority,
            ValidFromLocal,ValidToLocal,PosEnabled,WebEnabled,MemberOnly,CodeRequired,
            IsCombinable,IsPublic,BuyQuantity,PayQuantity,PercentOff,FixedAmount,
            BundlePrice,MinimumQuantity,MinimumSubtotal,GlobalLimit,CreatedBy,UpdatedBy
          )
          VALUES
          (
            @Rfc,@SiteId,@Name,@PublicDescription,@PublicTerms,@Status,@RuleType,@Priority,
            @ValidFromLocal,@ValidToLocal,@PosEnabled,@WebEnabled,@MemberOnly,@CodeRequired,
            @IsCombinable,@IsPublic,@BuyQuantity,@PayQuantity,@PercentOff,@FixedAmount,
            @BundlePrice,@MinimumQuantity,@MinimumSubtotal,@GlobalLimit,@UserName,@UserName
          );
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """,
          new
          {
            Rfc = rfc,
            request.SiteId,
            Name = request.Name.Trim(),
            PublicDescription = request.PublicDescription.Trim(),
            PublicTerms = request.PublicTerms.Trim(),
            request.Status,
            request.RuleType,
            request.Priority,
            request.ValidFromLocal,
            request.ValidToLocal,
            request.PosEnabled,
            request.WebEnabled,
            request.MemberOnly,
            request.CodeRequired,
            request.IsCombinable,
            request.IsPublic,
            request.BuyQuantity,
            request.PayQuantity,
            request.PercentOff,
            request.FixedAmount,
            request.BundlePrice,
            request.MinimumQuantity,
            request.MinimumSubtotal,
            request.GlobalLimit,
            UserName = userName
          },
          tx,
          cancellationToken: ct));
      }

      foreach (var productId in request.ProductIds.Distinct())
      {
        await conn.ExecuteAsync(new CommandDefinition(
          "INSERT restaurante.PromotionProduct(Rfc,PromotionId,ProductId) VALUES(@Rfc,@PromotionId,@ProductId);",
          new { Rfc = rfc, PromotionId = promotionId, ProductId = productId },
          tx,
          cancellationToken: ct));
      }
      foreach (var categoryId in request.MaterialCategoryIds.Distinct())
      {
        await conn.ExecuteAsync(new CommandDefinition(
          "INSERT restaurante.PromotionMaterialCategory(Rfc,PromotionId,MaterialCategoryId) VALUES(@Rfc,@PromotionId,@CategoryId);",
          new { Rfc = rfc, PromotionId = promotionId, CategoryId = categoryId },
          tx,
          cancellationToken: ct));
      }
      foreach (var schedule in request.Schedules)
      {
        await conn.ExecuteAsync(new CommandDefinition(
          """
          INSERT restaurante.PromotionSchedule(Rfc,PromotionId,DayOfWeek,StartsAt,EndsAt)
          VALUES(@Rfc,@PromotionId,@DayOfWeek,@StartsAt,@EndsAt);
          """,
          new { Rfc = rfc, PromotionId = promotionId, schedule.DayOfWeek, schedule.StartsAt, schedule.EndsAt },
          tx,
          cancellationToken: ct));
      }
      foreach (var code in codes)
      {
        var existingCodeId = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
          "SELECT Id FROM restaurante.PromotionCode WITH(UPDLOCK,HOLDLOCK) WHERE Rfc=@Rfc AND Code=@Code;",
          new { Rfc = rfc, code.Code },
          tx,
          cancellationToken: ct));
        if (existingCodeId.HasValue)
        {
          var belongs = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT PromotionId FROM restaurante.PromotionCode WHERE Rfc=@Rfc AND Id=@Id;",
            new { Rfc = rfc, Id = existingCodeId.Value },
            tx,
            cancellationToken: ct));
          if (belongs != promotionId)
          {
            throw new InvalidOperationException($"El código {code.Code} ya pertenece a otra promoción.");
          }
          await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE restaurante.PromotionCode
            SET GlobalLimit=@GlobalLimit,PerMemberLimit=@PerMemberLimit,IsActive=@IsActive
            WHERE Rfc=@Rfc AND Id=@Id;
            """,
            new { Rfc = rfc, Id = existingCodeId.Value, code.GlobalLimit, code.PerMemberLimit, code.IsActive },
            tx,
            cancellationToken: ct));
        }
        else
        {
          await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT restaurante.PromotionCode(Rfc,PromotionId,Code,GlobalLimit,PerMemberLimit,IsActive)
            VALUES(@Rfc,@PromotionId,@Code,@GlobalLimit,@PerMemberLimit,@IsActive);
            """,
            new { Rfc = rfc, PromotionId = promotionId, code.Code, code.GlobalLimit, code.PerMemberLimit, code.IsActive },
            tx,
            cancellationToken: ct));
        }
      }

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La promoción y sus condiciones fueron guardadas.", promotionId);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantPromotionReportDto> GetReportAsync(
    string rfc,
    int siteId,
    DateTime from,
    DateTime to,
    CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    var rows = (await conn.QueryAsync<RestaurantPromotionPerformanceDto>(new CommandDefinition(
      """
      SELECT orderPromotion.PromotionId,orderPromotion.PromotionNameSnapshot AS PromotionName,
             orderPromotion.CodeSnapshot AS Code,
             COUNT_BIG(*) AS RedemptionCount,
             COUNT(DISTINCT orderPromotion.OrderId) AS OrderCount,
             CAST(SUM(orderInfo.Total+orderInfo.DiscountTotal) AS decimal(18,2)) AS GrossSales,
             CAST(SUM(orderPromotion.DiscountAmount) AS decimal(18,2)) AS DiscountAmount,
             CAST(SUM(orderInfo.Total) AS decimal(18,2)) AS NetSales
      FROM restaurante.OrderPromotion orderPromotion
      JOIN restaurante.[Order] orderInfo
        ON orderInfo.Rfc=orderPromotion.Rfc AND orderInfo.Id=orderPromotion.OrderId
      WHERE orderPromotion.Rfc=@Rfc AND orderInfo.SiteId=@SiteId
        AND orderInfo.OperationalDate>=@From AND orderInfo.OperationalDate<=@To
        AND orderInfo.PaymentStatus IN('Paid','PartiallyRefunded')
      GROUP BY orderPromotion.PromotionId,orderPromotion.PromotionNameSnapshot,orderPromotion.CodeSnapshot
      ORDER BY DiscountAmount DESC,PromotionName;
      """,
      new { Rfc = normalizedRfc, SiteId = siteId, From = from.Date, To = to.Date },
      cancellationToken: ct))).AsList();
    return new RestaurantPromotionReportDto
    {
      From = from.Date,
      To = to.Date,
      GrossSales = rows.Sum(row => row.GrossSales),
      PromotionDiscount = rows.Sum(row => row.DiscountAmount),
      NetSales = rows.Sum(row => row.NetSales),
      OrderCount = rows.Sum(row => row.OrderCount),
      Promotions = rows
    };
  }

  internal static async Task<IReadOnlyList<RestaurantPromotionDefinition>> LoadDefinitionsAsync(
    DbConnection conn,
    DbTransaction? tx,
    string rfc,
    int siteId,
    Guid? memberId,
    string? code,
    bool includeInactive,
    CancellationToken ct)
  {
    var promotions = (await conn.QueryAsync<RestaurantPromotionDefinition>(new CommandDefinition(
      """
      SELECT promotion.Id,promotion.[Name],promotion.[Status],promotion.RuleType,promotion.Priority,
             promotion.ValidFromLocal,promotion.ValidToLocal,promotion.PosEnabled,promotion.WebEnabled,
             promotion.MemberOnly,promotion.CodeRequired,promotion.IsCombinable,promotion.BuyQuantity,
             promotion.PayQuantity,promotion.PercentOff,promotion.FixedAmount,promotion.BundlePrice,
             promotion.MinimumQuantity,promotion.MinimumSubtotal,promotion.GlobalLimit,promotion.RedemptionCount
      FROM restaurante.Promotion promotion WITH(UPDLOCK,HOLDLOCK)
      WHERE promotion.Rfc=@Rfc
        AND (promotion.SiteId IS NULL OR promotion.SiteId=@SiteId)
        AND (@IncludeInactive=1 OR promotion.[Status] IN('Active','Scheduled'));
      """,
      new { Rfc = rfc, SiteId = siteId, IncludeInactive = includeInactive },
      tx,
      cancellationToken: ct))).AsList();
    if (promotions.Count == 0)
    {
      return promotions;
    }

    var ids = promotions.Select(promotion => promotion.Id).ToArray();
    var schedules = (await conn.QueryAsync<PromotionScheduleRow>(new CommandDefinition(
      """
      SELECT PromotionId,Id,DayOfWeek,StartsAt,EndsAt
      FROM restaurante.PromotionSchedule
      WHERE Rfc=@Rfc AND PromotionId IN @Ids;
      """,
      new { Rfc = rfc, Ids = ids },
      tx,
      cancellationToken: ct))).AsList();
    var products = (await conn.QueryAsync<PromotionProductRow>(new CommandDefinition(
      "SELECT PromotionId,ProductId FROM restaurante.PromotionProduct WHERE Rfc=@Rfc AND PromotionId IN @Ids;",
      new { Rfc = rfc, Ids = ids },
      tx,
      cancellationToken: ct))).AsList();
    var categories = (await conn.QueryAsync<PromotionCategoryRow>(new CommandDefinition(
      "SELECT PromotionId,MaterialCategoryId FROM restaurante.PromotionMaterialCategory WHERE Rfc=@Rfc AND PromotionId IN @Ids;",
      new { Rfc = rfc, Ids = ids },
      tx,
      cancellationToken: ct))).AsList();
    var normalizedCode = RestaurantPromotionEngine.NormalizeCode(code);
    var codes = (await conn.QueryAsync<PromotionCodeRow>(new CommandDefinition(
      """
      SELECT codeInfo.PromotionId,codeInfo.Id,codeInfo.Code,codeInfo.GlobalLimit,
             codeInfo.PerMemberLimit,codeInfo.RedemptionCount,codeInfo.IsActive,
             CAST(CASE WHEN @MemberId IS NULL THEN 0 ELSE
               (SELECT COUNT(*) FROM restaurante.PromotionRedemption redemption
                WHERE redemption.Rfc=codeInfo.Rfc AND redemption.CodeId=codeInfo.Id
                  AND redemption.MemberId=@MemberId) END AS int) AS MemberRedemptionCount
      FROM restaurante.PromotionCode codeInfo WITH(UPDLOCK,HOLDLOCK)
      WHERE codeInfo.Rfc=@Rfc AND codeInfo.PromotionId IN @Ids
        AND (@Code IS NULL OR codeInfo.Code=@Code OR codeInfo.IsActive=1);
      """,
      new { Rfc = rfc, Ids = ids, MemberId = memberId, Code = normalizedCode },
      tx,
      cancellationToken: ct))).AsList();

    foreach (var promotion in promotions)
    {
      promotion.Schedules = schedules
        .Where(row => row.PromotionId == promotion.Id)
        .Select(row => new RestaurantPromotionScheduleDto
        {
          Id = row.Id,
          DayOfWeek = row.DayOfWeek,
          StartsAt = row.StartsAt,
          EndsAt = row.EndsAt
        })
        .ToList();
      promotion.ProductIds = products
        .Where(row => row.PromotionId == promotion.Id)
        .Select(row => row.ProductId)
        .ToHashSet();
      promotion.MaterialCategoryIds = categories
        .Where(row => row.PromotionId == promotion.Id)
        .Select(row => row.MaterialCategoryId)
        .ToHashSet();
      promotion.Codes = codes
        .Where(row => row.PromotionId == promotion.Id)
        .Select(row => new RestaurantPromotionCodeDto
        {
          Id = row.Id,
          Code = row.Code,
          GlobalLimit = row.GlobalLimit,
          PerMemberLimit = row.PerMemberLimit,
          RedemptionCount = row.RedemptionCount,
          MemberRedemptionCount = row.MemberRedemptionCount,
          IsActive = row.IsActive
        })
        .ToList();
    }
    return promotions;
  }

  private static async Task HydratePromotionsAsync(
    DbConnection conn,
    DbTransaction? tx,
    string rfc,
    IReadOnlyList<RestaurantPromotionDto> promotions,
    Guid? memberId,
    CancellationToken ct)
  {
    if (promotions.Count == 0)
    {
      return;
    }
    var ids = promotions.Select(promotion => promotion.Id).ToArray();
    var schedules = (await conn.QueryAsync<PromotionScheduleRow>(new CommandDefinition(
      "SELECT PromotionId,Id,DayOfWeek,StartsAt,EndsAt FROM restaurante.PromotionSchedule WHERE Rfc=@Rfc AND PromotionId IN @Ids;",
      new { Rfc = rfc, Ids = ids }, tx, cancellationToken: ct))).AsList();
    var products = (await conn.QueryAsync<PromotionProductRow>(new CommandDefinition(
      "SELECT PromotionId,ProductId FROM restaurante.PromotionProduct WHERE Rfc=@Rfc AND PromotionId IN @Ids;",
      new { Rfc = rfc, Ids = ids }, tx, cancellationToken: ct))).AsList();
    var categories = (await conn.QueryAsync<PromotionCategoryRow>(new CommandDefinition(
      "SELECT PromotionId,MaterialCategoryId FROM restaurante.PromotionMaterialCategory WHERE Rfc=@Rfc AND PromotionId IN @Ids;",
      new { Rfc = rfc, Ids = ids }, tx, cancellationToken: ct))).AsList();
    var codes = (await conn.QueryAsync<PromotionCodeRow>(new CommandDefinition(
      """
      SELECT codeInfo.PromotionId,codeInfo.Id,codeInfo.Code,codeInfo.GlobalLimit,
             codeInfo.PerMemberLimit,codeInfo.RedemptionCount,codeInfo.IsActive,0 AS MemberRedemptionCount
      FROM restaurante.PromotionCode codeInfo
      WHERE codeInfo.Rfc=@Rfc AND codeInfo.PromotionId IN @Ids;
      """,
      new { Rfc = rfc, Ids = ids, MemberId = memberId }, tx, cancellationToken: ct))).AsList();
    foreach (var promotion in promotions)
    {
      promotion.Schedules = schedules.Where(row => row.PromotionId == promotion.Id).Select(row => new RestaurantPromotionScheduleDto
      {
        Id = row.Id, DayOfWeek = row.DayOfWeek, StartsAt = row.StartsAt, EndsAt = row.EndsAt
      }).ToList();
      promotion.ProductIds = products.Where(row => row.PromotionId == promotion.Id).Select(row => row.ProductId).ToList();
      promotion.MaterialCategoryIds = categories.Where(row => row.PromotionId == promotion.Id).Select(row => row.MaterialCategoryId).ToList();
      promotion.Codes = codes.Where(row => row.PromotionId == promotion.Id).Select(row => new RestaurantPromotionCodeDto
      {
        Id = row.Id,
        Code = row.Code,
        GlobalLimit = row.GlobalLimit,
        PerMemberLimit = row.PerMemberLimit,
        RedemptionCount = row.RedemptionCount,
        IsActive = row.IsActive
      }).ToList();
    }
  }

  private static void ValidateSaveRequest(RestaurantPromotionSaveRequest request)
  {
    if (!RestaurantPromotionStatuses.All.Contains(request.Status))
      throw new InvalidOperationException("Estado de promoción no válido.");
    if (!RestaurantPromotionRuleTypes.All.Contains(request.RuleType))
      throw new InvalidOperationException("Tipo de regla promocional no válido.");
    if (request.ValidFromLocal.HasValue && request.ValidToLocal.HasValue &&
        request.ValidToLocal.Value <= request.ValidFromLocal.Value)
      throw new InvalidOperationException("La fecha final debe ser posterior a la inicial.");
    if (request.Schedules.Any(schedule => schedule.DayOfWeek > 6))
      throw new InvalidOperationException("El día configurado no es válido.");
    if (request.CodeRequired && request.Codes.All(code => !code.IsActive))
      throw new InvalidOperationException("Una promoción con código obligatorio necesita al menos un código activo.");
    switch (request.RuleType)
    {
      case RestaurantPromotionRuleTypes.BuyXPayY when
        request.BuyQuantity <= 0 || request.PayQuantity < 0 || request.PayQuantity >= request.BuyQuantity:
        throw new InvalidOperationException("Compra X, paga Y requiere X mayor que Y y Y no negativo.");
      case RestaurantPromotionRuleTypes.PercentOff when request.PercentOff <= 0 || request.PercentOff > 100:
        throw new InvalidOperationException("El porcentaje debe estar entre 0 y 100.");
      case RestaurantPromotionRuleTypes.FixedAmountOff when request.FixedAmount <= 0:
        throw new InvalidOperationException("El descuento fijo debe ser mayor que cero.");
      case RestaurantPromotionRuleTypes.FixedBundlePrice when request.BuyQuantity <= 0 || request.BundlePrice < 0:
        throw new InvalidOperationException("El paquete requiere cantidad y precio válidos.");
    }
    if (request.Status is RestaurantPromotionStatuses.Active or RestaurantPromotionStatuses.Scheduled &&
        string.IsNullOrWhiteSpace(request.PublicTerms))
      throw new InvalidOperationException("Una promoción publicable requiere condiciones completas.");
  }

  private static async Task ValidateReferencesAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    RestaurantPromotionSaveRequest request,
    CancellationToken ct)
  {
    if (request.SiteId.HasValue && !await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
      "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM restaurante.Site WHERE Rfc=@Rfc AND Id=@Id) THEN 1 ELSE 0 END AS bit);",
      new { Rfc = rfc, Id = request.SiteId.Value }, tx, cancellationToken: ct)))
      throw new InvalidOperationException("La sede no pertenece al RFC seleccionado.");
    if (request.ProductIds.Count > 0)
    {
      var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(*) FROM restaurante.Product WHERE Rfc=@Rfc AND Id IN @Ids;",
        new { Rfc = rfc, Ids = request.ProductIds.Distinct().ToArray() }, tx, cancellationToken: ct));
      if (count != request.ProductIds.Distinct().Count())
        throw new InvalidOperationException("Uno o más productos no pertenecen al RFC seleccionado.");
    }
    if (request.MaterialCategoryIds.Count > 0)
    {
      var count = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(*) FROM logistica.MaterialCategory WHERE Rfc=@Rfc AND Id IN @Ids;",
        new { Rfc = rfc, Ids = request.MaterialCategoryIds.Distinct().ToArray() }, tx, cancellationToken: ct));
      if (count != request.MaterialCategoryIds.Distinct().Count())
        throw new InvalidOperationException("Una o más categorías no pertenecen al RFC seleccionado.");
    }
  }

  private static RestaurantPromotionQuoteDto EmptyQuote(
    RestaurantPromotionQuoteRequest request,
    DateTimeOffset localAt,
    string message)
  {
    var subtotal = decimal.Round(request.Lines.Sum(line => line.UnitPrice * line.Quantity), 2, MidpointRounding.AwayFromZero);
    var manual = decimal.Round(request.Lines.Sum(line => Math.Clamp(line.ManualDiscountAmount, 0, line.UnitPrice * line.Quantity)), 2, MidpointRounding.AwayFromZero);
    return new RestaurantPromotionQuoteDto
    {
      EvaluatedAt = localAt,
      NormalizedCode = RestaurantPromotionEngine.NormalizeCode(request.Code),
      MerchandiseSubtotal = subtotal,
      ManualDiscountTotal = manual,
      DiscountedMerchandise = Math.Max(0, subtotal - manual),
      CodeAccepted = string.IsNullOrWhiteSpace(request.Code),
      Message = message
    };
  }

  private static DateTimeOffset ConvertToSiteTime(DateTimeOffset at, string timeZoneId)
  {
    try
    {
      return TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
    }
    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
    {
      throw new InvalidOperationException("La zona horaria de la sede no es válida.", ex);
    }
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica no devolvió una DbConnection.");

  private sealed class SiteTimeZoneRow
  {
    public string TimeZoneId { get; set; } = string.Empty;
    public bool IsPromotionsEnabled { get; set; }
  }
  private sealed class PromotionScheduleRow
  {
    public long PromotionId { get; set; }
    public long Id { get; set; }
    public byte DayOfWeek { get; set; }
    public TimeSpan StartsAt { get; set; }
    public TimeSpan EndsAt { get; set; }
  }
  private sealed class PromotionProductRow { public long PromotionId { get; set; } public long ProductId { get; set; } }
  private sealed class PromotionCategoryRow { public long PromotionId { get; set; } public int MaterialCategoryId { get; set; } }
  private sealed class PromotionCodeRow
  {
    public long PromotionId { get; set; }
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public int? GlobalLimit { get; set; }
    public int? PerMemberLimit { get; set; }
    public int RedemptionCount { get; set; }
    public int MemberRedemptionCount { get; set; }
    public bool IsActive { get; set; }
  }
}
