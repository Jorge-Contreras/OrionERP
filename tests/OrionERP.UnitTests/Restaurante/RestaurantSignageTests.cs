using OrionERP.Application.Features.Restaurante;

namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantSignageTests
{
  [Theory]
  [InlineData("Comida", "comida")]
  [InlineData("  BEBIDAS  ", "bebidas")]
  [InlineData("Terraza Jardín 3!!", "terraza-jardin-3")]
  [InlineData("Menú Principal", "menu-principal")]
  [InlineData("barra___lateral", "barra-lateral")]
  [InlineData("---", "")]
  public void NormalizeKey_ProducesUrlSafeLowercaseSlugs(string input, string expected)
    => Assert.Equal(expected, RestaurantSignageDefaults.NormalizeKey(input));

  [Theory]
  [InlineData("comida")]
  [InlineData("pantalla-2")]
  [InlineData("a1")]
  public void IsValidKey_AcceptsSlugs(string key)
    => Assert.True(RestaurantSignageDefaults.IsValidKey(key));

  [Theory]
  [InlineData("")]
  [InlineData("a")]
  [InlineData("Comida")]
  [InlineData("menú")]
  [InlineData("con espacio")]
  [InlineData("../etc")]
  // Ambos son segmentos hermanos de /menus/{rfc}/{screenKey}: permitirlos como
  // clave haría que una pantalla secuestrara la ruta de imágenes o del manifiesto.
  [InlineData("media")]
  [InlineData("manifest.json")]
  public void IsValidKey_RejectsUnsafeOrReservedKeys(string key)
    => Assert.False(RestaurantSignageDefaults.IsValidKey(key));

  [Fact]
  public void IsValidKey_RejectsKeysBeyondTheColumnLength()
    => Assert.False(RestaurantSignageDefaults.IsValidKey(new string('a', RestaurantSignageDefaults.MaxKeyLength + 1)));

  [Fact]
  public void SniffContentType_RecognizesTheThreeAllowedFormats()
  {
    Assert.Equal("image/png", RestaurantSignageDefaults.SniffContentType(
      [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
    Assert.Equal("image/jpeg", RestaurantSignageDefaults.SniffContentType(
      [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0]));
    Assert.Equal("image/webp", RestaurantSignageDefaults.SniffContentType(
      [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8]));
  }

  [Fact]
  public void SniffContentType_RejectsPayloadsThatWouldBecomeStoredXss()
  {
    // Estos bytes se sirven de vuelta desde una URL pública y anónima, así que
    // el tipo jamás se toma del navegador.
    Assert.Null(RestaurantSignageDefaults.SniffContentType("<svg xmlns=\"http://www.w3.org/2000/svg\">"u8.ToArray()));
    Assert.Null(RestaurantSignageDefaults.SniffContentType("<!DOCTYPE html><script>x()</script>"u8.ToArray()));
    Assert.Null(RestaurantSignageDefaults.SniffContentType([1, 2, 3]));
    Assert.Null(RestaurantSignageDefaults.SniffContentType(null));
  }

  [Fact]
  public void PublicReads_PinTheRowLevelSecurityContextToTheRouteRfc()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSignageService.cs");

    // La fábrica de conexiones deja SESSION_CONTEXT('OrionRfc') en '__UNSCOPED__'
    // cuando no hay sesión, y el predicado RLS solo abre cuando ese contexto es
    // NULL. Sin este ajuste el tablero anónimo devolvería cero filas.
    Assert.Contains("sp_set_session_context", service, StringComparison.Ordinal);
    Assert.Contains("OpenPublicAsync", service, StringComparison.Ordinal);

    // La capacidad debe estar acotada al ayudante: si aparece en otro sitio, el
    // servicio dejó de ser auditable.
    Assert.Equal(1, CountOccurrences(service, "sp_set_session_context"));

    var publicScreen = Slice(service, "GetPublicScreenAsync", "GetPublicImageAsync");
    var publicImage = Slice(service, "public async Task<RestaurantSignageImagePayload?> GetPublicImageAsync", "private DbConnection CreateConnection");
    foreach (var body in new[] { publicScreen, publicImage })
    {
      Assert.Contains("OpenPublicAsync", body, StringComparison.Ordinal);
      Assert.Contains("LogisticsRfc.Require", body, StringComparison.Ordinal);
      Assert.Contains("IsEnabled = 1", body, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void AdminScreenListing_NeverSelectsTheImageBlobs()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSignageService.cs");
    var listing = Slice(service, "public async Task<IReadOnlyList<RestaurantSignageScreenDto>> GetScreensAsync", "public async Task<RestaurantSignageImagePayload?> GetImageThumbnailAsync");

    // Los tableros pesan varios MB; traerlos en el listado de la pestaña haría
    // que abrirla descargara decenas de megabytes.
    Assert.DoesNotContain("image.Content AS Bytes", listing, StringComparison.Ordinal);
    Assert.DoesNotContain("image.Thumbnail AS Bytes", listing, StringComparison.Ordinal);
    Assert.DoesNotContain("image.Content,", listing, StringComparison.Ordinal);
    Assert.DoesNotContain("image.Thumbnail,", listing, StringComparison.Ordinal);
    Assert.Contains("image.ByteLength", listing, StringComparison.Ordinal);
    Assert.Contains("WHERE screen.Rfc = @Rfc", listing, StringComparison.Ordinal);
  }

  [Fact]
  public void EveryStatementInTheSignageServiceIsScopedByRfc()
  {
    var service = ReadRepoFile("src/OrionERP.Infrastructure/Features/Restaurante/RestaurantSignageService.cs");

    foreach (var table in new[] { "restaurante.SignageScreen", "restaurante.SignageScreenImage" })
    {
      Assert.Contains(table, service, StringComparison.Ordinal);
    }

    // Ninguna sentencia toca las tablas sin filtrar por RFC.
    Assert.DoesNotContain("FROM restaurante.SignageScreen;", service, StringComparison.Ordinal);
    Assert.DoesNotContain("DELETE FROM restaurante.SignageScreen WHERE Id", service, StringComparison.Ordinal);
    Assert.True(
      CountOccurrences(service, "LogisticsRfc.Require(request.Rfc)") + CountOccurrences(service, "LogisticsRfc.Require(rfc)") >=
      CountOccurrences(service, "public async Task<RestaurantCommandResult>"),
      "Cada comando público del servicio debe normalizar el RFC antes de tocar tablas de señalización.");
  }

  [Fact]
  public void BoardUpload_KeepsFullResolutionAndConsumesOneRemoteStream()
  {
    var panel = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/Signage/RestaurantScreensPanel.razor");

    // RequestImageFileAsync recodifica a JPEG y reduce la imagen: aplicarlo a un
    // tablero 4K arruinaría la nitidez del texto en la televisión.
    Assert.DoesNotContain("RequestImageFileAsync", panel, StringComparison.Ordinal);
    Assert.Contains("file.OpenReadStream(RestaurantSignageDefaults.MaxImageBytes", panel, StringComparison.Ordinal);
    Assert.Equal(1, CountOccurrences(panel, "OpenReadStream("));
    Assert.Contains("catch (TimeoutException)", panel, StringComparison.Ordinal);
    Assert.Contains("catch (OperationCanceledException)", panel, StringComparison.Ordinal);
    Assert.Contains("CancellationTokenSource(TimeSpan.FromMinutes(2))", panel, StringComparison.Ordinal);
  }

  [Fact]
  public void SignagePage_StaysAnonymousAndFallsBackWhenNothingResolves()
  {
    var model = ReadRepoFile("src/OrionERP.Web/Pages/Menus.cshtml.cs");
    var page = ReadRepoFile("src/OrionERP.Web/Pages/Menus.cshtml");

    Assert.Contains("[AllowAnonymous]", model, StringComparison.Ordinal);
    Assert.Contains("@page \"/menus/{rfc?}/{screenKey?}\"", page, StringComparison.Ordinal);

    // Un televisor sin vigilancia nunca debe quedar en negro: cualquier fallo
    // cae al respaldo estático, que es el comportamiento previo a esta función.
    Assert.Contains("UseLegacyStaticBoards", model, StringComparison.Ordinal);
    Assert.Contains("catch (Exception ex)", model, StringComparison.Ordinal);
    Assert.Contains("Signage:DefaultRfc", model, StringComparison.Ordinal);
    Assert.Contains("/Images/Brunos/Menus/menu-principal.png", page, StringComparison.Ordinal);
  }

  [Fact]
  public void SignageJs_RefreshesFromTheManifestAndKeepsSingleImageScreensStatic()
  {
    var js = ReadRepoFile("src/OrionERP.Web/wwwroot/js/menu-signage.js");

    Assert.Contains("data-manifest", ReadRepoFile("src/OrionERP.Web/Pages/Menus.cshtml"), StringComparison.Ordinal);
    Assert.Contains("dataset.manifest", js, StringComparison.Ordinal);

    // Una sola imagen es un tablero fijo, pero debe seguir recogiendo reemplazos:
    // la rotación se detiene, el refresco no.
    Assert.Contains("current.length < 2", js, StringComparison.Ordinal);
    Assert.Contains("refreshMs", js, StringComparison.Ordinal);

    // El refresco anterior usaba ?v=Date.now(), que anulaba toda la caché HTTP.
    Assert.DoesNotContain("Date.now()", js, StringComparison.Ordinal);
  }

  private static string Slice(string source, string from, string to)
  {
    var start = source.IndexOf(from, StringComparison.Ordinal);
    Assert.True(start >= 0, $"No se encontró «{from}».");
    var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
    Assert.True(end > start, $"No se encontró «{to}» después de «{from}».");
    return source[start..end];
  }

  private static int CountOccurrences(string source, string value)
  {
    var count = 0;
    var index = source.IndexOf(value, StringComparison.Ordinal);
    while (index >= 0)
    {
      count++;
      index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
    }

    return count;
  }

  private static string ReadRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
  }
}
