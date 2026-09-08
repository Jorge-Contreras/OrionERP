using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OrionERP.Application.Common;
using OrionERP.Application.Features.Ajustes;
using OrionERP.Application.Features.Cfdi.Facturama;
using OrionERP.Application.Features.Contabilidad.Transacciones;
using OrionERP.Application.Features.Rfcs.Contracts;
using OrionERP.Application.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Reservaciones;
using OrionERP.Infrastructure.Features.Cfdi;
using OrionERP.Infrastructure.Features.Cfdi.Facturama;

namespace OrionERP.Infrastructure.Features.Contabilidad.Transacciones.Services;

public sealed class TransaccionService : ITransaccionService
{
  private readonly IConfiguration _cfg;
  private readonly string _cs;
  private readonly IFacturamaApiClient _facturamaApiClient;
  private readonly ISatRfcProfileRepository _satRfcProfileRepository;
  private readonly ICfdiStampingService _cfdiStampingService;
  private readonly ILogger<TransaccionService> _logger;
  private readonly ICurrentUserAccessor? _currentUserAccessor;
  private readonly IHospitalityScopeAccessor? _hospitalityScopeAccessor;
  private readonly ICurrentCompanyContext? _companyContext;
  private AccountingConnectionFactory? _accountingConnections;

  public TransaccionService(
      IConfiguration cfg,
      IFacturamaApiClient facturamaApiClient,
      ISatRfcProfileRepository satRfcProfileRepository,
      ICfdiStampingService cfdiStampingService,
      ILogger<TransaccionService> logger,
      ICurrentUserAccessor? currentUserAccessor = null,
      IHospitalityScopeAccessor? hospitalityScopeAccessor = null,
      ICurrentCompanyContext? companyContext = null)
  {
    _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
    _cs = _cfg.GetConnectionString("OrionDb")
         ?? throw new InvalidOperationException("Missing connection string: OrionDb");
    _facturamaApiClient = facturamaApiClient ?? throw new ArgumentNullException(nameof(facturamaApiClient));
    _satRfcProfileRepository = satRfcProfileRepository ?? throw new ArgumentNullException(nameof(satRfcProfileRepository));
    _cfdiStampingService = cfdiStampingService ?? throw new ArgumentNullException(nameof(cfdiStampingService));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _currentUserAccessor = currentUserAccessor;
    _hospitalityScopeAccessor = hospitalityScopeAccessor;
    _companyContext = companyContext;
  }

