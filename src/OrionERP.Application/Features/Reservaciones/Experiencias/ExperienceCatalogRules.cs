using System;

namespace OrionERP.Application.Features.Reservaciones.Experiencias;

public static class ExperienceCatalogRules
{
  public static bool IsAvailableForStay(ExperienceCatalogItemDto experience, DateOnly checkIn, DateOnly checkOut)
  {
    ArgumentNullException.ThrowIfNull(experience);

    if (checkOut <= checkIn)
    {
      return false;
    }

    if (experience.SeasonStart.HasValue && experience.SeasonStart.Value >= checkOut)
    {
      return false;
    }

    if (experience.SeasonEnd.HasValue && experience.SeasonEnd.Value < checkIn)
    {
      return false;
    }

    return true;
  }

  public static DateOnly GetFirstSelectableDate(ExperienceCatalogItemDto experience, DateOnly checkIn)
  {
    ArgumentNullException.ThrowIfNull(experience);
    return experience.SeasonStart.HasValue && experience.SeasonStart.Value > checkIn
      ? experience.SeasonStart.Value
      : checkIn;
  }

  public static DateOnly GetLastSelectableDate(ExperienceCatalogItemDto experience, DateOnly checkOut)
  {
    ArgumentNullException.ThrowIfNull(experience);
    var lastStayDate = checkOut.AddDays(-1);
    return experience.SeasonEnd.HasValue && experience.SeasonEnd.Value < lastStayDate
      ? experience.SeasonEnd.Value
      : lastStayDate;
  }

  public static DateOnly ResolveDefaultDate(ExperienceCatalogItemDto experience, DateOnly checkIn, DateOnly checkOut)
  {
    ArgumentNullException.ThrowIfNull(experience);

    if (!IsAvailableForStay(experience, checkIn, checkOut))
    {
      return checkIn;
    }

    var firstSelectable = GetFirstSelectableDate(experience, checkIn);
    var lastSelectable = GetLastSelectableDate(experience, checkOut);
    return firstSelectable <= lastSelectable ? firstSelectable : checkIn;
  }

  public static int GetParticipantLimit(ExperienceCatalogItemDto? experience, int suiteCapacity)
  {
    var limit = Math.Max(suiteCapacity, 1);
    if (experience?.MaximumParticipants is int maximumParticipants && maximumParticipants > 0)
    {
      limit = Math.Min(limit, maximumParticipants);
    }

    return Math.Max(limit, 1);
  }
}
