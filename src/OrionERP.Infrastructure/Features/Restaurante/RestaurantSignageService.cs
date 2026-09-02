using System.Data.Common;
using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Logistica.Shared;
using OrionERP.Application.Features.Restaurante;
using SkiaSharp;

namespace OrionERP.Infrastructure.Features.Restaurante;

public sealed class RestaurantSignageService : IRestaurantSignageService
{
  private const int ThumbnailMaxEdge = 480;

  private readonly IDbConnectionFactory _connectionFactory;

  public RestaurantSignageService(IDbConnectionFactory connectionFactory)
  {
    _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
  }

  // ---------------------------------------------------------------- lectura administrativa

  public async Task<IReadOnlyList<RestaurantSignageScreenDto>> GetScreensAsync(string rfc, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);

    // Content y Thumbnail se excluyen a propósito: son varbinary(max) de varios MB
    // y esta consulta alimenta el listado completo de la pestaña.
    const string sql =
      """
      SELECT screen.Id, screen.Rfc, screen.SiteId, site.[Name] AS SiteName, screen.ScreenKey,
             screen.[Name], screen.RotationSeconds, screen.RefreshSeconds, screen.TransitionMs,
             screen.SortOrder, screen.IsEnabled, screen.UpdatedAt, screen.UpdatedBy
      FROM restaurante.SignageScreen screen
      LEFT JOIN restaurante.Site site ON site.Rfc = screen.Rfc AND site.Id = screen.SiteId
      WHERE screen.Rfc = @Rfc
      ORDER BY screen.SortOrder, screen.Id;

      SELECT image.Id, image.ScreenId, image.SortOrder, image.[FileName], image.ContentType,
             image.ByteLength, image.Width, image.Height, image.AltText, image.IsEnabled,
             CONVERT(varchar(64), image.ContentHash, 2) AS ContentHash
      FROM restaurante.SignageScreenImage image
      WHERE image.Rfc = @Rfc
      ORDER BY image.ScreenId, image.SortOrder, image.Id;
      """;

    using var conn = CreateConnection();
    using var grid = await conn.QueryMultipleAsync(
      new CommandDefinition(sql, new { Rfc = normalizedRfc }, cancellationToken: ct));

    var screens = (await grid.ReadAsync<RestaurantSignageScreenDto>()).AsList();
    var images = (await grid.ReadAsync<SignageImageRow>()).AsList();

    var byScreen = images.GroupBy(image => image.ScreenId).ToDictionary(group => group.Key, group => group.ToList());
    foreach (var screen in screens)
    {
      if (!byScreen.TryGetValue(screen.Id, out var rows)) continue;
      screen.Images = rows.Select(row => new RestaurantSignageImageDto
      {
        Id = row.Id,
        SortOrder = row.SortOrder,
        FileName = row.FileName,
        ContentType = row.ContentType,
        ByteLength = row.ByteLength,
        Width = row.Width,
        Height = row.Height,
        AltText = row.AltText,
        IsEnabled = row.IsEnabled,
        ContentHash = row.ContentHash
      }).ToList();
    }

