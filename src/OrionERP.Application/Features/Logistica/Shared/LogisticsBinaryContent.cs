namespace OrionERP.Application.Features.Logistica.Shared;

public sealed class LogisticsBinaryContent
{
  public int Id { get; set; }
  public string FileName { get; set; } = string.Empty;
  public string ContentType { get; set; } = "application/octet-stream";
  public byte[] Bytes { get; set; } = Array.Empty<byte>();
}
