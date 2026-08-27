using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantCatalogService : IRestaurantCatalogService
{
  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantCatalogService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  public async Task<IReadOnlyList<RestaurantSiteDto>> GetSitesAsync(string rfc, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT Id, Rfc, SiteCode, [Name], TimeZoneId, OperationalDayCutoff, TaxRate,
             PricesIncludeTax, IsEnabled, AllowSupervisorDeficit, CrossContaminationWarning
      FROM restaurante.Site
      WHERE Rfc = @Rfc
      ORDER BY [Name], Id;
      """;

    using var conn = CreateConnection();
    return (await conn.QueryAsync<RestaurantSiteDto>(
      new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc) }, cancellationToken: ct))).AsList();
  }

  public async Task<RestaurantCommandResult> SaveSiteAsync(RestaurantSiteUpsertRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    try
    {
      if (request.Id.HasValue)
      {
        const string updateSql =
          """
          UPDATE restaurante.Site
          SET SiteCode = @SiteCode,
              [Name] = @Name,
              TimeZoneId = @TimeZoneId,
              OperationalDayCutoff = @OperationalDayCutoff,
              TaxRate = @TaxRate,
              PricesIncludeTax = @PricesIncludeTax,
              IsEnabled = @IsEnabled,
              AllowSupervisorDeficit = @AllowSupervisorDeficit,
              CrossContaminationWarning = @CrossContaminationWarning,
              UpdatedAt = SYSUTCDATETIME()
          WHERE Rfc = @Rfc AND Id = @Id;
          """;
        var affected = await conn.ExecuteAsync(new CommandDefinition(updateSql, new
        {
          Rfc = rfc,
          request.Id,
          SiteCode = request.SiteCode.Trim().ToUpperInvariant(),
          Name = request.Name.Trim(),
          request.TimeZoneId,
          request.OperationalDayCutoff,
          request.TaxRate,
          request.PricesIncludeTax,
          request.IsEnabled,
          request.AllowSupervisorDeficit,
          request.CrossContaminationWarning
        }, cancellationToken: ct));
        return affected == 1
          ? RestaurantCommandResult.Ok("La sede fue actualizada.", request.Id)
          : RestaurantCommandResult.Fail("La sede no existe en el RFC seleccionado.");
      }

      const string insertSql =
        """
        INSERT INTO restaurante.Site
          (Rfc, SiteCode, [Name], TimeZoneId, OperationalDayCutoff, TaxRate, PricesIncludeTax,
           IsEnabled, AllowSupervisorDeficit, CrossContaminationWarning)
        VALUES
          (@Rfc, @SiteCode, @Name, @TimeZoneId, @OperationalDayCutoff, @TaxRate, @PricesIncludeTax,
           @IsEnabled, @AllowSupervisorDeficit, @CrossContaminationWarning);
        SELECT CAST(SCOPE_IDENTITY() AS int);
        """;
      var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(insertSql, new
      {
        Rfc = rfc,
        SiteCode = request.SiteCode.Trim().ToUpperInvariant(),
        Name = request.Name.Trim(),
        request.TimeZoneId,
        request.OperationalDayCutoff,
        request.TaxRate,
        request.PricesIncludeTax,
        request.IsEnabled,
        request.AllowSupervisorDeficit,
        request.CrossContaminationWarning
      }, cancellationToken: ct));
      return RestaurantCommandResult.Ok("La sede fue creada. Permanece deshabilitada hasta completar su configuración.", id);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return RestaurantCommandResult.Fail("Ya existe una sede con ese código para el RFC seleccionado.");
    }
  }

  public async Task<IReadOnlyList<RestaurantProductDto>> GetProductsAsync(string rfc, int? siteId = null, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT p.Id, p.ProductCardId, p.MaterialId,m.CategoryId AS MaterialCategoryId,
             p.Sku, c.[Name], c.[Description], p.VariantName,
             p.Price, p.KitchenStationId, station.[Name] AS KitchenStationName, p.PreparationMinutes,
             p.IsActive, CAST(p.SoldOutOverride AS bit) AS IsSoldOut,
             CAST(CASE WHEN p.VariantImageThumbnail IS NOT NULL OR p.VariantImage IS NOT NULL OR c.FamilyImageThumbnail IS NOT NULL OR c.FamilyImage IS NOT NULL OR m.PrimaryImage IS NOT NULL THEN 1 ELSE 0 END AS bit) AS HasImage,
             CAST(CASE WHEN p.VariantImageThumbnail IS NOT NULL OR p.VariantImage IS NOT NULL THEN 1 ELSE 0 END AS bit) AS HasVariantImage,
             m.ProductType, m.FulfillmentMode,
             CAST(ISNULL(activeBom.FrozenTheoreticalCost, 0) AS decimal(18,6)) AS TheoreticalCost
      FROM restaurante.Product p
      JOIN restaurante.ProductCard c ON c.Rfc = p.Rfc AND c.Id = p.ProductCardId
      JOIN logistica.Material m ON m.Rfc = p.Rfc AND m.Id = p.MaterialId
      LEFT JOIN restaurante.KitchenStation station ON station.Rfc = p.Rfc AND station.Id = p.KitchenStationId
      OUTER APPLY
      (
        SELECT TOP (1) versionInfo.FrozenTheoreticalCost
        FROM logistica.BomHeader headerInfo
        JOIN logistica.BomVersion versionInfo ON versionInfo.Rfc = headerInfo.Rfc AND versionInfo.BomHeaderId = headerInfo.Id
        WHERE headerInfo.Rfc = p.Rfc AND headerInfo.ProductMaterialId = p.MaterialId AND versionInfo.[Status] = 'Active'
      ) activeBom
      WHERE p.Rfc = @Rfc
        AND (@SiteId IS NULL OR station.SiteId = @SiteId OR p.KitchenStationId IS NULL)
      ORDER BY c.[Name], p.VariantName, p.Id;

      SELECT pg.ProductId, g.Id, g.[Name], g.MinSelections, g.MaxSelections
      FROM restaurante.ProductModifierGroup pg
      JOIN restaurante.ModifierGroup g ON g.Rfc = pg.Rfc AND g.Id = pg.ModifierGroupId
      WHERE pg.Rfc = @Rfc AND g.IsActive = 1
      ORDER BY pg.ProductId, pg.SortOrder, g.Id;

      SELECT pg.ProductId, optionInfo.ModifierGroupId, optionInfo.Id, optionInfo.[Name], optionInfo.PriceDelta
      FROM restaurante.ProductModifierGroup pg
      JOIN restaurante.ModifierOption optionInfo ON optionInfo.Rfc = pg.Rfc AND optionInfo.ModifierGroupId = pg.ModifierGroupId
      WHERE pg.Rfc = @Rfc AND optionInfo.IsActive = 1
      ORDER BY pg.ProductId, optionInfo.ModifierGroupId, optionInfo.SortOrder, optionInfo.Id;

      WITH BomTree AS
      (
        SELECT product.Id AS ProductId, product.MaterialId
        FROM restaurante.Product product
        WHERE product.Rfc = @Rfc
        UNION ALL
        SELECT tree.ProductId, component.ComponentMaterialId
        FROM BomTree tree
        JOIN logistica.BomHeader headerInfo ON headerInfo.Rfc = @Rfc AND headerInfo.ProductMaterialId = tree.MaterialId
        JOIN logistica.BomVersion versionInfo ON versionInfo.Rfc = headerInfo.Rfc AND versionInfo.BomHeaderId = headerInfo.Id AND versionInfo.[Status] = 'Active'
        JOIN logistica.BomComponent component ON component.Rfc = versionInfo.Rfc AND component.BomVersionId = versionInfo.Id
      )
      SELECT DISTINCT tree.ProductId, allergen.[Name]
      FROM BomTree tree
      JOIN logistica.MaterialAllergen materialAllergen ON materialAllergen.Rfc = @Rfc AND materialAllergen.MaterialId = tree.MaterialId
      JOIN logistica.Allergen allergen ON allergen.Id = materialAllergen.AllergenId AND allergen.IsActive = 1
      OPTION (MAXRECURSION 32);

      SELECT ProductId,Tag AS [Name]
      FROM restaurante.ProductDietaryTag
      WHERE Rfc=@Rfc
      ORDER BY ProductId,Tag;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc, SiteId = siteId }, cancellationToken: ct));
    var products = (await multi.ReadAsync<RestaurantProductDto>()).AsList();
    var groups = (await multi.ReadAsync<ProductGroupRow>()).AsList();
    var options = (await multi.ReadAsync<ProductOptionRow>()).AsList();
    var allergens = (await multi.ReadAsync<ProductAllergenRow>()).AsList();
    var dietaryTags = (await multi.ReadAsync<ProductAllergenRow>()).AsList();

    foreach (var product in products)
    {
      product.ModifierGroups = groups.Where(row => row.ProductId == product.Id)
        .Select(row => new RestaurantModifierGroupDto
        {
          Id = row.Id,
          Name = row.Name,
          MinSelections = row.MinSelections,
          MaxSelections = row.MaxSelections,
          Options = options.Where(option => option.ProductId == product.Id && option.ModifierGroupId == row.Id)
            .Select(option => new RestaurantModifierOptionDto { Id = option.Id, Name = option.Name, PriceDelta = option.PriceDelta })
            .ToList()
        }).ToList();
      product.Allergens = allergens.Where(row => row.ProductId == product.Id).Select(row => row.Name).Distinct().ToList();
      product.DietaryTags = dietaryTags.Where(row => row.ProductId == product.Id).Select(row => row.Name).Distinct().ToList();
    }

    return products;
  }

  public async Task<RestaurantCommandResult> SaveProductAsync(RestaurantProductUpsertRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (!IsValidImage(request.FamilyImage, request.ImageContentType) || !IsValidImage(request.VariantImage, request.VariantImageContentType))
      return RestaurantCommandResult.Fail("Las fotografías deben ser imágenes válidas de hasta 8 MB.");
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var materialExists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM logistica.Material WHERE Rfc = @Rfc AND Id = @MaterialId AND IsActive = 1) THEN 1 ELSE 0 END AS bit);",
        new { Rfc = rfc, request.MaterialId }, tx, cancellationToken: ct));
      if (!materialExists)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El material no existe en el RFC seleccionado.");
      }

      long cardId;
      if (request.ProductCardId.HasValue)
      {
        cardId = request.ProductCardId.Value;
        var affectedCard = await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE restaurante.ProductCard
          SET [Name] = @Name, [Description] = @Description,
              FamilyImage = CASE WHEN @FamilyImage IS NULL THEN FamilyImage ELSE @FamilyImage END,
              FamilyImageThumbnail = CASE WHEN @FamilyImage IS NULL THEN FamilyImageThumbnail ELSE @FamilyImageThumbnail END,
              ImageFileName = CASE WHEN @FamilyImage IS NULL THEN ImageFileName ELSE @ImageFileName END,
              ImageContentType = CASE WHEN @FamilyImage IS NULL THEN ImageContentType ELSE @ImageContentType END
          WHERE Rfc = @Rfc AND Id = @CardId;
          """, new
          {
            Rfc = rfc,
            CardId = cardId,
            Name = request.Name.Trim(),
            Description = NullIfWhiteSpace(request.Description),
            request.FamilyImage,
            request.FamilyImageThumbnail,
            request.ImageFileName,
            request.ImageContentType
          }, tx, cancellationToken: ct));
        if (affectedCard != 1)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("La tarjeta comercial no pertenece al RFC seleccionado.");
        }
      }
      else
      {
        cardId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT INTO restaurante.ProductCard
            (Rfc, CardCode, [Name], [Description], FamilyImage, FamilyImageThumbnail, ImageFileName, ImageContentType)
          VALUES
            (@Rfc, CONCAT('CARD-', LEFT(REPLACE(CONVERT(varchar(36), NEWID()), '-', ''), 16)), @Name, @Description,
             @FamilyImage, @FamilyImageThumbnail, @ImageFileName, @ImageContentType);
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new
          {
            Rfc = rfc,
            Name = request.Name.Trim(),
            Description = NullIfWhiteSpace(request.Description),
            request.FamilyImage,
            request.FamilyImageThumbnail,
            request.ImageFileName,
            request.ImageContentType
          }, tx, cancellationToken: ct));
      }

      long productId;
      if (request.Id.HasValue)
      {
        productId = request.Id.Value;
        var affected = await conn.ExecuteAsync(new CommandDefinition(
          """
          UPDATE restaurante.Product
          SET ProductCardId = @ProductCardId, MaterialId = @MaterialId, Sku = @Sku,
              VariantName = @VariantName, Price = @Price, KitchenStationId = @KitchenStationId,
              PreparationMinutes = @PreparationMinutes, IsActive = @IsActive, SoldOutOverride = @SoldOutOverride,
              VariantImage=CASE WHEN @VariantImage IS NULL THEN VariantImage ELSE @VariantImage END,
              VariantImageThumbnail=CASE WHEN @VariantImage IS NULL THEN VariantImageThumbnail ELSE @VariantImageThumbnail END,
              VariantImageFileName=CASE WHEN @VariantImage IS NULL THEN VariantImageFileName ELSE @VariantImageFileName END,
              VariantImageContentType=CASE WHEN @VariantImage IS NULL THEN VariantImageContentType ELSE @VariantImageContentType END
          WHERE Rfc = @Rfc AND Id = @Id;
          """, new
          {
            Rfc = rfc,
            Id = productId,
            ProductCardId = cardId,
            request.MaterialId,
            Sku = request.Sku.Trim().ToUpperInvariant(),
            VariantName = NullIfWhiteSpace(request.VariantName),
            request.Price,
            request.KitchenStationId,
            request.PreparationMinutes,
            request.IsActive,
            request.SoldOutOverride,
            request.VariantImage,
            request.VariantImageThumbnail,
            request.VariantImageFileName,
            request.VariantImageContentType
          }, tx, cancellationToken: ct));
        if (affected != 1)
        {
          await tx.RollbackAsync(ct);
          return RestaurantCommandResult.Fail("El producto no pertenece al RFC seleccionado.");
        }
      }
      else
      {
        productId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT INTO restaurante.Product
            (Rfc, ProductCardId, MaterialId, Sku, VariantName, Price, KitchenStationId, PreparationMinutes, IsActive, SoldOutOverride,
             VariantImage,VariantImageThumbnail,VariantImageFileName,VariantImageContentType)
          VALUES
            (@Rfc, @ProductCardId, @MaterialId, @Sku, @VariantName, @Price, @KitchenStationId, @PreparationMinutes, @IsActive, @SoldOutOverride,
             @VariantImage,@VariantImageThumbnail,@VariantImageFileName,@VariantImageContentType);
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new
          {
            Rfc = rfc,
            ProductCardId = cardId,
            request.MaterialId,
            Sku = request.Sku.Trim().ToUpperInvariant(),
            VariantName = NullIfWhiteSpace(request.VariantName),
            request.Price,
            request.KitchenStationId,
            request.PreparationMinutes,
            request.IsActive,
            request.SoldOutOverride,
            request.VariantImage,
            request.VariantImageThumbnail,
            request.VariantImageFileName,
            request.VariantImageContentType
          }, tx, cancellationToken: ct));
      }

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE logistica.Material
        SET ProductType = @ProductType, FulfillmentMode = @FulfillmentMode, UpdatedDate = CONVERT(date, SYSUTCDATETIME())
        WHERE Rfc = @Rfc AND Id = @MaterialId;
        """, new
        {
          Rfc = rfc,
          request.MaterialId,
          ProductType = request.ProductType.Trim(),
          FulfillmentMode = request.FulfillmentMode.Trim()
        }, tx, cancellationToken: ct));

      await conn.ExecuteAsync(new CommandDefinition(
        """
        DELETE restaurante.ProductDietaryTag WHERE Rfc=@Rfc AND ProductId=@ProductId;
        INSERT restaurante.ProductDietaryTag(Rfc,ProductId,Tag)
        SELECT @Rfc,@ProductId,valueInfo.Tag
        FROM (SELECT DISTINCT TRIM(value) AS Tag FROM STRING_SPLIT(@Tags,'|')) valueInfo
        WHERE valueInfo.Tag<>'';
        """,
        new
        {
          Rfc = rfc,
          ProductId = productId,
          Tags = string.Join("|", request.DietaryTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase))
        },
        tx,
        cancellationToken: ct));

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El producto y su variante fueron guardados.", productId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return RestaurantCommandResult.Fail("El SKU o el material ya están asignados a otro producto del RFC.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantPosCatalogDto> GetPosCatalogAsync(string rfc, int siteId, DateTimeOffset at, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var sites = await GetSitesAsync(normalizedRfc, ct);
    var site = sites.SingleOrDefault(item => item.Id == siteId)
      ?? throw new InvalidOperationException("La sede no existe en el RFC seleccionado.");
    if (!site.IsEnabled)
    {
      throw new InvalidOperationException("El módulo Restaurante está deshabilitado para esta sede.");
    }

    var timeZone = TimeZoneInfo.FindSystemTimeZoneById(site.TimeZoneId);
    var local = TimeZoneInfo.ConvertTime(at, timeZone);
    var products = await GetProductsAsync(normalizedRfc, siteId, ct);

    const string sql =
      """
      DECLARE @MenuId bigint =
      (
        SELECT TOP (1) menuInfo.Id
        FROM restaurante.Menu menuInfo
        LEFT JOIN restaurante.MenuSchedule scheduleInfo
          ON scheduleInfo.Rfc = menuInfo.Rfc AND scheduleInfo.MenuId = menuInfo.Id AND scheduleInfo.SiteId = @SiteId
        WHERE menuInfo.Rfc = @Rfc AND menuInfo.IsActive = 1 AND menuInfo.IsPublished = 1
          AND (scheduleInfo.Id IS NULL OR (scheduleInfo.DayOfWeek = @DayOfWeek AND
              ((scheduleInfo.StartsAt < scheduleInfo.EndsAt AND @LocalTime >= scheduleInfo.StartsAt AND @LocalTime < scheduleInfo.EndsAt)
               OR (scheduleInfo.StartsAt > scheduleInfo.EndsAt AND (@LocalTime >= scheduleInfo.StartsAt OR @LocalTime < scheduleInfo.EndsAt)))))
        ORDER BY CASE WHEN scheduleInfo.Id IS NULL THEN 1 ELSE 0 END, menuInfo.Id
      );

      SELECT ISNULL((SELECT [Name] FROM restaurante.Menu WHERE Rfc = @Rfc AND Id = @MenuId), 'Menú') AS MenuName;
      SELECT sectionInfo.Id, sectionInfo.[Name], sectionInfo.SortOrder
      FROM restaurante.MenuSection sectionInfo
      WHERE sectionInfo.Rfc = @Rfc AND sectionInfo.MenuId = @MenuId
      ORDER BY sectionInfo.SortOrder, sectionInfo.Id;
      SELECT item.MenuSectionId, item.ProductId
      FROM restaurante.MenuItem item
      WHERE item.Rfc = @Rfc AND item.MenuSectionId IN
        (SELECT Id FROM restaurante.MenuSection WHERE Rfc = @Rfc AND MenuId = @MenuId)
      ORDER BY item.MenuSectionId, item.SortOrder, item.ProductId;
      SELECT Id, TableCode AS Code, [Name] FROM restaurante.DiningTable
      WHERE Rfc = @Rfc AND SiteId = @SiteId AND IsActive = 1 ORDER BY [Name], Id;
      SELECT Id, [Name] FROM restaurante.ExternalProvider
      WHERE Rfc = @Rfc AND SiteId = @SiteId AND IsActive = 1 ORDER BY [Name], Id;
      """;

    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new
    {
      Rfc = normalizedRfc,
      SiteId = siteId,
      DayOfWeek = (byte)local.DayOfWeek,
      LocalTime = local.TimeOfDay
    }, cancellationToken: ct));
    var menuName = await multi.ReadSingleAsync<string>();
    var sections = (await multi.ReadAsync<MenuSectionRow>()).AsList();
    var items = (await multi.ReadAsync<MenuItemRow>()).AsList();
    var tables = (await multi.ReadAsync<RestaurantDiningTableDto>()).AsList();
    var providers = (await multi.ReadAsync<RestaurantExternalProviderDto>()).AsList();

    var sectionDtos = sections.Select(section => new RestaurantMenuSectionDto
    {
      Id = section.Id,
      Name = section.Name,
      SortOrder = section.SortOrder,
      Products = items.Where(item => item.MenuSectionId == section.Id)
        .Select(item => products.SingleOrDefault(product => product.Id == item.ProductId))
        .Where(product => product is not null && product.IsActive)
        .Cast<RestaurantProductDto>()
        .ToList()
    }).ToList();
    if (sectionDtos.Count == 0)
    {
      sectionDtos.Add(new RestaurantMenuSectionDto
      {
        Id = -1,
        Name = "Todos",
        Products = products.Where(product => product.IsActive).ToList()
      });
      menuName = "Catálogo activo";
    }

    return new RestaurantPosCatalogDto
    {
      Site = site,
      MenuName = menuName,
      Sections = sectionDtos,
      Tables = tables,
      ExternalProviders = providers
    };
  }

  public async Task<(byte[] Bytes, string ContentType)?> GetProductImageAsync(string rfc, long productId, bool thumbnail, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT
        CASE WHEN @Thumbnail = 1
             THEN COALESCE(product.VariantImageThumbnail, product.VariantImage, card.FamilyImageThumbnail, material.PrimaryImageThumbnail, card.FamilyImage, material.PrimaryImage)
             ELSE COALESCE(product.VariantImage, card.FamilyImage, material.PrimaryImage) END AS Bytes,
        COALESCE(product.VariantImageContentType, card.ImageContentType, material.PrimaryImageContentType, 'image/jpeg') AS ContentType
      FROM restaurante.Product product
      JOIN restaurante.ProductCard card ON card.Rfc = product.Rfc AND card.Id = product.ProductCardId
      JOIN logistica.Material material ON material.Rfc = product.Rfc AND material.Id = product.MaterialId
      WHERE product.Rfc = @Rfc AND product.Id = @ProductId;
      """;
    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<ImageRow>(new CommandDefinition(sql, new
    {
      Rfc = LogisticsRfc.Require(rfc),
      ProductId = productId,
      Thumbnail = thumbnail
    }, cancellationToken: ct));
    return row?.Bytes is { Length: > 0 } ? (row.Bytes, row.ContentType ?? "image/jpeg") : null;
  }

  public async Task<IReadOnlyList<RestaurantMenuAdminDto>> GetMenusAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT Id,MenuCode,[Name],IsPublished,IsActive FROM restaurante.Menu WHERE Rfc=@Rfc ORDER BY [Name],Id;
      SELECT MenuId,SiteId,DayOfWeek,StartsAt,EndsAt FROM restaurante.MenuSchedule WHERE Rfc=@Rfc ORDER BY MenuId,SiteId,DayOfWeek,StartsAt;
      SELECT Id,MenuId,[Name],SortOrder FROM restaurante.MenuSection WHERE Rfc=@Rfc ORDER BY MenuId,SortOrder,Id;
      SELECT sectionInfo.MenuId,item.MenuSectionId,item.ProductId
      FROM restaurante.MenuItem item JOIN restaurante.MenuSection sectionInfo ON sectionInfo.Rfc=item.Rfc AND sectionInfo.Id=item.MenuSectionId
      WHERE item.Rfc=@Rfc ORDER BY sectionInfo.MenuId,item.MenuSectionId,item.SortOrder,item.ProductId;
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    var menus = (await multi.ReadAsync<MenuAdminRow>()).AsList();
    var schedules = (await multi.ReadAsync<MenuScheduleAdminRow>()).AsList();
    var sections = (await multi.ReadAsync<MenuSectionAdminRow>()).AsList();
    var items = (await multi.ReadAsync<MenuItemAdminRow>()).AsList();
    return menus.Select(menu => new RestaurantMenuAdminDto
    {
      Id = menu.Id, MenuCode = menu.MenuCode, Name = menu.Name, IsPublished = menu.IsPublished, IsActive = menu.IsActive,
      Schedules = schedules.Where(row => row.MenuId == menu.Id).Select(row => new RestaurantMenuScheduleAdminDto
      { SiteId = row.SiteId, DayOfWeek = row.DayOfWeek, StartsAt = row.StartsAt, EndsAt = row.EndsAt }).ToList(),
      Sections = sections.Where(row => row.MenuId == menu.Id).Select(section => new RestaurantMenuSectionAdminDto
      {
        Id = section.Id, Name = section.Name, SortOrder = section.SortOrder,
        ProductIds = items.Where(item => item.MenuSectionId == section.Id).Select(item => item.ProductId).ToList()
      }).ToList()
    }).ToList();
  }

  public async Task<RestaurantCommandResult> SaveMenuAsync(RestaurantMenuSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.Sections.Count == 0 || request.Sections.Any(section => string.IsNullOrWhiteSpace(section.Name)))
      return RestaurantCommandResult.Fail("El menú requiere al menos una sección con nombre.");
    if (request.Schedules.Any(row => row.SiteId <= 0 || row.StartsAt == row.EndsAt))
      return RestaurantCommandResult.Fail("Cada horario requiere sede y horas de inicio/fin distintas.");
    if (request.Sections.SelectMany(section => section.ProductIds).GroupBy(id => id).Any(group => group.Count() > 1))
      return RestaurantCommandResult.Fail("Un producto sólo puede aparecer una vez dentro del mismo menú.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var productIds = request.Sections.SelectMany(section => section.ProductIds).Distinct().ToArray();
      var productCount = productIds.Length == 0 ? 0 : await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(*) FROM restaurante.Product WHERE Rfc=@Rfc AND Id IN @Ids;", new { Rfc = rfc, Ids = productIds }, tx, cancellationToken: ct));
      var siteIds = request.Schedules.Select(row => row.SiteId).Distinct().ToArray();
      var siteCount = siteIds.Length == 0 ? 0 : await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(*) FROM restaurante.Site WHERE Rfc=@Rfc AND Id IN @Ids;", new { Rfc = rfc, Ids = siteIds }, tx, cancellationToken: ct));
      if (productCount != productIds.Length || siteCount != siteIds.Length)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("Una sede o producto no pertenece al RFC activo.");
      }

      long menuId;
      if (request.Id.HasValue)
      {
        menuId = request.Id.Value;
        var affected = await conn.ExecuteAsync(new CommandDefinition(
          "UPDATE restaurante.Menu SET MenuCode=@Code,[Name]=@Name,IsPublished=@Published,IsActive=@Active WHERE Rfc=@Rfc AND Id=@Id;",
          new { Rfc = rfc, Id = menuId, Code = request.MenuCode.Trim().ToUpperInvariant(), Name = request.Name.Trim(), Published = request.IsPublished, Active = request.IsActive }, tx, cancellationToken: ct));
        if (affected != 1) { await tx.RollbackAsync(ct); return RestaurantCommandResult.Fail("El menú no pertenece al RFC activo."); }
        await conn.ExecuteAsync(new CommandDefinition(
          """
          DELETE item FROM restaurante.MenuItem item JOIN restaurante.MenuSection sectionInfo ON sectionInfo.Rfc=item.Rfc AND sectionInfo.Id=item.MenuSectionId WHERE sectionInfo.Rfc=@Rfc AND sectionInfo.MenuId=@Id;
          DELETE FROM restaurante.MenuSection WHERE Rfc=@Rfc AND MenuId=@Id;
          DELETE FROM restaurante.MenuSchedule WHERE Rfc=@Rfc AND MenuId=@Id;
          """, new { Rfc = rfc, Id = menuId }, tx, cancellationToken: ct));
      }
      else
      {
        menuId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          """
          INSERT INTO restaurante.Menu (Rfc,MenuCode,[Name],IsPublished,IsActive) VALUES (@Rfc,@Code,@Name,@Published,@Active);
          SELECT CAST(SCOPE_IDENTITY() AS bigint);
          """, new { Rfc = rfc, Code = request.MenuCode.Trim().ToUpperInvariant(), Name = request.Name.Trim(), Published = request.IsPublished, Active = request.IsActive }, tx, cancellationToken: ct));
      }
      foreach (var schedule in request.Schedules)
        await conn.ExecuteAsync(new CommandDefinition(
          "INSERT INTO restaurante.MenuSchedule (Rfc,MenuId,SiteId,DayOfWeek,StartsAt,EndsAt) VALUES (@Rfc,@MenuId,@SiteId,@Day,@Starts,@Ends);",
          new { Rfc = rfc, MenuId = menuId, schedule.SiteId, Day = schedule.DayOfWeek, Starts = schedule.StartsAt, Ends = schedule.EndsAt }, tx, cancellationToken: ct));
      foreach (var section in request.Sections.OrderBy(item => item.SortOrder))
      {
        var sectionId = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
          "INSERT INTO restaurante.MenuSection (Rfc,MenuId,[Name],SortOrder) VALUES (@Rfc,@MenuId,@Name,@Sort); SELECT CAST(SCOPE_IDENTITY() AS bigint);",
          new { Rfc = rfc, MenuId = menuId, Name = section.Name.Trim(), Sort = section.SortOrder }, tx, cancellationToken: ct));
        var sort = 0;
        foreach (var productId in section.ProductIds)
          await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO restaurante.MenuItem (Rfc,MenuSectionId,ProductId,SortOrder) VALUES (@Rfc,@SectionId,@ProductId,@Sort);",
            new { Rfc = rfc, SectionId = sectionId, ProductId = productId, Sort = sort++ }, tx, cancellationToken: ct));
      }
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El menú, sus secciones y horarios fueron guardados.", menuId);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await tx.RollbackAsync(ct);
      return RestaurantCommandResult.Fail("El código, horario o nombre de sección está duplicado.");
    }
    catch { await tx.RollbackAsync(ct); throw; }
  }

  public async Task<IReadOnlyList<RestaurantModifierAdminDto>> GetModifierGroupsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    const string sql =
      """
      SELECT Id,[Name],MinSelections,MaxSelections,SortOrder,IsActive FROM restaurante.ModifierGroup WHERE Rfc=@Rfc ORDER BY SortOrder,[Name];
      SELECT ModifierGroupId,ProductId FROM restaurante.ProductModifierGroup WHERE Rfc=@Rfc ORDER BY ModifierGroupId,SortOrder,ProductId;
      SELECT Id,ModifierGroupId,[Name],PriceDelta,SortOrder FROM restaurante.ModifierOption WHERE Rfc=@Rfc ORDER BY ModifierGroupId,SortOrder,Id;
      SELECT ModifierOptionId,MaterialId,QuantityDelta,UnitId FROM restaurante.ModifierIngredientDelta WHERE Rfc=@Rfc ORDER BY ModifierOptionId,Id;
      """;
    using var conn = CreateConnection();
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));
    var groups = (await multi.ReadAsync<ModifierGroupAdminRow>()).AsList();
    var products = (await multi.ReadAsync<ModifierProductAdminRow>()).AsList();
    var options = (await multi.ReadAsync<ModifierOptionAdminRow>()).AsList();
    var deltas = (await multi.ReadAsync<ModifierDeltaAdminRow>()).AsList();
    return groups.Select(group => new RestaurantModifierAdminDto
    {
      Id=group.Id,Name=group.Name,MinSelections=group.MinSelections,MaxSelections=group.MaxSelections,SortOrder=group.SortOrder,IsActive=group.IsActive,
      ProductIds=products.Where(row=>row.ModifierGroupId==group.Id).Select(row=>row.ProductId).ToList(),
      Options=options.Where(row=>row.ModifierGroupId==group.Id).Select(option=>new RestaurantModifierOptionAdminDto
      {
        Id=option.Id,Name=option.Name,PriceDelta=option.PriceDelta,SortOrder=option.SortOrder,
        IngredientDeltas=deltas.Where(row=>row.ModifierOptionId==option.Id).Select(row=>new RestaurantModifierDeltaAdminDto
        { MaterialId=row.MaterialId,QuantityDelta=row.QuantityDelta,UnitId=row.UnitId }).ToList()
      }).ToList()
    }).ToList();
  }

  public async Task<RestaurantCommandResult> SaveModifierGroupAsync(RestaurantModifierSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.MinSelections < 0 || request.MaxSelections < request.MinSelections || request.Options.Count == 0)
      return RestaurantCommandResult.Fail("El grupo requiere opciones y límites de selección válidos.");
    if (request.Options.Any(option => string.IsNullOrWhiteSpace(option.Name)) || request.Options.GroupBy(option => option.Name.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count()>1))
      return RestaurantCommandResult.Fail("Las opciones requieren nombres únicos.");
    if (request.Options.SelectMany(option=>option.IngredientDeltas).Any(delta=>delta.MaterialId<=0 || delta.UnitId<=0 || delta.QuantityDelta==0))
      return RestaurantCommandResult.Fail("Cada delta requiere material, unidad y cantidad distinta de cero.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var productIds=request.ProductIds.Distinct().ToArray();
      var materialIds=request.Options.SelectMany(option=>option.IngredientDeltas).Select(delta=>delta.MaterialId).Distinct().ToArray();
      var validProducts=productIds.Length==0?0:await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM restaurante.Product WHERE Rfc=@Rfc AND Id IN @Ids;",new{Rfc=rfc,Ids=productIds},tx,cancellationToken:ct));
      var validMaterials=materialIds.Length==0?0:await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM logistica.Material WHERE Rfc=@Rfc AND Id IN @Ids;",new{Rfc=rfc,Ids=materialIds},tx,cancellationToken:ct));
      if(validProducts!=productIds.Length||validMaterials!=materialIds.Length){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("Un producto o material no pertenece al RFC activo.");}

      long groupId;
      if(request.Id.HasValue)
      {
        groupId=request.Id.Value;
        var affected=await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.ModifierGroup SET [Name]=@Name,MinSelections=@Min,MaxSelections=@Max,SortOrder=@Sort,IsActive=@Active WHERE Rfc=@Rfc AND Id=@Id;",new{Rfc=rfc,Id=groupId,Name=request.Name.Trim(),Min=request.MinSelections,Max=request.MaxSelections,Sort=request.SortOrder,Active=request.IsActive},tx,cancellationToken:ct));
        if(affected!=1){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("El grupo no pertenece al RFC activo.");}
      }
      else
      {
        groupId=await conn.ExecuteScalarAsync<long>(new CommandDefinition("INSERT INTO restaurante.ModifierGroup (Rfc,[Name],MinSelections,MaxSelections,SortOrder,IsActive) VALUES (@Rfc,@Name,@Min,@Max,@Sort,@Active); SELECT CAST(SCOPE_IDENTITY() AS bigint);",new{Rfc=rfc,Name=request.Name.Trim(),Min=request.MinSelections,Max=request.MaxSelections,Sort=request.SortOrder,Active=request.IsActive},tx,cancellationToken:ct));
      }
      await conn.ExecuteAsync(new CommandDefinition("DELETE FROM restaurante.ProductModifierGroup WHERE Rfc=@Rfc AND ModifierGroupId=@Id;",new{Rfc=rfc,Id=groupId},tx,cancellationToken:ct));
      var productSort=0;
      foreach(var productId in productIds)
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO restaurante.ProductModifierGroup (Rfc,ProductId,ModifierGroupId,SortOrder) VALUES (@Rfc,@ProductId,@GroupId,@Sort);",new{Rfc=rfc,ProductId=productId,GroupId=groupId,Sort=productSort++},tx,cancellationToken:ct));

      var retained=new HashSet<long>();
      foreach(var option in request.Options.OrderBy(item=>item.SortOrder))
      {
        long optionId;
        if(option.Id.HasValue)
        {
          optionId=option.Id.Value;
          var affected=await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.ModifierOption SET [Name]=@Name,PriceDelta=@Price,SortOrder=@Sort,IsActive=1 WHERE Rfc=@Rfc AND ModifierGroupId=@GroupId AND Id=@Id;",new{Rfc=rfc,GroupId=groupId,Id=optionId,Name=option.Name.Trim(),Price=option.PriceDelta,Sort=option.SortOrder},tx,cancellationToken:ct));
          if(affected!=1)throw new InvalidOperationException("Una opción no pertenece al grupo y RFC activos.");
          await conn.ExecuteAsync(new CommandDefinition("DELETE FROM restaurante.ModifierIngredientDelta WHERE Rfc=@Rfc AND ModifierOptionId=@Id;",new{Rfc=rfc,Id=optionId},tx,cancellationToken:ct));
        }
        else
          optionId=await conn.ExecuteScalarAsync<long>(new CommandDefinition("INSERT INTO restaurante.ModifierOption (Rfc,ModifierGroupId,[Name],PriceDelta,SortOrder) VALUES (@Rfc,@GroupId,@Name,@Price,@Sort); SELECT CAST(SCOPE_IDENTITY() AS bigint);",new{Rfc=rfc,GroupId=groupId,Name=option.Name.Trim(),Price=option.PriceDelta,Sort=option.SortOrder},tx,cancellationToken:ct));
        retained.Add(optionId);
        foreach(var delta in option.IngredientDeltas)
          await conn.ExecuteAsync(new CommandDefinition("INSERT INTO restaurante.ModifierIngredientDelta (Rfc,ModifierOptionId,MaterialId,QuantityDelta,UnitId) VALUES (@Rfc,@OptionId,@MaterialId,@Quantity,@UnitId);",new{Rfc=rfc,OptionId=optionId,delta.MaterialId,Quantity=delta.QuantityDelta,delta.UnitId},tx,cancellationToken:ct));
      }
      await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.ModifierOption SET IsActive=0 WHERE Rfc=@Rfc AND ModifierGroupId=@GroupId AND Id NOT IN @Ids;",new{Rfc=rfc,GroupId=groupId,Ids=retained.Count==0?[-1L]:retained.ToArray()},tx,cancellationToken:ct));
      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El grupo, opciones e ingredientes fueron guardados.",groupId);
    }
    catch(SqlException ex) when(ex.Number is 2601 or 2627){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("El nombre del grupo, opción o material delta está duplicado.");}
    catch{await tx.RollbackAsync(ct);throw;}
  }

  public async Task<IReadOnlyList<RestaurantKitchenStationLookupDto>> GetKitchenStationsAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc=LogisticsRfc.Require(rfc);
    const string sql=
      """
      SELECT station.Id,station.SiteId,site.[Name] AS SiteName,station.StationCode AS Code,
             station.[Name],station.SortOrder,station.IsActive
      FROM restaurante.KitchenStation station
      JOIN restaurante.Site site ON site.Rfc=station.Rfc AND site.Id=station.SiteId
      WHERE station.Rfc=@Rfc
      ORDER BY site.[Name],station.SortOrder,station.[Name],station.Id;
      """;
    using var conn=CreateConnection();
    return (await conn.QueryAsync<RestaurantKitchenStationLookupDto>(new CommandDefinition(sql,new{Rfc=normalizedRfc},cancellationToken:ct))).AsList();
  }

  public async Task<RestaurantSiteOperationsDto> GetSiteOperationsAsync(string rfc, int siteId, CancellationToken ct = default)
  {
    var normalizedRfc=LogisticsRfc.Require(rfc);
    const string sql=
      """
      IF NOT EXISTS (SELECT 1 FROM restaurante.Site WHERE Rfc=@Rfc AND Id=@SiteId)
        THROW 51040,'La sede no pertenece al RFC activo.',1;
      SELECT Id,TableCode AS Code,[Name],Capacity,IsActive FROM restaurante.DiningTable WHERE Rfc=@Rfc AND SiteId=@SiteId ORDER BY [Name],Id;
      SELECT Id,StationCode AS Code,[Name],SortOrder,IsActive FROM restaurante.KitchenStation WHERE Rfc=@Rfc AND SiteId=@SiteId ORDER BY SortOrder,[Name],Id;
      SELECT Id,ProviderCode AS Code,[Name],DefaultCommissionRate,IsActive FROM restaurante.ExternalProvider WHERE Rfc=@Rfc AND SiteId=@SiteId ORDER BY [Name],Id;
      SELECT priorityInfo.LocationId,locationInfo.LocationName,priorityInfo.StationCode,priorityInfo.Priority
      FROM restaurante.SiteLocationPriority priorityInfo JOIN logistica.Location locationInfo ON locationInfo.Rfc=priorityInfo.Rfc AND locationInfo.Id=priorityInfo.LocationId
      WHERE priorityInfo.Rfc=@Rfc AND priorityInfo.SiteId=@SiteId ORDER BY priorityInfo.StationCode,priorityInfo.Priority;
      SELECT CAST(Id AS bigint) AS Id,CONCAT(LocationName,' · ',LocationCode) AS Label FROM logistica.Location WHERE Rfc=@Rfc AND IsActive=1 AND IsInventoryEnabled=1 ORDER BY LocationName;
      SELECT CashAccount,CardBankAccount,TransferBankAccount,PlatformReceivableAccount,SalesAccount,VatAccount,DiscountAccount,
             TipsPayableAccount,PlatformCommissionAccount,InventoryAccount,CostOfSalesAccount,WasteAccount,DailyPolicyEnabled
      FROM restaurante.AccountingConfiguration WHERE Rfc=@Rfc AND SiteId=@SiteId;
      """;
    using var conn=CreateConnection();
    using var multi=await conn.QueryMultipleAsync(new CommandDefinition(sql,new{Rfc=normalizedRfc,SiteId=siteId},cancellationToken:ct));
    return new RestaurantSiteOperationsDto
    {
      Tables=(await multi.ReadAsync<RestaurantDiningTableDto>()).AsList(),
      Stations=(await multi.ReadAsync<RestaurantKitchenStationAdminDto>()).AsList(),
      ExternalProviders=(await multi.ReadAsync<RestaurantExternalProviderDto>()).AsList(),
      LocationPriorities=(await multi.ReadAsync<RestaurantLocationPriorityDto>()).AsList(),
      AvailableLocations=(await multi.ReadAsync<RestaurantLookupDto>()).AsList(),
      Accounting=await multi.ReadSingleOrDefaultAsync<RestaurantAccountingConfigurationDto>()??new()
    };
  }

  public async Task<RestaurantCommandResult> SaveSiteOperationsAsync(RestaurantSiteOperationsSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc=LogisticsRfc.Require(request.Rfc);
    if(request.LocationPriorities.GroupBy(item=>new{Code=item.StationCode.Trim().ToUpperInvariant(),item.Priority}).Any(group=>group.Count()>1))
      return RestaurantCommandResult.Fail("No se puede repetir la prioridad dentro de una estación.");
    using var conn=CreateConnection(); await conn.OpenAsync(ct); await using var tx=await conn.BeginTransactionAsync(ct);
    try
    {
      if(!await conn.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM restaurante.Site WHERE Rfc=@Rfc AND Id=@SiteId) THEN 1 ELSE 0 END AS bit);",new{Rfc=rfc,request.SiteId},tx,cancellationToken:ct)))
      {await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("La sede no pertenece al RFC activo.");}
      if(request.OperationalDayCutoff<TimeSpan.Zero||request.OperationalDayCutoff>=TimeSpan.FromHours(24))
      {await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("El corte del día operativo debe estar entre 00:00 y 23:59.");}
      await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.Site SET OperationalDayCutoff=@Cutoff WHERE Rfc=@Rfc AND Id=@SiteId;",new{Rfc=rfc,request.SiteId,Cutoff=request.OperationalDayCutoff},tx,cancellationToken:ct));
      var locationIds=request.LocationPriorities.Select(row=>row.LocationId).Distinct().ToArray();
      var locationCount=locationIds.Length==0?0:await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM logistica.Location WHERE Rfc=@Rfc AND Id IN @Ids AND IsInventoryEnabled=1;",new{Rfc=rfc,Ids=locationIds},tx,cancellationToken:ct));
      if(locationCount!=locationIds.Length){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("Una ubicación no pertenece al RFC activo.");}
      foreach(var table in request.Tables)
      {
        if(string.IsNullOrWhiteSpace(table.Code)||string.IsNullOrWhiteSpace(table.Name))throw new InvalidOperationException("Cada mesa requiere código y nombre.");
        if(table.Id.HasValue)
        {
          var affected=await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.DiningTable SET TableCode=@Code,[Name]=@Name,Capacity=@Capacity,IsActive=@Active WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id;",new{Rfc=rfc,request.SiteId,table.Id,Code=table.Code.Trim().ToUpperInvariant(),Name=table.Name.Trim(),table.Capacity,Active=table.IsActive},tx,cancellationToken:ct));
          if(affected!=1)throw new InvalidOperationException("Una mesa no pertenece a la sede activa.");
        }
        else await conn.ExecuteAsync(new CommandDefinition("INSERT INTO restaurante.DiningTable (Rfc,SiteId,TableCode,[Name],Capacity,IsActive) VALUES (@Rfc,@SiteId,@Code,@Name,@Capacity,@Active);",new{Rfc=rfc,request.SiteId,Code=table.Code.Trim().ToUpperInvariant(),Name=table.Name.Trim(),table.Capacity,Active=table.IsActive},tx,cancellationToken:ct));
      }
      foreach(var station in request.Stations)
      {
        if(string.IsNullOrWhiteSpace(station.Code)||string.IsNullOrWhiteSpace(station.Name))throw new InvalidOperationException("Cada estación requiere código y nombre.");
        if(station.Id.HasValue)
        {
          var affected=await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.KitchenStation SET StationCode=@Code,[Name]=@Name,SortOrder=@Sort,IsActive=@Active WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id;",new{Rfc=rfc,request.SiteId,station.Id,Code=station.Code.Trim().ToUpperInvariant(),Name=station.Name.Trim(),Sort=station.SortOrder,Active=station.IsActive},tx,cancellationToken:ct));
          if(affected!=1)throw new InvalidOperationException("Una estación no pertenece a la sede activa.");
        }
        else await conn.ExecuteAsync(new CommandDefinition("INSERT INTO restaurante.KitchenStation (Rfc,SiteId,StationCode,[Name],SortOrder,IsActive) VALUES (@Rfc,@SiteId,@Code,@Name,@Sort,@Active);",new{Rfc=rfc,request.SiteId,Code=station.Code.Trim().ToUpperInvariant(),Name=station.Name.Trim(),Sort=station.SortOrder,Active=station.IsActive},tx,cancellationToken:ct));
      }
      foreach(var provider in request.ExternalProviders)
      {
        if(string.IsNullOrWhiteSpace(provider.Code)||string.IsNullOrWhiteSpace(provider.Name)||provider.DefaultCommissionRate is <0 or >1)throw new InvalidOperationException("Cada proveedor requiere código, nombre y comisión válida.");
        if(provider.Id.HasValue)
        {
          var affected=await conn.ExecuteAsync(new CommandDefinition("UPDATE restaurante.ExternalProvider SET ProviderCode=@Code,[Name]=@Name,DefaultCommissionRate=@Rate,IsActive=@Active WHERE Rfc=@Rfc AND SiteId=@SiteId AND Id=@Id;",new{Rfc=rfc,request.SiteId,provider.Id,Code=provider.Code.Trim().ToUpperInvariant(),Name=provider.Name.Trim(),Rate=provider.DefaultCommissionRate,Active=provider.IsActive},tx,cancellationToken:ct));
          if(affected!=1)throw new InvalidOperationException("Un proveedor no pertenece a la sede activa.");
        }
        else await conn.ExecuteAsync(new CommandDefinition("INSERT INTO restaurante.ExternalProvider (Rfc,SiteId,ProviderCode,[Name],DefaultCommissionRate,IsActive) VALUES (@Rfc,@SiteId,@Code,@Name,@Rate,@Active);",new{Rfc=rfc,request.SiteId,Code=provider.Code.Trim().ToUpperInvariant(),Name=provider.Name.Trim(),Rate=provider.DefaultCommissionRate,Active=provider.IsActive},tx,cancellationToken:ct));
      }
      await conn.ExecuteAsync(new CommandDefinition("DELETE FROM restaurante.SiteLocationPriority WHERE Rfc=@Rfc AND SiteId=@SiteId;",new{Rfc=rfc,request.SiteId},tx,cancellationToken:ct));
      foreach(var priority in request.LocationPriorities)
        await conn.ExecuteAsync(new CommandDefinition("INSERT INTO restaurante.SiteLocationPriority (Rfc,SiteId,StationCode,LocationId,Priority) VALUES (@Rfc,@SiteId,@Code,@LocationId,@Priority);",new{Rfc=rfc,request.SiteId,Code=priority.StationCode.Trim().ToUpperInvariant(),priority.LocationId,priority.Priority},tx,cancellationToken:ct));
      var account=request.Accounting??new();
      var invalidAccount = await FindInvalidAccountingAccountAsync(conn, tx, rfc, account, ct);
      if (invalidAccount is not null)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail(invalidAccount);
      }
      await conn.ExecuteAsync(new CommandDefinition(
        """
        MERGE restaurante.AccountingConfiguration AS target
        USING (SELECT @Rfc Rfc,@SiteId SiteId) source ON target.Rfc=source.Rfc AND target.SiteId=source.SiteId
        WHEN MATCHED THEN UPDATE SET CashAccount=@Cash,CardBankAccount=@Card,TransferBankAccount=@Transfer,PlatformReceivableAccount=@Platform,
          SalesAccount=@Sales,VatAccount=@Vat,DiscountAccount=@Discount,TipsPayableAccount=@Tips,PlatformCommissionAccount=@Commission,
          InventoryAccount=@Inventory,CostOfSalesAccount=@Cost,WasteAccount=@Waste,DailyPolicyEnabled=@Enabled
        WHEN NOT MATCHED THEN INSERT (Rfc,SiteId,CashAccount,CardBankAccount,TransferBankAccount,PlatformReceivableAccount,SalesAccount,VatAccount,
          DiscountAccount,TipsPayableAccount,PlatformCommissionAccount,InventoryAccount,CostOfSalesAccount,WasteAccount,DailyPolicyEnabled)
        VALUES (@Rfc,@SiteId,@Cash,@Card,@Transfer,@Platform,@Sales,@Vat,@Discount,@Tips,@Commission,@Inventory,@Cost,@Waste,@Enabled);
        """,new{Rfc=rfc,request.SiteId,Cash=NullIfWhiteSpace(account.CashAccount),Card=NullIfWhiteSpace(account.CardBankAccount),Transfer=NullIfWhiteSpace(account.TransferBankAccount),Platform=NullIfWhiteSpace(account.PlatformReceivableAccount),Sales=NullIfWhiteSpace(account.SalesAccount),Vat=NullIfWhiteSpace(account.VatAccount),Discount=NullIfWhiteSpace(account.DiscountAccount),Tips=NullIfWhiteSpace(account.TipsPayableAccount),Commission=NullIfWhiteSpace(account.PlatformCommissionAccount),Inventory=NullIfWhiteSpace(account.InventoryAccount),Cost=NullIfWhiteSpace(account.CostOfSalesAccount),Waste=NullIfWhiteSpace(account.WasteAccount),Enabled=account.DailyPolicyEnabled},tx,cancellationToken:ct));
      await tx.CommitAsync(ct); return RestaurantCommandResult.Ok("La configuración operativa y contable fue guardada.");
    }
    catch(SqlException ex) when(ex.Number is 2601 or 2627){await tx.RollbackAsync(ct);return RestaurantCommandResult.Fail("Hay códigos o prioridades duplicados en la configuración.");}
    catch{await tx.RollbackAsync(ct);throw;}
  }

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  private static bool IsValidImage(byte[]? bytes, string? contentType)
    => bytes is null || (bytes.Length <= 8 * 1024 * 1024 &&
                         contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);

  private static string? NullIfWhiteSpace(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private static async Task<string?> FindInvalidAccountingAccountAsync(
    DbConnection conn,
    DbTransaction tx,
    string rfc,
    RestaurantAccountingConfigurationDto configuration,
    CancellationToken ct)
  {
    foreach (var (label, code) in GetConfiguredAccountingAccounts(configuration))
    {
      if (string.IsNullOrWhiteSpace(code))
      {
        continue;
      }

      var parts = code.Split(['.', '-', '/', '>'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
      {
        return $"La cuenta de {label} tiene un formato inválido. Selecciónala nuevamente desde el catálogo contable.";
      }

      var exists = await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
        """
        SELECT CAST(CASE WHEN EXISTS
        (
          SELECT 1 FROM dbo.CuentasContables
          WHERE RFC=@Rfc AND Nivel1=@Nivel1 AND Nivel2=@Nivel2 AND Nivel3=@Nivel3 AND Nivel3<>'00'
        ) THEN 1 ELSE 0 END AS bit);
        """,
        new { Rfc = rfc, Nivel1 = parts[0], Nivel2 = parts[1], Nivel3 = parts[2] },
        tx,
        cancellationToken: ct));
      if (!exists)
      {
        return $"La cuenta {code} de {label} no existe como nivel 3 en el catálogo del RFC activo.";
      }
    }

    return null;
  }

  private static IEnumerable<(string Label, string? Code)> GetConfiguredAccountingAccounts(
    RestaurantAccountingConfigurationDto configuration)
  {
    yield return ("efectivo", configuration.CashAccount);
    yield return ("banco / tarjetas", configuration.CardBankAccount);
    yield return ("transferencias", configuration.TransferBankAccount);
    yield return ("CxC plataformas", configuration.PlatformReceivableAccount);
    yield return ("ventas", configuration.SalesAccount);
    yield return ("IVA", configuration.VatAccount);
    yield return ("descuentos", configuration.DiscountAccount);
    yield return ("propinas por pagar", configuration.TipsPayableAccount);
    yield return ("comisiones", configuration.PlatformCommissionAccount);
    yield return ("inventario", configuration.InventoryAccount);
    yield return ("costo de venta", configuration.CostOfSalesAccount);
    yield return ("merma", configuration.WasteAccount);
  }

  private sealed class ProductGroupRow
  {
    public long ProductId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; }
  }

  private sealed class ProductOptionRow
  {
    public long ProductId { get; set; }
    public long ModifierGroupId { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PriceDelta { get; set; }
  }

  private sealed class ProductAllergenRow
  {
    public long ProductId { get; set; }
    public string Name { get; set; } = string.Empty;
  }

  private sealed class MenuSectionRow
  {
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
  }

  private sealed class MenuItemRow
  {
    public long MenuSectionId { get; set; }
    public long ProductId { get; set; }
  }

  private sealed class ImageRow
  {
    public byte[]? Bytes { get; set; }
    public string? ContentType { get; set; }
  }

  private sealed class MenuAdminRow { public long Id { get; set; } public string MenuCode { get; set; }=string.Empty; public string Name { get; set; }=string.Empty; public bool IsPublished { get; set; } public bool IsActive { get; set; } }
  private sealed class MenuScheduleAdminRow { public long MenuId { get; set; } public int SiteId { get; set; } public byte DayOfWeek { get; set; } public TimeSpan StartsAt { get; set; } public TimeSpan EndsAt { get; set; } }
  private sealed class MenuSectionAdminRow { public long Id { get; set; } public long MenuId { get; set; } public string Name { get; set; }=string.Empty; public int SortOrder { get; set; } }
  private sealed class MenuItemAdminRow { public long MenuId { get; set; } public long MenuSectionId { get; set; } public long ProductId { get; set; } }
  private sealed class ModifierGroupAdminRow { public long Id { get; set; } public string Name { get; set; }=string.Empty; public int MinSelections { get; set; } public int MaxSelections { get; set; } public int SortOrder { get; set; } public bool IsActive { get; set; } }
  private sealed class ModifierProductAdminRow { public long ModifierGroupId { get; set; } public long ProductId { get; set; } }
  private sealed class ModifierOptionAdminRow { public long Id { get; set; } public long ModifierGroupId { get; set; } public string Name { get; set; }=string.Empty; public decimal PriceDelta { get; set; } public int SortOrder { get; set; } }
  private sealed class ModifierDeltaAdminRow { public long ModifierOptionId { get; set; } public int MaterialId { get; set; } public decimal QuantityDelta { get; set; } public int UnitId { get; set; } }
}
