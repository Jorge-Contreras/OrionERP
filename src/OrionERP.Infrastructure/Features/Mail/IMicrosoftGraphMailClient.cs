namespace OrionERP.Infrastructure.Features.Mail;

public interface IMicrosoftGraphMailClient<TOptions>
  where TOptions : MicrosoftGraphMailOptions
{
  /// <summary>
  /// Creates a durable draft and returns the Outlook immutable message id.
  /// Persist this id before asking Graph to send the draft.
  /// </summary>
  Task<string> CreateDraftAsync(
    MicrosoftGraphMailMessage mail,
    CancellationToken ct = default);

  /// <summary>
  /// Reads the current state of a message by its Outlook immutable id.
  /// </summary>
  Task<MicrosoftGraphMailMessageState> GetMessageStateAsync(
    string immutableMessageId,
    CancellationToken ct = default);

  /// <summary>
  /// Sends an existing draft identified by its Outlook immutable id.
  /// </summary>
  Task SendDraftAsync(
    string immutableMessageId,
    CancellationToken ct = default);

  Task SendEmailAsync(
    MicrosoftGraphMailMessage mail,
    CancellationToken ct = default);

  Task SendEmailAsync(
    string email,
    string subject,
    string message,
    CancellationToken ct = default);
}

public enum MicrosoftGraphMailMessageState
{
  Draft,
  Sent,
  Missing
}

public sealed class MicrosoftGraphMailMessage
{
  public IReadOnlyList<string> ToRecipients { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> CcRecipients { get; set; } = Array.Empty<string>();
  public IReadOnlyList<string> BccRecipients { get; set; } = Array.Empty<string>();
  public string Subject { get; set; } = string.Empty;
  public string Message { get; set; } = string.Empty;
}
