namespace OrionERP.Application.Features.Contabilidad.Transacciones;

public class TransaccionCreateResult
{
    public int NewTransaccionId { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }

    public static TransaccionCreateResult Ok(int newTransaccionId, string? message = null)
        => new() { Success = true, NewTransaccionId = newTransaccionId, Message = message };

    public static TransaccionCreateResult Fail(string message)
        => new() { Success = false, Message = message };
}
