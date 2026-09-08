namespace OrionERP.Application.Features.Reservaciones;

public interface IHospitalityAdministrationAccessValidator
{
  Task EnsureAuthorizedAsync(string actorUserId, string companyRfc, CancellationToken ct = default);
}
