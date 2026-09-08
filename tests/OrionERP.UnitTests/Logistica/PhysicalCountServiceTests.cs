using System.Data;
using OrionERP.Application.Features.Logistica.PhysicalCounts;
using OrionERP.Infrastructure.Features.Logistica.PhysicalCounts;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.Logistica;

public class PhysicalCountServiceTests
{
  [Fact]
  public async Task GetSessionsAsync_MapsCountedLineProgress()
  {
    var resultTable = new DataTable();
    resultTable.Columns.Add("Id", typeof(int));
    resultTable.Columns.Add("LineCount", typeof(int));
    resultTable.Columns.Add("CountedLineCount", typeof(int));
    resultTable.Rows.Add(51, 8, 3);

    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = (_, _) => resultTable
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.GetSessionsAsync();

    var session = Assert.Single(result);
    Assert.Equal(8, session.LineCount);
    Assert.Equal(3, session.CountedLineCount);
    var commandText = Assert.Single(connection.ExecutedCommands).CommandText;
    Assert.Contains("s.[Status] = 'Recount'", commandText, StringComparison.Ordinal);
    Assert.Contains("activePlanLine.Id IS NOT NULL AND line.CountedQuantity IS NOT NULL", commandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task GetSessionAsync_MapsLifecycleAndMaterialCapturesIntoAuditTrail()
  {
    var startedAt = new DateTime(2026, 8, 30, 13, 0, 0);
    var countedAt = startedAt.AddMinutes(15);
    var submittedAt = startedAt.AddMinutes(30);
    var results = new DataSet();

    var sessionTable = new DataTable();
    sessionTable.Columns.Add("Id", typeof(int));
    sessionTable.Columns.Add("SessionCode", typeof(string));
    sessionTable.Columns.Add("CreatedAt", typeof(DateTime));
    sessionTable.Columns.Add("CreatedBy", typeof(string));
    sessionTable.Rows.Add(51, "PC-000051", startedAt, "jefe@orionerp.local");
    results.Tables.Add(sessionTable);

    var lineTable = new DataTable();
    lineTable.Columns.Add("Id", typeof(int));
    lineTable.Columns.Add("MaterialId", typeof(int));
    results.Tables.Add(lineTable);

    var attachmentTable = new DataTable();
    attachmentTable.Columns.Add("Id", typeof(int));
    attachmentTable.Columns.Add("PhysicalCountLineId", typeof(int));
    results.Tables.Add(attachmentTable);

    var auditTable = new DataTable();
    auditTable.Columns.Add("EventType", typeof(string));
    auditTable.Columns.Add("OccurredAt", typeof(DateTime));
    auditTable.Columns.Add("PerformedBy", typeof(string));
    auditTable.Columns.Add("MaterialId", typeof(int));
    auditTable.Columns.Add("MaterialCode", typeof(string));
    auditTable.Columns.Add("MaterialDescription", typeof(string));
    auditTable.Columns.Add("LocationName", typeof(string));
    auditTable.Columns.Add("ExpectedQuantity", typeof(decimal));
    auditTable.Columns.Add("CountedQuantity", typeof(decimal));
    auditTable.Columns.Add("Details", typeof(string));
    auditTable.Rows.Add(PhysicalCountAuditEventTypes.Submitted, submittedAt, "contador@orionerp.local", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
    auditTable.Rows.Add(PhysicalCountAuditEventTypes.LineCounted, countedAt, "contador@orionerp.local", 810, "MAT-810", "Aceite", "Estante A-3", 6m, 5m, "Envase abierto.");
    auditTable.Rows.Add(PhysicalCountAuditEventTypes.SessionStarted, startedAt, "jefe@orionerp.local", DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, "Conteo mensual.");
    results.Tables.Add(auditTable);

    var scopeMaterialTable = new DataTable();
    scopeMaterialTable.Columns.Add("MaterialId", typeof(int));
    scopeMaterialTable.Columns.Add("MaterialCode", typeof(string));
    scopeMaterialTable.Columns.Add("MaterialDescription", typeof(string));
    scopeMaterialTable.Columns.Add("LineCount", typeof(int));
    scopeMaterialTable.Columns.Add("LocationCount", typeof(int));
    scopeMaterialTable.Rows.Add(810, "MAT-810", "Aceite", 4, 4);
    results.Tables.Add(scopeMaterialTable);

    var connection = new FakeQueryDbConnection
    {
      MultiResultReaderFactory = (_, _) => results
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.GetSessionAsync(51);

    Assert.NotNull(result);
    Assert.Equal(3, result!.AuditEvents.Count);
    var capture = Assert.Single(result.AuditEvents, auditEvent => auditEvent.EventType == PhysicalCountAuditEventTypes.LineCounted);
    Assert.Equal("contador@orionerp.local", capture.PerformedBy);
    Assert.Equal(countedAt, capture.OccurredAt);
    Assert.Equal("Aceite", capture.MaterialDescription);
    Assert.Equal(5m, capture.CountedQuantity);

    // Sin la ubicación, un conteo por material repite la misma línea una vez por parada.
    Assert.Equal("Estante A-3", capture.LocationName);

    var scopeMaterial = Assert.Single(result.Materials);
    Assert.Equal(810, scopeMaterial.MaterialId);
    Assert.Equal(4, scopeMaterial.LocationCount);

    var commandText = Assert.Single(connection.ExecutedCommands).CommandText;
    Assert.Contains("recountLine.PreviousCapturedAt", commandText, StringComparison.Ordinal);
    Assert.Contains("'EvidenceAdded'", commandText, StringComparison.Ordinal);
    Assert.Contains("ORDER BY audit.OccurredAt DESC", commandText, StringComparison.Ordinal);
    Assert.Contains("logistica.PhysicalCountSessionMaterial sessionMaterial", commandText, StringComparison.Ordinal);
    Assert.Contains("ORDER BY line.CountSequence", commandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CaptureLineAsync_RejectsStaleSaveAndDoesNotInsertAttachment()
  {
    var originalCapturedAt = new DateTime(2026, 8, 27, 14, 0, 0, DateTimeKind.Utc);
    var newerCapturedAt = originalCapturedAt.AddMinutes(2);
    var lockedLineTable = new DataTable();
    lockedLineTable.Columns.Add("Id", typeof(int));
    lockedLineTable.Columns.Add("CapturedAt", typeof(DateTime));
    lockedLineTable.Rows.Add(10, newerCapturedAt);

    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Draft" : null,
      ReaderResultFactory = (commandText, _) => commandText.Contains("WITH (UPDLOCK, HOLDLOCK)", StringComparison.Ordinal)
        ? lockedLineTable
        : new DataTable(),
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CaptureLineAsync(new PhysicalCountLineCaptureRequest
    {
      SessionId = 51,
      LineId = 10,
      ExpectedCapturedAt = originalCapturedAt,
      CountedQuantity = 12.5m,
      CapturedBy = "contador@orionerp.local",
      AttachmentBytes = [1, 2, 3],
      AttachmentFileName = "evidencia.jpg",
      AttachmentContentType = "image/jpeg"
    });

    Assert.False(result.Success);
    Assert.Equal("Otro empleado actualizó este material. Se recargó el conteo para proteger su captura.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET CountedQuantity = @CountedQuantity", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountAttachment", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RequestRecountAsync_Fails_WhenNoLinesAreSelected()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local"
    });

    Assert.False(result.Success);
    Assert.Equal("Selecciona al menos una línea para enviar a reconteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task RequestRecountAsync_Fails_WhenIssueCodeIsInvalid()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local",
      Lines =
      [
        new PhysicalCountRecountLineRequest
        {
          LineId = 10,
          IssueCode = "Unknown",
          Reason = "La cantidad capturada no cuadra."
        }
      ]
    });

    Assert.False(result.Success);
    Assert.Equal("Selecciona un tipo de incidencia válido para cada línea de reconteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task RequestRecountAsync_Fails_WhenSessionIsNotSubmittedOrApproved()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Draft" : null
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local",
      Lines =
      [
        new PhysicalCountRecountLineRequest
        {
          LineId = 10,
          IssueCode = PhysicalCountRecountIssueCodes.QuantityMismatch,
          Reason = "La cantidad capturada no cuadra."
        }
      ]
    });

    Assert.False(result.Success);
    Assert.Equal("Solo las sesiones enviadas o aprobadas pueden enviarse a reconteo.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlan", StringComparison.Ordinal));
  }

  [Fact]
  public async Task RequestRecountAsync_CreatesPlanSnapshotsClearsLinesAndMarksSessionForRecount()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT [Status]", StringComparison.Ordinal))
        {
          return "Approved";
        }

        if (commandText.Contains("FROM logistica.PhysicalCountRecountPlan", StringComparison.Ordinal)
          && commandText.Contains("COUNT(*)", StringComparison.Ordinal))
        {
          return 0;
        }

        if (commandText.Contains("FROM logistica.PhysicalCountLine", StringComparison.Ordinal)
          && commandText.Contains("COUNT(*)", StringComparison.Ordinal))
        {
          return 2;
        }

        if (commandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlan", StringComparison.Ordinal))
        {
          return 701;
        }

        return null;
      }
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.RequestRecountAsync(new PhysicalCountRecountRequest
    {
      SessionId = 51,
      RequestedBy = "admin@orionerp.local",
      Lines =
      [
        new PhysicalCountRecountLineRequest
        {
          LineId = 10,
          IssueCode = PhysicalCountRecountIssueCodes.QuantityMismatch,
          Reason = "La cantidad capturada no cuadra."
        },
        new PhysicalCountRecountLineRequest
        {
          LineId = 11,
          IssueCode = PhysicalCountRecountIssueCodes.EvidenceMissing,
          Reason = "Falta evidencia del conteo."
        }
      ]
    });

    Assert.True(result.Success);
    Assert.Equal(51, result.EntityId);
    Assert.Equal("Sesión enviada a reconteo correctamente.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var planInsert = Assert.Single(connection.ExecutedCommands, command =>
      command.CommandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlan", StringComparison.Ordinal)
      && !command.CommandText.Contains("PhysicalCountRecountPlanLine", StringComparison.Ordinal));
    AssertParameter(planInsert.Parameters, "@SessionId", 51);
    AssertParameter(planInsert.Parameters, "@RequestedBy", "admin@orionerp.local");

    var planLineInserts = connection.ExecutedCommands
      .Where(command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountRecountPlanLine", StringComparison.Ordinal))
      .ToList();
    Assert.Equal(2, planLineInserts.Count);
    Assert.All(planLineInserts, command => AssertParameter(command.Parameters, "@RecountPlanId", 701));

    var lineClears = connection.ExecutedCommands
      .Where(command => command.CommandText.Contains("SET CountedQuantity = NULL", StringComparison.Ordinal))
      .ToList();
    Assert.Equal(2, lineClears.Count);

    var sessionUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Recount'", StringComparison.Ordinal));
    AssertParameter(sessionUpdate.Parameters, "@SessionId", 51);
  }

  [Fact]
  public async Task CancelSessionAsync_Fails_WhenReasonIsMissing()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CancelSessionAsync(new PhysicalCountCancelRequest
    {
      SessionId = 51,
      CanceledBy = "admin@orionerp.local",
      Reason = " "
    });

    Assert.False(result.Success);
    Assert.Equal("Captura una razón para cancelar el conteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task CancelSessionAsync_Fails_WhenSessionIsPosted()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Posted" : null
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CancelSessionAsync(new PhysicalCountCancelRequest
    {
      SessionId = 51,
      CanceledBy = "admin@orionerp.local",
      Reason = "Conteo duplicado."
    });

    Assert.False(result.Success);
    Assert.Equal("Las sesiones contabilizadas no se pueden cancelar.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Canceled'", StringComparison.Ordinal));
  }

  [Fact]
  public async Task CancelSessionAsync_SoftCancelsUnpostedSessionWithoutDeletingRows()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) => commandText.Contains("SELECT [Status]", StringComparison.Ordinal) ? "Approved" : null,
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CancelSessionAsync(new PhysicalCountCancelRequest
    {
      SessionId = 51,
      CanceledBy = "admin@orionerp.local",
      Reason = "Conteo creado por error."
    });