    return screens;
  }

  public async Task<RestaurantSignageImagePayload?> GetImageThumbnailAsync(string rfc, long imageId, CancellationToken ct = default)
  {
    const string sql =
      """
      SELECT COALESCE(Thumbnail, Content) AS Bytes, ContentType,
             CONVERT(varchar(64), ContentHash, 2) AS ContentHash
      FROM restaurante.SignageScreenImage
      WHERE Rfc = @Rfc AND Id = @ImageId;
      """;

    using var conn = CreateConnection();
    var row = await conn.QueryFirstOrDefaultAsync<SignageImagePayloadRow>(
      new CommandDefinition(sql, new { Rfc = LogisticsRfc.Require(rfc), ImageId = imageId }, cancellationToken: ct));
    return ToPayload(row);
  }

  // ---------------------------------------------------------------- escritura administrativa

  public async Task<RestaurantCommandResult> SaveScreenAsync(RestaurantSignageScreenSaveRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);

    var key = RestaurantSignageDefaults.NormalizeKey(request.ScreenKey);
    if (!RestaurantSignageDefaults.IsValidKey(key))
      return RestaurantCommandResult.Fail(
        "La clave de la pantalla debe tener entre 2 y 40 caracteres, usar solo letras, números y guiones, y no puede ser «media» ni «manifest.json».");

    var name = request.Name?.Trim() ?? string.Empty;
    if (name.Length == 0) return RestaurantCommandResult.Fail("Captura el nombre de la pantalla.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);

    if (request.SiteId.HasValue)
    {
      var siteExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(1) FROM restaurante.Site WHERE Rfc = @Rfc AND Id = @SiteId;",
        new { Rfc = rfc, request.SiteId }, cancellationToken: ct));
      if (siteExists == 0)
        return RestaurantCommandResult.Fail("La sede seleccionada no pertenece al RFC de la sesión.");
    }

    try
    {
      if (request.Id.HasValue)
      {
        const string updateSql =
          """
          UPDATE restaurante.SignageScreen
          SET SiteId = @SiteId,
              ScreenKey = @ScreenKey,
              [Name] = @Name,
              RotationSeconds = @RotationSeconds,
              RefreshSeconds = @RefreshSeconds,
              TransitionMs = @TransitionMs,
              SortOrder = @SortOrder,
              IsEnabled = @IsEnabled,
              UpdatedAt = SYSUTCDATETIME(),
              UpdatedBy = @UpdatedBy
          WHERE Rfc = @Rfc AND Id = @Id;
          """;
        var affected = await conn.ExecuteAsync(new CommandDefinition(updateSql, new
        {
          Rfc = rfc,
          request.Id,
          request.SiteId,
          ScreenKey = key,
          Name = name,
          request.RotationSeconds,
          request.RefreshSeconds,
          request.TransitionMs,
          request.SortOrder,
          request.IsEnabled,
          request.UpdatedBy
        }, cancellationToken: ct));

        return affected == 1
          ? RestaurantCommandResult.Ok("La pantalla fue actualizada.", request.Id)
          : RestaurantCommandResult.Fail("La pantalla no existe en el RFC seleccionado.");
      }

      const string insertSql =
        """
        INSERT INTO restaurante.SignageScreen
          (Rfc, SiteId, ScreenKey, [Name], RotationSeconds, RefreshSeconds, TransitionMs, SortOrder, IsEnabled, UpdatedBy)
        VALUES
          (@Rfc, @SiteId, @ScreenKey, @Name, @RotationSeconds, @RefreshSeconds, @TransitionMs,
           COALESCE(@SortOrder, (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM restaurante.SignageScreen WHERE Rfc = @Rfc)),
           @IsEnabled, @UpdatedBy);
        SELECT CAST(SCOPE_IDENTITY() AS int);
        """;
      var id = await conn.ExecuteScalarAsync<int>(new CommandDefinition(insertSql, new
      {
        Rfc = rfc,
        request.SiteId,
        ScreenKey = key,
        Name = name,
        request.RotationSeconds,
        request.RefreshSeconds,
        request.TransitionMs,
        SortOrder = request.SortOrder == 0 ? (int?)null : request.SortOrder,
        request.IsEnabled,
        request.UpdatedBy
      }, cancellationToken: ct));

      return RestaurantCommandResult.Ok($"La pantalla fue creada. Su URL pública es /menus/{rfc}/{key}.", id);
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      return RestaurantCommandResult.Fail("Ya existe una pantalla con esa clave para el RFC seleccionado.");
    }
  }

  public async Task<RestaurantCommandResult> DeleteScreenAsync(string rfc, int screenId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);

    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var screenExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(1) FROM restaurante.SignageScreen WHERE Rfc = @Rfc AND Id = @ScreenId;",
        new { Rfc = normalizedRfc, ScreenId = screenId }, tx, cancellationToken: ct));
      if (screenExists == 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La pantalla no existe en el RFC seleccionado.");
      }

      await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM restaurante.SignageScreenImage WHERE Rfc = @Rfc AND ScreenId = @ScreenId;",
        new { Rfc = normalizedRfc, ScreenId = screenId }, tx, cancellationToken: ct));
      await conn.ExecuteAsync(new CommandDefinition(
        "DELETE FROM restaurante.SignageScreen WHERE Rfc = @Rfc AND Id = @ScreenId;",
        new { Rfc = normalizedRfc, ScreenId = screenId }, tx, cancellationToken: ct));

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("La pantalla y sus imágenes fueron eliminadas.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> AddImageAsync(RestaurantSignageImageUploadRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);

    var bytes = request.Content;
    if (bytes is null || bytes.Length == 0)
      return RestaurantCommandResult.Fail("El archivo llegó vacío. Vuelve a seleccionarlo.");
    if (bytes.Length > RestaurantSignageDefaults.MaxImageBytes)
      return RestaurantCommandResult.Fail("La imagen del tablero debe pesar como máximo 25 MB.");

    // El tipo se deduce de los bytes, nunca del navegador: esta imagen se sirve
    // después en una URL pública y anónima.
    var contentType = RestaurantSignageDefaults.SniffContentType(bytes);
    if (contentType is null)
      return RestaurantCommandResult.Fail("Usa una imagen PNG, JPEG o WebP válida.");

    var decoded = DecodeMetadata(bytes);
    if (decoded is null)
      return RestaurantCommandResult.Fail("No fue posible leer la imagen. Verifica que el archivo no esté dañado.");

    var (width, height, thumbnail) = decoded.Value;
    var hash = SHA256.HashData(bytes);

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var screenExists = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(1) FROM restaurante.SignageScreen WHERE Rfc = @Rfc AND Id = @ScreenId;",
        new { Rfc = rfc, request.ScreenId }, tx, cancellationToken: ct));
      if (screenExists == 0)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("La pantalla no existe en el RFC seleccionado.");
      }

      var imageCount = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
        "SELECT COUNT(1) FROM restaurante.SignageScreenImage WHERE Rfc = @Rfc AND ScreenId = @ScreenId;",
        new { Rfc = rfc, request.ScreenId }, tx, cancellationToken: ct));
      if (imageCount >= RestaurantSignageDefaults.MaxImagesPerScreen)
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail(
          $"Una pantalla admite como máximo {RestaurantSignageDefaults.MaxImagesPerScreen} imágenes.");
      }

      const string insertSql =
        """
        INSERT INTO restaurante.SignageScreenImage
          (Rfc, ScreenId, SortOrder, [FileName], ContentType, ByteLength, Width, Height,
           Content, Thumbnail, ContentHash, AltText, UpdatedBy)
        VALUES
          (@Rfc, @ScreenId,
           (SELECT COALESCE(MAX(SortOrder), -1) + 1 FROM restaurante.SignageScreenImage WHERE Rfc = @Rfc AND ScreenId = @ScreenId),
           @FileName, @ContentType, @ByteLength, @Width, @Height,
           @Content, @Thumbnail, @ContentHash, @AltText, @UpdatedBy);
        SELECT CAST(SCOPE_IDENTITY() AS bigint);
        """;
      var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(insertSql, new
      {
        Rfc = rfc,
        request.ScreenId,
        FileName = TrimTo(request.FileName, 260),
        ContentType = contentType,
        ByteLength = bytes.Length,
        Width = width,
        Height = height,
        Content = bytes,
        Thumbnail = thumbnail,
        ContentHash = hash,
        AltText = TrimTo(request.AltText, 300),
        request.UpdatedBy
      }, tx, cancellationToken: ct));

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok($"La imagen fue agregada ({width}×{height}).", id);
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> ReorderImagesAsync(RestaurantSignageOrderRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var rfc = LogisticsRfc.Require(request.Rfc);
    if (request.ImageIdsInOrder.Count == 0)
      return RestaurantCommandResult.Ok("No hay imágenes que reordenar.");

    using var conn = CreateConnection();
    await conn.OpenAsync(ct);
    await using var tx = await conn.BeginTransactionAsync(ct);
    try
    {
      var current = (await conn.QueryAsync<long>(new CommandDefinition(
        "SELECT Id FROM restaurante.SignageScreenImage WHERE Rfc = @Rfc AND ScreenId = @ScreenId;",
        new { Rfc = rfc, request.ScreenId }, tx, cancellationToken: ct))).ToHashSet();

      if (current.Count != request.ImageIdsInOrder.Count || !request.ImageIdsInOrder.All(current.Contains))
      {
        await tx.RollbackAsync(ct);
        return RestaurantCommandResult.Fail("El orden recibido no coincide con las imágenes de la pantalla. Actualiza la página.");
      }

      // SortOrder no es único a propósito: reescribir el conjunto completo en un
      // solo lote evita las colisiones transitorias que provocaría una restricción.
      var updates = request.ImageIdsInOrder
        .Select((imageId, index) => new { Rfc = rfc, request.ScreenId, Id = imageId, SortOrder = index })
        .ToArray();

      await conn.ExecuteAsync(new CommandDefinition(
        """
        UPDATE restaurante.SignageScreenImage
        SET SortOrder = @SortOrder, UpdatedAt = SYSUTCDATETIME()
        WHERE Rfc = @Rfc AND ScreenId = @ScreenId AND Id = @Id;
        """,
        updates, tx, cancellationToken: ct));

      await tx.CommitAsync(ct);
      return RestaurantCommandResult.Ok("El orden de las imágenes fue actualizado.");
    }
    catch
    {
      await tx.RollbackAsync(ct);
      throw;
    }
  }

  public async Task<RestaurantCommandResult> DeleteImageAsync(string rfc, long imageId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    using var conn = CreateConnection();
    await conn.OpenAsync(ct);

    var affected = await conn.ExecuteAsync(new CommandDefinition(
      "DELETE FROM restaurante.SignageScreenImage WHERE Rfc = @Rfc AND Id = @ImageId;",
      new { Rfc = normalizedRfc, ImageId = imageId }, cancellationToken: ct));

    return affected == 1
      ? RestaurantCommandResult.Ok("La imagen fue eliminada.")
      : RestaurantCommandResult.Fail("La imagen no existe en el RFC seleccionado.");
  }

  // ---------------------------------------------------------------- lectura pública (anónima)

  public async Task<RestaurantSignagePublicScreenDto?> GetPublicScreenAsync(string rfc, string? screenKey, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);
    var key = string.IsNullOrWhiteSpace(screenKey) ? null : RestaurantSignageDefaults.NormalizeKey(screenKey);

    const string sql =
      """
      SELECT TOP (1) Id, Rfc, ScreenKey, [Name], RotationSeconds, RefreshSeconds, TransitionMs
      FROM restaurante.SignageScreen
      WHERE Rfc = @Rfc AND IsEnabled = 1 AND (@ScreenKey IS NULL OR ScreenKey = @ScreenKey)
      ORDER BY SortOrder, Id;
      """;

    using var conn = await OpenPublicAsync(normalizedRfc, ct);
    var screen = await conn.QuerySingleOrDefaultAsync<SignagePublicScreenRow>(
      new CommandDefinition(sql, new { Rfc = normalizedRfc, ScreenKey = key }, cancellationToken: ct));
    if (screen is null) return null;

    const string imagesSql =
      """
      SELECT Id, CONVERT(varchar(64), ContentHash, 2) AS ContentHash, AltText
      FROM restaurante.SignageScreenImage
      WHERE Rfc = @Rfc AND ScreenId = @ScreenId AND IsEnabled = 1
      ORDER BY SortOrder, Id;
      """;
    var images = (await conn.QueryAsync<RestaurantSignagePublicImageDto>(
      new CommandDefinition(imagesSql, new { Rfc = normalizedRfc, ScreenId = screen.Id }, cancellationToken: ct))).AsList();

    // Una pantalla sin imágenes no puede pintarse: el llamador debe caer al respaldo.
    if (images.Count == 0) return null;

    return new RestaurantSignagePublicScreenDto
    {
      Rfc = screen.Rfc,
      ScreenKey = screen.ScreenKey,
      Name = screen.Name,
      RotationSeconds = screen.RotationSeconds,
      RefreshSeconds = screen.RefreshSeconds,
      TransitionMs = screen.TransitionMs,
      Images = images
    };
  }

  public async Task<RestaurantSignageImagePayload?> GetPublicImageAsync(string rfc, long imageId, CancellationToken ct = default)
  {
    var normalizedRfc = LogisticsRfc.Require(rfc);

    const string sql =
      """
      SELECT image.Content AS Bytes, image.ContentType,
             CONVERT(varchar(64), image.ContentHash, 2) AS ContentHash
      FROM restaurante.SignageScreenImage image
      JOIN restaurante.SignageScreen screen ON screen.Rfc = image.Rfc AND screen.Id = image.ScreenId
      WHERE image.Rfc = @Rfc AND image.Id = @ImageId AND image.IsEnabled = 1 AND screen.IsEnabled = 1;
      """;

    using var conn = await OpenPublicAsync(normalizedRfc, ct);
    var row = await conn.QueryFirstOrDefaultAsync<SignageImagePayloadRow>(
      new CommandDefinition(sql, new { Rfc = normalizedRfc, ImageId = imageId }, cancellationToken: ct));
    return ToPayload(row);
  }

  // ---------------------------------------------------------------- infraestructura

  private DbConnection CreateConnection()
    => _connectionFactory.Create() as DbConnection
      ?? throw new InvalidOperationException("La fábrica de conexiones no devolvió una DbConnection.");

  /// <summary>
  /// Abre una conexión para las lecturas públicas del tablero.
  ///
  /// El tablero es público por diseño: la televisión no inicia sesión. La fábrica
  /// de conexiones deja SESSION_CONTEXT('OrionRfc') en '__UNSCOPED__' cuando no hay
  /// RFC de sesión, y el predicado de seguridad a nivel de fila
  /// (logistica.fn_RfcAccessPredicate) solo permite todas las filas cuando ese
  /// contexto es NULL. Sin este ajuste la consulta devolvería cero filas y la
  /// pantalla quedaría en negro justo en producción, aunque funcione al estar
  /// con sesión iniciada.
  ///
  /// El alcance es deliberadamente estrecho: solo lo usan las dos lecturas
  /// públicas, solo tocan tablas de señalización y siempre conservan Rfc = @Rfc.
  /// </summary>
  private async Task<DbConnection> OpenPublicAsync(string rfc, CancellationToken ct)
  {
    var conn = CreateConnection();
    try
    {
      await conn.OpenAsync(ct);
      await conn.ExecuteAsync(new CommandDefinition(
        "EXEC sys.sp_set_session_context @key=N'OrionRfc', @value=@Rfc, @read_only=0;",
        new { Rfc = rfc }, cancellationToken: ct));
      return conn;
    }
    catch
    {
      await conn.DisposeAsync();
      throw;
    }
  }

  private static (int Width, int Height, byte[]? Thumbnail)? DecodeMetadata(byte[] bytes)
  {
    try
    {
      using var source = SKBitmap.Decode(bytes);
      if (source is null || source.Width <= 0 || source.Height <= 0) return null;

      var scale = Math.Min(1d, (double)ThumbnailMaxEdge / Math.Max(source.Width, source.Height));
      var width = Math.Max(1, (int)Math.Round(source.Width * scale));
      var height = Math.Max(1, (int)Math.Round(source.Height * scale));

      using var resized = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
      using (var canvas = new SKCanvas(resized))
      {
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
      }

      using var image = SKImage.FromBitmap(resized);
      using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
      return (source.Width, source.Height, data?.ToArray());
    }
    catch
    {
      return null;
    }
  }

  // Dapper no puede materializar un record struct a través de un genérico
  // nullable, así que las imágenes se leen en una clase y se proyectan después,
  // igual que RestaurantCatalogService.GetProductImageAsync.
  private static RestaurantSignageImagePayload? ToPayload(SignageImagePayloadRow? row)
    => row?.Bytes is { Length: > 0 } bytes
      ? new RestaurantSignageImagePayload(bytes, row.ContentType ?? "image/png", row.ContentHash ?? string.Empty)
      : null;

  private static string? TrimTo(string? value, int maxLength)
  {
    if (string.IsNullOrWhiteSpace(value)) return null;
    var trimmed = value.Trim();
    return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
  }

  private sealed class SignageImageRow
  {
    public long Id { get; set; }
    public int ScreenId { get; set; }
    public int SortOrder { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int ByteLength { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? AltText { get; set; }
    public bool IsEnabled { get; set; }
    public string ContentHash { get; set; } = string.Empty;
  }

  private sealed class SignageImagePayloadRow
  {
    public byte[]? Bytes { get; set; }
    public string? ContentType { get; set; }
    public string? ContentHash { get; set; }
  }

  private sealed class SignagePublicScreenRow
  {
    public int Id { get; set; }
    public string Rfc { get; set; } = string.Empty;
    public string ScreenKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int RotationSeconds { get; set; }
    public int RefreshSeconds { get; set; }
    public int TransitionMs { get; set; }
  }
}
