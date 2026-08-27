using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using OrionERP.Web.Features.Capacitacion;

namespace OrionERP.UnitTests.Capacitacion;

public sealed class CapacitacionUiTests
{
  [Theory]
  [InlineData(typeof(CapacitacionHubPage), "/capacitacion", "CapacitacionEmployee")]
  [InlineData(typeof(MiPlanCapacitacionPage), "/capacitacion/mi-plan", "CapacitacionEmployee")]
  [InlineData(typeof(CatalogoCapacitacionPage), "/capacitacion/catalogo", "CapacitacionEmployee")]
  [InlineData(typeof(CursosCapacitacionPage), "/capacitacion/cursos", "CapacitacionInstructor")]
  [InlineData(typeof(NuevaSesionCapacitacionPage), "/capacitacion/sesiones/nueva", "CapacitacionInstructor")]
  [InlineData(typeof(SesionCapacitacionPage), "/capacitacion/sesiones/{Id:long}", "CapacitacionEmployee")]
  [InlineData(typeof(AdminCapacitacionPage), "/capacitacion/admin", "CapacitacionAdmin")]
  public void TrainingRoutes_RequireExplicitPolicies(Type component, string route, string policy)
  {
    Assert.Contains(component.GetCustomAttributes<RouteAttribute>(), attribute => attribute.Template == route);
    Assert.Contains(component.GetCustomAttributes<AuthorizeAttribute>(), attribute => attribute.Policy == policy);
  }

  [Fact]
  public void SessionRoom_UsesCancellationAwarePollingAndPersonalEvidence()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/SesionCapacitacionPage.razor");