  public async Task<TransaccionHeaderDto?> GetHeaderAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"SELECT TOP (1)
    t.ID                AS Id,
    t.Concepto          AS Concepto,
    t.Fecha             AS Fecha,
    CAST(t.Monto AS decimal(18,4)) AS Monto,
    t.Cuenta            AS Cuenta,
    t.RFC               AS Rfc,
    t.Facturado         AS Facturado,
    t.Referencia        AS Referencia,
    t.Memo              AS Memo,
    t.ProyectoID        AS ProyectoId,
    t.CompraID          AS CompraId,
    t.ServicioID        AS ServicioId,
    t.NominaID          AS NominaId,
    t.Tipo_Poliza       AS TipoPoliza,
    t.Forma_Pago        AS FormaPago,
    tc.Comprobante_ID   AS ComprobanteId,
    CAST(tc.Monto AS decimal(18,4)) AS ComprobanteMonto
FROM dbo.Transacciones t
LEFT JOIN dbo.Transaccion_Comprobante tc
  ON tc.Transaccion_ID = t.ID
WHERE t.ID = @TransaccionId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    return await conn.QueryFirstOrDefaultAsync<TransaccionHeaderDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
  }

  public async Task<IReadOnlyList<TransaccionListItem>> GetCandidatesAsync(
      DateTime fechaXml,
      decimal montoAbs,
      string rfc,
      int daysBack = 60,
      int top = 200,
      CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    const string sql = @"SELECT TOP (@Top)
    t.ID                                    AS Id,
    t.Concepto                              AS Concepto,
    t.Fecha                                 AS Fecha,
    ABS(CONVERT(decimal(18,4), t.Monto))    AS Monto1,
    t.Cuenta                                AS Cuenta,
    COUNT(ta.ID)                            AS Adjuntos,
    c.Comprobante_Id                        AS ComprobanteId
FROM dbo.Transacciones t
LEFT JOIN dbo.TRANSACTION_ATTACHMENT ta
       ON ta.TranID = t.ID
LEFT JOIN dbo.Transaccion_Comprobante tc
       ON tc.Transaccion_ID = t.ID
LEFT JOIN cfdi.Comprobante c
       ON c.Comprobante_Id = tc.Comprobante_ID
WHERE t.Fecha > DATEADD(DAY, -@DaysBack, @FechaXml)
  AND ABS(CONVERT(decimal(18,4), t.Monto)) = @MontoAbs
  AND t.RFC = @Rfc
GROUP BY t.ID, t.Concepto, t.Fecha, t.Monto, t.Cuenta, c.Comprobante_Id
ORDER BY t.Fecha, t.OrdenBalance, t.ID;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionListItem>(
        new CommandDefinition(
            sql,
            new
            {
              FechaXml = fechaXml,
              DaysBack = daysBack,
              MontoAbs = montoAbs,
              Top = top,
              Rfc = rfc
            },
            commandType: CommandType.Text,
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionCfdiCandidateDto>> GetCfdiCandidatesAsync(
      TransaccionCfdiSearchRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    var scopedRfc = RequireCompanyRfc(request.Rfc);

    if (request is null)
      throw new ArgumentNullException(nameof(request));

    var parameters = new DynamicParameters();
    parameters.Add("@Monto", request.Monto);
    parameters.Add("@Concepto", request.Concepto);
    parameters.Add("@Rfc", scopedRfc);
    parameters.Add("@Comprobante_ID", request.ComprobanteId);
    parameters.Add("@Renglones", request.Renglones);
    parameters.Add("@Tipo", request.Tipo);
    parameters.Add("@Comprobantes_In", string.IsNullOrWhiteSpace(request.ComprobantesCsv) ? null : request.ComprobantesCsv);

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<CfdiCandidateRow>(
        new CommandDefinition(
            "cfdi.CFDIs_Candidatos_Para_Poliza",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

    return rows
        .Select(row => new TransaccionCfdiCandidateDto
        {
          ComprobanteId = row.Comprobante_Id,
          Fecha = row.Fecha,
          Tipo = row.Tipo,
          Serie = row.Serie,
          Folio = row.Folio,
          EmisorRfc = row.Emisor_Rfc,
          ReceptorRfc = row.Receptor_Rfc,
          Uuid = row.UUID,
          FormaPago = row.FormaPago,
          Total = row.Total,
          Polizas = row.Polizas,
          Asignado = row.Asignado,
          MetodoPago = row.MetodoPago,
          UsoCfdi = row.UsoCFDI,
          Conceptos = row.Conceptos,
          XmlAttachmentId = row.XML_Attachment_ID
        })
        .ToList();
  }

  public async Task<IReadOnlyList<long>> GetLinkedCfdiIdsAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"SELECT CAST(TC.Comprobante_ID AS bigint) AS Id
FROM dbo.Transaccion_Comprobante AS TC
WHERE TC.Transaccion_ID = @Id
UNION
SELECT CAST(TD.DoctoRelacionado_Id AS bigint) AS Id
FROM dbo.Transaccion_DoctoRelacionado AS TD
WHERE TD.Transaccion_ID = @Id;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<long>(
        new CommandDefinition(sql, new { Id = transaccionId }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<TransaccionCfdiLinkedDataDto> GetLinkedCfdiSummaryAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    using var conn = await OpenAccountingConnectionAsync(ct);
    using var multi = await conn.QueryMultipleAsync(
        new CommandDefinition(
            "cfdi.Transaccion_CFDI_Vinculados_Resumen",
            new { Transaccion_ID = transaccionId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

    var comprobantes = (await multi.ReadAsync<TransaccionCfdiLinkedSummaryDto>()).AsList();
    var comprobantePolizas = (await multi.ReadAsync<TransaccionCfdiLinkedPolizaDto>()).AsList();
    var complementos = (await multi.ReadAsync<TransaccionPago20LinkedSummaryDto>()).AsList();
    var documentos = (await multi.ReadAsync<TransaccionPago20DoctoRelacionadoDto>()).AsList();
    var complementoPolizas = (await multi.ReadAsync<TransaccionCfdiLinkedPolizaDto>()).AsList();
    var legacyComplementos = (await multi.ReadAsync<TransaccionPago20LegacyLinkDto>()).AsList();

    var data = new TransaccionCfdiLinkedDataDto();

    foreach (var comprobante in comprobantes)
    {
      comprobante.Polizas.AddRange(comprobantePolizas.Where(item => item.ComprobanteId == comprobante.ComprobanteId));
      data.Comprobantes.Add(comprobante);
    }

    foreach (var complemento in complementos)
    {
      var linkedDocuments = documentos.Where(item => item.ComprobanteId == complemento.ComprobanteId).ToList();
      foreach (var document in linkedDocuments)
      {
        document.Polizas.AddRange(complementoPolizas.Where(item => item.DoctoRelacionadoId == document.DoctoRelacionadoId));
      }

      complemento.Documentos.AddRange(linkedDocuments);
      complemento.Polizas.AddRange(complementoPolizas.Where(item => item.ComprobanteId == complemento.ComprobanteId));
      data.ComplementosPago.Add(complemento);
    }

    data.LegacyComplementosPago.AddRange(legacyComplementos);

    return data;
  }

  public async Task<TransaccionCfdiLinkingWorkspaceDto> GetTransaccionCfdiLinkingWorkspaceAsync(
      int transaccionId,
      TransaccionCfdiSearchRequest request,
      CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    if (request is null)
      throw new ArgumentNullException(nameof(request));

    var linked = await GetLinkedCfdiSummaryAsync(transaccionId, ct);

    var parameters = new DynamicParameters();
    parameters.Add("@Transaccion_ID", transaccionId);
    parameters.Add("@Monto", request.Monto);
    parameters.Add("@Concepto", string.IsNullOrWhiteSpace(request.Concepto) ? null : request.Concepto);
    parameters.Add("@Comprobante_ID", request.ComprobanteId);
    parameters.Add("@Tipo", string.IsNullOrWhiteSpace(request.Tipo) ? null : request.Tipo);
    parameters.Add("@Renglones", request.Renglones <= 0 ? 50 : request.Renglones);

    using var conn = await OpenAccountingConnectionAsync(ct);
    var transactionAmount = await conn.ExecuteScalarAsync<decimal>(new CommandDefinition(
        @"SELECT CAST(ABS(ISNULL(Monto, 0)) AS decimal(19,4))
FROM dbo.Transacciones
WHERE ID = @TransaccionId;",
        new { TransaccionId = transaccionId },
        cancellationToken: ct));
    using var multi = await conn.QueryMultipleAsync(
        new CommandDefinition(
            "cfdi.Transaccion_CFDI_Linking_Candidates",
            parameters,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

    var regularCandidates = (await multi.ReadAsync<TransaccionRegularCfdiLinkCandidateDto>()).AsList();
    var pago20Candidates = (await multi.ReadAsync<TransaccionPago20LinkCandidateDto>()).AsList();
    var cfdiIds = linked.Comprobantes
        .Select(item => item.ComprobanteId)
        .Concat(regularCandidates.Select(item => item.ComprobanteId))
        .Distinct()
        .ToArray();

    if (cfdiIds.Length > 0)
    {
      await using var identityConnection = await OpenAccountingConnectionAsync(ct);
      var parties = (await identityConnection.QueryAsync<CfdiPartyRow>(new CommandDefinition(
          @"SELECT
    Comprobante_Id AS ComprobanteId,
    MAX(NULLIF(LTRIM(RTRIM(EMISOR)), '')) AS Emisor,
    MAX(NULLIF(LTRIM(RTRIM(RECEPTOR)), '')) AS Receptor
FROM cfdi.Comprobante_Detalle
WHERE Comprobante_Id IN @CfdiIds
GROUP BY Comprobante_Id;",
          new { CfdiIds = cfdiIds },
          cancellationToken: ct)))
        .ToDictionary(item => item.ComprobanteId);

      foreach (var item in linked.Comprobantes)
      {
        if (parties.TryGetValue(item.ComprobanteId, out var party))
        {
          item.Emisor = party.Emisor;
          item.Receptor = party.Receptor;
        }
      }

      foreach (var item in regularCandidates)
      {
        if (parties.TryGetValue(item.ComprobanteId, out var party))
        {
          item.Emisor = party.Emisor;
          item.Receptor = party.Receptor;
        }
      }
    }

    var data = new TransaccionCfdiLinkingWorkspaceDto();
    data.Linked.Comprobantes.AddRange(linked.Comprobantes);
    data.Linked.ComplementosPago.AddRange(linked.ComplementosPago);
    data.Linked.LegacyComplementosPago.AddRange(linked.LegacyComplementosPago);
    data.RegularCandidates.AddRange(regularCandidates);
    data.Pago20Candidates.AddRange(pago20Candidates);

    var hasRegularLinks = data.Linked.Comprobantes.Count > 0;
    var hasPaymentLinks = data.Linked.ComplementosPago.Count > 0 || data.Linked.LegacyComplementosPago.Count > 0;
    var regularRemaining = Math.Max(0m, transactionAmount - data.Linked.Comprobantes.Sum(item => item.AsignadoCfdi));
    var pago20Remaining = Math.Max(
        0m,
        transactionAmount
          - data.Linked.ComplementosPago.Sum(item => item.MontoAsignado)
          - data.Linked.LegacyComplementosPago.Sum(item => item.MontoAsignado));
    foreach (var candidate in data.RegularCandidates)
    {
      candidate.Pendiente = Math.Max(0m, candidate.Total - candidate.AsignadoCfdi);
      candidate.MontoSugerido = Math.Min(candidate.MontoSugerido, Math.Min(candidate.Pendiente, regularRemaining));

      if (hasPaymentLinks)
      {
        candidate.CanLink = false;
        candidate.BlockReason = "La pÃ³liza ya contiene vÃ­nculos de complementos de pago.";
      }
      else if (candidate.Pendiente <= 0.01m)
      {
        candidate.CanLink = false;
        candidate.BlockReason = "El CFDI ya estÃ¡ totalmente asignado.";
      }
      else if (regularRemaining <= 0.01m)
      {
        candidate.CanLink = false;
        candidate.BlockReason = "La pÃ³liza no tiene monto disponible.";
      }
    }

    foreach (var candidate in data.Pago20Candidates)
    {
      candidate.Pendiente = Math.Max(0m, candidate.ImpPagado - candidate.AsignadoComplemento);
      candidate.MontoSugerido = Math.Min(candidate.MontoSugerido, Math.Min(candidate.Pendiente, pago20Remaining));

      if (hasRegularLinks)
      {
        candidate.CanLink = false;
        candidate.BlockReason = "La pÃ³liza ya contiene vÃ­nculos de CFDI regular.";
      }
      else if (!IsMxn(candidate.MonedaP) || !IsMxn(candidate.MonedaDr))
      {
        candidate.CanLink = false;
        candidate.BlockReason = "La asignaciÃ³n Pago20 solo estÃ¡ habilitada cuando MonedaP y MonedaDR son MXN.";
      }
      else if (candidate.Pendiente <= 0m)
      {
        candidate.CanLink = false;
        candidate.BlockReason = "El documento relacionado ya estÃ¡ totalmente asignado.";
      }
      else if (pago20Remaining <= 0.01m)
      {
        candidate.CanLink = false;
        candidate.BlockReason = "La pÃ³liza no tiene monto disponible.";
      }
    }

    return data;
  }

  public async Task<CfdiPolizaLinkingWorkspaceDto> GetCfdiPolizaLinkingWorkspaceAsync(
      int comprobanteId,
      string? rfc,
      TransaccionFilter filter,
      CancellationToken ct = default)
  {
    await EnsureComprobanteScopeAsync(comprobanteId, ct);
    rfc = RequireCompanyRfc(rfc);

    filter ??= new TransaccionFilter();

    using var conn = await OpenAccountingConnectionAsync(ct);
    using var multi = await conn.QueryMultipleAsync(
        new CommandDefinition(
            "cfdi.CFDI_Poliza_Linking_Workspace",
            BuildLinkingWorkspaceParameters(comprobanteId, null, rfc, filter),
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

    var data = new CfdiPolizaLinkingWorkspaceDto
    {
      Summary = await multi.ReadFirstOrDefaultAsync<CfdiPolizaLinkingSummaryDto>()
    };

    data.Polizas.AddRange((await multi.ReadAsync<CfdiPolizaLinkedPolizaDto>()).AsList());
    data.Candidates.AddRange((await multi.ReadAsync<CfdiPolizaCandidateDto>()).AsList());

    return data;
  }

  public async Task<Pago20PolizaLinkingWorkspaceDto> GetPago20PolizaLinkingWorkspaceAsync(
      int doctoRelacionadoId,
      string? rfc,
      TransaccionFilter filter,
      CancellationToken ct = default)
  {
    await EnsureDoctoScopeAsync(doctoRelacionadoId, ct);
    rfc = RequireCompanyRfc(rfc);

    filter ??= new TransaccionFilter();

    using var conn = await OpenAccountingConnectionAsync(ct);
    using var multi = await conn.QueryMultipleAsync(
        new CommandDefinition(
            "cfdi.Pago20_Poliza_Linking_Workspace",
            BuildLinkingWorkspaceParameters(null, doctoRelacionadoId, rfc, filter),
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

    var data = new Pago20PolizaLinkingWorkspaceDto
    {
      Summary = await multi.ReadFirstOrDefaultAsync<Pago20PolizaLinkingSummaryDto>()
    };

    data.Documentos.AddRange((await multi.ReadAsync<Pago20PolizaDoctoRelacionadoDto>()).AsList());
    data.Polizas.AddRange((await multi.ReadAsync<CfdiPolizaLinkedPolizaDto>()).AsList());
    data.Candidates.AddRange((await multi.ReadAsync<CfdiPolizaCandidateDto>()).AsList());
    multi.Dispose();

    if (data.Polizas.Count > 0)
    {
      var assignedByTransaction = (await conn.QueryAsync<(int TransaccionId, decimal Monto)>(new CommandDefinition(
          @"SELECT Transaccion_ID AS TransaccionId,
    CAST(ISNULL(SUM(Monto), 0) AS decimal(19,4)) AS Monto
FROM dbo.Transaccion_DoctoRelacionado
WHERE Transaccion_ID IN @Ids
GROUP BY Transaccion_ID;",
          new { Ids = data.Polizas.Select(item => item.TransaccionId).Distinct().ToArray() },
          cancellationToken: ct)))
        .ToDictionary(item => item.TransaccionId, item => item.Monto);

      foreach (var poliza in data.Polizas)
        poliza.TransaccionAsignadoPago20 = assignedByTransaction.GetValueOrDefault(poliza.TransaccionId);
    }

    if (data.Summary is not null)
    {
      data.Summary.Pendiente = Math.Max(0m, data.Summary.ImpPagado - data.Summary.AsignadoComplemento);
      var currencyAllowed = IsMxn(data.Summary.MonedaP) && IsMxn(data.Summary.MonedaDr);
      var blockedTransactionIds = await GetTransactionsWithDirectCfdiLinksAsync(conn, data.Candidates.Select(item => item.Id), ct);
      foreach (var candidate in data.Candidates)
      {
        if (!currencyAllowed)
        {
          candidate.CanLink = false;
          candidate.BlockReason = "La asignaciÃ³n Pago20 solo estÃ¡ habilitada cuando MonedaP y MonedaDR son MXN.";
        }
        else if (data.Summary.Pendiente <= 0m)
        {
          candidate.CanLink = false;
          candidate.BlockReason = "El documento relacionado ya estÃ¡ totalmente asignado.";
        }
        else if (candidate.Disponible <= 0m)
        {
          candidate.CanLink = false;
          candidate.BlockReason = "La pÃ³liza no tiene monto disponible.";
        }
        else if (blockedTransactionIds.Contains(candidate.Id))
        {
          candidate.CanLink = false;
          candidate.BlockReason = "La pÃ³liza ya contiene un vÃ­nculo de CFDI regular o Pago20 legado.";
        }
      }
    }

    return data;
  }

  public Task<TransaccionCommandResult> LinkRegularCfdiAsync(
      TransaccionRegularCfdiLinkRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    return WriteRegularCfdiLinkAsync(
        request.TransaccionId,
        request.ComprobanteId,
        request.Monto,
        updateExisting: false,
        relinkPlaceholder: true,
        reassignAttachment: true,
        ct);
  }

  public Task<TransaccionCommandResult> LinkPago20DoctoRelacionadoAsync(
      TransaccionPago20LinkRequest request,
      CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    return WritePago20AllocationAsync(
        request.TransaccionId,
        request.DoctoRelacionadoId,
        request.Monto,
        updateExisting: false,
        ct);
  }

  public async Task<Pago20AccountingBasisResult> GetPago20AccountingBasisAsync(
      int transaccionId,
      CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"
SELECT TOP (1)
    t.ID AS TransaccionId,
    CAST(t.Monto AS decimal(19,4)) AS TransaccionMonto,
    LTRIM(RTRIM(t.RFC)) AS Rfc,
    (
        SELECT COUNT(*)
        FROM dbo.Transaccion_Comprobante AS tc
        JOIN cfdi.Comprobante AS legacyCfdi
          ON legacyCfdi.Comprobante_Id = tc.Comprobante_ID
        WHERE tc.Transaccion_ID = t.ID
          AND legacyCfdi.TipoDeComprobante = 'P'
    ) AS LegacyPaymentLinks
FROM dbo.Transacciones AS t
WHERE t.ID = @TransaccionId;

SELECT
    td.DoctoRelacionado_Id AS DoctoRelacionadoId,
    CAST(td.Monto AS decimal(19,6)) AS MontoAsignado,
    CAST(dr.ImpPagado AS decimal(19,6)) AS ImpPagado,
    dr.MonedaDR AS MonedaDr,
    p.MonedaP,
    c.TipoDeComprobante,
    cd.RFC_EMISOR AS EmisorRfc,
    cd.RFC_RECEPTOR AS ReceptorRfc,
    CAST(ISNULL((
        SELECT SUM(CAST(allocation.Monto AS decimal(19,6)))
        FROM dbo.Transaccion_DoctoRelacionado AS allocation
        WHERE allocation.DoctoRelacionado_Id = td.DoctoRelacionado_Id
    ), 0) AS decimal(19,6)) AS DocumentAssigned
FROM dbo.Transaccion_DoctoRelacionado AS td
JOIN cfdi.Pagos20_DoctoRelacionado AS dr
  ON dr.DoctoRelacionado_Id = td.DoctoRelacionado_Id
JOIN cfdi.Pagos20_Pago AS p
  ON p.Pago_Id = dr.Pago_Id
JOIN cfdi.Pagos20 AS p20
  ON p20.Pagos20_Id = p.Pagos20_Id
JOIN cfdi.Comprobante AS c
  ON c.Comprobante_Id = p20.Comprobante_Id
JOIN cfdi.Comprobante_Detalle AS cd
  ON cd.Comprobante_Id = c.Comprobante_Id
WHERE td.Transaccion_ID = @TransaccionId;

SELECT
    td.DoctoRelacionado_Id AS DoctoRelacionadoId,
    traslado.ImpuestoDR,
    CAST(ISNULL(traslado.ImporteDR, 0) AS decimal(19,6)) AS Importe
FROM dbo.Transaccion_DoctoRelacionado AS td
JOIN cfdi.Pagos20_TrasladoDR AS traslado
  ON traslado.DoctoRelacionado_Id = td.DoctoRelacionado_Id
WHERE td.Transaccion_ID = @TransaccionId;

SELECT
    td.DoctoRelacionado_Id AS DoctoRelacionadoId,
    retencion.ImpuestoDR,
    CAST(retencion.ImporteDR AS decimal(19,6)) AS Importe
FROM dbo.Transaccion_DoctoRelacionado AS td
JOIN cfdi.Pagos20_RetencionDR AS retencion
  ON retencion.DoctoRelacionado_Id = td.DoctoRelacionado_Id
WHERE td.Transaccion_ID = @TransaccionId;";

    await using var conn = await OpenAccountingConnectionAsync(ct);
    using var multi = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    var header = await multi.ReadFirstOrDefaultAsync<Pago20AccountingHeaderRow>();
    var documents = (await multi.ReadAsync<Pago20AccountingDocumentRow>()).AsList();
    var transfers = (await multi.ReadAsync<Pago20AccountingTaxRow>()).AsList();
    var retentions = (await multi.ReadAsync<Pago20AccountingTaxRow>()).AsList();

    if (header is null)
      return Pago20AccountingBasisResult.Fail("No se encontrÃ³ la pÃ³liza.");
    if (header.LegacyPaymentLinks > 0)
      return Pago20AccountingBasisResult.Fail("La pÃ³liza contiene vÃ­nculos Pago20 legados. Migra o desliga esos vÃ­nculos antes de generar movimientos.");
    if (documents.Count == 0)
      return Pago20AccountingBasisResult.Fail("La pÃ³liza no tiene documentos Pago20 ligados.");
    if (documents.Any(item => !string.Equals(item.TipoDeComprobante, "P", StringComparison.OrdinalIgnoreCase)))
      return Pago20AccountingBasisResult.Fail("Uno de los documentos ligados no pertenece a un CFDI de tipo P.");
    if (documents.Any(item => !IsMxn(item.MonedaP) || !IsMxn(item.MonedaDr)))
      return Pago20AccountingBasisResult.Fail("La generaciÃ³n contable Pago20 solo admite MonedaP y MonedaDR en MXN.");
    if (documents.Any(item => item.MontoAsignado <= 0m || item.ImpPagado <= 0m))
      return Pago20AccountingBasisResult.Fail("Todos los vÃ­nculos Pago20 deben tener un monto e ImpPagado mayores que cero.");
    if (documents.Any(item => item.DocumentAssigned - item.ImpPagado > 0.01m))
      return Pago20AccountingBasisResult.Fail("Existe un documento Pago20 asignado por encima de su ImpPagado.");

    var directions = documents
        .Select(item => ResolvePago20Direction(header.Rfc, item.EmisorRfc, item.ReceptorRfc))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (directions.Length != 1 || string.Equals(directions[0], "Otro", StringComparison.OrdinalIgnoreCase))
      return Pago20AccountingBasisResult.Fail("Los complementos ligados no comparten una direcciÃ³n vÃ¡lida para el RFC de la pÃ³liza.");

    var unsupportedTaxes = transfers.Concat(retentions)
        .Where(item => item.Importe != 0m && item.ImpuestoDR is not ("001" or "002" or "003"))
        .Select(item => item.ImpuestoDR ?? "(vacÃ­o)")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (unsupportedTaxes.Length > 0)
      return Pago20AccountingBasisResult.Fail($"Impuestos Pago20 no soportados: {string.Join(", ", unsupportedTaxes)}.");

    var calculated = Pago20AccountingCalculator.Calculate(
        documents.Select(item => new Pago20AccountingDocumentInput(item.DoctoRelacionadoId, item.MontoAsignado, item.ImpPagado)).ToArray(),
        transfers.Select(item => new Pago20AccountingTaxInput(item.DoctoRelacionadoId, item.ImpuestoDR, item.Importe)).ToArray(),
        retentions.Select(item => new Pago20AccountingTaxInput(item.DoctoRelacionadoId, item.ImpuestoDR, item.Importe)).ToArray());
    if (calculated.Subtotal < 0m)
      return Pago20AccountingBasisResult.Fail("Los impuestos prorrateados producen un subtotal negativo.");

    var direction = directions[0];
    return Pago20AccountingBasisResult.Ok(new Pago20AccountingBasisDto
    {
      TransaccionId = transaccionId,
      Contexto = string.Equals(direction, "Recibido", StringComparison.OrdinalIgnoreCase)
          ? PlantillaContableContextos.Pago20Recibido
          : PlantillaContableContextos.Pago20Emitido,
      Direccion = direction,
      TransaccionMonto = header.TransaccionMonto,
      TotalAsignado = calculated.TotalAsignado,
      Subtotal = calculated.Subtotal,
      TrasladoIsr = calculated.TrasladoIsr,
      TrasladoIva = calculated.TrasladoIva,
      TrasladoIeps = calculated.TrasladoIeps,
      RetencionIsr = calculated.RetencionIsr,
      RetencionIva = calculated.RetencionIva,
      RetencionIeps = calculated.RetencionIeps
    });
  }

  public async Task<IReadOnlyList<TransaccionMovimientoDto>> GetMovimientosAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

      const string sql = @"SELECT
    rc.ID                 AS Id,
    rc.TransaccionID     AS TransaccionId,
    rc.Nivel1,
    rc.Nivel2,
    rc.Nivel3,
    nivel1.Descripcion   AS Nivel1Descripcion,
    nivel2.Descripcion   AS Nivel2Descripcion,
    COALESCE(cuenta.Descripcion, rc.Nombre_Cuenta) AS Nivel3Descripcion,
    rc.Nombre_Cuenta      AS NombreCuenta,
    rc.Concepto           AS Concepto,
    CAST(ISNULL(rc.Debe, 0) AS decimal(18,4))  AS Debe,
    CAST(ISNULL(rc.Haber, 0) AS decimal(18,4)) AS Haber
FROM dbo.Registro_Contable rc
LEFT JOIN dbo.Transacciones t
  ON t.ID = rc.TransaccionID
LEFT JOIN dbo.CuentasContables nivel1
  ON nivel1.RFC = t.RFC
 AND nivel1.Nivel1 = rc.Nivel1
 AND nivel1.Nivel2 = '00'
 AND nivel1.Nivel3 = '00'
LEFT JOIN dbo.CuentasContables nivel2
  ON nivel2.RFC = t.RFC
 AND nivel2.Nivel1 = rc.Nivel1
 AND nivel2.Nivel2 = rc.Nivel2
 AND nivel2.Nivel3 = '00'
LEFT JOIN dbo.CuentasContables cuenta
  ON cuenta.RFC = t.RFC
 AND cuenta.Nivel1 = rc.Nivel1
 AND cuenta.Nivel2 = rc.Nivel2
 AND cuenta.Nivel3 = rc.Nivel3
WHERE rc.TransaccionID = @TransaccionId
ORDER BY rc.ID;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionMovimientoDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetActividadesAsync(string rfc, CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    const string sql = @"SELECT
    a.ID          AS Id,
    a.Descripcion AS Description
FROM dbo.Actividad a
WHERE a.RFC = @Rfc
ORDER BY a.Descripcion ASC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> SearchActividadesAsync(
      string rfc,
      string? search,
      int top = 25,
      CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    if (string.IsNullOrWhiteSpace(rfc) || string.IsNullOrWhiteSpace(search))
    {
      return Array.Empty<LookupInt32Dto>();
    }

    var normalizedSearch = search.Trim();
    var searchLike = $"%{normalizedSearch}%";
    var actividadId = int.TryParse(normalizedSearch, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedActividadId)
        ? parsedActividadId
        : (int?)null;

    const string sql = @"SELECT TOP (@Top)
    a.ID          AS Id,
    a.Descripcion AS Description
FROM dbo.Actividad a
WHERE a.RFC = @Rfc
  AND (
    CONVERT(varchar(20), a.ID) LIKE @SearchLike
    OR a.Descripcion LIKE @SearchLike
  )
ORDER BY
  CASE WHEN @ActividadId IS NOT NULL AND a.ID = @ActividadId THEN 0 ELSE 1 END,
  a.Descripcion ASC,
  a.ID ASC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(
            sql,
            new
            {
              Rfc = rfc,
              SearchLike = searchLike,
              ActividadId = actividadId,
              Top = top <= 0 ? 25 : top
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<LookupInt32Dto?> GetActividadByIdAsync(string rfc, int actividadId, CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    if (string.IsNullOrWhiteSpace(rfc) || actividadId <= 0)
    {
      return null;
    }

    const string sql = @"SELECT TOP (1)
    a.ID          AS Id,
    a.Descripcion AS Description
FROM dbo.Actividad a
WHERE a.RFC = @Rfc
  AND a.ID = @ActividadId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    return await conn.QueryFirstOrDefaultAsync<LookupInt32Dto>(
        new CommandDefinition(
            sql,
            new
            {
              Rfc = rfc,
              ActividadId = actividadId
            },
            cancellationToken: ct));
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetComprasAsync(string rfc, CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    const string sql = @"SELECT
    c.ID          AS Id,
    c.Descripcion AS Description
FROM dbo.Compra c
WHERE c.RFC = @Rfc
ORDER BY c.Descripcion ASC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetServiciosAsync(string rfc, CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    const string sql = @"SELECT
    s.ID          AS Id,
    s.Descripcion AS Description
FROM dbo.Servicios s
WHERE s.RFC = @Rfc
ORDER BY s.Descripcion ASC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<LookupInt32Dto>> GetNominasAsync(string rfc, CancellationToken ct = default)
  {
    rfc = RequireCompanyRfc(rfc);

    const string sql = @"SELECT
    ch.ID          AS Id,
    ch.NombreCorto AS Description
FROM dbo.Capital_Humano ch
WHERE ch.RFC = @Rfc 
      AND ch.Status='ACTIVO'
ORDER BY ch.NombreCorto ASC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<LookupInt32Dto>(
        new CommandDefinition(sql, new { Rfc = rfc }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<FormaPagoLookupDto>> GetFormasPagoAsync(CancellationToken ct = default)
  {
    const string sql = @"SELECT
    fp.Clave        AS Clave,
    fp.Descripcion  AS Descripcion
FROM dbo.Formas_Pago fp
ORDER BY fp.Clave ASC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<FormaPagoLookupDto>(
        new CommandDefinition(sql, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<MovimientoTotalsDto> GetMovimientoTotalsAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    using var conn = await OpenAccountingConnectionAsync(ct);
    return await LoadTotalsAsync(conn, transaction: null, transaccionId, ct);
  }

  public async Task<IReadOnlyList<TransaccionAttachmentDto>> GetAttachmentsAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"SELECT
    ta.ID                    AS Id,
    ta.TranID                AS TransaccionId,
    ta.AttachmentName        AS AttachmentName,
    ta.AttachmentExtension   AS AttachmentExtension,
    ta.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ta.Attachment) AS bigint) AS Length
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.TranID = @TransaccionId
ORDER BY ta.ID DESC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionAttachmentDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<TransaccionAttachmentDto> AddAttachmentAsync(TransaccionAttachmentCreateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    await EnsureTransactionScopeAsync(request.TransaccionId, ct);

    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.Content is null || request.Content.Length == 0)
      throw new ArgumentException("El archivo adjunto no contiene datos.", nameof(request));

    if (request.Content.Length > TransaccionAttachmentCreateRequest.MaxFileSizeBytes)
      throw new InvalidOperationException("El archivo adjunto excede el tamaÃ±o mÃ¡ximo permitido (5 MB).");

    const string insertSql = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    var newId = await ExecuteInsertAsync(
      insertSql,
      new
      {
        TranID = request.TransaccionId,
        Attachment = request.Content,
        AttachmentName = request.FileName,
        AttachmentExtension = string.IsNullOrWhiteSpace(request.Extension) ? null : request.Extension,
        AttachmentDescription = string.IsNullOrWhiteSpace(request.Description)
          ? "Archivo adjunto (carga manual)"
          : request.Description
      },
      ct);

    const string selectSql = @"SELECT
    ta.ID                    AS Id,
    ta.TranID                AS TransaccionId,
    ta.AttachmentName        AS AttachmentName,
    ta.AttachmentExtension   AS AttachmentExtension,
    ta.AttachmentDescription AS AttachmentDescription,
    CAST(DATALENGTH(ta.Attachment) AS bigint) AS Length
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.ID = @AttachmentId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var dto = await conn.QueryFirstOrDefaultAsync<TransaccionAttachmentDto>(
      new CommandDefinition(selectSql, new { AttachmentId = newId }, cancellationToken: ct));

    if (dto is null)
      throw new InvalidOperationException("No se pudo recuperar el adjunto creado.");

    return dto;
  }

  public async Task<TransaccionAttachmentContent?> GetAttachmentContentAsync(int attachmentId, CancellationToken ct = default)
  {
    await EnsureAttachmentScopeAsync(attachmentId, ct);

    const string sql = @"SELECT TOP (1)
    ta.AttachmentName      AS AttachmentName,
    ta.AttachmentExtension AS AttachmentExtension,
    ta.Attachment          AS Attachment
FROM dbo.TRANSACTION_ATTACHMENT ta
WHERE ta.ID = @AttachmentId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var row = await conn.QueryFirstOrDefaultAsync<(string? AttachmentName, string? AttachmentExtension, byte[] Attachment)>(
        new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    if (row.Attachment is null || row.Attachment.Length == 0)
    {
      return null;
    }

    var fileName = row.AttachmentName ?? $"attachment-{attachmentId}";
    if (!string.IsNullOrWhiteSpace(row.AttachmentExtension) &&
        !fileName.EndsWith($".{row.AttachmentExtension}", StringComparison.OrdinalIgnoreCase))
    {
      fileName = $"{fileName}.{row.AttachmentExtension}";
    }

    return new TransaccionAttachmentContent
    {
      AttachmentId = attachmentId,
      FileName = fileName,
      ContentType = ResolveContentType(row.AttachmentExtension),
      Bytes = row.Attachment
    };
  }

  public async Task DeleteAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    await EnsureAttachmentScopeAsync(attachmentId, ct);

    const string sql = @"DELETE FROM dbo.TRANSACTION_ATTACHMENT WHERE ID = @AttachmentId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    await conn.ExecuteAsync(new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));
  }

  public async Task<int> GetComprobanteIdByXmlAttachmentAsync(int attachmentId, CancellationToken ct = default)
  {
    await EnsureAttachmentScopeAsync(attachmentId, ct);

    const string sql = @"SELECT TOP 1 Comprobante_ID
FROM cfdi.comprobante
WHERE XML_Attachment_ID = @AttachmentId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var comprobanteId = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(sql, new { AttachmentId = attachmentId }, cancellationToken: ct));

    return comprobanteId ?? 0;
  }

  public async Task<bool> IsComprobanteLinkedToTransaccionAsync(int transaccionId, int comprobanteId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    const string sql = @"SELECT TOP 1 1
FROM dbo.Transaccion_Comprobante
WHERE Transaccion_ID = @TransaccionId
  AND Comprobante_ID = @ComprobanteId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var exists = await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId, ComprobanteId = comprobanteId }, cancellationToken: ct));

    return exists.HasValue;
  }

  public async Task SetAttachmentTransaccionAsync(int attachmentId, int? transaccionId, CancellationToken ct = default)
  {
    if (transaccionId.HasValue) await EnsureTransactionScopeAsync(transaccionId.Value, ct);
    await EnsureAttachmentScopeAsync(attachmentId, ct);

    const string sql = @"UPDATE dbo.TRANSACTION_ATTACHMENT
SET TranID = @TransaccionId
WHERE ID = @AttachmentId;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    await conn.ExecuteAsync(
        new CommandDefinition(sql, new { AttachmentId = attachmentId, TransaccionId = transaccionId }, cancellationToken: ct));
  }

  public async Task<IReadOnlyList<TransaccionComprobanteDto>> GetComprobantesAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"SELECT
    CAST(tc.Monto AS decimal(18, 4))                       AS PolizaMonto,
    tc.Comprobante_ID                                      AS ComprobanteId,
    CASE WHEN cd.Incluir_En_Declaracion = 1 THEN N'âœ”' ELSE N'X' END AS D,
    cd.Fecha,
    cd.MESES                                               AS MesGlobal,
    cd.ANIO                                                AS AnioGlobal,
    cd.EMISOR                                              AS Emisor,
    CAST(cd.SubTotal AS decimal(18, 4))                    AS SubTotal,
    CAST(cd.Descuento AS decimal(18, 4))                   AS Descuento,
    CAST(cd.SubTotal_Desc AS decimal(18, 4))               AS SubTotalDesc,
    CAST(cd.Actos_16 AS decimal(18, 4))                    AS Actos16,
    CAST(cd.Actos_0 AS decimal(18, 4))                     AS Actos0,
    CAST(cd.IVA AS decimal(18, 4))                         AS Iva,
    CAST(cd.IEPS AS decimal(18, 4))                        AS Ieps,
    CAST(cd.IVA_RETENIDO AS decimal(18, 4))                AS IvaRetenido,
    CAST(cd.ISR_RETENIDO AS decimal(18, 4))                AS IsrRetenido,
    CAST(cd.IEPS_RETENIDO AS decimal(18, 4))               AS IepsRetenido,
    CAST(cd.Total AS decimal(18, 4))                       AS Total,
    cd.FOLIO_FISCAL                                        AS FolioFiscal,
    cd.FormaPago,
    cd.TipoDeComprobante,
    cd.MetodoPago,
    cd.UsoCFDI                                             AS UsoCfdi,
    cd.FechaCancelacion,
    cd.Estatus,
    tc.Transaccion_ID                                      AS TransaccionId
FROM dbo.Transaccion_Comprobante AS tc
INNER JOIN cfdi.Comprobante_Detalle AS cd ON tc.Comprobante_ID = cd.Comprobante_Id
WHERE tc.Transaccion_ID = @TransaccionId
ORDER BY cd.Fecha DESC;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionComprobanteDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  /// <summary>
  /// Toda conexión contable pasa por aquí: fija el RFC legacy y el CompanyId
  /// técnico de la sesión, y la base comprueba que el par exista. Antes cada método
  /// abría <c>new SqlConnection(_cs)</c> sin contexto alguno.
  /// </summary>
  private Task<SqlConnection> OpenAccountingConnectionAsync(CancellationToken ct)
  {
    _accountingConnections ??= new AccountingConnectionFactory(
        _cfg,
        _companyContext ?? throw new InvalidOperationException("Selecciona una empresa antes de operar pólizas."));
    return _accountingConnections.OpenAsync(ct);
  }

  private string RequireCompanyRfc(string? requestedRfc = null)
  {
    var context = _companyContext
        ?? throw new InvalidOperationException("Selecciona una empresa antes de operar pólizas.");
    var rfc = context.RequireRfc();
    if (!string.IsNullOrWhiteSpace(requestedRfc)) context.EnsureRfc(requestedRfc);
    return rfc;
  }

  private async Task EnsureTransactionScopeAsync(int transaccionId, CancellationToken ct)
  {
    var rfc = RequireCompanyRfc();
    using var conn = await OpenAccountingConnectionAsync(ct);
    var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition(
        "SELECT 1 FROM dbo.Transacciones WHERE ID = @Id AND RFC = @Rfc;",
        new { Id = transaccionId, Rfc = rfc }, cancellationToken: ct));
    if (!exists.HasValue)
      throw new UnauthorizedAccessException("La póliza no pertenece a la empresa seleccionada.");
  }

  private async Task EnsureAttachmentScopeAsync(int attachmentId, CancellationToken ct)
  {
    var rfc = RequireCompanyRfc();
    using var conn = await OpenAccountingConnectionAsync(ct);
    var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"""
SELECT 1 FROM dbo.TRANSACTION_ATTACHMENT a
WHERE a.ID = @Id AND
(
  EXISTS (SELECT 1 FROM dbo.Transacciones t WHERE t.ID = a.TranID AND t.RFC = @Rfc)
  OR (a.TranID IS NULL AND EXISTS (
    SELECT 1 FROM cfdi.Comprobante c
    WHERE c.XML_Attachment_ID = a.ID
      AND {CfdiCompanyScope.AccessPredicateSql("c.Comprobante_Id")}))
);
""", new { Id = attachmentId, Rfc = rfc }, cancellationToken: ct));
    if (!exists.HasValue)
      throw new UnauthorizedAccessException("El adjunto no pertenece a la empresa seleccionada.");
  }

  private async Task EnsureComprobanteScopeAsync(long comprobanteId, CancellationToken ct)
  {
    var rfc = RequireCompanyRfc();
    using var conn = await OpenAccountingConnectionAsync(ct);
    var exists = await conn.ExecuteScalarAsync<int?>(new CommandDefinition($"""
SELECT 1 FROM cfdi.Comprobante c WHERE c.Comprobante_Id = @Id
  AND {CfdiCompanyScope.AccessPredicateSql("c.Comprobante_Id")};
""", new { Id = comprobanteId, Rfc = rfc }, cancellationToken: ct));
    if (!exists.HasValue)
      throw new UnauthorizedAccessException("El CFDI no pertenece a la empresa seleccionada.");
  }

  private async Task EnsureDoctoScopeAsync(int doctoRelacionadoId, CancellationToken ct)
  {
    RequireCompanyRfc();
    using var conn = await OpenAccountingConnectionAsync(ct);
    var id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
SELECT p20.Comprobante_Id FROM cfdi.Pagos20_DoctoRelacionado dr
INNER JOIN cfdi.Pagos20_Pago p ON p.Pago_Id = dr.Pago_Id
INNER JOIN cfdi.Pagos20 p20 ON p20.Pagos20_Id = p.Pagos20_Id
WHERE dr.DoctoRelacionado_Id = @Id;
""", new { Id = doctoRelacionadoId }, cancellationToken: ct));
    if (!id.HasValue) throw new UnauthorizedAccessException("No se encontró el complemento en la empresa seleccionada.");
    await EnsureComprobanteScopeAsync(id.Value, ct);
  }

  private async Task<SqlConnection> OpenHospitalityConnectionAsync(CancellationToken ct)
  {
    var accessor = _hospitalityScopeAccessor
        ?? throw new InvalidOperationException("Selecciona una empresa y sede de hospedaje antes de operar reservaciones.");
    var scope = await accessor.ResolveRequiredAsync(ct);
    var conn = new SqlConnection(_cs);
    try
    {
      await conn.OpenAsync(ct);
      await HospitalityConnectionFactory.InitializeAsync(conn, scope, ct);
      // Los cobros de reservación escriben pólizas: la conexión también declara la
      // identidad contable, que el alcance de Hospedaje ya trae verificada.
      await AccountingConnectionFactory.InitializeAsync(conn, scope.CompanyRfc, scope.CompanyId, ct);
      return conn;
    }
    catch
    {
      await conn.DisposeAsync();
      throw;
    }
  }

  public async Task<IReadOnlyList<TransaccionReservacionLinkDto>> GetReservacionLinksAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"SELECT
    rt.ReservationID                                     AS ReservationId,
    rt.TransaccionID                                     AS TransaccionId,
    CAST(ISNULL(rt.Amount, 0) AS decimal(18, 2))         AS Amount,
    ISNULL(c.Nombre, '(Sin cliente)')                    AS Cliente,
    r.CHECKIN                                            AS CheckIn,
    r.CHECKOUT                                           AS CheckOut,
    r.STATUS                                             AS Status,
    CAST(ISNULL(r.TOTAL_PRICE, 0) AS decimal(18, 2))     AS TotalPrice,
    CAST(ISNULL(pa.PAGADO, 0) AS decimal(18, 2))         AS Pagado,
    CAST(ISNULL(r.TOTAL_PRICE, 0) - ISNULL(pa.PAGADO, 0) AS decimal(18, 2)) AS PorPagar,
    r.NOTES                                              AS Notes
FROM dbo.Reservation_Transacciones rt
INNER JOIN dbo.RESERVATION r
  ON r.ID = rt.ReservationID
LEFT JOIN dbo.Clientes c
  ON c.ID = r.CLIENTE_ID
 AND EXISTS (SELECT 1 FROM orion.HospitalitySiteCustomer cs WHERE cs.ClienteId = c.ID AND cs.CompanyId = r.OrionCompanyId AND cs.SiteId = r.OrionSiteId)
OUTER APPLY
(
  SELECT SUM(rt2.Amount) AS PAGADO
  FROM dbo.Reservation_Transacciones rt2
  INNER JOIN dbo.Transacciones t2 ON t2.ID = rt2.TransaccionID AND t2.RFC = (SELECT Rfc FROM orion.Company WHERE CompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId')))
  WHERE rt2.ReservationID = r.ID
) pa
WHERE rt.TransaccionID = @TransaccionId
  AND EXISTS (SELECT 1 FROM dbo.Transacciones t WHERE t.ID = rt.TransaccionID AND t.RFC = (SELECT Rfc FROM orion.Company WHERE CompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))))
ORDER BY rt.ReservationID DESC;";

    using var conn = await OpenHospitalityConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionReservacionLinkDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, cancellationToken: ct));
    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionReservacionSearchItemDto>> SearchReservacionesAsync(string? search, CancellationToken ct = default)
  {
    var normalizedSearch = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
    var reservationId = int.TryParse(normalizedSearch, out var parsedReservationId)
        ? parsedReservationId
        : (int?)null;

    const string sql = @"WITH ScopedReservations AS
(
  SELECT reservation.ID, reservation.CHECKIN, reservation.CHECKOUT, reservation.STATUS,
         reservation.TOTAL_PRICE, reservation.NOTES, customer.Nombre,
         ISNULL(payment.Pagado, 0) AS PAGADO,
         ISNULL(reservation.TOTAL_PRICE, 0) - ISNULL(payment.Pagado, 0) AS POR_PAGAR
  FROM dbo.RESERVATION reservation
  LEFT JOIN orion.HospitalitySiteCustomer customerScope
    ON customerScope.CompanyId = reservation.OrionCompanyId
   AND customerScope.SiteId = reservation.OrionSiteId
   AND customerScope.ClienteId = reservation.CLIENTE_ID
  LEFT JOIN dbo.Clientes customer ON customer.ID = customerScope.ClienteId
  OUTER APPLY
  (
    SELECT SUM(link.Amount) AS Pagado
    FROM dbo.Reservation_Transacciones link
    INNER JOIN dbo.Transacciones paymentTransaction ON paymentTransaction.ID = link.TransaccionID
    INNER JOIN orion.Company company ON company.CompanyId = reservation.OrionCompanyId
       AND company.Rfc = paymentTransaction.RFC
    WHERE link.ReservationID = reservation.ID
  ) payment
  WHERE reservation.OrionCompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))
    AND reservation.OrionSiteId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'))
)
SELECT TOP (100)
    lr.ID                                                 AS ReservationId,
    ISNULL(lr.Nombre, '(Sin cliente)')                    AS Cliente,
    lr.CHECKIN                                            AS CheckIn,
    lr.CHECKOUT                                           AS CheckOut,
    lr.STATUS                                             AS Status,
    CAST(ISNULL(lr.TOTAL_PRICE, 0) AS decimal(18, 2))     AS TotalPrice,
    CAST(ISNULL(lr.PAGADO, 0) AS decimal(18, 2))          AS Pagado,
    CAST(ISNULL(lr.POR_PAGAR, 0) AS decimal(18, 2))       AS PorPagar,
    lr.NOTES                                              AS Notes
FROM ScopedReservations lr
WHERE (
(
  @ReservationId IS NOT NULL
  AND lr.ID = @ReservationId
)
OR
(
  ABS(CONVERT(decimal(18, 2), ISNULL(lr.POR_PAGAR, 0))) > 2
  AND
  (
    @Search IS NULL
    OR lr.Nombre LIKE @SearchLike
    OR lr.NOTES LIKE @SearchLike
  )
)
)
ORDER BY
  CASE WHEN @ReservationId IS NOT NULL AND lr.ID = @ReservationId THEN 0 ELSE 1 END,
  lr.ID DESC;";

    using var conn = await OpenHospitalityConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionReservacionSearchItemDto>(
        new CommandDefinition(
            sql,
            new
            {
              Search = normalizedSearch,
              SearchLike = normalizedSearch is null ? null : $"%{normalizedSearch}%",
              ReservationId = reservationId
            },
            cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<TransaccionCommandResult> UpsertReservacionLinkAsync(TransaccionReservacionLinkUpsertRequest request, CancellationToken ct = default)
  {
    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.TransaccionId <= 0 || request.ReservationId <= 0)
      return TransaccionCommandResult.Fail("Selecciona una pÃ³liza y una reservaciÃ³n vÃ¡lidas.");

    if (decimal.Abs(request.Amount) < 0.01m)
      return TransaccionCommandResult.Fail("Ingresa un monto distinto de cero.");

    await using var conn = await OpenHospitalityConnectionAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      const string existsReservationSql = @"SELECT TOP (1) 1
FROM dbo.RESERVATION WITH (UPDLOCK, HOLDLOCK)
WHERE ID = @ReservationId
  AND OrionCompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))
  AND OrionSiteId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'));";

      var reservationExists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              existsReservationSql,
              new { request.ReservationId },
              tx,
              cancellationToken: ct));

      if (!reservationExists.HasValue)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La reservaciÃ³n seleccionada no existe.");
      }

      const string existsTransaccionSql = @"SELECT TOP (1) 1
FROM dbo.Transacciones WITH (UPDLOCK, HOLDLOCK)
WHERE ID = @TransaccionId
  AND RFC = (SELECT Rfc FROM orion.Company WHERE CompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId')));";

      var transaccionExists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              existsTransaccionSql,
              new { request.TransaccionId },
              tx,
              cancellationToken: ct));

      if (!transaccionExists.HasValue)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La pÃ³liza seleccionada no existe.");
      }

      const string existsLinkSql = @"SELECT TOP (1) 1
FROM dbo.Reservation_Transacciones
WHERE ReservationID = @ReservationId
  AND TransaccionID = @TransaccionId;";

      var linkExists = await conn.ExecuteScalarAsync<int?>(
          new CommandDefinition(
              existsLinkSql,
              new
              {
                request.ReservationId,
                request.TransaccionId
              },
              tx,
              cancellationToken: ct));

      if (linkExists.HasValue)
      {
        const string updateSql = @"UPDATE dbo.Reservation_Transacciones
SET Amount = @Amount
WHERE ReservationID = @ReservationId
  AND TransaccionID = @TransaccionId;";

        await conn.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new
                {
                  request.Amount,
                  request.ReservationId,
                  request.TransaccionId
                },
                tx,
                cancellationToken: ct));

        await tx.CommitAsync(ct);
        return TransaccionCommandResult.Ok("AsignaciÃ³n de reservaciÃ³n actualizada.");
      }

      const string insertSql = @"INSERT INTO dbo.Reservation_Transacciones
(ReservationID, TransaccionID, Amount, OrionCompanyId, OrionSiteId)
VALUES (@ReservationId, @TransaccionId, @Amount,
  TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId')),
  TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalitySiteId')));";

      await conn.ExecuteAsync(
          new CommandDefinition(
              insertSql,
              new
              {
                request.ReservationId,
                request.TransaccionId,
                request.Amount
              },
              tx,
              cancellationToken: ct));

      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok("ReservaciÃ³n ligada correctamente.");
    }
    catch (Exception ex)
    {
      await tx.RollbackAsync(ct);
      _logger.LogError(
          ex,
          "Error al guardar vÃ­nculo entre transacciÃ³n {TransaccionId} y reservaciÃ³n {ReservationId}",
          request.TransaccionId,
          request.ReservationId);

      return TransaccionCommandResult.Fail("No se pudo guardar la asignaciÃ³n de la reservaciÃ³n.");
    }
  }

  public async Task<TransaccionCommandResult> DeleteReservacionLinkAsync(int transaccionId, int reservationId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"DELETE rt
FROM dbo.Reservation_Transacciones rt
INNER JOIN dbo.Transacciones t ON t.ID = rt.TransaccionID
WHERE rt.TransaccionID = @TransaccionId
  AND rt.ReservationID = @ReservationId
  AND t.RFC = (SELECT Rfc FROM orion.Company WHERE CompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId')))
  AND rt.OrionCompanyId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalityCompanyId'))
  AND rt.OrionSiteId = TRY_CONVERT(bigint, SESSION_CONTEXT(N'OrionERP.HospitalitySiteId'));";

    try
    {
      using var conn = await OpenHospitalityConnectionAsync(ct);
      var affectedRows = await conn.ExecuteAsync(
          new CommandDefinition(
              sql,
              new
              {
                TransaccionId = transaccionId,
                ReservationId = reservationId
              },
              cancellationToken: ct));

      return affectedRows > 0
          ? TransaccionCommandResult.Ok("AsignaciÃ³n eliminada correctamente.")
          : TransaccionCommandResult.Fail("No se encontrÃ³ la asignaciÃ³n a eliminar.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Error al eliminar vÃ­nculo entre transacciÃ³n {TransaccionId} y reservaciÃ³n {ReservationId}",
          transaccionId,
          reservationId);

      return TransaccionCommandResult.Fail("No se pudo eliminar la asignaciÃ³n de la reservaciÃ³n.");
    }
  }

  public async Task<TransaccionCommandResult> LinkCfdiAndRelinkAttachmentAsync(
      int transaccionId,
      int comprobanteId,
      decimal monto,
      CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    return await WriteRegularCfdiLinkAsync(
        transaccionId,
        comprobanteId,
        monto,
        updateExisting: false,
        relinkPlaceholder: true,
        reassignAttachment: true,
        ct);
  }

  public async Task<TransaccionCommandResult> InsertTransaccionComprobanteAsync(int transaccionId, int comprobanteId, decimal monto, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    return await WriteRegularCfdiLinkAsync(
        transaccionId,
        comprobanteId,
        monto,
        updateExisting: false,
        relinkPlaceholder: false,
        reassignAttachment: true,
        ct);
  }

  public async Task<TransaccionCommandResult> UpdateComprobanteMontoAsync(int transaccionId, int comprobanteId, decimal monto, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    return await WriteRegularCfdiLinkAsync(
        transaccionId,
        comprobanteId,
        monto,
        updateExisting: true,
        relinkPlaceholder: false,
        reassignAttachment: false,
        ct);
  }

  public async Task ToggleComprobanteAsync(int transaccionId, int comprobanteId, bool vincular, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    if (vincular)
    {
      await using var lookupConnection = await OpenAccountingConnectionAsync(ct);
      var total = await lookupConnection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
          "SELECT CAST(Total AS decimal(19,4)) FROM cfdi.Comprobante WHERE Comprobante_Id = @ComprobanteId;",
          new { ComprobanteId = comprobanteId },
          cancellationToken: ct));
      if (!total.HasValue)
      {
        throw new InvalidOperationException("Comprobante no encontrado.");
      }

      var result = await WriteRegularCfdiLinkAsync(
          transaccionId,
          comprobanteId,
          total.Value,
          updateExisting: true,
          relinkPlaceholder: false,
          reassignAttachment: true,
          ct);
      if (!result.Success)
      {
        throw new InvalidOperationException(result.Message);
      }

      return;
    }

    var unlinkResult = await UnlinkRegularCfdiAsync(transaccionId, comprobanteId, ct);
    if (!unlinkResult.Success)
    {
      throw new InvalidOperationException(unlinkResult.Message);
    }

    return;
  }

  public Task<TransaccionCommandResult> UnlinkRegularCfdiAsync(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default)
    => UnlinkDirectCfdiAsync(transaccionId, comprobanteId, requirePaymentType: false, ct);

  public Task<TransaccionCommandResult> UnlinkLegacyPago20Async(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default)
    => UnlinkDirectCfdiAsync(transaccionId, comprobanteId, requirePaymentType: true, ct);

  public async Task<TransaccionCommandResult> UnlinkPago20DoctoRelacionadoAsync(
      int transaccionId,
      int doctoRelacionadoId,
      CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureDoctoScopeAsync(doctoRelacionadoId, ct);

    using var conn = await OpenAccountingConnectionAsync(ct);

    try
    {
      const string sql = @"DELETE FROM dbo.Transaccion_DoctoRelacionado
WHERE Transaccion_ID = @TransaccionId
  AND DoctoRelacionado_ID = @DoctoRelacionadoId;";
      var updated = await conn.ExecuteAsync(new CommandDefinition(
          sql,
          new { TransaccionId = transaccionId, DoctoRelacionadoId = doctoRelacionadoId },
          cancellationToken: ct));

      return updated > 0
          ? TransaccionCommandResult.Ok("Documento Pago20 desligado correctamente.")
          : TransaccionCommandResult.Fail("No se encontrÃ³ el vÃ­nculo de este documento Pago20 con la pÃ³liza actual.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to unlink Pago20 document {DoctoRelacionadoId} from Transaccion {TransaccionId}",
          doctoRelacionadoId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudo desligar el documento Pago20. IntÃ©ntalo de nuevo.");
    }
  }

  public async Task<TransaccionGuardarCerrarResult> GuardarYCerrarAsync(TransaccionGuardarCerrarRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    await EnsureTransactionScopeAsync(request.TransaccionId, ct);

    using var conn = await OpenConnectionWithAuditContextAsync(ct);
    using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

    try
    {
      var existingFecha = await conn.ExecuteScalarAsync<DateTime?>(
          new CommandDefinition(
              "SELECT Fecha FROM dbo.Transacciones WHERE ID = @TransaccionId;",
              new { request.TransaccionId },
              tx,
              cancellationToken: ct));

      if (!existingFecha.HasValue)
      {
        await tx!.RollbackAsync(ct);
        return TransaccionGuardarCerrarResult.Fail("TransacciÃ³n no encontrada.");
      }

      var effectiveFecha = PreserveTimeOfDay(request.Fecha, existingFecha.Value);

      const string sqlUpdate = @"UPDATE dbo.Transacciones
SET Concepto = @Concepto,
    Fecha = @Fecha,
    Cuenta = @Cuenta,
    Monto = @Monto,
    Facturado = @Facturado,
    Memo = @Memo,
    ProyectoID = @ProyectoId,
    CompraID = @CompraId,
    ServicioID = @ServicioId,
    NominaID = @NominaId,
    Tipo_Poliza = @TipoPoliza,
    Forma_Pago = @FormaPago
WHERE ID = @TransaccionId;";

      var parameters = new DynamicParameters();
      parameters.Add("@TransaccionId", request.TransaccionId);
      parameters.Add("@Concepto", request.Concepto);
      parameters.Add("@Fecha", effectiveFecha);
      parameters.Add("@Cuenta", request.Cuenta);
      parameters.Add("@Monto", request.Monto);
      parameters.Add("@Facturado", request.Facturado);
      parameters.Add("@Memo", request.Memo, DbType.String, size: -1);
      parameters.Add("@ProyectoId", request.ProyectoId);
      parameters.Add("@CompraId", request.CompraId);
      parameters.Add("@ServicioId", request.ServicioId);
      parameters.Add("@NominaId", request.NominaId);
      parameters.Add("@TipoPoliza", request.TipoPoliza);
      parameters.Add("@FormaPago", request.FormaPago);

      var affected = await conn.ExecuteAsync(
          new CommandDefinition(sqlUpdate, parameters, tx, cancellationToken: ct));

      if (affected == 0)
      {
        await tx!.RollbackAsync(ct);
        return TransaccionGuardarCerrarResult.Fail("TransacciÃ³n no encontrada.");
      }

      var totals = await LoadTotalsAsync(conn, tx, request.TransaccionId, ct);
      await tx!.CommitAsync(ct);
      return TransaccionGuardarCerrarResult.Ok(totals, "TransacciÃ³n guardada correctamente.");
    }
    catch (Exception ex)
    {
      try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
      _logger.LogError(ex, "Error al guardar y cerrar la transacciÃ³n {TransaccionId}", request.TransaccionId);
      return TransaccionGuardarCerrarResult.Fail(
          "No se pudo guardar la transacciÃ³n. Verifica los datos de la pÃ³liza (fecha, cuenta, tipo de pÃ³liza y forma de pago) e intÃ©ntalo de nuevo; si el problema persiste, reporta el incidente al Ã¡rea de sistemas.");
    }
  }

  public async Task<TransaccionCommandResult> ProcessSatXmlAsync(
      int attachmentId,
      int transaccionId,
      CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureAttachmentScopeAsync(attachmentId, ct);

    try
    {
      _logger.LogInformation(
          "Processing SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      using var conn = await OpenAccountingConnectionAsync(ct);
      await ProcessSatXmlV2Async(conn, transaction: null, transaccionId, attachmentId, ct);

      return TransaccionCommandResult.Ok("El XML del SAT se procesÃ³ correctamente para la transacciÃ³n seleccionada.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to process SAT XML attachment {AttachmentId} for transaction {TransactionId}",
          attachmentId,
          transaccionId);

      return TransaccionCommandResult.Fail("No se pudo procesar el XML del SAT. Verifica el adjunto y vuelve a intentar.");
    }
  }

  public async Task<TransaccionCommandResult> TimbrarCfdiPublicoAsync(
      TransaccionTimbrarPublicoRequest request,
      CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    await EnsureTransactionScopeAsync(request.TransaccionId, ct);

    if (request is null)
      throw new ArgumentNullException(nameof(request));

    if (request.TransaccionId <= 0)
      return TransaccionCommandResult.Fail("La pÃ³liza seleccionada no es vÃ¡lida.");

    if (request.Monto <= 0m)
      return TransaccionCommandResult.Fail("El monto para el CFDI pÃºblico debe ser mayor que cero.");

    var mes = NormalizeGlobalMonth(request.GlobalMes);
    if (mes is null)
      return TransaccionCommandResult.Fail("El mes global seleccionado no es vÃ¡lido.");

    if (request.GlobalAnio < 2000 || request.GlobalAnio > 2100)
      return TransaccionCommandResult.Fail("El aÃ±o global seleccionado no es vÃ¡lido.");

    try
    {
      var header = await GetHeaderAsync(request.TransaccionId, ct);
      if (header is null)
      {
        return TransaccionCommandResult.Fail("No se encontrÃ³ la pÃ³liza seleccionada.");
      }

      if (string.IsNullOrWhiteSpace(header.Rfc))
      {
        return TransaccionCommandResult.Fail("La pÃ³liza seleccionada no tiene RFC emisor configurado.");
      }

      var expeditionZipCode = await ResolveIssuerTaxZipCodeAsync(header.Rfc, ct);
      var payload = BuildPublicoPayload(request, mes, expeditionZipCode);

      await _cfdiStampingService.StampIssuedCfdiAsync(
          new CfdiStampRequest
          {
            TransaccionId = request.TransaccionId,
            AttachmentLabel = $"POLIZA {request.TransaccionId}",
            Payload = payload
          },
          ct);

      return TransaccionCommandResult.Ok("La factura al pÃºblico en general se generÃ³, timbrÃ³ y procesÃ³ correctamente.");
    }
    catch (CfdiStampingException ex)
    {
      _logger.LogError(
          ex,
          "Failed to stamp public CFDI for transaction {TransaccionId}",
          request.TransaccionId);

      if (!string.IsNullOrWhiteSpace(ex.FacturamaCfdiId))
      {
        return TransaccionCommandResult.Fail(
            $"El CFDI se timbrÃ³ en Facturama ({ex.FacturamaCfdiId}), pero no se pudo completar el registro local: {ex.InnerException?.Message ?? ex.Message}");
      }

      return TransaccionCommandResult.Fail(
          $"No se pudo generar el CFDI pÃºblico: {ex.InnerException?.Message ?? ex.Message}");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to stamp public CFDI for transaction {TransaccionId}",
          request.TransaccionId);

      return TransaccionCommandResult.Fail($"No se pudo generar el CFDI pÃºblico: {ex.Message}");
    }
  }

  public async Task<TransaccionCommandResult> RegenerarPolizaDesdeComprobanteEnTransaccionAsync(
      int transaccionId,
      long comprobanteId,
      CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    using var conn = await OpenConnectionWithAuditContextAsync(ct);

    try
    {
      await conn.ExecuteAsync(
          new CommandDefinition(
              "[contabilidad].[Regenerar_Poliza_Desde_Comprobante_En_Transaccion]",
              new
              {
                Comprobante_Id = comprobanteId,
                Transaccion_ID = transaccionId
              },
              commandType: CommandType.StoredProcedure,
              cancellationToken: ct));

      return TransaccionCommandResult.Ok("Movimientos regenerados correctamente.");
    }
    catch (Exception ex)
    {
      _logger.LogError(
          ex,
          "Failed to regenerate poliza movements from comprobante {ComprobanteId} for transaccion {TransaccionId}",
          comprobanteId,
          transaccionId);

      return TransaccionCommandResult.Fail($"No se pudieron regenerar los movimientos desde el comprobante: {ex.Message}");
    }
  }

  public async Task DeleteMovimientoAsync(int transaccionId, int movimientoId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

    const string sql = @"DELETE FROM dbo.Registro_Contable
WHERE ID = @MovimientoId
  AND TransaccionID = @TransaccionId;";

    using var conn = await OpenConnectionWithAuditContextAsync(ct);
    await conn.ExecuteAsync(
      new CommandDefinition(sql, new { MovimientoId = movimientoId, TransaccionId = transaccionId }, cancellationToken: ct));
  }

  public async Task<TransaccionCommandResult> DeleteTransaccionAsync(int transaccionId, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);

      using var conn = await OpenConnectionWithAuditContextAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
          const string checkSql = @"
              IF EXISTS (SELECT 1 FROM dbo.Actividad_Transacciones WHERE TransaccionID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Registro_Contable WHERE TransaccionID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Transaccion_Comprobante WHERE Transaccion_ID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Transaccion_DoctoRelacionado WHERE Transaccion_ID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.Reservation_Transacciones WHERE TransaccionID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM dbo.TRANSACTION_ATTACHMENT WHERE TranID = @TransaccionId) OR
                 EXISTS (SELECT 1 FROM bancos.Movimiento_Transaccion WHERE Transaccion_ID = @TransaccionId)
              BEGIN
                  SELECT 1;
              END
              ELSE
              BEGIN
                  SELECT 0;
              END";

          var hasRelatedRecords = await conn.ExecuteScalarAsync<bool>(
              new CommandDefinition(checkSql, new { TransaccionId = transaccionId }, tx, cancellationToken: ct));

          if (hasRelatedRecords)
          {
              await tx!.RollbackAsync(ct);
              return TransaccionCommandResult.Fail("No se puede eliminar la transacciÃ³n porque tiene registros relacionados (movimientos, adjuntos, comprobantes, etc.).");
          }

          const string deleteSql = @"DELETE FROM dbo.Transacciones WHERE ID = @TransaccionId;";
          var affectedRows = await conn.ExecuteAsync(
              new CommandDefinition(deleteSql, new { TransaccionId = transaccionId }, tx, cancellationToken: ct));

          if (affectedRows == 0)
          {
              await tx!.RollbackAsync(ct);
              return TransaccionCommandResult.Fail("No se encontrÃ³ la transacciÃ³n a eliminar.");
          }

          await tx!.CommitAsync(ct);
          return TransaccionCommandResult.Ok("TransacciÃ³n eliminada correctamente.");
      }
      catch (Exception ex)
      {
          try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
          _logger.LogError(ex, "Error al eliminar la transacciÃ³n {TransaccionId}", transaccionId);
          return TransaccionCommandResult.Fail($"OcurriÃ³ un error al eliminar la transacciÃ³n: {ex.Message}");
      }
  }

  public async Task<TransaccionCreateResult> CreateTransaccionAsync(TransaccionCreateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    request.Rfc = RequireCompanyRfc(request.Rfc);

      const string sql = @"
          INSERT INTO dbo.Transacciones (RFC, Fecha, Concepto, Monto, Tipo_Poliza, Forma_Pago, Facturado, Memo, ProyectoID, CompraID, ServicioID, NominaID, Cuenta)
          VALUES (@Rfc, @Fecha, @Concepto, @Monto, @TipoPoliza, @FormaPago, @Facturado, @Memo, @ProyectoId, @CompraId, @ServicioId, @NominaId, @Cuenta);
          SELECT CAST(SCOPE_IDENTITY() as int);";

      try
      {
          using var conn = await OpenConnectionWithAuditContextAsync(ct);

          var parameters = new DynamicParameters();
          parameters.Add("@Rfc", request.Rfc);
          parameters.Add("@Fecha", request.Fecha);
          parameters.Add("@Concepto", request.Concepto);
          parameters.Add("@Monto", request.Monto);
          parameters.Add("@TipoPoliza", request.TipoPoliza);
          parameters.Add("@FormaPago", request.FormaPago);
          parameters.Add("@Facturado", request.Facturado);
          parameters.Add("@Memo", request.Memo, DbType.String, size: -1);
          parameters.Add("@ProyectoId", request.ProyectoId);
          parameters.Add("@CompraId", request.CompraId);
          parameters.Add("@ServicioId", request.ServicioId);
          parameters.Add("@NominaId", request.NominaId);
          parameters.Add("@Cuenta", request.Cuenta);

          var newId = await conn.ExecuteScalarAsync<int>(
              new CommandDefinition(
                  sql,
                  parameters,
                  cancellationToken: ct));
          return TransaccionCreateResult.Ok(newId, "TransacciÃ³n creada correctamente.");
      }
      catch (Exception ex)
      {
          _logger.LogError(ex, "Error al crear la transacciÃ³n.");
          return TransaccionCreateResult.Fail($"OcurriÃ³ un error al crear la transacciÃ³n: {ex.Message}");
      }
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesListAsync(TransaccionFilter filter, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(filter);
    filter.Rfc = RequireCompanyRfc(filter.Rfc);

      var sqlBuilder = new SqlBuilder();
      var template = sqlBuilder.AddTemplate(@"
          SELECT
              t.ID AS Id,
              t.Fecha,
              t.Concepto,
              t.Monto,
              t.Tipo_Poliza AS TipoPoliza,
              t.Forma_Pago AS FormaPago,
              ISNULL(apLinks.ApLinkCount, 0) AS ApLinkCount
          FROM dbo.Transacciones t
          OUTER APPLY (
              SELECT COUNT(*) AS ApLinkCount
              FROM AP.OccurrencePayment apPayment
              WHERE apPayment.TransaccionId = t.ID
          ) apLinks
          /**where**/
          /**orderby**/"
      );

      if (filter.Id.HasValue)
      {
          sqlBuilder.Where("t.ID = @Id", new { filter.Id });
      }
      if (TryBuildFechaRange(filter.Year, filter.Month, out var fechaInicio, out var fechaFin))
      {
          sqlBuilder.Where("t.Fecha >= @FechaInicio AND t.Fecha < @FechaFin", new { FechaInicio = fechaInicio, FechaFin = fechaFin });
      }
      else
      {
          if (filter.Year.HasValue)
          {
              sqlBuilder.Where("YEAR(t.Fecha) = @Year", new { filter.Year });
          }

          if (filter.Month.HasValue)
          {
              sqlBuilder.Where("MONTH(t.Fecha) = @Month", new { filter.Month });
          }
      }
      if (!string.IsNullOrWhiteSpace(filter.Concepto))
      {
          sqlBuilder.Where("t.Concepto LIKE @Concepto", new { Concepto = $"%{filter.Concepto}%" });
      }
      if (filter.Monto.HasValue)
      {
          sqlBuilder.Where("t.Monto = @Monto", new { filter.Monto });
      }
      if (!string.IsNullOrWhiteSpace(filter.TipoPoliza))
      {
          sqlBuilder.Where("t.Tipo_Poliza LIKE @TipoPoliza", new { TipoPoliza = $"%{filter.TipoPoliza}%" });
      }
      if (!string.IsNullOrWhiteSpace(filter.FormaPago))
      {
          sqlBuilder.Where("t.Forma_Pago LIKE @FormaPago", new { FormaPago = $"%{filter.FormaPago}%" });
      }
      if (!string.IsNullOrWhiteSpace(filter.Rfc))
      {
          sqlBuilder.Where("t.RFC = @Rfc", new { filter.Rfc });
      }

      if (!string.IsNullOrWhiteSpace(filter.SortBy))
      {
          var columnMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
          {
              { "Id", "t.ID" },
              { "Fecha", "t.Fecha" },
              { "Concepto", "t.Concepto" },
              { "Monto", "t.Monto" },
              { "TipoPoliza", "t.Tipo_Poliza" },
              { "FormaPago", "t.Forma_Pago" }
          };

          if (columnMap.TryGetValue(filter.SortBy, out var dbColumn))
          {
            var orderClause = string.Equals(filter.SortBy, "Fecha", StringComparison.OrdinalIgnoreCase)
              ? BuildFechaOrderClause("t", descending: !filter.SortAsc)
              : $"{dbColumn} {(filter.SortAsc ? "ASC" : "DESC")}, {BuildFechaOrderClause("t", descending: true)}";

            _ = sqlBuilder.OrderBy(orderClause);
          }
          else
          {
            _ = sqlBuilder.OrderBy(BuildFechaOrderClause("t", descending: true));
          }
      }
      else
      {
        _ = sqlBuilder.OrderBy(BuildFechaOrderClause("t", descending: true));
      }

      using var conn = await OpenAccountingConnectionAsync(ct);
      var rows = await conn.QueryAsync<TransaccionListItemDto>(
          new CommandDefinition(template.RawSql, template.Parameters, cancellationToken: ct)
      );
      return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByUuidAsync(string uuid, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(uuid))
      return Array.Empty<TransaccionListItemDto>();

    const string sql = @"SELECT
    T.ID                          AS Id,
    T.Fecha                       AS Fecha,
    T.Concepto                    AS Concepto,
    CAST(T.Monto AS decimal(18,4)) AS Monto,
    CAST(TC.Monto AS decimal(18,4))   AS MontoAsignado,
    T.Tipo_Poliza                 AS TipoPoliza,
    T.Forma_Pago                  AS FormaPago
FROM cfdi.TimbreFiscalDigital AS TFD
JOIN dbo.Transaccion_Comprobante AS TC
  ON TC.Comprobante_ID = TFD.Comprobante_Id
JOIN dbo.Transacciones AS T
  ON T.ID = TC.Transaccion_ID
WHERE TFD.UUID = @Uuid
  AND T.RFC = @ScopeRfc
ORDER BY T.Fecha, T.OrdenBalance, T.ID;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionListItemDto>(
        new CommandDefinition(sql, new { Uuid = uuid, ScopeRfc = RequireCompanyRfc() }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByComprobanteIdAsync(int comprobanteId, CancellationToken ct = default)
  {
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    const string sql = @"SELECT
    T.ID                          AS Id,
    T.Fecha                       AS Fecha,
    T.Concepto                    AS Concepto,
    CAST(T.Monto AS decimal(18,4)) AS Monto,
    CAST(TC.Monto AS decimal(18,4))   AS MontoAsignado,
    T.Tipo_Poliza                 AS TipoPoliza,
    T.Forma_Pago                  AS FormaPago
FROM dbo.Transaccion_Comprobante AS TC
JOIN dbo.Transacciones AS T
  ON T.ID = TC.Transaccion_ID
WHERE TC.Comprobante_ID = @ComprobanteId
  AND T.RFC = @ScopeRfc
ORDER BY T.Fecha, T.OrdenBalance, T.ID;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionListItemDto>(
        new CommandDefinition(sql, new { ComprobanteId = comprobanteId, ScopeRfc = RequireCompanyRfc() }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<IReadOnlyList<TransaccionListItemDto>> GetTransaccionesByDoctoRelacionadoIdAsync(int doctoRelacionadoId, CancellationToken ct = default)
  {
    await EnsureDoctoScopeAsync(doctoRelacionadoId, ct);

    const string sql = @"SELECT
    T.ID                            AS Id,
    T.Fecha                         AS Fecha,
    T.Concepto                      AS Concepto,
    CAST(T.Monto AS decimal(18,4))  AS Monto,
    CAST(TD.Monto AS decimal(18,4)) AS MontoAsignado,
    T.Tipo_Poliza                   AS TipoPoliza,
    T.Forma_Pago                    AS FormaPago
FROM cfdi.Pagos20_DoctoRelacionado AS DR
JOIN dbo.Transaccion_DoctoRelacionado AS TD
  ON TD.DoctoRelacionado_Id = DR.DoctoRelacionado_Id
JOIN dbo.Transacciones AS T
  ON T.ID = TD.Transaccion_ID
WHERE DR.DoctoRelacionado_Id = @DoctoRelacionadoId
  AND T.RFC = @ScopeRfc
ORDER BY T.Fecha, T.OrdenBalance, T.ID;";

    using var conn = await OpenAccountingConnectionAsync(ct);
    var rows = await conn.QueryAsync<TransaccionListItemDto>(
      new CommandDefinition(sql, new { DoctoRelacionadoId = doctoRelacionadoId, ScopeRfc = RequireCompanyRfc() }, cancellationToken: ct));

    return rows.AsList();
  }

  public async Task<TransaccionCommandResult> InsertTransaccionDoctoRelacionadoAsync(int transaccionId, int doctoRelacionadoId, decimal monto, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureDoctoScopeAsync(doctoRelacionadoId, ct);

    return await WritePago20AllocationAsync(transaccionId, doctoRelacionadoId, monto, updateExisting: false, ct);
  }

  public async Task<TransaccionCommandResult> UpdateDoctoRelacionadoMontoAsync(int transaccionId, int doctoRelacionadoId, decimal monto, CancellationToken ct = default)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureDoctoScopeAsync(doctoRelacionadoId, ct);

    return await WritePago20AllocationAsync(transaccionId, doctoRelacionadoId, monto, updateExisting: true, ct);
  }

  public async Task<TransaccionCommandResult> GuardarMovimientosAsync(TransaccionMovimientosUpdateRequest request, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(request);
    await EnsureTransactionScopeAsync(request.TransaccionId, ct);

      if (request is null)
          throw new ArgumentNullException(nameof(request));

      var cuadreError = MovimientosCuadreValidator.Validate(request.Movimientos);
      if (cuadreError is not null)
      {
          _logger.LogWarning(
              "PÃ³liza descuadrada rechazada para la transacciÃ³n {TransaccionId}: {Detalle}",
              request.TransaccionId, cuadreError);
          return TransaccionCommandResult.Fail(cuadreError);
      }

      using var conn = await OpenConnectionWithAuditContextAsync(ct);
      using var tx = await conn.BeginTransactionAsync(ct) as SqlTransaction;

      try
      {
          const string existingIdsSql = @"SELECT ID
FROM dbo.Registro_Contable
WHERE TransaccionID = @TransaccionId;";

          var existingIds = (await conn.QueryAsync<int>(
              new CommandDefinition(existingIdsSql, new { request.TransaccionId }, tx, cancellationToken: ct)))
              .ToHashSet();

          var requestedExistingIds = request.Movimientos
              .Where(m => existingIds.Contains(m.Id))
              .Select(m => m.Id)
              .ToHashSet();

          var movementIdsToDelete = existingIds
              .Where(id => !requestedExistingIds.Contains(id))
              .ToArray();

          if (movementIdsToDelete.Length != 0)
          {
              const string deleteSql = @"DELETE FROM dbo.Registro_Contable
WHERE TransaccionID = @TransaccionId
  AND ID IN @MovimientoIds;";

              await conn.ExecuteAsync(new CommandDefinition(
                  deleteSql,
                  new { request.TransaccionId, MovimientoIds = movementIdsToDelete },
                  tx,
                  cancellationToken: ct));
          }

          var movementsToUpdate = request.Movimientos
              .Where(m => existingIds.Contains(m.Id))
              .Select(m => new
              {
                  MovimientoId = m.Id,
                  request.TransaccionId,
                  m.Nivel1,
                  m.Nivel2,
                  m.Nivel3,
                  m.NombreCuenta,
                  m.Concepto,
                  m.Debe,
                  m.Haber
              })
              .ToArray();

          if (movementsToUpdate.Length != 0)
          {
              const string updateSql = @"
UPDATE dbo.Registro_Contable
SET Nivel1 = @Nivel1,
    Nivel2 = @Nivel2,
    Nivel3 = @Nivel3,
    Nombre_Cuenta = @NombreCuenta,
    Concepto = @Concepto,
    Debe = @Debe,
    Haber = @Haber
WHERE ID = @MovimientoId
  AND TransaccionID = @TransaccionId
  AND EXISTS
  (
      SELECT @Nivel1, @Nivel2, @Nivel3, @NombreCuenta, @Concepto,
             CAST(@Debe AS decimal(18,4)), CAST(@Haber AS decimal(18,4))
      EXCEPT
      SELECT currentRow.Nivel1, currentRow.Nivel2, currentRow.Nivel3, currentRow.Nombre_Cuenta, currentRow.Concepto,
             CAST(ISNULL(currentRow.Debe, 0) AS decimal(18,4)), CAST(ISNULL(currentRow.Haber, 0) AS decimal(18,4))
      FROM dbo.Registro_Contable AS currentRow
      WHERE currentRow.ID = @MovimientoId
        AND currentRow.TransaccionID = @TransaccionId
  );";

              await conn.ExecuteAsync(new CommandDefinition(updateSql, movementsToUpdate, tx, cancellationToken: ct));
          }

          var movementsToInsert = request.Movimientos
              .Where(m => !existingIds.Contains(m.Id))
              .Select(m => new
              {
                  request.TransaccionId,
                  m.Nivel1,
                  m.Nivel2,
                  m.Nivel3,
                  m.NombreCuenta,
                  m.Concepto,
                  m.Debe,
                  m.Haber
              })
              .ToArray();

          if (movementsToInsert.Length != 0)
          {
              const string insertSql = @"
                  INSERT INTO dbo.Registro_Contable (TransaccionID, Nivel1, Nivel2, Nivel3, Nombre_Cuenta, Concepto, Debe, Haber)
                  VALUES (@TransaccionId, @Nivel1, @Nivel2, @Nivel3, @NombreCuenta, @Concepto, @Debe, @Haber);";

              await conn.ExecuteAsync(new CommandDefinition(insertSql, movementsToInsert, tx, cancellationToken: ct));
          }

          await tx!.CommitAsync(ct);
          return TransaccionCommandResult.Ok("Movimientos guardados correctamente.");
      }
      catch (Exception ex)
      {
          try { await tx!.RollbackAsync(ct); } catch { /* ignored */ }
          _logger.LogError(ex, "Error al guardar movimientos para la transacciÃ³n {TransaccionId}", request.TransaccionId);
          return TransaccionCommandResult.Fail(
              "No se pudieron guardar los movimientos contables. Revisa que las cuentas y los importes sean vÃ¡lidos e intÃ©ntalo de nuevo; si el problema persiste, reporta el incidente al Ã¡rea de sistemas.");
      }
  }

  private async Task<SqlConnection> OpenConnectionWithAuditContextAsync(CancellationToken ct)
  {
      var conn = await OpenAccountingConnectionAsync(ct);

      try
      {
          await SetAuditSessionContextAsync(conn, transaction: null, ct);
          return conn;
      }
      catch
      {
          await conn.DisposeAsync();
          throw;
      }
  }

  private async Task SetAuditSessionContextAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      CancellationToken ct)
  {
      var userName = NormalizeAuditUserName(
          _currentUserAccessor is null
              ? null
              : await _currentUserAccessor.GetUserNameAsync(ct));

      const string sql = @"
EXEC sys.sp_set_session_context @key = N'OrionERP.UserName', @value = @UserName;
EXEC sys.sp_set_session_context @key = N'OrionERP.Application', @value = N'OrionERP';";

      await conn.ExecuteAsync(new CommandDefinition(
          sql,
          new { UserName = userName },
          transaction,
          cancellationToken: ct));
  }

  private static string NormalizeAuditUserName(string? userName)
  {
      userName = userName?.Trim();
      return string.IsNullOrWhiteSpace(userName)
          ? "OrionERP"
          : userName.Length <= 256
              ? userName
              : userName[..256];
  }

  private sealed class CfdiCandidateRow
  {
    public long Comprobante_Id { get; set; }
    public DateTime Fecha { get; set; }
    public string? Tipo { get; set; }
    public string? Serie { get; set; }
    public string? Folio { get; set; }
    public string? Emisor_Rfc { get; set; }
    public string? Receptor_Rfc { get; set; }
    public string? UUID { get; set; }
    public string? FormaPago { get; set; }
    public decimal Total { get; set; }
    public int Polizas { get; set; }
    public decimal Asignado { get; set; }
    public string? MetodoPago { get; set; }
    public string? UsoCFDI { get; set; }
    public string? Conceptos { get; set; }
    public int? XML_Attachment_ID { get; set; }
  }

  private sealed class RegularCfdiLinkContextRow
  {
    public decimal TransaccionTotal { get; set; }
    public string? TransaccionRfc { get; set; }
    public string? TipoDeComprobante { get; set; }
    public decimal CfdiTotal { get; set; }
    public string? EmisorRfc { get; set; }
    public string? ReceptorRfc { get; set; }
  }

  private sealed class RegularCfdiLinkStateRow
  {
    public decimal CfdiAssignedOther { get; set; }
    public decimal TransaccionAssignedOther { get; set; }
    public bool CurrentLinkExists { get; set; }
    public bool HasPaymentLinks { get; set; }
    public bool PlaceholderExists { get; set; }
    public bool CompanyLinkElsewhere { get; set; }
  }

  private sealed class Pago20LinkContextRow
  {
    public decimal TransaccionTotal { get; set; }
    public string? TransaccionRfc { get; set; }
    public decimal ImpPagado { get; set; }
    public string? MonedaDr { get; set; }
    public string? MonedaP { get; set; }
    public string? TipoDeComprobante { get; set; }
    public string? EmisorRfc { get; set; }
    public string? ReceptorRfc { get; set; }
  }

  private sealed class Pago20LinkStateRow
  {
    public decimal DocumentAssignedOther { get; set; }
    public decimal TransaccionAssignedOther { get; set; }
    public bool CurrentLinkExists { get; set; }
    public bool HasDirectCfdiLinks { get; set; }
  }

  private sealed class Pago20AccountingHeaderRow
  {
    public int TransaccionId { get; set; }
    public decimal TransaccionMonto { get; set; }
    public string? Rfc { get; set; }
    public int LegacyPaymentLinks { get; set; }
  }

  private sealed class Pago20AccountingDocumentRow
  {
    public int DoctoRelacionadoId { get; set; }
    public decimal MontoAsignado { get; set; }
    public decimal ImpPagado { get; set; }
    public string? MonedaDr { get; set; }
    public string? MonedaP { get; set; }
    public string? TipoDeComprobante { get; set; }
    public string? EmisorRfc { get; set; }
    public string? ReceptorRfc { get; set; }
    public decimal DocumentAssigned { get; set; }
  }

  private sealed class Pago20AccountingTaxRow
  {
    public int DoctoRelacionadoId { get; set; }
    public string? ImpuestoDR { get; set; }
    public decimal Importe { get; set; }
  }

  private async Task<int> InsertAttachmentAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      string fileName,
      string extension,
      string description,
      byte[] content,
      CancellationToken ct)
  {
    const string sql = @"
INSERT INTO dbo.TRANSACTION_ATTACHMENT
(TranID, Attachment, AttachmentName, AttachmentExtension, AttachmentDescription)
VALUES (@TranID, @Attachment, @AttachmentName, @AttachmentExtension, @AttachmentDescription);
SELECT CAST(SCOPE_IDENTITY() AS int);";

    return await conn.ExecuteScalarAsync<int>(
        new CommandDefinition(
            sql,
            new
            {
              TranID = transaccionId,
              Attachment = content,
              AttachmentName = fileName,
              AttachmentExtension = extension,
              AttachmentDescription = description
            },
            transaction,
            cancellationToken: ct));
  }

  private static async Task<int?> ProcessSatXmlV2Async(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      int attachmentId,
      CancellationToken ct)
  {
    return await conn.QueryFirstOrDefaultAsync<int?>(
        new CommandDefinition(
            "cfdi.PROCESAR_SAT_XML_V2",
            new { TransaccionID = transaccionId, AttachmentID = attachmentId },
            transaction,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
  }

  private async Task<TransaccionCommandResult> WriteRegularCfdiLinkAsync(
      int transaccionId,
      long comprobanteId,
      decimal monto,
      bool updateExisting,
      bool relinkPlaceholder,
      bool reassignAttachment,
      CancellationToken ct)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    if (monto <= 0m)
      return TransaccionCommandResult.Fail("El monto asignado debe ser mayor que cero.");

    await using var conn = await OpenAccountingConnectionAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      const string contextSql = @"
SELECT TOP (1)
    CAST(ABS(t.Monto) AS decimal(19,4)) AS TransaccionTotal,
    LTRIM(RTRIM(t.RFC)) AS TransaccionRfc,
    c.TipoDeComprobante,
    CAST(ABS(c.Total) AS decimal(19,4)) AS CfdiTotal,
    cd.RFC_EMISOR AS EmisorRfc,
    cd.RFC_RECEPTOR AS ReceptorRfc
FROM dbo.Transacciones AS t WITH (UPDLOCK, HOLDLOCK)
CROSS JOIN cfdi.Comprobante AS c WITH (UPDLOCK, HOLDLOCK)
LEFT JOIN cfdi.Comprobante_Detalle AS cd
  ON cd.Comprobante_Id = c.Comprobante_Id
WHERE t.ID = @TransaccionId
  AND c.Comprobante_Id = @ComprobanteId;";
      var context = await conn.QueryFirstOrDefaultAsync<RegularCfdiLinkContextRow>(new CommandDefinition(
          contextSql,
          new { TransaccionId = transaccionId, ComprobanteId = comprobanteId },
          tx,
          cancellationToken: ct));

      if (context is null)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("No se encontrÃ³ la pÃ³liza o el CFDI.");
      }
      if (string.Equals(context.TipoDeComprobante, "P", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("Los CFDI tipo P deben ligarse por DoctoRelacionado, no por comprobante.");
      }
      if (context.TipoDeComprobante is not ("I" or "N" or "E"))
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El tipo de CFDI no es compatible con el ligado contable regular.");
      }
      if (!RfcMatches(context.TransaccionRfc, context.EmisorRfc, context.ReceptorRfc))
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El RFC de la pÃ³liza no corresponde al emisor ni al receptor del CFDI.");
      }

      const string stateSql = @"
SELECT
    CAST(ISNULL(SUM(CASE
        WHEN tc.Transaccion_ID = @TransaccionId AND tc.Comprobante_ID = @ComprobanteId THEN 0
        WHEN @RelinkPlaceholder = 1 AND tc.Transaccion_ID = 5505 THEN 0
        ELSE tc.Monto END), 0) AS decimal(19,4)) AS CfdiAssignedOther,
    CAST(ISNULL((
        SELECT SUM(tc2.Monto)
        FROM dbo.Transaccion_Comprobante AS tc2 WITH (UPDLOCK, HOLDLOCK)
        JOIN cfdi.Comprobante AS c2 ON c2.Comprobante_Id = tc2.Comprobante_ID
        WHERE tc2.Transaccion_ID = @TransaccionId
          AND c2.TipoDeComprobante IN ('I','N','E')
          AND NOT (tc2.Comprobante_ID = @ComprobanteId)
    ), 0) AS decimal(19,4)) AS TransaccionAssignedOther,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.Transaccion_Comprobante AS currentLink WITH (UPDLOCK, HOLDLOCK)
        WHERE currentLink.Transaccion_ID = @TransaccionId AND currentLink.Comprobante_ID = @ComprobanteId
    ) THEN 1 ELSE 0 END AS bit) AS CurrentLinkExists,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.Transaccion_Comprobante AS paymentLink WITH (UPDLOCK, HOLDLOCK)
        JOIN cfdi.Comprobante AS paymentCfdi ON paymentCfdi.Comprobante_Id = paymentLink.Comprobante_ID
        WHERE paymentLink.Transaccion_ID = @TransaccionId AND paymentCfdi.TipoDeComprobante = 'P'
    ) OR EXISTS (
        SELECT 1 FROM dbo.Transaccion_DoctoRelacionado AS paymentDocumentLink WITH (UPDLOCK, HOLDLOCK)
        WHERE paymentDocumentLink.Transaccion_ID = @TransaccionId
    ) THEN 1 ELSE 0 END AS bit) AS HasPaymentLinks,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.Transaccion_Comprobante AS placeholder WITH (UPDLOCK, HOLDLOCK)
        WHERE placeholder.Transaccion_ID = 5505 AND placeholder.Comprobante_ID = @ComprobanteId
    ) THEN 1 ELSE 0 END AS bit) AS PlaceholderExists,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.Transaccion_Comprobante AS companyLink WITH (UPDLOCK, HOLDLOCK)
        JOIN dbo.Transacciones AS companyTransaction ON companyTransaction.ID = companyLink.Transaccion_ID
        WHERE companyLink.Comprobante_ID = @ComprobanteId
          AND companyTransaction.RFC = @CompanyRfc
          AND companyLink.Transaccion_ID <> @TransaccionId
          AND companyLink.Transaccion_ID <> 5505
    ) THEN 1 ELSE 0 END AS bit) AS CompanyLinkElsewhere
FROM dbo.Transaccion_Comprobante AS tc WITH (UPDLOCK, HOLDLOCK)
WHERE tc.Comprobante_ID = @ComprobanteId;";
      var state = await conn.QuerySingleAsync<RegularCfdiLinkStateRow>(new CommandDefinition(
          stateSql,
          new
          {
            TransaccionId = transaccionId,
            ComprobanteId = comprobanteId,
            RelinkPlaceholder = relinkPlaceholder,
            CompanyRfc = RequireCompanyRfc()
          },
          tx,
          cancellationToken: ct));

      if (state.HasPaymentLinks)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La pÃ³liza ya contiene vÃ­nculos de complementos de pago.");
      }
      // El mismo CFDI puede seguir pendiente para la otra empresa, pero dentro de
      // ésta no se duplica: una sola póliza suya lo lleva.
      if (!updateExisting && state.CompanyLinkElsewhere)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("Este CFDI ya está ligado a otra póliza de la misma empresa.");
      }
      if (updateExisting != state.CurrentLinkExists)
      {
        await tx.RollbackAsync(ct);
        return updateExisting
            ? TransaccionCommandResult.Fail("No se encontrÃ³ el vÃ­nculo CFDI-pÃ³liza a actualizar.")
            : TransaccionCommandResult.Fail("Ya existe un vÃ­nculo entre esta pÃ³liza y el CFDI.");
      }
      if (monto - (context.CfdiTotal - state.CfdiAssignedOther) > 0.01m)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El monto excede el saldo disponible del CFDI.");
      }
      if (monto - (context.TransaccionTotal - state.TransaccionAssignedOther) > 0.01m)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El monto excede el saldo disponible de la pÃ³liza.");
      }

      int affected;
      if (updateExisting)
      {
        affected = await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.Transaccion_Comprobante SET Monto = @Monto
WHERE Transaccion_ID = @TransaccionId AND Comprobante_ID = @ComprobanteId;",
            new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, Monto = monto },
            tx,
            cancellationToken: ct));
      }
      else if (relinkPlaceholder && state.PlaceholderExists)
      {
        affected = await conn.ExecuteAsync(new CommandDefinition(
            @"UPDATE dbo.Transaccion_Comprobante SET Transaccion_ID = @TransaccionId, Monto = @Monto
WHERE Transaccion_ID = 5505 AND Comprobante_ID = @ComprobanteId;",
            new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, Monto = monto },
            tx,
            cancellationToken: ct));
      }
      else
      {
        affected = await conn.ExecuteAsync(new CommandDefinition(
            @"INSERT INTO dbo.Transaccion_Comprobante (Transaccion_ID, Comprobante_ID, Monto)
VALUES (@TransaccionId, @ComprobanteId, @Monto);",
            new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, Monto = monto },
            tx,
            cancellationToken: ct));
      }

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("No se pudo guardar el vÃ­nculo CFDI-pÃ³liza.");
      }

      if (reassignAttachment)
        await ReassignXmlAttachmentAsync(conn, tx, comprobanteId, transaccionId, ct);

      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok(updateExisting
          ? "Monto asignado actualizado correctamente."
          : "TransacciÃ³n ligada correctamente.");
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await RollbackQuietlyAsync(tx, ct);
      return TransaccionCommandResult.Fail("Ya existe un vÃ­nculo entre esta pÃ³liza y el CFDI.");
    }
    catch (Exception ex)
    {
      await RollbackQuietlyAsync(tx, ct);
      _logger.LogError(ex, "Error al guardar vÃ­nculo regular {TransaccionId}/{ComprobanteId}", transaccionId, comprobanteId);
      return TransaccionCommandResult.Fail("No se pudo guardar el vÃ­nculo CFDI-pÃ³liza.");
    }
  }

  private async Task<TransaccionCommandResult> WritePago20AllocationAsync(
      int transaccionId,
      int doctoRelacionadoId,
      decimal monto,
      bool updateExisting,
      CancellationToken ct)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureDoctoScopeAsync(doctoRelacionadoId, ct);

    if (monto <= 0m)
      return TransaccionCommandResult.Fail("El monto asignado debe ser mayor que cero.");

    await using var conn = await OpenAccountingConnectionAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    try
    {
      const string contextSql = @"
SELECT TOP (1)
    CAST(ABS(t.Monto) AS decimal(19,4)) AS TransaccionTotal,
    LTRIM(RTRIM(t.RFC)) AS TransaccionRfc,
    CAST(dr.ImpPagado AS decimal(19,4)) AS ImpPagado,
    dr.MonedaDR AS MonedaDr,
    p.MonedaP,
    c.TipoDeComprobante,
    cd.RFC_EMISOR AS EmisorRfc,
    cd.RFC_RECEPTOR AS ReceptorRfc
FROM dbo.Transacciones AS t WITH (UPDLOCK, HOLDLOCK)
CROSS JOIN cfdi.Pagos20_DoctoRelacionado AS dr WITH (UPDLOCK, HOLDLOCK)
JOIN cfdi.Pagos20_Pago AS p WITH (UPDLOCK, HOLDLOCK) ON p.Pago_Id = dr.Pago_Id
JOIN cfdi.Pagos20 AS p20 WITH (UPDLOCK, HOLDLOCK) ON p20.Pagos20_Id = p.Pagos20_Id
JOIN cfdi.Comprobante AS c WITH (UPDLOCK, HOLDLOCK) ON c.Comprobante_Id = p20.Comprobante_Id
JOIN cfdi.Comprobante_Detalle AS cd ON cd.Comprobante_Id = c.Comprobante_Id
WHERE t.ID = @TransaccionId
  AND dr.DoctoRelacionado_Id = @DoctoRelacionadoId;";
      var context = await conn.QueryFirstOrDefaultAsync<Pago20LinkContextRow>(new CommandDefinition(
          contextSql,
          new { TransaccionId = transaccionId, DoctoRelacionadoId = doctoRelacionadoId },
          tx,
          cancellationToken: ct));

      if (context is null)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("No se encontrÃ³ la pÃ³liza o el documento Pago20.");
      }
      if (!string.Equals(context.TipoDeComprobante, "P", StringComparison.OrdinalIgnoreCase))
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El documento relacionado no pertenece a un CFDI tipo P.");
      }
      if (!IsMxn(context.MonedaP) || !IsMxn(context.MonedaDr))
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La asignaciÃ³n Pago20 solo admite MonedaP y MonedaDR en MXN.");
      }
      if (!RfcMatches(context.TransaccionRfc, context.EmisorRfc, context.ReceptorRfc))
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El RFC de la pÃ³liza no corresponde al emisor ni al receptor del complemento.");
      }

      const string stateSql = @"
SELECT
    CAST(ISNULL(SUM(CASE WHEN td.Transaccion_ID = @TransaccionId THEN 0 ELSE td.Monto END), 0) AS decimal(19,4)) AS DocumentAssignedOther,
    CAST(ISNULL((
        SELECT SUM(CASE WHEN currentAllocation.DoctoRelacionado_Id = @DoctoRelacionadoId THEN 0 ELSE currentAllocation.Monto END)
        FROM dbo.Transaccion_DoctoRelacionado AS currentAllocation WITH (UPDLOCK, HOLDLOCK)
        WHERE currentAllocation.Transaccion_ID = @TransaccionId
    ), 0) AS decimal(19,4)) AS TransaccionAssignedOther,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.Transaccion_DoctoRelacionado AS currentLink WITH (UPDLOCK, HOLDLOCK)
        WHERE currentLink.Transaccion_ID = @TransaccionId AND currentLink.DoctoRelacionado_Id = @DoctoRelacionadoId
    ) THEN 1 ELSE 0 END AS bit) AS CurrentLinkExists,
    CAST(CASE WHEN EXISTS (
        SELECT 1 FROM dbo.Transaccion_Comprobante AS directLink WITH (UPDLOCK, HOLDLOCK)
        WHERE directLink.Transaccion_ID = @TransaccionId
    ) THEN 1 ELSE 0 END AS bit) AS HasDirectCfdiLinks
FROM dbo.Transaccion_DoctoRelacionado AS td WITH (UPDLOCK, HOLDLOCK)
WHERE td.DoctoRelacionado_Id = @DoctoRelacionadoId;";
      var state = await conn.QuerySingleAsync<Pago20LinkStateRow>(new CommandDefinition(
          stateSql,
          new { TransaccionId = transaccionId, DoctoRelacionadoId = doctoRelacionadoId },
          tx,
          cancellationToken: ct));

      if (state.HasDirectCfdiLinks)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("La pÃ³liza ya contiene un vÃ­nculo de CFDI regular o Pago20 legado.");
      }
      if (updateExisting != state.CurrentLinkExists)
      {
        await tx.RollbackAsync(ct);
        return updateExisting
            ? TransaccionCommandResult.Fail("No se encontrÃ³ el vÃ­nculo Pago20 a actualizar.")
            : TransaccionCommandResult.Fail("Ya existe un vÃ­nculo entre esta pÃ³liza y el documento Pago20.");
      }
      if (monto - (context.ImpPagado - state.DocumentAssignedOther) > 0.01m)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El monto excede el saldo disponible del documento Pago20.");
      }
      if (monto - (context.TransaccionTotal - state.TransaccionAssignedOther) > 0.01m)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("El monto excede el saldo disponible de la pÃ³liza.");
      }

      var affected = updateExisting
          ? await conn.ExecuteAsync(new CommandDefinition(
              @"UPDATE dbo.Transaccion_DoctoRelacionado SET Monto = @Monto
WHERE Transaccion_ID = @TransaccionId AND DoctoRelacionado_Id = @DoctoRelacionadoId;",
              new { TransaccionId = transaccionId, DoctoRelacionadoId = doctoRelacionadoId, Monto = monto },
              tx,
              cancellationToken: ct))
          : await conn.ExecuteAsync(new CommandDefinition(
              @"INSERT INTO dbo.Transaccion_DoctoRelacionado (Transaccion_ID, DoctoRelacionado_Id, Monto)
VALUES (@TransaccionId, @DoctoRelacionadoId, @Monto);",
              new { TransaccionId = transaccionId, DoctoRelacionadoId = doctoRelacionadoId, Monto = monto },
              tx,
              cancellationToken: ct));

      if (affected == 0)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("No se pudo guardar el vÃ­nculo Pago20.");
      }

      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok(updateExisting
          ? "Monto Pago20 actualizado correctamente."
          : "Documento Pago20 ligado correctamente.");
    }
    catch (SqlException ex) when (ex.Number is 2601 or 2627)
    {
      await RollbackQuietlyAsync(tx, ct);
      return TransaccionCommandResult.Fail("Ya existe un vÃ­nculo entre esta pÃ³liza y el documento Pago20.");
    }
    catch (Exception ex)
    {
      await RollbackQuietlyAsync(tx, ct);
      _logger.LogError(ex, "Error al guardar vÃ­nculo Pago20 {TransaccionId}/{DoctoRelacionadoId}", transaccionId, doctoRelacionadoId);
      return TransaccionCommandResult.Fail("No se pudo guardar el vÃ­nculo Pago20.");
    }
  }

  private async Task<TransaccionCommandResult> UnlinkDirectCfdiAsync(
      int transaccionId,
      long comprobanteId,
      bool requirePaymentType,
      CancellationToken ct)
  {
    await EnsureTransactionScopeAsync(transaccionId, ct);
    await EnsureComprobanteScopeAsync(comprobanteId, ct);

    await using var conn = await OpenAccountingConnectionAsync(ct);
    await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

    try
    {
      const string sql = @"
DELETE tc
FROM dbo.Transaccion_Comprobante AS tc
JOIN cfdi.Comprobante AS c ON c.Comprobante_Id = tc.Comprobante_ID
WHERE tc.Transaccion_ID = @TransaccionId
  AND tc.Comprobante_ID = @ComprobanteId
  AND ((@RequirePaymentType = 1 AND c.TipoDeComprobante = 'P')
    OR (@RequirePaymentType = 0 AND c.TipoDeComprobante IN ('I','N','E')));";
      var updated = await conn.ExecuteAsync(new CommandDefinition(
          sql,
          new { TransaccionId = transaccionId, ComprobanteId = comprobanteId, RequirePaymentType = requirePaymentType },
          tx,
          cancellationToken: ct));

      if (updated == 0)
      {
        await tx.RollbackAsync(ct);
        return TransaccionCommandResult.Fail("No se encontrÃ³ el vÃ­nculo solicitado con la pÃ³liza actual.");
      }

      var nextTransaccionId = await GetPreferredLinkedTransaccionIdAsync(conn, tx, comprobanteId, ct);
      await ReassignXmlAttachmentAsync(conn, tx, comprobanteId, nextTransaccionId, ct);
      await tx.CommitAsync(ct);
      return TransaccionCommandResult.Ok(requirePaymentType
          ? "VÃ­nculo Pago20 legado desligado correctamente."
          : "Comprobante desligado correctamente.");
    }
    catch (Exception ex)
    {
      await RollbackQuietlyAsync(tx, ct);
      _logger.LogError(ex, "Error al desligar vÃ­nculo directo {TransaccionId}/{ComprobanteId}", transaccionId, comprobanteId);
      return TransaccionCommandResult.Fail("No se pudo desligar el comprobante.");
    }
  }

  private static async Task<HashSet<int>> GetTransactionsWithDirectCfdiLinksAsync(
      SqlConnection conn,
      IEnumerable<int> transaccionIds,
      CancellationToken ct)
  {
    var ids = transaccionIds.Distinct().ToArray();
    if (ids.Length == 0)
      return [];

    var rows = await conn.QueryAsync<int>(new CommandDefinition(
        "SELECT DISTINCT Transaccion_ID FROM dbo.Transaccion_Comprobante WHERE Transaccion_ID IN @Ids;",
        new { Ids = ids },
        cancellationToken: ct));
    return rows.ToHashSet();
  }

  private static bool IsMxn(string? currency)
    => string.Equals(currency?.Trim(), "MXN", StringComparison.OrdinalIgnoreCase);

  private static bool RfcMatches(string? transactionRfc, string? emisorRfc, string? receptorRfc)
    => !string.IsNullOrWhiteSpace(transactionRfc)
      && (string.Equals(transactionRfc.Trim(), emisorRfc?.Trim(), StringComparison.OrdinalIgnoreCase)
        || string.Equals(transactionRfc.Trim(), receptorRfc?.Trim(), StringComparison.OrdinalIgnoreCase));

  private static string ResolvePago20Direction(string? transactionRfc, string? emisorRfc, string? receptorRfc)
  {
    if (string.Equals(transactionRfc?.Trim(), emisorRfc?.Trim(), StringComparison.OrdinalIgnoreCase))
      return "Emitido";
    if (string.Equals(transactionRfc?.Trim(), receptorRfc?.Trim(), StringComparison.OrdinalIgnoreCase))
      return "Recibido";
    return "Otro";
  }

  private static async Task RollbackQuietlyAsync(SqlTransaction tx, CancellationToken ct)
  {
    try { await tx.RollbackAsync(ct); } catch { /* ignored */ }
  }

  private static async Task<int?> GetXmlAttachmentIdAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      long comprobanteId,
      CancellationToken ct)
  {
    const string sql = @"SELECT XML_Attachment_ID
FROM cfdi.Comprobante
WHERE Comprobante_ID = @ComprobanteId;";

    return await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
            sql,
            new { ComprobanteId = comprobanteId },
            transaction,
            cancellationToken: ct));
  }

  private static async Task<int?> GetPreferredLinkedTransaccionIdAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      long comprobanteId,
      CancellationToken ct)
  {
    const string sql = @"SELECT TOP (1) tc.Transaccion_ID
FROM dbo.Transaccion_Comprobante tc
INNER JOIN dbo.Transacciones t
        ON t.ID = tc.Transaccion_ID
WHERE tc.Comprobante_ID = @ComprobanteId
ORDER BY t.Fecha, t.OrdenBalance, t.ID;";

    return await conn.ExecuteScalarAsync<int?>(
        new CommandDefinition(
            sql,
            new { ComprobanteId = comprobanteId },
            transaction,
            cancellationToken: ct));
  }

  private static async Task ReassignXmlAttachmentAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      long comprobanteId,
      int? transaccionId,
      CancellationToken ct)
  {
    var attachmentId = await GetXmlAttachmentIdAsync(conn, transaction, comprobanteId, ct);
    if (!attachmentId.HasValue || attachmentId.Value <= 0)
    {
      return;
    }

    const string sql = @"UPDATE dbo.TRANSACTION_ATTACHMENT
SET TranID = @TransaccionId
WHERE ID = @AttachmentId;";

    await conn.ExecuteAsync(
        new CommandDefinition(
            sql,
            new
            {
              AttachmentId = attachmentId.Value,
              TransaccionId = transaccionId
            },
            transaction,
            cancellationToken: ct));
  }

  private async Task<int> ExecuteInsertAsync(string sql, object parameters, CancellationToken ct)
  {
    using var conn = await OpenAccountingConnectionAsync(ct);
    return await conn.ExecuteScalarAsync<int>(
      new CommandDefinition(sql, parameters, cancellationToken: ct));
  }

  private static async Task<MovimientoTotalsDto> LoadTotalsAsync(
      SqlConnection conn,
      SqlTransaction? transaction,
      int transaccionId,
      CancellationToken ct)
  {
    const string sql = @"SELECT
    CAST(ISNULL(SUM(rc.Debe), 0) AS decimal(18,4))  AS Debe,
    CAST(ISNULL(SUM(rc.Haber), 0) AS decimal(18,4)) AS Haber
FROM dbo.Registro_Contable rc
WHERE rc.TransaccionID = @TransaccionId;";

    var totals = await conn.QueryFirstOrDefaultAsync<MovimientoTotalsDto>(
        new CommandDefinition(sql, new { TransaccionId = transaccionId }, transaction, cancellationToken: ct));

    return totals ?? new MovimientoTotalsDto();
  }

  private static string BuildFechaOrderClause(string tableAlias, bool descending)
  {
    var direction = descending ? "DESC" : "ASC";
    return $"{tableAlias}.Fecha {direction}, {tableAlias}.OrdenBalance {direction}, {tableAlias}.ID {direction}";
  }

  private static DateTime PreserveTimeOfDay(DateTime requestedDate, DateTime existingDate)
  {
    if (requestedDate.TimeOfDay != TimeSpan.Zero)
    {
      return requestedDate;
    }

    return requestedDate.Date.Add(existingDate.TimeOfDay);
  }

  private static bool TryBuildFechaRange(int? year, int? month, out DateTime fechaInicio, out DateTime fechaFin)
  {
    fechaInicio = default;
    fechaFin = default;

    if (!year.HasValue)
    {
      return false;
    }

    if (month.HasValue)
    {
      if (month.Value is < 1 or > 12)
      {
        return false;
      }

      fechaInicio = new DateTime(year.Value, month.Value, 1);
      fechaFin = fechaInicio.AddMonths(1);
      return true;
    }

    fechaInicio = new DateTime(year.Value, 1, 1);
    fechaFin = fechaInicio.AddYears(1);
    return true;
  }

  private static string ResolveContentType(string? extension)
  {
    if (string.IsNullOrWhiteSpace(extension))
    {
      return "application/octet-stream";
    }

    return extension.ToLowerInvariant() switch
    {
      "pdf" => "application/pdf",
      "xml" => "application/xml",
      "jpg" or "jpeg" => "image/jpeg",
      "png" => "image/png",
      "txt" => "text/plain",
      _ => "application/octet-stream"
    };
  }

  private static DynamicParameters BuildLinkingWorkspaceParameters(
      int? comprobanteId,
      int? doctoRelacionadoId,
      string? rfc,
      TransaccionFilter filter)
  {
    var parameters = new DynamicParameters();

    if (comprobanteId.HasValue)
    {
      parameters.Add("@Comprobante_Id", comprobanteId.Value);
    }

    if (doctoRelacionadoId.HasValue)
    {
      parameters.Add("@DoctoRelacionado_Id", doctoRelacionadoId.Value);
    }

    parameters.Add("@RFC", string.IsNullOrWhiteSpace(rfc) ? null : rfc);
    parameters.Add("@Year", filter.Year);
    parameters.Add("@Month", filter.Month);
    parameters.Add("@TransaccionId", filter.Id);
    parameters.Add("@Concepto", string.IsNullOrWhiteSpace(filter.Concepto) ? null : filter.Concepto);
    parameters.Add("@Monto", filter.Monto);
    parameters.Add("@TipoPoliza", string.IsNullOrWhiteSpace(filter.TipoPoliza) ? null : filter.TipoPoliza);
    parameters.Add("@FormaPago", string.IsNullOrWhiteSpace(filter.FormaPago) ? null : filter.FormaPago);
    parameters.Add("@Top", 200);

    return parameters;
  }

  private static string? NormalizeGlobalMonth(string? value)
  {
    if (string.IsNullOrWhiteSpace(value))
    {
      return null;
    }

    return int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var month) &&
           month >= 1 &&
           month <= 12
        ? month.ToString("00", CultureInfo.InvariantCulture)
        : null;
  }

  private async Task<string> ResolveIssuerTaxZipCodeAsync(string issuerRfc, CancellationToken ct)
  {
    var profile = await _satRfcProfileRepository.GetAsync(issuerRfc);
    var zipCode = NormalizePostalCode(profile?.CodigoPostal);
    if (!string.IsNullOrWhiteSpace(zipCode))
    {
      return zipCode;
    }

    var taxEntity = await _facturamaApiClient.GetTaxEntityAsync(ct);
    zipCode = NormalizePostalCode(taxEntity.IssuedIn?.ZipCode)
        ?? NormalizePostalCode(taxEntity.TaxAddress?.ZipCode);

    if (!string.IsNullOrWhiteSpace(zipCode))
    {
      return zipCode;
    }

    throw new InvalidOperationException($"No se pudo resolver el codigo postal de expedicion para el RFC {issuerRfc}.");
  }

  private FacturamaIssuedCfdiRequest BuildPublicoPayload(
      TransaccionTimbrarPublicoRequest request,
      string globalMonth,
      string expeditionZipCode)
  {
    var subtotal = RoundCurrency(request.Monto / 1.16m);
    var tax = RoundCurrency(subtotal * 0.16m);
    var total = RoundCurrency(subtotal + tax);
    var receiverName = _cfg["Facturama:PublicoGeneral:ReceiverName"];
    if (string.IsNullOrWhiteSpace(receiverName))
    {
      receiverName = "4537778 - PUBLICO EN GENERAL";
    }

    var paymentForm = string.IsNullOrWhiteSpace(request.FormaPago)
        ? "03"
        : request.FormaPago.Trim();

    return new FacturamaIssuedCfdiRequest
    {
      Header = new FacturamaIssuedCfdiHeader
      {
        Folio = request.TransaccionId.ToString(CultureInfo.InvariantCulture),
        Date = DateTime.Now.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        Currency = "MXN",
        ExpeditionPlace = expeditionZipCode,
        CfdiType = "I",
        PaymentForm = paymentForm,
        PaymentMethod = "PUE",
        TaxZipCode = expeditionZipCode
      },
      GlobalInformation = new FacturamaGlobalInformation
      {
        Periodicity = "01",
        Months = globalMonth,
        Year = request.GlobalAnio.ToString("0000", CultureInfo.InvariantCulture)
      },
      Receiver = new FacturamaReceiver
      {
        Rfc = "XAXX010101000",
        Name = receiverName,
        CfdiUse = "S01",
        FiscalRegime = "616",
        TaxZipCode = expeditionZipCode
      },
      Items = new[]
      {
        new FacturamaIssuedCfdiItem
        {
          ProductCode = "80131501",
          IdentificationNumber = "PUB",
          Description = "UNIDAD AL PUBLICO EN GENERAL",
          Unit = "Unidad de servicio",
          UnitCode = "E48",
          UnitPrice = subtotal,
          Quantity = 1m,
          Subtotal = subtotal,
          Discount = 0m,
          TaxObject = "02",
          Taxes = new[]
          {
            new FacturamaIssuedCfdiTax
            {
              Name = "IVA",
              Rate = 0.16m,
              Total = tax,
              Base = subtotal,
              IsRetention = false
            }
          },
          Total = total
        }
      }
    };
  }

  private static decimal RoundCurrency(decimal value)
    => decimal.Round(value, 2, MidpointRounding.ToEven);

  private static string? NormalizePostalCode(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  private sealed class CfdiPartyRow
  {
    public long ComprobanteId { get; set; }
    public string? Emisor { get; set; }
    public string? Receptor { get; set; }
  }
}
