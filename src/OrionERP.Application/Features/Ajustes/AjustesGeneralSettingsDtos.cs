namespace OrionERP.Application.Features.Ajustes;

public sealed record AjustesGeneralSettingsDto
{
  public const int DefaultApOccurrenceNotificationDays = 5;
  public const int MinApOccurrenceNotificationDays = 0;
  public const int MaxApOccurrenceNotificationDays = 365;

  public int ApOccurrenceNotificationDays { get; init; } = DefaultApOccurrenceNotificationDays;
}

public sealed record AjustesGeneralSettingsSaveRequest
{
  public int ApOccurrenceNotificationDays { get; init; } = AjustesGeneralSettingsDto.DefaultApOccurrenceNotificationDays;
}
