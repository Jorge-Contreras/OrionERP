using OrionERP.Application.Features.OrdenesTrabajo;
using OrionERP.Infrastructure.Features.OrdenesTrabajo;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.OrdenesTrabajo;

public class OrdenTrabajoRecoveryTests
{
  [Fact]
  public void Permissions_RequireOwnerOrHelperEmployee()
  {
    Assert.False(OrdenTrabajoPermissions.CanExecute(null, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.False(OrdenTrabajoPermissions.CanExecute(0, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.True(OrdenTrabajoPermissions.CanExecute(10, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.True(OrdenTrabajoPermissions.CanExecute(20, ownerEmployeeId: 10, helperEmployeeIds: [20]));
    Assert.False(OrdenTrabajoPermissions.CanExecute(30, ownerEmployeeId: 10, helperEmployeeIds: [20]));
  }

  [Fact]
  public async Task StartWorkOrderAsync_RejectsMissingActorEmployee()
  {
    var connection = new FakeQueryDbConnection();
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    var result = await service.StartWorkOrderAsync(42, "admin-user", actorEmployeeId: null);

    Assert.False(result.Success);
    Assert.Contains("responsable o ayudantes", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(connection.ExecutedCommands);
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Fact]
  public async Task StartWorkOrderAsync_RejectsUnassignedActor()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => false
    };
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    var result = await service.StartWorkOrderAsync(42, "operator-user", actorEmployeeId: 99);

    Assert.False(result.Success);
    Assert.Contains("responsable o ayudantes", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(connection.ExecutedCommands, command => command.CommandText.Contains("OrdenTrabajoParticipante", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Theory]
  [InlineData(1, 0, 0, "Todos los pasos")]
  [InlineData(0, 1, 0, "fotografia requerida")]
  [InlineData(0, 0, 1, "incidencia o no aplica requieren notas")]
  public async Task SubmitForReviewAsync_BlocksInvalidStepState(int pendingSteps, int missingRequiredPhotos, int missingNotes, string expectedMessage)
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT CAST(CASE WHEN EXISTS", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("SELECT Estado FROM dbo.OrdenTrabajo", StringComparison.Ordinal))
        {
          return OrdenTrabajoCodes.EstadoEnProceso;
        }

        if (commandText.Contains("Estado = 'PENDIENTE'", StringComparison.Ordinal))
        {
          return pendingSteps;
        }

        if (commandText.Contains("p.PoliticaFoto = 'REQUERIDA'", StringComparison.Ordinal))
        {
          return missingRequiredPhotos;
        }

        if (commandText.Contains("RequiereNotasEnIncidencia", StringComparison.Ordinal)
          && commandText.Contains("RequiereNotasEnNoAplica", StringComparison.Ordinal))
        {
          return missingNotes;
        }

        return 0;
      }
    };
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    var result = await service.SubmitForReviewAsync(42, "operator-user", actorEmployeeId: 10);

    Assert.False(result.Success);
    Assert.Contains(expectedMessage, result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.True(connection.LastTransaction?.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET Estado = 'EN_REVISION'", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RemoveStepEvidenceAsync_ChecksFirstReviewSubmissionBeforeSoftDelete()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => true,
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    var result = await service.RemoveStepEvidenceAsync(42, 7, 100, "operator-user", actorEmployeeId: 10);

    Assert.True(result.Success);
    var evidenceUpdate = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE ev", StringComparison.Ordinal));
    Assert.Contains("Eliminada = 1", evidenceUpdate.CommandText, StringComparison.Ordinal);
    Assert.Contains("NOT EXISTS", evidenceUpdate.CommandText, StringComparison.Ordinal);
    Assert.Contains("ENVIADA_REVISION", evidenceUpdate.CommandText, StringComparison.Ordinal);
    Assert.True(connection.LastTransaction?.WasCommitted);
  }

  [Fact]
  public async Task LinkTransactionAsync_RejectsCrossRfcTransaction()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (_, _) => false,
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = new OrdenTrabajoService(new FakeQueryConnectionFactory(connection));

    var result = await service.LinkTransactionAsync(42, 9001, "supervisor");

    Assert.False(result.Success);
    Assert.Contains("mismo RFC", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO dbo.OrdenTrabajoTransaccion", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasRolledBack);
  }

  [Fact]
  public void SqlScript_RecreatesDuplicateGuardForCleaningOnly()
  {
    var sql = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/Sql/20260425_ordenes_trabajo_v1.sql"));

    Assert.Contains("DROP INDEX IF EXISTS UX_OrdenTrabajo_OpenCleaningRoomDate", sql, StringComparison.Ordinal);
    Assert.Contains("WHERE Codigo = 'LIMPIEZA'", sql, StringComparison.Ordinal);
    Assert.Contains("ON dbo.OrdenTrabajo (RoomId, FechaProgramada)", sql, StringComparison.Ordinal);
    Assert.Contains("AND CategoriaId = ' + CONVERT(varchar(20), @CleaningCategoryId)", sql, StringComparison.Ordinal);
    Assert.DoesNotContain("ON dbo.OrdenTrabajo (RoomId, FechaProgramada, CategoriaId)", sql, StringComparison.Ordinal);
  }

  [Fact]
  public void DetailQuery_ReturnsOnlyActiveEvidenceAndSubmittedFlag()
  {
    var source = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/OrdenTrabajoService.cs"));

    Assert.Contains("AS HasBeenSubmittedForReview", source, StringComparison.Ordinal);
    Assert.Contains("AND ev.Eliminada = 0", source, StringComparison.Ordinal);
    Assert.Contains("Falta aplicar el esquema de Ordenes de Trabajo", source, StringComparison.Ordinal);
  }

  [Fact]
  public void LegacySeed_FallsBackToSuiteCleaningActivitiesWhenPlantillaHasNoSteps()
  {
    var source = File.ReadAllText(GetRepoFile("src/OrionERP.Infrastructure/Features/OrdenesTrabajo/OrdenTrabajoService.cs"));

    Assert.Contains("legacy_sources", source, StringComparison.Ordinal);
    Assert.Contains("CONCAT('PLANTILLA PARA LIMPIEZA ', room.ROOM_NAME)", source, StringComparison.Ordinal);
    Assert.Contains("room.ROOM_TYPE = 'SUITE'", source, StringComparison.Ordinal);
    Assert.Contains("StepCount DESC", source, StringComparison.Ordinal);
  }

  private static string GetRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OrionERP.sln")))
    {
      directory = directory.Parent;
    }

    if (directory is null)
    {
      throw new InvalidOperationException("Could not locate repository root.");
    }

    return Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
  }
}
