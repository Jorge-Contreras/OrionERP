namespace OrionERP.Application.Features.Ajustes;

public sealed record PlantillaContableMovimientoDraft
{
  public int Orden { get; init; }
  public int CuentaContableId { get; init; }
  public string CuentaRfc { get; init; } = string.Empty;
  public string Nivel1 { get; init; } = string.Empty;
  public string Nivel2 { get; init; } = string.Empty;
  public string Nivel3 { get; init; } = string.Empty;
  public string CuentaContable { get; init; } = string.Empty;
  public string Concepto { get; init; } = string.Empty;
  public decimal Debe { get; init; }
  public decimal Haber { get; init; }
}

public static class PlantillaContableMovimientoCalculator
{
  private const decimal IvaRate = 0.16m;
  private const decimal SubtotalDivisor = 1m + IvaRate;

  public static IReadOnlyList<PlantillaContableMovimientoDraft> CreateDrafts(
      IEnumerable<PlantillaContableLineaDto> lineas,
      decimal monto,
      string? conceptoTransaccion)
  {
    ArgumentNullException.ThrowIfNull(lineas);

    return lineas
        .Where(line => line.Activa)
        .OrderBy(line => line.Orden)
        .ThenBy(line => line.PlantillaContableLineaId)
        .Select(line => CreateDraft(line, monto, conceptoTransaccion))
        .ToList();
  }

  public static IReadOnlyList<PlantillaContableMovimientoDraft> CreatePago20Drafts(
      IEnumerable<PlantillaContableLineaDto> lineas,
      IReadOnlyDictionary<string, decimal> amounts,
      string? conceptoTransaccion)
  {
    ArgumentNullException.ThrowIfNull(lineas);
    ArgumentNullException.ThrowIfNull(amounts);

    var activeLines = lineas
        .Where(line => line.Activa)
        .OrderBy(line => line.Orden)
        .ThenBy(line => line.PlantillaContableLineaId)
        .ToList();

    var missingSources = amounts
        .Where(pair => decimal.Abs(pair.Value) > 0.005m)
        .Where(pair => !activeLines.Any(line => string.Equals(line.MontoTipo?.Trim(), pair.Key, StringComparison.OrdinalIgnoreCase)))
        .Select(pair => pair.Key)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    if (missingSources.Length > 0)
    {
      throw new ArgumentException(
          $"La plantilla no contiene lineas para: {string.Join(", ", missingSources)}.",
          nameof(lineas));
    }

    var drafts = activeLines
        .Select(line => CreatePago20Draft(line, amounts, conceptoTransaccion))
        .Where(draft => draft.Debe != 0m || draft.Haber != 0m)
        .ToList();

    var debe = drafts.Sum(draft => draft.Debe);
    var haber = drafts.Sum(draft => draft.Haber);
    if (decimal.Abs(debe - haber) > 0.01m)
    {
      throw new ArgumentException(
          $"Los movimientos Pago20 no cuadran. Debe: {debe:N2}; Haber: {haber:N2}.",
          nameof(lineas));
    }

    return drafts;
  }

  private static PlantillaContableMovimientoDraft CreateDraft(
      PlantillaContableLineaDto line,
      decimal monto,
      string? conceptoTransaccion)
  {
    var amount = CalculateLineAmount(line, monto);
    var naturaleza = NormalizeCode(line.Naturaleza);
    var concepto = ResolveConcepto(line, conceptoTransaccion);

    return new PlantillaContableMovimientoDraft
    {
      Orden = line.Orden,
      CuentaContableId = line.CuentaContableId,
      CuentaRfc = line.CuentaRfc,
      Nivel1 = line.Nivel1,
      Nivel2 = line.Nivel2,
      Nivel3 = line.Nivel3,
      CuentaContable = line.CuentaContable,
      Concepto = concepto,
      Debe = naturaleza == "DEBE" ? amount : 0m,
      Haber = naturaleza == "HABER" ? amount : 0m
    };
  }

  private static PlantillaContableMovimientoDraft CreatePago20Draft(
      PlantillaContableLineaDto line,
      IReadOnlyDictionary<string, decimal> amounts,
      string? conceptoTransaccion)
  {
    var montoTipo = NormalizeCode(line.MontoTipo);
    if (!PlantillaContableMontoTipos.Pago20Tipos.Contains(montoTipo, StringComparer.OrdinalIgnoreCase))
    {
      throw new ArgumentException($"Tipo de monto no soportado para Pago20: {montoTipo}.", nameof(line));
    }

    if (!amounts.TryGetValue(montoTipo, out var baseAmount))
    {
      baseAmount = 0m;
    }

    var amount = RoundCurrencyAmount(baseAmount * line.Factor);
    var naturaleza = NormalizeCode(line.Naturaleza);

    return new PlantillaContableMovimientoDraft
    {
      Orden = line.Orden,
      CuentaContableId = line.CuentaContableId,
      CuentaRfc = line.CuentaRfc,
      Nivel1 = line.Nivel1,
      Nivel2 = line.Nivel2,
      Nivel3 = line.Nivel3,
      CuentaContable = line.CuentaContable,
      Concepto = ResolveConcepto(line, conceptoTransaccion),
      Debe = naturaleza == "DEBE" ? amount : 0m,
      Haber = naturaleza == "HABER" ? amount : 0m
    };
  }

  private static decimal CalculateLineAmount(PlantillaContableLineaDto line, decimal monto)
  {
    var baseAmount = NormalizeCode(line.MontoTipo) switch
    {
      PlantillaContableMontoTipos.MontoTotal => monto,
      PlantillaContableMontoTipos.SubtotalIva16 => CalculateSubtotal(monto),
      PlantillaContableMontoTipos.Iva16 => CalculateIva(monto),
      var montoTipo => throw new ArgumentException($"Tipo de monto no soportado: {montoTipo}.", nameof(line))
    };

    return RoundCurrencyAmount(baseAmount * line.Factor);
  }

  private static string ResolveConcepto(PlantillaContableLineaDto line, string? conceptoTransaccion)
    => NormalizeCode(line.ConceptoTipo) switch
    {
      "TRANSACCION" => conceptoTransaccion?.Trim() ?? string.Empty,
      "FIJO" => line.ConceptoFijo?.Trim() ?? string.Empty,
      var conceptoTipo => throw new ArgumentException($"Tipo de concepto no soportado: {conceptoTipo}.", nameof(line))
    };

  private static decimal CalculateSubtotal(decimal amount)
    => amount == 0m
      ? 0m
      : RoundCurrencyAmount(amount / SubtotalDivisor);

  private static decimal CalculateIva(decimal amount)
    => amount == 0m
      ? 0m
      : RoundCurrencyAmount(amount - CalculateSubtotal(amount));

  private static decimal RoundCurrencyAmount(decimal amount)
    => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

  private static string NormalizeCode(string? value)
  {
    var normalized = value?.Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(normalized))
    {
      throw new ArgumentException("La linea de plantilla tiene un valor requerido vacio.");
    }

    return normalized;
  }
}
