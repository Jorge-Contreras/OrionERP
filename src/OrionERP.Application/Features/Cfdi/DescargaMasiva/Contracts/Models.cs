using System;
using System.Collections.Generic;

namespace OrionERP.Application.Features.Cfdi.DescargaMasiva.Contracts
{
  // Keep this layer free of external deps (SAT lib).
  // Same numeric values as SAT WS docs.
  public enum EstadoSolicitud
  {
    Aceptada = 1,
    EnProceso = 2,
    Terminada = 3,
    Error = 4,
    Rechazada = 5,
    Vencida = 6,
    Procesada = 7
  }

  public sealed record SolicitudParams(
      bool Issued,
      string RfcSolicitante,
      string? FilterRfc,
      string TipoSolicitud,              // "CFDI" or "Metadata"
      string? EstadoComprobante,         // for CFDI
      DateTime StartUtc,
      DateTime EndUtc,
      string? Notes = null
  );

  public sealed class SatSolicitudDto
  {
    public int Id { get; set; }
    public Guid? Folio { get; set; }
    public string RfcSolicitante { get; set; } = "";
    public bool Issued { get; set; }
    public string TipoSolicitud { get; set; } = "CFDI"; // or "Metadata"
    public string? EstadoComprobante { get; set; }
    public string? RfcEmisor { get; set; }
    public string? RfcReceptor { get; set; }
    public DateTime FechaInicialUtc { get; set; }
    public DateTime FechaFinalUtc { get; set; }
    public int? EstadoSolicitud { get; set; }
    public string? CodigoEstadoSolicitud { get; set; }
    public string? CodEstatus { get; set; }
    public string? Mensaje { get; set; }
    public int? NumeroCfdis { get; set; }
    public int PackageCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastCheckedAtUtc { get; set; }
    public DateTime? TerminatedAtUtc { get; set; }
    public string RequestKey { get; set; } = "";
  }

  public sealed class SatPaqueteDto
  {
    public int Id { get; set; }
    public int SolicitudId { get; set; }
    public string PackageId { get; set; } = "";
    public DateTime? DownloadedAtUtc { get; set; }
    public long? ZipSizeBytes { get; set; }
    public bool Processed { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int? XmlCount { get; set; }      // For Metadata we reuse as "files processed"
    public int? SuccessCount { get; set; }
    public int? FailureCount { get; set; }
    public string? ErrorMessage { get; set; }
  }

  public sealed class SatVerifySnapshot
  {
    public EstadoSolicitud Estado { get; init; }
    public string? CodigoEstadoSolicitud { get; init; }
    public string? CodEstatus { get; init; }
    public string? Mensaje { get; init; }
    public int NumeroCfdis { get; init; }
    public IEnumerable<string> PackageIds { get; init; } = Array.Empty<string>();
    public bool IsTerminated { get; init; }
  }

  public sealed class SatPackageProcessInfo
  {
    public int XmlCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public long ZipSizeBytes { get; init; }
    public string? ErrorMessage { get; init; }
  }

  // Detailed info for each processed XML (metadata + pipeline result)
  public sealed class XmlProcessedItem
  {
    public string PackageId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string? Uuid { get; init; }
    public string? RfcEmisor { get; init; }
    public string? RfcReceptor { get; init; }
    public DateTime? FechaEmisionUtc { get; init; }
    public decimal? SubTotal { get; init; }
    public decimal? Total { get; init; }
    public string? TipoDeComprobante { get; init; }  // I, E, P, N, T
    public bool Success { get; init; }
    public string? Error { get; init; }
  }

  // Metadata processed file (TXT/CSV) outcome
  public sealed class MetadataProcessedItem
  {
    public string PackageId { get; init; } = "";
    public string FileName { get; init; } = "";
    public int ByteCount { get; init; }
    public int LineCount { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
  }

  // Bucket for common errors (message + count) in CFDI processing
  public sealed class ErrorBucket
  {
    public string Message { get; init; } = "";
    public int Count { get; init; }
  }

  // Final summary returned to the UI
  public sealed class ProcessSummary
  {
    // CFDI totals
    public int Packages { get; set; }
    public int Xmls { get; set; }
    public int Ok { get; set; }
    public int Fail { get; set; }
    public List<XmlProcessedItem> Details { get; } = new();
    public Dictionary<string, int> ByEmisor { get; } = new();
    public Dictionary<string, int> ByReceptor { get; } = new();
    public List<ErrorBucket> Errors { get; } = new();
    public decimal? TotalImporte { get; set; }

    // Metadata totals
    public int MetaFiles { get; set; }
    public int MetaOk { get; set; }
    public int MetaFail { get; set; }
    public List<MetadataProcessedItem> MetaDetails { get; } = new();
  }

  public sealed class VerifyResultDto
  {
    public EstadoSolicitud Estado { get; init; }
    public string? CodigoEstadoSolicitud { get; init; }
    public string? CodEstatus { get; init; }
    public string? Mensaje { get; init; }
    public int NumeroCfdis { get; init; }
    public IReadOnlyList<string> PackageIds { get; init; } = Array.Empty<string>();
    public string HumanStatus { get; init; } = "";
  }
}
