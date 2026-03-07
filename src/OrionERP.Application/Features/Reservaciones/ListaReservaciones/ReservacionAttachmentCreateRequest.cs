using System;

namespace OrionERP.Application.Features.Reservaciones.ListaReservaciones;

public sealed class ReservacionAttachmentCreateRequest
{
  public const int MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

  public int ReservationId { get; init; }
  public string FileName { get; init; } = string.Empty;
  public string? Extension { get; init; }
  public string? Description { get; init; }
  public byte[] Content { get; init; } = Array.Empty<byte>();
}
