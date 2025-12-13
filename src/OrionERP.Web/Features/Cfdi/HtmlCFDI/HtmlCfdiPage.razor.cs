using Microsoft.AspNetCore.Components;
using OrionERP.Application.Features.Cfdi.HtmlCFDI;
using OrionERP.Web.Services;

namespace OrionERP.Web.Features.Cfdi.HtmlCFDI;

public partial class HtmlCfdiPage : ComponentBase
{
  [Parameter]
  public int Id { get; set; }

  [Inject]
  public IHtmlCfdiService HtmlCfdiService { get; set; } = default!;

  [Inject]
  public IUiMessageService UiMessages { get; set; } = default!;

  protected CfdiReadableDocument? Document { get; set; }
  protected string? ErrorMessage { get; set; }
  protected bool IsLoading { get; set; }

  protected override async Task OnParametersSetAsync()
  {
    IsLoading = true;
    ErrorMessage = null;
    Document = null;

    try
    {
      Document = await HtmlCfdiService.GetHtmlCfdiAsync(Id);
    }
    catch (Exception ex)
    {
      ErrorMessage = ex.Message;
      UiMessages.ShowError(ErrorMessage);
    }
    finally
    {
      IsLoading = false;
    }
  }
}
