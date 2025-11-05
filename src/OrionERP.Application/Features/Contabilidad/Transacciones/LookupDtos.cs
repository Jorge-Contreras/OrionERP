namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public sealed class LookupInt32Dto
{
  public int Id { get; set; }
  public string? Description { get; set; }

  public string Display => string.IsNullOrWhiteSpace(Description)
    ? Id.ToString()
    : Description!;
}

public sealed class LookupStringDto
{
  public string Id { get; set; } = string.Empty;
  public string? Description { get; set; }

  public string Display => string.IsNullOrWhiteSpace(Description)
    ? Id
    : Description!;
}

public sealed class FormaPagoLookupDto
{
  public string Clave { get; set; } = string.Empty;
  public string? Descripcion { get; set; }

  public string Display => string.IsNullOrWhiteSpace(Descripcion)
    ? Clave
    : $"{Clave} - {Descripcion}";
}
