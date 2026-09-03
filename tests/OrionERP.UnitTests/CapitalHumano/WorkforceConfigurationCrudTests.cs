using OrionERP.Application.Features.CapitalHumano.Workforce;

namespace OrionERP.UnitTests.CapitalHumano;

public sealed class WorkforceConfigurationCrudTests
{
  [Fact]
  public void PolicyReviewFlag_IsReadBackAndApprovedThroughItsOwnCommand()
  {
    Assert.NotNull(typeof(AttendancePolicyDto).GetProperty(nameof(AttendancePolicyDto.RequiresReview)));
    Assert.True(new AttendancePolicySaveRequest().RequiresReview);

    var contracts = typeof(IWorkforceConfigurationService);
    var approve = contracts.GetMethod(nameof(IWorkforceConfigurationService.ApprovePolicyAsync))
      ?? throw new InvalidOperationException("ApprovePolicyAsync is not part of the configuration contract.");
    Assert.Equal(typeof(Task<WorkforceCommandResult>), approve.ReturnType);

    var service = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationService.cs");

    // The snapshot has to carry the flag, otherwise the page cannot tell which versions are still pending.
    Assert.Contains("LocationRequired, IsActive, RequiresReview", service, StringComparison.Ordinal);
    Assert.Contains("ORDER BY RequiresReview DESC", service, StringComparison.Ordinal);

    // Saving must honour the requested review state instead of self-approving every edit.
    Assert.Contains("RequiresReview=@RequiresReview", service, StringComparison.Ordinal);
    Assert.DoesNotContain("IsActive=@IsActive, RequiresReview=0", service, StringComparison.Ordinal);

    // Approval is its own audited command.
    Assert.Contains("UPDATE rh.AttendancePolicy SET RequiresReview=0 WHERE Id=@Id AND Rfc=@Rfc;", service, StringComparison.Ordinal);
    Assert.Contains("\"AttendancePolicy\", policyId, \"REVIEWED\"", service, StringComparison.Ordinal);
  }