    Assert.True(result.Success);
    Assert.Equal(51, result.EntityId);
    Assert.Equal("Sesión cancelada correctamente.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountLine", StringComparison.Ordinal));
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE attachment", StringComparison.Ordinal));

    var sessionUpdate = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("SET [Status] = 'Canceled'", StringComparison.Ordinal));
    AssertParameter(sessionUpdate.Parameters, "@SessionId", 51);
    AssertParameter(sessionUpdate.Parameters, "@CanceledBy", "admin@orionerp.local");
    AssertParameter(sessionUpdate.Parameters, "@CancelReason", "Conteo creado por error.");
  }

  [Fact]
  public async Task SubmitSessionAsync_ClosesActiveRecountPlan_WhenSessionIsInRecount()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("SELECT [Status]", StringComparison.Ordinal))
        {
          return "Recount";
        }

        if (commandText.Contains("FROM logistica.PhysicalCountLine", StringComparison.Ordinal)
          && commandText.Contains("COUNT(*)", StringComparison.Ordinal))
        {
          return 0;
        }

        return null;
      },
      NonQueryResultFactory = (_, _) => 1
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.SubmitSessionAsync(51, "contador@orionerp.local");

    Assert.True(result.Success);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var recountPlanClose = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("UPDATE logistica.PhysicalCountRecountPlan", StringComparison.Ordinal));
    AssertParameter(recountPlanClose.Parameters, "@SessionId", 51);
    AssertParameter(recountPlanClose.Parameters, "@SubmittedBy", "contador@orionerp.local");
  }

  [Fact]
  public async Task DeleteDraftSessionAsync_Fails_WhenSessionDoesNotExist()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => null
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftSessionAsync(51);

    Assert.False(result.Success);
    Assert.Equal("La sesión de conteo no existe.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountLine", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DeleteDraftSessionAsync_Fails_WhenSessionIsNotDraft()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => "Posted"
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftSessionAsync(51);

    Assert.False(result.Success);
    Assert.Equal("Solo las sesiones en borrador se pueden cancelar o eliminar.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasRolledBack);
    Assert.DoesNotContain(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE attachment", StringComparison.Ordinal));
  }

  [Fact]
  public async Task DeleteDraftSessionAsync_DeletesAttachmentsLinesAndSession()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = (_, _) => "Draft",
      NonQueryResultFactory = (commandText, _) => commandText.Contains("DELETE FROM logistica.PhysicalCountSession", StringComparison.Ordinal) ? 1 : 2
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeleteDraftSessionAsync(51);

    Assert.True(result.Success);
    Assert.Equal(51, result.EntityId);
    Assert.Equal("Sesión en borrador eliminada correctamente.", result.Message);
    Assert.NotNull(connection.LastTransaction);
    Assert.True(connection.LastTransaction!.WasCommitted);

    var attachmentDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE attachment", StringComparison.Ordinal));
    AssertParameter(attachmentDelete.Parameters, "@SessionId", 51);

    var lineDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountLine", StringComparison.Ordinal));
    AssertParameter(lineDelete.Parameters, "@SessionId", 51);

    // Los materiales del alcance apuntan a la sesión: si no se purgan, la llave foránea impide borrarla.
    var scopeDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountSessionMaterial", StringComparison.Ordinal));
    AssertParameter(scopeDelete.Parameters, "@SessionId", 51);

    var sessionDelete = Assert.Single(connection.ExecutedCommands, command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountSession WHERE", StringComparison.Ordinal));
    AssertParameter(sessionDelete.Parameters, "@SessionId", 51);

    // La sesión se borra al final, cuando ya nada la referencia.
    Assert.Equal(
      connection.ExecutedCommands.Count - 1,
      connection.ExecutedCommands.ToList().FindIndex(command => command.CommandText.Contains("DELETE FROM logistica.PhysicalCountSession WHERE", StringComparison.Ordinal)));
  }

  [Fact]
  public async Task CreateSessionAsync_MaterialScope_WalksEveryLocationInRouteOrder()
  {
    var connection = BuildScopeConnection(lineCount: 6);
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material,
      MaterialIds = [7113],
      CreatedBy = "jefe@orionerp.local"
    });

    Assert.True(result.Success);
    Assert.Equal(42, result.EntityId);

    var lineInsert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountLine", StringComparison.Ordinal));

    // Sin restriccion de ubicacion el recorrido abarca todo el almacen.
    AssertParameter(lineInsert.Parameters, "@LocationId", DBNull.Value);
    AssertParameter(lineInsert.Parameters, "@HasMaterialFilter", true);

    // El orden de recorrido es ubicacion primero: sala, codigo de ubicacion y luego material.
    Assert.Contains("ORDER BY room.ROOM_NAME, loc.LocationCode, material.[Description]", lineInsert.CommandText, StringComparison.Ordinal);
    Assert.Contains("CountSequence", lineInsert.CommandText, StringComparison.Ordinal);

    var scopeInsert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountSessionMaterial", StringComparison.Ordinal));
    AssertParameter(scopeInsert.Parameters, "@SessionId", 42);

    var sessionInsert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountSession\r\n", StringComparison.Ordinal)
              || command.CommandText.Contains("INSERT INTO logistica.PhysicalCountSession\n", StringComparison.Ordinal));
    AssertParameter(sessionInsert.Parameters, "@ScopeType", PhysicalCountSessionScopeTypes.Material);
    Assert.True(connection.LastTransaction!.WasCommitted);
  }

  [Fact]
  public async Task CreateSessionAsync_LocationScope_KeepsSubtreeGenerator()
  {
    var connection = BuildScopeConnection(lineCount: 12);
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      LocationId = 5,
      CreatedBy = "jefe@orionerp.local"
    });

    Assert.True(result.Success);

    var lineInsert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountLine", StringComparison.Ordinal));

    AssertParameter(lineInsert.Parameters, "@LocationId", 5);
    AssertParameter(lineInsert.Parameters, "@HasMaterialFilter", false);
    Assert.Contains("JOIN LocationScope parent", lineInsert.CommandText, StringComparison.Ordinal);

    // El generador historico nunca filtro por cantidad; excluir los ceros cambiaria lo que se cuenta.
    Assert.DoesNotContain("sb.Quantity <> 0", lineInsert.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateSessionAsync_MaterialScope_VisitsLocationsTheSystemBelievesEmpty()
  {
    var connection = BuildScopeConnection(lineCount: 3);
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material,
      MaterialIds = [7113]
    });

    var lineInsert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountLine", StringComparison.Ordinal));

    // Un conteo por material afirma donde esta el material, asi que tiene que poder probar el vacio:
    // el unico filtro sobre los saldos sigue siendo el borrado logico.
    Assert.DoesNotContain("sb.Quantity <> 0", lineInsert.CommandText, StringComparison.Ordinal);
    Assert.Contains("ISNULL(sb.IsRemoved, 0) = 0", lineInsert.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateSessionAsync_CapsLocationsPerMaterial_PreferringTheStalest()
  {
    var connection = BuildScopeConnection(lineCount: 2);
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material,
      MaterialIds = [7113],
      MaxLocationsPerMaterial = 2
    });

    var lineInsert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountLine", StringComparison.Ordinal));

    AssertParameter(lineInsert.Parameters, "@MaxLocationsPerMaterial", 2);
    Assert.Contains("PARTITION BY sb.MaterialId", lineInsert.CommandText, StringComparison.Ordinal);
    Assert.Contains("CASE WHEN sb.LastCountedAt IS NULL THEN 0 ELSE 1 END", lineInsert.CommandText, StringComparison.Ordinal);
    Assert.Contains("candidate.MaterialRank <= @MaxLocationsPerMaterial", lineInsert.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task CreateSessionAsync_BlocksWhenAnOpenSessionAlreadyClaimsTheSameBalances()
  {
    var conflicts = new DataTable();
    conflicts.Columns.Add("SessionCode", typeof(string));
    conflicts.Rows.Add("PC-000012");

    var connection = BuildScopeConnection(lineCount: 6, conflictTable: conflicts);
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material,
      MaterialIds = [7113]
    });

    Assert.False(result.Success);
    Assert.Contains("PC-000012", result.Message, StringComparison.Ordinal);
    Assert.True(connection.LastTransaction!.WasRolledBack);

    // Dos sesiones sobre el mismo saldo significan que la segunda en aplicarse pisa a la primera.
    Assert.DoesNotContain(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO logistica.PhysicalCountLine", StringComparison.Ordinal));
  }

  [Fact]
  public async Task CreateSessionAsync_EmptyMaterialScope_SaysMaterialsNotLocation()
  {
    var connection = BuildScopeConnection(lineCount: 0);
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material,
      MaterialIds = [7113]
    });

    Assert.False(result.Success);
    Assert.Equal("Los materiales seleccionados no tienen existencias registradas en ninguna ubicación.", result.Message);
    Assert.True(connection.LastTransaction!.WasRolledBack);
  }

  [Fact]
  public async Task CreateSessionAsync_MaterialScope_RequiresAtLeastOneMaterial()
  {
    var connection = new FakeQueryDbConnection();
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var result = await service.CreateSessionAsync(new PhysicalCountSessionCreateRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material
    });

    Assert.False(result.Success);
    Assert.Equal("Selecciona al menos un material para el conteo.", result.Message);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task PreviewScopeAsync_ReportsRouteSizeAndOpenConflicts()
  {
    var results = new DataSet();

    var totals = new DataTable();
    totals.Columns.Add("LineCount", typeof(int));
    totals.Columns.Add("LocationCount", typeof(int));
    totals.Columns.Add("MaterialCount", typeof(int));
    totals.Rows.Add(9, 6, 2);
    results.Tables.Add(totals);

    var materials = new DataTable();
    materials.Columns.Add("MaterialId", typeof(int));
    materials.Columns.Add("MaterialCode", typeof(string));
    materials.Columns.Add("MaterialDescription", typeof(string));
    materials.Columns.Add("LocationCount", typeof(int));
    materials.Columns.Add("TotalQuantity", typeof(decimal));
    materials.Rows.Add(7113, "7113", "Tornillo hex", 6, 148m);
    results.Tables.Add(materials);

    var conflicts = new DataTable();
    conflicts.Columns.Add("SessionId", typeof(int));
    conflicts.Columns.Add("SessionCode", typeof(string));
    conflicts.Columns.Add("Status", typeof(string));
    conflicts.Columns.Add("MaterialCode", typeof(string));
    conflicts.Columns.Add("MaterialDescription", typeof(string));
    conflicts.Columns.Add("LocationName", typeof(string));
    conflicts.Columns.Add("OverlappingLineCount", typeof(int));
    conflicts.Rows.Add(12, "PC-000012", "Draft", "7113", "Tornillo hex", "Estante A-3", 1);
    results.Tables.Add(conflicts);

    var connection = new FakeQueryDbConnection
    {
      MultiResultReaderFactory = (_, _) => results
    };
    var service = new PhysicalCountService(new FakeQueryConnectionFactory(connection));

    var preview = await service.PreviewScopeAsync(new PhysicalCountScopePreviewRequest
    {
      ScopeType = PhysicalCountSessionScopeTypes.Material,
      MaterialIds = [7113, 8420]
    });

    Assert.Equal(9, preview.LineCount);
    Assert.Equal(6, preview.LocationCount);
    Assert.Equal(2, preview.MaterialCount);
    Assert.Equal(6, Assert.Single(preview.Materials).LocationCount);
    Assert.True(preview.HasConflicts);
    Assert.Equal("PC-000012", Assert.Single(preview.Conflicts).SessionCode);
  }

  /// <summary>
  /// Encadena las respuestas que espera <c>CreateSessionAsync</c>: validaciones, guardia de
  /// solapamiento, alta de la sesion y conteo final de renglones.
  /// </summary>
  private static FakeQueryDbConnection BuildScopeConnection(int lineCount, DataTable? conflictTable = null)
    => new()
    {
      ScalarResultFactory = (commandText, _) =>
      {
        if (commandText.Contains("FROM logistica.Location", StringComparison.Ordinal)
          && commandText.Contains("THEN 1 ELSE 0 END AS bit", StringComparison.Ordinal))
        {
          return true;
        }

        if (commandText.Contains("FROM logistica.Material", StringComparison.Ordinal)
          && commandText.Contains("AND IsActive = 1", StringComparison.Ordinal))
        {
          return 1;
        }

        if (commandText.Contains("INSERT INTO logistica.PhysicalCountSession", StringComparison.Ordinal))
        {
          return 42;
        }

        if (commandText.Contains("FROM logistica.PhysicalCountLine WHERE SessionId", StringComparison.Ordinal))
        {
          return lineCount;
        }

        return null;
      },
      ReaderResultFactory = (_, _) =>
      {
        if (conflictTable is not null)
        {
          return conflictTable;
        }

        var empty = new DataTable();
        empty.Columns.Add("SessionCode", typeof(string));
        return empty;
      }
    };

  private static void AssertParameter(IReadOnlyList<FakeQueryParameter> parameters, string name, object expectedValue)
  {
    var parameter = Assert.Single(parameters, parameter => HasParameterName(parameter, name));
    Assert.Equal(expectedValue, parameter.Value);
  }

  private static bool HasParameterName(FakeQueryParameter parameter, string expectedName)
    => string.Equals(parameter.Name.TrimStart('@'), expectedName.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
}
