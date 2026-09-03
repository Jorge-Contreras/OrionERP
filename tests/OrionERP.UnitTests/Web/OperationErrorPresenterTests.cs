using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrionERP.Web.Services;

namespace OrionERP.UnitTests.Web;

public sealed class OperationErrorPresenterTests
{
  private static readonly OperationErrorPresenter Presenter = new(NullLogger<OperationErrorPresenter>.Instance);

  [Fact]
  public void ToUserMessage_ForUnexpectedException_ExplainsOperationAndGivesReference()
  {
    var message = Presenter.ToUserMessage(new InvalidOperationException("boom"), "guardar la orden de compra");

    Assert.Contains("guardar la orden de compra", message);
    Assert.Contains("referencia", message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("boom", message);
  }

  [Fact]
  public void ToUserMessage_ForCancellation_IsFriendlyAndDoesNotLeakDetails()
  {
    var message = Presenter.ToUserMessage(new OperationCanceledException(), "cargar los materiales");

    Assert.Contains("cargar los materiales", message);
    Assert.Contains("conexión", message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void ToUserMessage_ForConcurrencyConflict_TellsUserToRefresh()
  {
    var message = Presenter.ToUserMessage(new DbUpdateConcurrencyException("stale"), "registrar la recepción");

    Assert.Contains("registrar la recepción", message);
    Assert.Contains("Actualiza la página", message);
  }

  [Fact]
  public void ToUserMessage_UnwrapsInnerException()
  {
    var wrapped = new Exception("outer", new OperationCanceledException());

    var message = Presenter.ToUserMessage(wrapped, "emitir la orden de compra");

    Assert.Contains("canceló o tardó demasiado", message);
  }

  [Fact]
  public void ToUserMessage_WithBlankOperation_FallsBackToGenericPhrasing()
  {
    var message = Presenter.ToUserMessage(new Exception("x"), "   ");

    Assert.Contains("completar la operación", message);
  }
}
