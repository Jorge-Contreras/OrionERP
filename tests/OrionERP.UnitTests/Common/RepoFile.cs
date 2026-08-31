namespace OrionERP.UnitTests.Common;

/// <summary>Lee un archivo del repositorio a partir de la raíz que contiene la solución.</summary>
public static class RepoFile
{
  public static string Read(string relativePath)
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    if (current is null)
    {
      throw new InvalidOperationException("No se encontró la raíz del repositorio desde el directorio de pruebas.");
    }

    return File.ReadAllText(Path.Combine(current.FullName, relativePath));
  }
}
