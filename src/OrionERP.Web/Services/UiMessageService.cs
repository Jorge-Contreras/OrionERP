using System;

namespace OrionERP.Web.Services
{
  public enum UiMessageLevel
  {
    Info,
    Success,
    Warning,
    Error
  }

  public record UiMessage(UiMessageLevel Level, string Message, string? Title = null)
  {
    public string CssClass => Level switch
    {
      UiMessageLevel.Success => "alert-success",
      UiMessageLevel.Warning => "alert-warning",
      UiMessageLevel.Error => "alert-danger",
      _ => "alert-info"
    };
  }

  public interface IUiMessageService
  {
    UiMessage? Current { get; }
    event Action? OnChange;
    void Show(UiMessage message);
    void ShowInfo(string message, string? title = null);
    void ShowSuccess(string message, string? title = null);
    void ShowWarning(string message, string? title = null);
    void ShowError(string message, string? title = null);
    void Clear();
  }

  public class UiMessageService : IUiMessageService
  {
    private readonly object _sync = new();
    private UiMessage? _current;

    public UiMessage? Current
    {
      get
      {
        lock (_sync)
        {
          return _current;
        }
      }
    }

    public event Action? OnChange;

    public void Show(UiMessage message)
    {
      lock (_sync)
      {
        _current = message;
      }
      OnChange?.Invoke();
    }

    public void ShowInfo(string message, string? title = null)
      => Show(new UiMessage(UiMessageLevel.Info, message, title));

    public void ShowSuccess(string message, string? title = null)
      => Show(new UiMessage(UiMessageLevel.Success, message, title));

    public void ShowWarning(string message, string? title = null)
      => Show(new UiMessage(UiMessageLevel.Warning, message, title));

    public void ShowError(string message, string? title = null)
      => Show(new UiMessage(UiMessageLevel.Error, message, title));

    public void Clear()
    {
      lock (_sync)
      {
        _current = null;
      }
      OnChange?.Invoke();
    }
  }
}
