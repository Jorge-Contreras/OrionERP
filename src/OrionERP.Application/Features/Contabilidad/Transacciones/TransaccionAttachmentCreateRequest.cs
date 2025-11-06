using System;

namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class TransaccionAttachmentCreateRequest
{
  public const int MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

  public int TransaccionId { get; init; }
  public string FileName { get; init; } = string.Empty;
  public string? Extension { get; init; }
  public string? Description { get; init; }
  public byte[] Content { get; init; } = Array.Empty<byte>();
}
