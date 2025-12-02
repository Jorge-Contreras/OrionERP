namespace OrionERP.Web.Shared;

public sealed record CuentaContableSelection
{
    public int? Id { get; init; }
    public string? Rfc { get; init; }
    public string? Nivel1 { get; set; }
    public string? Nivel2 { get; set; }
    public string? Nivel3 { get; set; }
    public string? Descripcion { get; set; }

    public bool HasNivel1 => !string.IsNullOrWhiteSpace(Nivel1);
    public bool HasNivel2 => HasNivel1 && !string.IsNullOrWhiteSpace(Nivel2);
    public bool HasNivel3 => HasNivel2 && !string.IsNullOrWhiteSpace(Nivel3);
}
