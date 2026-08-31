using System.Text.RegularExpressions;

namespace OrionERP.UnitTests.Shared;

public class InputEventSafetyTests
{
  private static readonly Regex InputTagPattern = new(
    "<(?:input|textarea|InputText|InputTextArea)\\b(?:(?!>).)*>",
    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

  [Fact]
  public void TextEditors_DoNotMixDeferredBindingWithOnInputRerenders()
  {
    foreach (var (path, tag) in EnumerateInputTags())
    {
      var hasBinding = Regex.IsMatch(tag, "@bind(?:-Value|-value)?=\\\"");
      var hasOnInputHandler = tag.Contains("@oninput=", StringComparison.Ordinal);
      var bindsOnInput = Regex.IsMatch(tag, "@bind(?::|-Value:|-value:)?event=\\\"oninput\\\"");
      var onInputHandler = ExtractOnInputHandler(tag);

      Assert.False(
        hasBinding && hasOnInputHandler && !bindsOnInput,
        $"{path} mixes deferred binding with an oninput handler: {Normalize(tag)}");
      Assert.False(
        onInputHandler.Contains("Async", StringComparison.Ordinal),
        $"{path} routes every input event through an asynchronous handler: {Normalize(tag)}");
    }
  }

  [Fact]
  public void KeyboardEnabledInputs_GuardNativeEnterBehavior()
  {
    foreach (var (path, tag) in EnumerateInputTags())
    {
      if (!tag.Contains("@onkeyup=", StringComparison.Ordinal)
        && !tag.Contains("@onkeydown=", StringComparison.Ordinal))
      {
        continue;
      }

      var isSearchInput = Regex.IsMatch(tag, "type=\\\"search\\\"", RegexOptions.IgnoreCase);
      var hasExplicitGuard = tag.Contains("data-orion-prevent-keys=", StringComparison.Ordinal);
      var isReadOnly = tag.Contains("readonly", StringComparison.OrdinalIgnoreCase);

      Assert.True(
        isSearchInput || hasExplicitGuard || isReadOnly,
        $"{path} handles keyboard input without guarding native form submission: {Normalize(tag)}");
    }
  }

  [Fact]
  public void ManagementShell_LoadsCompositionAwareKeyboardGuard()
  {
    var layout = ReadRepoFile("src/OrionERP.Web/Pages/_Layout.cshtml");
    var script = ReadRepoFile("src/OrionERP.Web/wwwroot/js/orion-input-guard.js");

    Assert.Contains("orion-input-guard.js", layout, StringComparison.Ordinal);
    Assert.Contains("event.isComposing", script, StringComparison.Ordinal);
    Assert.Contains("input.type === \"search\"", script, StringComparison.Ordinal);
    Assert.Contains("input.getAttribute(\"role\") === \"combobox\"", script, StringComparison.Ordinal);
    Assert.Contains("event.preventDefault()", script, StringComparison.Ordinal);
  }

  [Fact]
  public void HighTrafficEditors_AvoidPerKeystrokeSideEffects()
  {
    var recipes = ReadRepoFile("src/OrionERP.Web/Features/Restaurante/RestaurantRecipesPage.razor");
    var booking = ReadRepoFile("src/OrionERP.Bonhomia.Web/Features/Bonhomia/BonhomiaReservationPage.razor");

    Assert.DoesNotContain("@oninput=\"MarkDirty\"", recipes, StringComparison.Ordinal);
    Assert.DoesNotContain("_ = PersistCheckoutAsync();", booking, StringComparison.Ordinal);
    Assert.Contains("QueueCheckoutPersistence();", booking, StringComparison.Ordinal);
    Assert.Contains("CheckoutPersistDebounceMilliseconds", booking, StringComparison.Ordinal);
  }

  private static IEnumerable<(string Path, string Tag)> EnumerateInputTags()
  {
    var root = GetRepoRoot();
    foreach (var project in new[] { "OrionERP.Web", "OrionERP.Bonhomia.Web", "OrionERP.Bruno.Web" })
    {
      var projectRoot = Path.Combine(root, "src", project);
      foreach (var file in Directory.EnumerateFiles(projectRoot, "*.razor", SearchOption.AllDirectories))
      {
        var relativePath = Path.GetRelativePath(root, file);
        var source = File.ReadAllText(file);
        foreach (Match match in InputTagPattern.Matches(source))
        {
          yield return (relativePath, match.Value);
        }
      }
    }
  }

  private static string ReadRepoFile(string relativePath)
    => File.ReadAllText(Path.Combine(GetRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

  private static string GetRepoRoot()
  {
    var current = new DirectoryInfo(AppContext.BaseDirectory);
    while (current is not null && !File.Exists(Path.Combine(current.FullName, "OrionERP.sln")))
    {
      current = current.Parent;
    }

    return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
  }

  private static string Normalize(string value)
    => Regex.Replace(value, "\\s+", " ").Trim();

  private static string ExtractOnInputHandler(string tag)
    => Regex.Match(tag, "@oninput=\\\"([^\\\"]*)\\\"").Groups[1].Value;
}
