using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.CapitalHumano;

/// <summary>
/// El kiosco es la única pantalla del módulo que no vive dentro de
/// <c>.workforce-page</c>. Cuando los tokens de color se definían sólo en ese
/// contenedor, dentro del kiosco <c>var(--wf-accent)</c> quedaba indefinida y
/// eso invalidaba por completo el <c>background</c> de <c>.workforce-punch</c>:
/// el botón "Confirmar registro" quedaba en texto blanco sobre el panel blanco.
///
/// No era cosmético. Ese botón es el que envía el registro, así que un botón
/// invisible significaba entradas y salidas que nunca se guardaban.
/// </summary>
public class KioskUxTests
{
  private const string Stylesheet = "src/OrionERP.Web/wwwroot/css/workforce.css";
  private const string KioskPage = "src/OrionERP.Web/Features/CapitalHumano/Workforce/KioskPage.razor";

  [Fact]
  public void TokensDeColor_AlcanzanTambienAlKiosco()
  {
    var css = RepoFile.Read(Stylesheet);

    var declaration = css[..css.IndexOf("--wf-warm", StringComparison.Ordinal)];
    Assert.Contains(".workforce-kiosk", declaration, StringComparison.Ordinal);
    Assert.Contains(".workforce-page", declaration, StringComparison.Ordinal);
  }

  [Fact]
  public void BotonDeRegistro_NuncaQuedaSinFondoAunqueFalteElToken()
  {
    var css = RepoFile.Read(Stylesheet);
    var rule = css[css.IndexOf(".workforce-punch {", StringComparison.Ordinal)..];
    rule = rule[..rule.IndexOf('}')];

    // Sin respaldo, un token ausente invalida todo el background y el boton
    // desaparece sobre el panel blanco.
    Assert.Contains("var(--wf-accent, #087f8c)", rule, StringComparison.Ordinal);
    Assert.Contains("var(--wf-accent-dark, #075e67)", rule, StringComparison.Ordinal);
  }

  /// <summary>
  /// El kiosco lo comparte todo el personal. Si el movimiento quedara preseleccionado
  /// o se quedara pegado despues de marcar, la siguiente persona registraria con el
  /// movimiento de la anterior sin notarlo: exactamente lo que se reporto en
  /// produccion como "manda el comando anterior".
  /// </summary>
  [Fact]
  public void Movimiento_NoVienePreseleccionadoNiSeQuedaPegado()
  {
    var page = RepoFile.Read(KioskPage);

    Assert.Contains("private string eventType=string.Empty;", page, StringComparison.Ordinal);
    Assert.DoesNotContain("private string eventType=AttendanceEventTypes.In;", page, StringComparison.Ordinal);

    var punch = page[page.IndexOf("private async Task PunchAsync()", StringComparison.Ordinal)..];
    punch = punch[..punch.IndexOf("private static string Clean", StringComparison.Ordinal)];
    Assert.Contains("eventType=string.Empty;", punch, StringComparison.Ordinal);
  }

  /// <summary>
  /// Confirmar tiene que decir que movimiento va a enviar, y no dejarse tocar
  /// mientras no haya uno elegido.
  /// </summary>
  [Fact]
  public void Confirmar_SeBloqueaSinMovimientoYNombraElQueEnviara()
  {
    var page = RepoFile.Read(KioskPage);

    Assert.Contains("disabled=\"@(busy || string.IsNullOrEmpty(eventType))\"", page, StringComparison.Ordinal);
    Assert.Contains("Selecciona el movimiento", page, StringComparison.Ordinal);
    Assert.Contains("$\"Confirmar {SelectedLabel}\"", page, StringComparison.Ordinal);
  }

  /// <summary>
  /// Con @bind normal el valor viaja al perder el foco, asi que el primer toque en un
  /// boton se gastaba disparando ese cambio y redibujando justo entre el toque y el
  /// clic. Enlazar al teclear quita esa carrera en tabletas.
  /// </summary>
  [Fact]
  public void GafeteYPin_SeEnlazanAlTeclearNoAlPerderElFoco()
  {
    var page = RepoFile.Read(KioskPage);

    Assert.Equal(2, page.Split("@bind:event=\"oninput\"").Length - 1);
  }

  [Fact]
  public void Botones_DeclaranTypeButton()
  {
    var page = RepoFile.Read(KioskPage);

    // Un <button> sin type es submit por defecto; en un kiosco eso es una recarga
    // silenciosa esperando a ocurrir.
    Assert.DoesNotContain("<button class=", page, StringComparison.Ordinal);
  }
}
