using System.Text;
using System.Text.RegularExpressions;

namespace OrionERP.DatabaseMigrator;

internal static partial class SqlBatchSplitter
{
  public static IReadOnlyList<string> Split(string script)
  {
    var batches = new List<string>();
    var current = new StringBuilder();

    using var reader = new StringReader(script);
    while (reader.ReadLine() is { } line)
    {
      var match = GoLine().Match(line);
      if (!match.Success)
      {
        current.AppendLine(line);
        continue;
      }

      var batch = current.ToString().Trim();
      current.Clear();
      if (batch.Length == 0)
        continue;

      var repeat = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 1;
      if (repeat is < 1 or > 100)
        throw new InvalidOperationException("La repetición GO debe estar entre 1 y 100.");

      for (var index = 0; index < repeat; index++)
        batches.Add(batch);
    }

    var finalBatch = current.ToString().Trim();
    if (finalBatch.Length > 0)
      batches.Add(finalBatch);

    return batches;
  }

  [GeneratedRegex(@"^\s*GO(?:\s+(\d+))?\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex GoLine();
}
