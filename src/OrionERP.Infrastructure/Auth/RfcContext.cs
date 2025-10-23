namespace OrionERP.Infrastructure.Auth
{
    public interface IRfcContext
    {
        string? CurrentRfc { get; set; }
    }

    public class RfcContext : IRfcContext
    {
        public string? CurrentRfc { get; set; }
    }
}
