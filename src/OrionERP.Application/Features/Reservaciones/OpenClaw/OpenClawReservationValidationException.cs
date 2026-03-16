using System;

namespace OrionERP.Application.Features.Reservaciones.OpenClaw;

public sealed class OpenClawReservationValidationException : Exception
{
  public OpenClawReservationValidationException(string message)
    : base(message)
  {
  }
}
