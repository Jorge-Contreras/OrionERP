namespace OrionERP.Application.Features.OrdenesTrabajo;

public static class OrdenTrabajoSchedule
{
  public static DateTime CleaningDate(DateTime occupiedRoomDate) => occupiedRoomDate.Date.AddDays(1);
}