    Assert.Contains("PeriodicTimer(TimeSpan.FromSeconds(5))", source, StringComparison.Ordinal);
    Assert.Contains("WaitForNextTickAsync(LifetimeToken)", source, StringComparison.Ordinal);
    Assert.Contains("await InvokeAsync", source, StringComparison.Ordinal);
    Assert.Contains("RegistrarProgresoBloqueAsync", source, StringComparison.Ordinal);
    Assert.Contains("RegistrarEvaluacionAsync", source, StringComparison.Ordinal);
    Assert.Contains("RegistrarResultadoPracticoAsync", source, StringComparison.Ordinal);
    Assert.Contains("FirmarFinalizacionAsync", source, StringComparison.Ordinal);
    Assert.Contains("AcusarFinalizacionAsync", source, StringComparison.Ordinal);
    Assert.Contains("EstadoAsignacion == CapacitacionCodes.AsignacionEsperaAcuse", source, StringComparison.Ordinal);
    Assert.Contains("EstadoAsignacion != CapacitacionCodes.AsignacionEsperaFirma", source, StringComparison.Ordinal);
    Assert.Contains("cap-live--presentation", source, StringComparison.Ordinal);
  }

  [Fact]
  public void ContentRenderer_CoversEveryPublishedBlockTypeWithoutRawHtml()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/Components/BloqueContenidoCapacitacion.razor");

    foreach (var blockType in new[] { "TEORIA", "OBJETIVOS", "IMAGEN", "PASOS", "DEMOSTRACION", "PRACTICA", "EVALUACION", "RESUMEN", "ALERTA" })
      Assert.Contains($"\"{blockType}\"", source, StringComparison.Ordinal);

    Assert.DoesNotContain("MarkupString", source, StringComparison.Ordinal);
    Assert.DoesNotContain("@((MarkupString)", source, StringComparison.Ordinal);
    Assert.Contains("@paragraph", source, StringComparison.Ordinal);
  }

  [Fact]
  public void SandboxLinks_RequireTheConfiguredExactAuthority()
  {
    var probe = new PageProbe();
    probe.UseConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
      ["Capacitacion:SandboxBaseUrl"] = "https://training.orion.land:8443",
      ["Capacitacion:AllowedVisualAidOrigins:0"] = "https://assets.orion.land"
    }).Build());

    Assert.Equal("https://training.orion.land:8443/cfdi/practica", probe.SandboxUrl("/cfdi/practica"));
    Assert.Equal("https://training.orion.land:8443/reservaciones", probe.SandboxUrl("https://training.orion.land:8443/reservaciones"));
    Assert.Null(probe.SandboxUrl("http://training.orion.land:8443/cfdi"));
    Assert.Null(probe.SandboxUrl("https://training.orion.land/cfdi"));
    Assert.Null(probe.SandboxUrl("https://example.test/cfdi"));
    Assert.Null(probe.SandboxUrl("//example.test/cfdi"));
    Assert.Null(probe.SandboxUrl("/cfdi\\example"));
    Assert.Equal("/Images/Training/flujo.svg", probe.ResourceUrl("/Images/Training/flujo.svg"));
    Assert.Equal("https://assets.orion.land/training/flujo.webp", probe.ResourceUrl("https://assets.orion.land/training/flujo.webp"));
    Assert.Equal("https://training.orion.land:8443/Images/Training/flujo.svg", probe.ResourceUrl("https://training.orion.land:8443/Images/Training/flujo.svg"));
    Assert.Null(probe.ResourceUrl("javascript:alert(1)"));
    Assert.Null(probe.ResourceUrl("data:image/svg+xml;base64,PHN2Zz4="));
    Assert.Null(probe.ResourceUrl("//example.test/asset.png"));
    Assert.Null(probe.ResourceUrl("https://example.test/asset.png"));
    Assert.Null(probe.ResourceUrl("http://assets.orion.land/asset.png"));
    Assert.Null(probe.ResourceUrl("/Images\\evil.png"));
  }

  [Fact]
  public void LearnerPlan_RequiresEvaluationApprovalBeforeAdvancing()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/MiPlanCapacitacionPage.razor");

    Assert.Contains("evaluationResult?.Aprobada == true", source, StringComparison.Ordinal);
    Assert.Contains("AllQuestionsAnswered", source, StringComparison.Ordinal);
    Assert.Contains("Un instructor validará tu lista práctica", source, StringComparison.Ordinal);
    Assert.Contains("Confirmar que recibí la capacitación", source, StringComparison.Ordinal);
  }

  [Fact]
  public void LearnerEvaluations_MarkCorrectAndIncorrectQuestionsAfterGrading()
  {
    foreach (var file in new[]
    {
      "src/OrionERP.Web/Features/Capacitacion/MiPlanCapacitacionPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/SesionCapacitacionPage.razor"
    })
    {
      var source = ReadRepoFile(file);
      Assert.Contains("QuestionResultCss(question.PreguntaId)", source, StringComparison.Ordinal);
      Assert.Contains("PreguntasIncorrectas.Contains(questionId)", source, StringComparison.Ordinal);
      Assert.Contains("Incorrecta", source, StringComparison.Ordinal);
      Assert.Contains("Correcta", source, StringComparison.Ordinal);
      Assert.Contains("evaluationResult?.Success == true", source, StringComparison.Ordinal);
    }

    foreach (var file in new[]
    {
      "src/OrionERP.Web/Features/Capacitacion/MiPlanCapacitacionPage.razor.css",
      "src/OrionERP.Web/Features/Capacitacion/SesionCapacitacionPage.razor.css"
    })
    {
      var source = ReadRepoFile(file);
      Assert.Contains(".is-correct", source, StringComparison.Ordinal);
      Assert.Contains(".is-incorrect", source, StringComparison.Ordinal);
    }
  }

  [Fact]
  public void LearnerPlan_PersistsTheLastRequiredBlockBeforeSignoff()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/MiPlanCapacitacionPage.razor");

    Assert.Contains("else if (!LastBlockCompleted)", source, StringComparison.Ordinal);
    Assert.Contains("Guardar y completar contenido", source, StringComparison.Ordinal);
    Assert.Contains("else await RefreshSelectedAssignmentAsync();", source, StringComparison.Ordinal);
    Assert.Contains("GetCursoAsignadoAsync", source, StringComparison.Ordinal);
  }

  [Fact]
  public void FinalizedGuidedSession_AllowsPendingLastBlockEvidence()
  {
    var source = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/SesionCapacitacionPage.razor");

    Assert.Contains("!CurrentParticipant.BloqueActualCompletado", source, StringComparison.Ordinal);
    Assert.Contains("Confirmar último bloque revisado", source, StringComparison.Ordinal);
    Assert.Contains("ConfirmBlockAsync(finalBlock.BloqueId, assignmentId)", source, StringComparison.Ordinal);
  }

  [Fact]
  public void TrainingPages_LogExceptionsWithoutRenderingRawDetails()
  {
    var files = new[]
    {
      "src/OrionERP.Web/Features/Capacitacion/CapacitacionPageBase.cs",
      "src/OrionERP.Web/Features/Capacitacion/CapacitacionHubPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/CatalogoCapacitacionPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/MiPlanCapacitacionPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/CursosCapacitacionPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/NuevaSesionCapacitacionPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/SesionCapacitacionPage.razor",
      "src/OrionERP.Web/Features/Capacitacion/AdminCapacitacionPage.razor"
    };

    foreach (var file in files)
      Assert.DoesNotContain("exception.Message", ReadRepoFile(file), StringComparison.OrdinalIgnoreCase);

    Assert.Contains(
      "Logger.LogError",
      ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/CapacitacionPageBase.cs"),
      StringComparison.Ordinal);
  }

  [Fact]
  public void InstructorCourseWorkspace_ProvidesVersionSafeCrudActions()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/CursosCapacitacionPage.razor");
    var navigation = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/Components/CapacitacionShell.razor");

    Assert.Contains("GuardarCursoAsync", page, StringComparison.Ordinal);
    Assert.Contains("GetCursoAdministrableAsync", page, StringComparison.Ordinal);
    Assert.Contains("PrepararEdicionCursoAsync", page, StringComparison.Ordinal);
    Assert.Contains("PublicarCursoAsync", page, StringComparison.Ordinal);
    Assert.Contains("CambiarEstadoCursoAsync", page, StringComparison.Ordinal);
    Assert.Contains("El historial se conservará", page, StringComparison.Ordinal);
    Assert.Contains("href=\"/capacitacion/cursos\"", navigation, StringComparison.Ordinal);
  }

  [Fact]
  public void InstructorCourseWorkspace_AuthorsEvaluationsAndPracticesInTheDraft()
  {
    var page = ReadRepoFile("src/OrionERP.Web/Features/Capacitacion/CursosCapacitacionPage.razor");

    foreach (var action in new[]
    {
      "AddEvaluation",
      "RemoveEvaluation",
      "AddQuestion",
      "SelectCorrectOption",
      "AddPractice",
      "RemovePractice",
      "AddPracticeStep",
      "RemovePracticeStep"
    })
      Assert.Contains(action, page, StringComparison.Ordinal);

    Assert.Contains("Evaluaciones = course.Evaluaciones.Select", page, StringComparison.Ordinal);
    Assert.Contains("Practicas = course.Practicas.Select", page, StringComparison.Ordinal);
    Assert.Contains("EsCorrecta = option.EsCorrecta", page, StringComparison.Ordinal);
    Assert.DoesNotContain("se conservarán al publicar", page, StringComparison.OrdinalIgnoreCase);
  }

  private sealed class PageProbe : CapacitacionPageBase
  {
    public void UseConfiguration(IConfiguration configuration) => Configuration = configuration;
    public string? SandboxUrl(string? path) => BuildSandboxUrl(path);
    public string? ResourceUrl(string? path) => BuildResourceUrl(path);
  }

  private static string ReadRepoFile(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln"))) current = current.Parent;
    Assert.NotNull(current);
    return File.ReadAllText(Path.Combine(current!.FullName, relativePath));
  }
}
