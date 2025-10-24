using System.Threading.Tasks;

namespace OrionERP.Application.Features.Rfcs.Contracts
{
  public interface ISatRfcProfileRepository
  {
    Task UpsertAsync(SatRfcProfileUpsert dto);
    Task<SatRfcProfile?> GetAsync(string rfc);
  }
}