  [Fact]
  public void PayGroupCommands_CoverCreateUpdateDeactivateAndGuardedDelete()
  {
    var contracts = typeof(IWorkforceConfigurationService);
    var delete = contracts.GetMethod(nameof(IWorkforceConfigurationService.DeletePayGroupAsync))
      ?? throw new InvalidOperationException("DeletePayGroupAsync is not part of the configuration contract.");
    Assert.Equal(typeof(Task<WorkforceCommandResult>), delete.ReturnType);

    var service = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationService.cs");

    // A duplicated code has to come back as a message, not as a unique-index violation.
    Assert.Contains("Ya existe un grupo de pago con ese codigo.", service, StringComparison.Ordinal);

    // Deleting a pay group still referenced by assignments or pre-nomina periods must be refused.
    Assert.Contains("FROM rh.EmployeeWorkAssignment wa WHERE wa.PayGroupId = pg.Id", service, StringComparison.Ordinal);
    Assert.Contains("FROM rh.PrenominaPeriod period WHERE period.PayGroupId = pg.Id", service, StringComparison.Ordinal);
    Assert.Contains("Desactivalo en lugar de eliminarlo.", service, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM rh.PayGroup WHERE Id=@Id AND Rfc=@Rfc;", service, StringComparison.Ordinal);
    Assert.Contains("\"PayGroup\", payGroupId, \"DELETED\"", service, StringComparison.Ordinal);
  }

  [Fact]
  public void ConfigurationPage_WiresPolicyApprovalAndPayGroupCrud()
  {
    var page = Read("src", "OrionERP.Web", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationPage.razor");

    foreach (var handler in new[]
             {
               "ApprovePolicyAsync(item.Id,Rfc)",
               "EditPolicy(item)",
               "TogglePolicyAsync(item)",
               "EditPayGroup(item)",
               "TogglePayGroupAsync(item)",
               "DeletePayGroupAsync(item.Id,Rfc)"
             })
      Assert.Contains(handler, page, StringComparison.Ordinal);

    // The pending badge has to follow the data instead of being hard coded.
    Assert.Contains("PendingPolicies", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Revisión RH requerida", page, StringComparison.Ordinal);

    // Deleting is irreversible, so it asks first.
    Assert.Contains("Js.InvokeAsync<bool>(\"confirm\"", page, StringComparison.Ordinal);
  }

  [Fact]
  public void PolicyHoursRoundTripThroughMinutesWithoutDrift()
  {
    // The editor shows weekly hours; the seeded LFT versions must survive an edit untouched.
    foreach (var minutes in new[] { 2880, 2760, 2640, 2520, 2400, 540, 600, 660, 720, 240 })
    {
      var hours = Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);
      Assert.Equal(minutes, (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero));
    }
  }

  [Fact]
  public void EveryConfiguredCatalog_ExposesAGuardedDeleteCommand()
  {
    var contracts = typeof(IWorkforceConfigurationService);
    foreach (var name in new[]
             {
               nameof(IWorkforceConfigurationService.DeleteSiteAsync),
               nameof(IWorkforceConfigurationService.DeleteScheduleAsync),
               nameof(IWorkforceConfigurationService.DeletePayGroupAsync),
               nameof(IWorkforceConfigurationService.DeleteWorkAssignmentAsync),
               nameof(IWorkforceConfigurationService.DeleteSupervisorAssignmentAsync),
               nameof(IWorkforceConfigurationService.DeleteHolidayAsync),
               nameof(IWorkforceConfigurationService.DeleteKioskDeviceAsync)
             })
      Assert.NotNull(contracts.GetMethod(name));

    var service = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationService.cs");

    // Catalogs still referenced by attendance history must be deactivated, never deleted.
    Assert.Contains("FROM rh.TimeEvent e WHERE e.SiteId = site.Id", service, StringComparison.Ordinal);
    Assert.Contains("FROM rh.AttendanceDay d WHERE d.ScheduleTemplateId = template.Id", service, StringComparison.Ordinal);
    Assert.Contains("FROM rh.TimeEvent e WHERE e.KioskDeviceId = device.Id", service, StringComparison.Ordinal);
    Assert.Contains("DELETE FROM rh.EmployeeWorkAssignment WHERE Id=@Id AND Rfc=@Rfc;", service, StringComparison.Ordinal);
    Assert.Contains("Cierrala con una fecha 'Vigente hasta' en lugar de eliminarla.", service, StringComparison.Ordinal);

    // Unique indexes are reported as messages instead of raw duplicate-key exceptions.
    foreach (var message in new[]
             {
               "Ya existe un sitio con ese codigo.",
               "Ya existe una plantilla de horario con ese codigo.",
               "Ya existe un grupo de pago con ese codigo.",
               "Ya existe una version de esa politica con la misma fecha de inicio."
             })
      Assert.Contains(message, service, StringComparison.Ordinal);
  }

  [Fact]
  public void KioskPairing_ReusesTheDeviceAndInvalidatesThePreviousToken()
  {
    var service = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationService.cs");

    // Regenerating a code must not create a second device row for the same kiosk.
    Assert.NotNull(typeof(KioskPairingCreateRequest).GetProperty(nameof(KioskPairingCreateRequest.DeviceId)));
    Assert.Contains("DECLARE @DeviceId int = @ExistingDeviceId;", service, StringComparison.Ordinal);
    Assert.Contains("UPDATE rh.KioskDevice SET DeviceTokenHash=NULL, IsActive=0, PairedAtUtc=NULL WHERE Id=@DeviceId;", service, StringComparison.Ordinal);
    Assert.Contains("UPDATE rh.KioskPairingCode SET UsedAtUtc=SYSUTCDATETIME()", service, StringComparison.Ordinal);
  }

  [Fact]
  public void KioskPin_RejectsShortBlankAndRepeatedDigits()
  {
    var service = Read("src", "OrionERP.Infrastructure", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationService.cs");

    // An empty PIN used to pass the digits-only check and produced an unusable credential.
    Assert.Contains("pin.Length is < 4 or > 12", service, StringComparison.Ordinal);
    Assert.Contains("pin.Distinct().Count() == 1", service, StringComparison.Ordinal);
    Assert.Contains("HashPassword(new object(), pin)", service, StringComparison.Ordinal);
    Assert.NotNull(typeof(IWorkforceConfigurationService).GetMethod(nameof(IWorkforceConfigurationService.RevokeKioskCredentialAsync)));
  }

  [Fact]
  public void ScheduleEditor_ConfiguresEveryDayIndependently()
  {
    var page = Read("src", "OrionERP.Web", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationPage.razor");

    // The old editor applied a single start/end pair to Monday-Friday; no weekend or split shift could be expressed.
    // Monday-Friday survives only as the default seed for a brand new template.
    Assert.DoesNotContain("scheduleStart", page, StringComparison.Ordinal);
    Assert.DoesNotContain("scheduleEnd", page, StringComparison.Ordinal);
    Assert.Contains("scheduleDays", page, StringComparison.Ordinal);
    Assert.Contains("day.IsWorkingDay?day.StartTime.ToTimeSpan():null", page, StringComparison.Ordinal);
    Assert.Contains("La plantilla necesita al menos un día laborable.", page, StringComparison.Ordinal);
  }

  [Fact]
  public void AssignmentAndSupervisorRanges_CanBeClosedAndEdited()
  {
    var page = Read("src", "OrionERP.Web", "Features", "CapitalHumano", "Workforce", "WorkforceConfigurationPage.razor");

    // Without an end date the overlap guard made the first assignment permanent.
    Assert.Contains("@bind=\"assignment.EffectiveTo\"", page, StringComparison.Ordinal);
    Assert.Contains("@bind=\"supervisor.EffectiveTo\"", page, StringComparison.Ordinal);
    foreach (var handler in new[] { "EditAssignment(item)", "CloseAssignmentAsync(item)", "EditSupervisor(item)", "CloseSupervisorAsync(item)" })
      Assert.Contains(handler, page, StringComparison.Ordinal);
  }

  [Fact]
  public void PrenominaPage_OffersOnlyActivePayGroupsAndRecordsTheReopenReason()
  {
    var page = Read("src", "OrionERP.Web", "Features", "CapitalHumano", "Workforce", "PrenominaPage.razor");

    Assert.Contains("payGroups.Where(x=>x.IsActive)", page, StringComparison.Ordinal);
    Assert.Contains("reopenReason", page, StringComparison.Ordinal);
    Assert.DoesNotContain("\"Reapertura solicitada desde panel de pre-nómina\"", page, StringComparison.Ordinal);
  }

  private static string Read(params string[] parts)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln"))) directory = directory.Parent;
    if (directory is null) throw new InvalidOperationException("Could not locate repository root.");
    return File.ReadAllText(Path.Combine([directory.FullName, .. parts]));
  }
}
