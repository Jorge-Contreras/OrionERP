using System.Text.RegularExpressions;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Contabilidad;

public sealed class TransaccionAttachmentCameraTests
{
  private const string PagePath = "src/OrionERP.Web/Features/Contabilidad/Transacciones/TransaccionPage.razor";
  private const string CodePath = "src/OrionERP.Web/Features/Contabilidad/Transacciones/TransaccionPage.razor.cs";

  [Fact]
  public void Attachments_CanBeTakenWithTheCameraOrUploaded()
  {
    var page = RepoFile.Read(PagePath);
    var code = RepoFile.Read(CodePath);

    Assert.Contains("<InputFile", page, StringComparison.Ordinal);
    Assert.Contains("Cargar archivo", page, StringComparison.Ordinal);
    Assert.Contains("Tomar foto", page, StringComparison.Ordinal);
    Assert.Contains("transaccion-camera-overlay", page, StringComparison.Ordinal);
    Assert.Contains("CaptureCameraAsync", code, StringComparison.Ordinal);
    Assert.Contains("getLastImage", code, StringComparison.Ordinal);
    // Mismo módulo que Órdenes de trabajo y Merma: no hay una segunda copia del acceso a la cámara.
    Assert.Contains("./js/orden-trabajo-camera.js", code, StringComparison.Ordinal);
    Assert.Contains("TransaccionService.AddAttachmentAsync", code, StringComparison.Ordinal);
  }

  [Fact]
  public void Camera_IsReleasedWhenLeavingThePage()
  {
    var code = RepoFile.Read(CodePath);

    // Navegar dentro de Blazor no dispara pagehide: sin esto la cámara queda encendida.
    Assert.Contains("IAsyncDisposable", code, StringComparison.Ordinal);
    Assert.Matches(
      new Regex(@"public async ValueTask DisposeAsync\(\)\s*\{[^}]*CloseCameraAsync\(showState: false\)", RegexOptions.Singleline),
      code);
  }
}
