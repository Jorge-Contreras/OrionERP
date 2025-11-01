using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OrionERP.Web.Services;

public interface ITransaccionDetailService
{
  Task<TransaccionDetailDto> GetAsync(int id, CancellationToken ct = default);
}

public sealed record TransaccionDetailDto(
  string? Folio,
  string Rfc,
  DateTime Fecha,
  string Categoria,
  string Concepto,
  string Referencia,
  string? Memo,
  decimal Subtotal,
  decimal Iva,
  decimal Monto,
  string Divisa,
  string Status,
  IReadOnlyList<TransaccionMovimientoDto> Movimientos,
  IReadOnlyList<TransaccionAttachmentDto> Adjuntos,
  IReadOnlyList<TransaccionComprobanteDto> Comprobantes,
  IReadOnlyList<TransaccionCategoriaDto> Categorias);

public sealed record TransaccionMovimientoDto(int Id, string Cuenta, string Concepto, decimal Debe, decimal Haber, string? Memo);
public sealed record TransaccionAttachmentDto(string Nombre, long TamanoBytes, DateTimeOffset CargadoEn);
public sealed record TransaccionComprobanteDto(string Uuid, string Emisor, decimal Total);
public sealed record TransaccionCategoriaDto(string Clave, string Descripcion);

public sealed class FakeTransaccionDetailService : ITransaccionDetailService
{
  private static readonly IReadOnlyList<TransaccionCategoriaDto> DefaultCategorias = new List<TransaccionCategoriaDto>
  {
    new("101-01", "Bancos"),
    new("102-01", "Cuentas por cobrar"),
    new("201-02", "Proveedores nacionales"),
    new("301-01", "Capital social"),
    new("401-05", "Ventas nacionales"),
    new("501-12", "Gastos generales"),
    new("601-07", "Impuestos trasladados"),
    new("701-03", "Impuestos acreditables"),
    new("801-01", "Otros productos"),
    new("901-01", "Otros gastos")
  };

  public Task<TransaccionDetailDto> GetAsync(int id, CancellationToken ct = default)
  {
    var random = new Random(id);
    var fecha = DateTime.Today.AddDays(-random.Next(0, 30));
    var subtotal = Math.Round((decimal)random.NextDouble() * 10000m + 500m, 2);
    var iva = Math.Round(subtotal * 0.16m, 2);
    var monto = subtotal + iva;

    var movimientos = Enumerable.Range(1, 4).Select(index =>
      new TransaccionMovimientoDto(
        index,
        $"{100 + index}-00",
        index switch
        {
          1 => "Registro de ingreso",
          2 => "IVA trasladado",
          3 => "Banco",
          _ => "Contrapartida"
        },
        index % 2 == 0 ? 0m : Math.Round(monto / 2m, 2),
        index % 2 == 0 ? Math.Round(monto / 2m, 2) : 0m,
        index == 2 ? "IVA por pagar" : null)).ToList();

    var adjuntos = new List<TransaccionAttachmentDto>
    {
      new($"Factura_{id}.pdf", 152_320, DateTimeOffset.Now.AddDays(-2)),
      new($"Voucher_{id}.jpg", 83_712, DateTimeOffset.Now.AddDays(-1))
    };

    var comprobantes = new List<TransaccionComprobanteDto>
    {
      new($"UUID-{Guid.NewGuid():N}", "Proveedor Demo", Math.Round(subtotal, 2)),
      new($"UUID-{Guid.NewGuid():N}", "SAT CFDI", Math.Round(monto, 2))
    };

    var dto = new TransaccionDetailDto(
      Folio: $"TRX-{id:0000}",
      Rfc: "ABC123456T78",
      Fecha: fecha,
      Categoria: "Bancos",
      Concepto: "Pago de servicios",
      Referencia: $"REF{id:0000}",
      Memo: "Transacción generada para demostración",
      Subtotal: subtotal,
      Iva: iva,
      Monto: monto,
      Divisa: "MXN",
      Status: "Abierta",
      Movimientos: movimientos,
      Adjuntos: adjuntos,
      Comprobantes: comprobantes,
      Categorias: DefaultCategorias);

    return Task.FromResult(dto);
  }
}
