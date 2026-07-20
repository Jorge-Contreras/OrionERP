namespace OrionERP.UnitTests.Restaurante;

public sealed class RestaurantAdminPageTests
{
  [Fact]
  public void InitialEditors_AreScopedBeforeTheFirstFormValidation()
  {
    var source = File.ReadAllText(GetRepoFile(
      "src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor"));
    var initializationStart = source.IndexOf(
      "protected override async Task OnInitializedAsync()",
      StringComparison.Ordinal);
    var newSite = source.IndexOf("NewSite();", initializationStart, StringComparison.Ordinal);
    var newProduct = source.IndexOf("NewProduct();", initializationStart, StringComparison.Ordinal);
    var firstAwait = source.IndexOf("await ReloadAsync();", initializationStart, StringComparison.Ordinal);

    Assert.True(initializationStart >= 0);
    Assert.InRange(newSite, initializationStart, firstAwait - 1);
    Assert.InRange(newProduct, initializationStart, firstAwait - 1);
    Assert.Contains("RFC activo:", source, StringComparison.Ordinal);
  }

  [Fact]
  public void ImageUpload_ConsumesRemoteStreamsSequentiallyAndHandlesTimeouts()
  {
    var source = File.ReadAllText(GetRepoFile(
      "src/OrionERP.Web/Features/Restaurante/RestaurantAdminPage.razor"));
    var fullImageRead = source.IndexOf(
      "ReadResizedImageAsync(file, 1600",
      StringComparison.Ordinal);
    var thumbnailRead = source.IndexOf(
      "ReadResizedImageAsync(file, 480",
      StringComparison.Ordinal);

    Assert.True(fullImageRead >= 0);
    Assert.True(thumbnailRead > fullImageRead);
    Assert.DoesNotContain("fullStream", source, StringComparison.Ordinal);
    Assert.DoesNotContain("thumbStream", source, StringComparison.Ordinal);
    Assert.Contains("catch (TimeoutException)", source, StringComparison.Ordinal);
    Assert.Contains("catch (OperationCanceledException)", source, StringComparison.Ordinal);
    Assert.Contains("Convert.ToBase64String(thumbnailBytes)", source, StringComparison.Ordinal);
    Assert.Contains("disabled=\"@(isSaving || isUploadingImage)\"", source, StringComparison.Ordinal);
  }

  [Fact]
  public void ImageUpload_ExtendsTheCircuitInteropTimeout()
  {
    var program = File.ReadAllText(GetRepoFile("src/OrionERP.Web/Program.cs"));

    Assert.Contains(
      "options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(2);",
      program,
      StringComparison.Ordinal);
  }

  private static string GetRepoFile(string relativePath)
    => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../", relativePath));
}
