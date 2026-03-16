using System;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public sealed class OpenClawReservationConflictException : Exception
{
  public OpenClawReservationConflictException(string message)
    : base(message)
  {
  }
}
