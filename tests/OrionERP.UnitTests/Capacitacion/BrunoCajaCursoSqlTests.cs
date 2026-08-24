using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrionERP.UnitTests.Capacitacion;

/// <summary>
/// El curso de caja de Bruno's enseña cifras comerciales reales: la política de
/// puntos, los mínimos de cada promoción y el límite del código BIENVENIDA. Estas
/// pruebas cuidan tres cosas que el servidor no puede verificar por sí solo: que el
/// lote no pueda sembrarse en la base de capacitación sintética, que redacte en
/// borrador antes de publicar (los disparadores hacen inmutable lo publicado) y que
/// el texto siga citando la misma política que el propio lote exige encontrar.
/// </summary>
public sealed class BrunoCajaCursoSqlTests
{
  private const string CursoPath =
    "src/OrionERP.Infrastructure/Features/Capacitacion/Sql/20260824_bruno_curso_caja_membresia.sql";

  private static readonly string Sql = ReadRepoFile(CursoPath);

  [Fact]
  public void Curso_NoPuedeSembrarseEnLaBaseDeCapacitacionSintetica()
  {
    Assert.Contains("N'Orion_Sandbox', N'Orion_SandBox', N'grupocarpio'", Sql, StringComparison.Ordinal);
    Assert.DoesNotContain("N'Orion_Training'", Sql, StringComparison.Ordinal);
    Assert.Contains("DB_NAME() <> @ExpectedDatabase", Sql, StringComparison.Ordinal);
    Assert.Contains("@ApplyChanges IS NULL", Sql, StringComparison.Ordinal);
    Assert.Contains("SESSION_CONTEXT(N'OrionRfc') IS NOT NULL", Sql, StringComparison.Ordinal);

    // La simulación es el modo por defecto documentado: sin ApplyChanges=1 revierte.
    Assert.Contains("IF @ApplyChanges = 1\n    COMMIT TRANSACTION;", Normalize(Sql), StringComparison.Ordinal);
    Assert.Contains("ROLLBACK TRANSACTION;", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Curso_ValidaLaPoliticaVigenteAntesDeEscribirNada()
  {
    var politica = Sql.IndexOf("PesosPerPoint = 10 AND PointValueMxn = 1.00", StringComparison.Ordinal);
    var codigo = Sql.IndexOf("Code = 'BIENVENIDA' AND PerMemberLimit = 1", StringComparison.Ordinal);
    var primeraEscritura = Sql.IndexOf("INSERT INTO capacitacion.Curso", StringComparison.Ordinal);

    Assert.True(politica > 0, "Falta la comprobación de la política de puntos.");
    Assert.True(codigo > politica);
    Assert.True(primeraEscritura > codigo, "El lote debe validar la política antes de escribir el curso.");

    // El texto del curso enseña exactamente la política que el lote exige encontrar.
    Assert.Contains("MinimumRedeemPoints = 100 AND PointsValidityMonths = 12", Sql, StringComparison.Ordinal);
    Assert.Contains("un punto por cada $10", Sql, StringComparison.Ordinal);
    Assert.Contains("cada punto vale $1", Sql, StringComparison.Ordinal);
    Assert.Contains("el canje empieza en 100 puntos", Sql, StringComparison.Ordinal);
    Assert.Contains("vigencia de 12 meses", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Curso_RedactaEnBorradorYPublicaAlFinal()
  {
    var borrador = Sql.IndexOf("1, N'BORRADOR'", StringComparison.Ordinal);
    var lecciones = Sql.IndexOf("INSERT INTO capacitacion.Leccion", StringComparison.Ordinal);
    var pasos = Sql.IndexOf("INSERT INTO capacitacion.PracticaPaso", StringComparison.Ordinal);
    var publicacion = Sql.IndexOf("SET Estado = N'PUBLICADA'", StringComparison.Ordinal);

    Assert.True(borrador > 0);
    Assert.True(lecciones > borrador);
    Assert.True(pasos > lecciones);
    Assert.True(publicacion > pasos, "Publicar antes de terminar el contenido choca con los disparadores de inmutabilidad.");
  }

  [Fact]
  public void Curso_CubreLasCincoLeccionesYSusBloquesSinHuecos()
  {
    var lecciones = Regex.Matches(
        Section("INSERT INTO @Lecciones (", "INSERT INTO capacitacion.Leccion ("),
        @"(?m)^\s*\(\d+, N'(?<clave>[A-Z]+)'")
      .Select(match => match.Groups["clave"].Value)
      .ToArray();

    Assert.Equal(new[] { "PORQUE", "IDENTIFICAR", "SUGERIR", "PROMOS", "CERRAR" }, lecciones);

    var bloques = Bloques();
    Assert.Empty(bloques.Select(bloque => bloque.Leccion).Except(lecciones, StringComparer.Ordinal));

    foreach (var leccion in lecciones)
    {
      var ordenes = bloques.Where(bloque => bloque.Leccion == leccion).Select(bloque => bloque.Orden).Order().ToArray();
      Assert.NotEmpty(ordenes);
      Assert.Equal(Enumerable.Range(1, ordenes.Length), ordenes);
    }

    // El recorrido completo termina en práctica, evaluación y cierre.
    var cierre = bloques.Where(bloque => bloque.Leccion == "CERRAR").OrderBy(bloque => bloque.Orden).ToArray();
    Assert.Equal(["PRACTICA", "EVALUACION", "RESUMEN"], cierre.TakeLast(3).Select(bloque => bloque.Tipo));
  }

  [Fact]
  public void Curso_UsaSoloTiposDeBloqueYRecursoQueElEsquemaAcepta()
  {
    string[] tiposValidos =
    [
      "OBJETIVOS", "TEORIA", "IMAGEN", "PASOS", "DEMOSTRACION", "PRACTICA", "EVALUACION", "RESUMEN", "ALERTA"
    ];

    Assert.All(Bloques(), bloque => Assert.Contains(bloque.Tipo, tiposValidos));

    var recursos = Section("INSERT INTO @Recursos (", "INSERT INTO capacitacion.Recurso (");
    var tiposRecurso = Regex.Matches(recursos, @"(?m)^\s*\(N'[A-Z]+', \d+, \d+, N'(?<tipo>[A-Z]+)'")
      .Select(match => match.Groups["tipo"].Value)
      .Distinct(StringComparer.Ordinal)
      .ToArray();

    Assert.NotEmpty(tiposRecurso);
    Assert.All(tiposRecurso, tipo => Assert.Contains(tipo, new[] { "IMAGEN", "DIAGRAMA", "VIDEO", "ARCHIVO", "ENLACE" }));
  }

  [Fact]
  public void Curso_TraeConfiguracionJsonValidaEnCadaBloque()
  {
    var literales = Regex.Matches(Sql, @"N'(?<json>\{.*?\})'(?=[,)\s])", RegexOptions.Singleline)
      .Select(match => match.Groups["json"].Value.Replace("''", "'"))
      .ToArray();

    // Un bloque, una configuración: la comprobación pierde valor si dejan de coincidir.
    Assert.Equal(Bloques().Length, literales.Length);

    foreach (var literal in literales)
    {
      using var document = JsonDocument.Parse(literal);
      foreach (var propiedad in new[] { "items", "diagram", "demoSteps" })
      {
        if (document.RootElement.TryGetProperty(propiedad, out var valor))
        {
          Assert.Equal(JsonValueKind.Array, valor.ValueKind);
          Assert.All(valor.EnumerateArray(), item => Assert.Equal(JsonValueKind.String, item.ValueKind));
        }
      }
    }
  }

  [Fact]
  public void Evaluacion_TieneTresOpcionesPorPreguntaYPreguntasCriticas()
  {
    var cuerpo = Section("INSERT INTO @Preguntas (", "INSERT INTO capacitacion.Pregunta (");
    var ordenes = Regex.Matches(cuerpo, @"(?m)^\s*\((?<orden>\d+), N'")
      .Select(match => int.Parse(match.Groups["orden"].Value))
      .ToArray();

    Assert.True(ordenes.Length >= 10, "La evaluación necesita cobertura suficiente del guion.");
    Assert.Equal(Enumerable.Range(1, ordenes.Length), ordenes);

    // El CROSS APPLY arma una correcta y dos incorrectas por pregunta.
    Assert.Contains("VALUES (1, source.Correcta, CONVERT(bit, 1))", Sql, StringComparison.Ordinal);
    Assert.Contains("(2, source.Incorrecta1, CONVERT(bit, 0))", Sql, StringComparison.Ordinal);
    Assert.Contains("(3, source.Incorrecta2, CONVERT(bit, 0))", Sql, StringComparison.Ordinal);

    // Las preguntas que cuestan dinero no se pueden fallar: deben existir varias críticas.
    var criticas = Regex.Matches(cuerpo, @"(?m)^\s*N'[^\n]*',\s*1,\s*$").Count;
    Assert.True(criticas >= 3, $"Se esperaban al menos 3 preguntas críticas y hay {criticas}.");

    Assert.Contains("HAVING COUNT(optionInfo.OpcionId) <> 1", Sql, StringComparison.Ordinal);
  }

  [Fact]
  public void Practica_NoEnviaNiCobraLaOrdenYSeVerificaAlCerrar()
  {
    var pasos = Section("INSERT INTO @Pasos (", "INSERT INTO capacitacion.PracticaPaso (");
    var ordenes = Regex.Matches(pasos, @"(?m)^\s*\((?<orden>\d+), N'")
      .Select(match => int.Parse(match.Groups["orden"].Value))
      .ToArray();

    Assert.NotEmpty(ordenes);
    Assert.Equal(Enumerable.Range(1, ordenes.Length), ordenes);

    // La práctica corre sobre el punto de venta real, así que el paso que impide
    // cobrar es la única salvaguarda: debe existir y debe estar marcado como crítico.
    Assert.Contains("N'/restaurante/pos'", Sql, StringComparison.Ordinal);
    Assert.Matches(new Regex(@"sin enviar ni cobrar la orden[^\n]*', 1\)"), pasos);
  }

  [Fact]
  public void Curso_ApuntaSoloAPantallasLocales()
  {
    var rutas = Regex.Matches(Sql, @"N'(?<ruta>/[^']*)'")
      .Select(match => match.Groups["ruta"].Value)
      .ToArray();

    Assert.NotEmpty(rutas);
    Assert.All(rutas, ruta => Assert.StartsWith("/", ruta, StringComparison.Ordinal));
    Assert.DoesNotContain("N'http:", Sql, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("N'https:", Sql, StringComparison.OrdinalIgnoreCase);
  }

  private static (string Leccion, int Orden, string Tipo)[] Bloques()
  {
    var cuerpo = Section("INSERT INTO @Bloques (", "INSERT INTO capacitacion.BloqueContenido (");
    return Regex.Matches(cuerpo, @"(?m)^\s*\(N'(?<leccion>[A-Z]+)', (?<orden>\d+), N'(?<tipo>[A-Z]+)'")
      .Select(match => (
        match.Groups["leccion"].Value,
        int.Parse(match.Groups["orden"].Value),
        match.Groups["tipo"].Value))
      .ToArray();
  }

  private static string Section(string start, string end)
  {
    var from = Sql.IndexOf(start, StringComparison.Ordinal);
    Assert.True(from >= 0, $"No se encontró {start} en el curso.");
    var to = Sql.IndexOf(end, from, StringComparison.Ordinal);
    Assert.True(to > from, $"No se encontró {end} después de {start}.");
    return Sql[from..to];
  }

  private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

  private static string ReadRepoFile(string relativePath)
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
      var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
      if (File.Exists(candidate)) return File.ReadAllText(candidate);
      directory = directory.Parent;
    }

    throw new FileNotFoundException($"No se encontró {relativePath} desde {AppContext.BaseDirectory}.");
  }
}
