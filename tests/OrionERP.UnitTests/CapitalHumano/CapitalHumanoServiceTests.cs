using System.Data;
using OrionERP.Application.Features.CapitalHumano;
using OrionERP.Infrastructure.Features.CapitalHumano;
using OrionERP.UnitTests.Common;

namespace OrionERP.UnitTests.CapitalHumano;

public class CapitalHumanoServiceTests
{
  [Fact]
  public async Task GetEmployeesAsync_ScopesByCompanyRfcNotWorkerRfc()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) => Table(["Id"])
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    await service.GetEmployeesAsync(new CapitalHumanoFilter
    {
      Rfc = "OHM191112Q26",
      SearchText = "XAXX010101000",
      Take = 25
    });

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("WHERE ch.RFC = @Rfc", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("ch.RFC_Capital_Humano LIKE @Search", command.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("@RfcAND", command.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("WHERE ch.RFC_Capital_Humano", command.CommandText, StringComparison.Ordinal);
    Assert.Contains(command.Parameters, parameter => string.Equals(parameter.Name, "Rfc", StringComparison.OrdinalIgnoreCase)
      && string.Equals(parameter.Value?.ToString(), "OHM191112Q26", StringComparison.Ordinal));
  }

  [Fact]
  public async Task GetEmployeesAsync_SeparatesBaseFilterFromOrdering()
  {
    var connection = new FakeQueryDbConnection
    {
      ReaderResultFactory = static (_, _) => Table(["Id"])
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    await service.GetEmployeesAsync(new CapitalHumanoFilter
    {
      Rfc = "OHM191112Q26",
      Take = 25
    });

    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("WHERE ch.RFC = @Rfc", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("ORDER BY", command.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("@RfcORDER", command.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SaveEmployeeAsync_RejectsInvalidWorkerRfcBeforeSql()
  {
    var connection = new FakeQueryDbConnection();
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveEmployeeAsync(CreateValidRequest(workerRfc: "OHM191112Q26"));

    Assert.False(result.Success);
    Assert.Contains("persona fisica", result.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(connection.ExecutedCommands);
  }

  [Fact]
  public async Task SaveEmployeeAsync_CreatesWithGeneratedIdAndBlankLegacyCredentialFields()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (commandText, _) => commandText.Contains("MAX(ID)", StringComparison.Ordinal) ? 84 : null,
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    var result = await service.SaveEmployeeAsync(CreateValidRequest(workerRfc: "XAXX010101000"));

    Assert.True(result.Success);
    Assert.Equal(84, result.EntityId);

    var idCommand = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("MAX(ID)", StringComparison.Ordinal)
        && command.CommandText.Contains("UPDLOCK, HOLDLOCK", StringComparison.Ordinal));
    Assert.NotNull(idCommand);

    var insert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO dbo.Capital_Humano", StringComparison.Ordinal));
    Assert.Contains("Contrasena_Acceso", insert.CommandText, StringComparison.Ordinal);
    Assert.Contains("Usuario_Acceso", insert.CommandText, StringComparison.Ordinal);
    Assert.Contains("''", insert.CommandText, StringComparison.Ordinal);
    Assert.Contains(insert.Parameters, parameter => string.Equals(parameter.Name, "Id", StringComparison.OrdinalIgnoreCase)
      && Convert.ToInt32(parameter.Value) == 84);
    Assert.Contains(insert.Parameters, parameter => string.Equals(parameter.Name, "RFC_Capital_Humano", StringComparison.OrdinalIgnoreCase)
      && string.Equals(parameter.Value?.ToString(), "XAXX010101000", StringComparison.Ordinal));
    Assert.Contains(insert.Parameters, parameter => string.Equals(parameter.Name, "CURP", StringComparison.OrdinalIgnoreCase)
      && string.Equals(parameter.Value?.ToString(), "CUGO880913HMCRRS09", StringComparison.Ordinal));
    Assert.True(connection.LastTransaction?.WasCommitted);
  }

  [Fact]
  public async Task SaveEmployeeAsync_UpdatesPhotoOnlyWhenNewBytesAreProvided()
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    var withoutPhoto = CreateValidRequest(workerRfc: "XAXX010101000");
    withoutPhoto.Id = 10;
    withoutPhoto.FotografiaBytes = null;
    var withoutPhotoResult = await service.SaveEmployeeAsync(withoutPhoto);

    Assert.True(withoutPhotoResult.Success);
    var updateWithoutPhoto = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE dbo.Capital_Humano", StringComparison.Ordinal));
    Assert.DoesNotContain("Fotografia = @Fotografia", updateWithoutPhoto.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain(updateWithoutPhoto.Parameters, parameter => string.Equals(parameter.Name, "Fotografia", StringComparison.OrdinalIgnoreCase));

    var withPhoto = CreateValidRequest(workerRfc: "XAXX010101000");
    withPhoto.Id = 11;
    withPhoto.FotografiaBytes = [0xFF, 0xD8, 0xFF];
    var withPhotoResult = await service.SaveEmployeeAsync(withPhoto);

    Assert.True(withPhotoResult.Success);
    var updateWithPhoto = connection.ExecutedCommands.Last(command => command.CommandText.Contains("UPDATE dbo.Capital_Humano", StringComparison.Ordinal));
    Assert.Contains("Fotografia = @Fotografia", updateWithPhoto.CommandText, StringComparison.Ordinal);
    Assert.Contains(updateWithPhoto.Parameters, parameter => string.Equals(parameter.Name, "Fotografia", StringComparison.OrdinalIgnoreCase)
      && parameter.Value is byte[] bytes
      && bytes.Length == 3);
  }

  [Fact]
  public async Task DeactivateEmployeeAsync_PreservesRowAndSetsInactiveState()
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = static (_, _) => 1
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    var result = await service.DeactivateEmployeeAsync(42, "OHM191112Q26");

    Assert.True(result.Success);
    var command = Assert.Single(connection.ExecutedCommands);
    Assert.Contains("UPDATE dbo.Capital_Humano", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("[Status] = 'INACTIVO'", command.CommandText, StringComparison.Ordinal);
    Assert.Contains("Fecha_Baja = ISNULL", command.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("DELETE", command.CommandText, StringComparison.OrdinalIgnoreCase);
    Assert.Contains(command.Parameters, parameter => string.Equals(parameter.Name, "Rfc", StringComparison.OrdinalIgnoreCase)
      && string.Equals(parameter.Value?.ToString(), "OHM191112Q26", StringComparison.Ordinal));
  }

  [Fact]
  public async Task AddEmployeeAttachmentAsync_InsertsIntoEmployeeAttachmentScopedByCompanyRfc()
  {
    var connection = new FakeQueryDbConnection
    {
      ScalarResultFactory = static (commandText, _) =>
      {
        if (commandText.Contains("COUNT(1)", StringComparison.Ordinal))
          return 1;

        if (commandText.Contains("SCOPE_IDENTITY", StringComparison.Ordinal))
          return 22;

        return null;
      },
      ReaderResultFactory = static (_, _) => Table(
        ["Id", "EmployeeId", "AttachmentName", "AttachmentExtension", "AttachmentDescription", "Length"],
        [22, 84, "ine.pdf", "pdf", "INE", 3L])
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    var attachment = await service.AddEmployeeAttachmentAsync(new CapitalHumanoAttachmentCreateRequest
    {
      EmployeeId = 84,
      Rfc = "OHM191112Q26",
      FileName = "ine.pdf",
      Extension = "pdf",
      Description = "INE",
      Content = [1, 2, 3]
    });

    Assert.Equal(22, attachment.Id);

    var employeeCheck = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("FROM dbo.Capital_Humano", StringComparison.Ordinal)
        && command.CommandText.Contains("RFC = @Rfc", StringComparison.Ordinal));
    Assert.Contains(employeeCheck.Parameters, parameter => string.Equals(parameter.Name, "EmployeeId", StringComparison.OrdinalIgnoreCase)
      && Convert.ToInt32(parameter.Value) == 84);

    var insert = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("INSERT INTO dbo.EMPLOYEE_ATTACHMENT", StringComparison.Ordinal));
    Assert.Contains("EmpID", insert.CommandText, StringComparison.Ordinal);
    Assert.Contains(insert.Parameters, parameter => string.Equals(parameter.Name, "Attachment", StringComparison.OrdinalIgnoreCase)
      && parameter.Value is byte[] bytes
      && bytes.Length == 3);
  }

  [Fact]
  public async Task UpdateEmployeeAttachmentAsync_PreservesContentWhenNoReplacementProvided()
  {
    var connection = new FakeQueryDbConnection
    {
      NonQueryResultFactory = static (_, _) => 1,
      ReaderResultFactory = static (_, _) => Table(
        ["Id", "EmployeeId", "AttachmentName", "AttachmentExtension", "AttachmentDescription", "Length"],
        [22, 84, "contrato.pdf", "pdf", "Contrato firmado", 12L])
    };
    var service = new CapitalHumanoService(new FakeQueryConnectionFactory(connection));

    var attachment = await service.UpdateEmployeeAttachmentAsync(new CapitalHumanoAttachmentUpdateRequest
    {
      AttachmentId = 22,
      EmployeeId = 84,
      Rfc = "OHM191112Q26",
      FileName = "contrato.pdf",
      Extension = "pdf",
      Description = "Contrato firmado"
    });

    Assert.Equal("Contrato firmado", attachment.AttachmentDescription);

    var update = Assert.Single(
      connection.ExecutedCommands,
      command => command.CommandText.Contains("UPDATE ea", StringComparison.Ordinal));
    Assert.Contains("dbo.EMPLOYEE_ATTACHMENT", update.CommandText, StringComparison.Ordinal);
    Assert.Contains("INNER JOIN dbo.Capital_Humano", update.CommandText, StringComparison.Ordinal);
    Assert.Contains("ch.RFC = @Rfc", update.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("Attachment = @Attachment", update.CommandText, StringComparison.Ordinal);
    Assert.DoesNotContain("@AttachmentDescriptionFROM", update.CommandText, StringComparison.Ordinal);
  }

  [Fact]
  public void CapitalHumanoUi_DoesNotUseLegacyRolesOrCredentialFields()
  {
    var page = File.ReadAllText(GetRepoFile("src/OrionERP.Web/Features/CapitalHumano/CapitalHumanoPage.razor"));
    var codeBehind = File.ReadAllText(GetRepoFile("src/OrionERP.Web/Features/CapitalHumano/CapitalHumanoPage.razor.cs"));
    var models = File.ReadAllText(GetRepoFile("src/OrionERP.Application/Features/CapitalHumano/CapitalHumanoModels.cs"));

    Assert.DoesNotContain("Roles_Usuario", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Roles_Usuario", codeBehind, StringComparison.Ordinal);
    Assert.DoesNotContain("Usuario_Acceso", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Contrasena_Acceso", page, StringComparison.Ordinal);
    Assert.DoesNotContain("Usuario_Acceso", models, StringComparison.Ordinal);
    Assert.DoesNotContain("Contrasena_Acceso", models, StringComparison.Ordinal);
  }

  private static CapitalHumanoSaveRequest CreateValidRequest(string workerRfc)
    => new()
    {
      Rfc = "OHM191112Q26",
      Nombre = "Orion",
      ApellidoPaterno = "Contreras",
      ApellidoMaterno = "Garcia",
      NombreCorto = "Orion C",
      Status = "activo",
      CURP = "cugo880913hmcrrs09",
      Fecha_Nacimiento = new DateTime(1988, 9, 13),
      RFC_Capital_Humano = workerRfc,
      Fecha_Alta = new DateTime(2026, 1, 1)
    };

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

  private static DataTable Table(string[] columns, params object?[][] rows)
  {
    var table = new DataTable();
    foreach (var column in columns)
    {
      table.Columns.Add(column);
    }

    foreach (var row in rows)
    {
      table.Rows.Add(row);
    }

    return table;
  }
}
