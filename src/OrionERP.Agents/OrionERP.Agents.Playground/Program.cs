using OrionERP.Agents.Hospedaje;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
var sqlConn = Environment.GetEnvironmentVariable("ORIONERP_SQL_CONN");

if (string.IsNullOrWhiteSpace(apiKey))
{
  Console.WriteLine("Missing OPENAI_API_KEY env var.");
  return;
}
if (string.IsNullOrWhiteSpace(sqlConn))
{
  Console.WriteLine("Missing ORIONERP_SQL_CONN env var.");
  return;
}

// Pick a model. You can change this later.
// The OpenAI .NET docs show ChatClient usage with models like gpt-4o. :contentReference[oaicite:5]{index=5}
var model = "gpt-4o-mini";

var repo = new CalendarRepository(sqlConn);
var agent = new SuiteAvailabilityAgent(model, apiKey, repo);

Console.WriteLine("Hospedaje Availability Agent (type 'exit' to quit)");
while (true)
{
  Console.Write("\n> ");
  var q = Console.ReadLine();
  if (q is null) continue;
  if (q.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

  try
  {
    var answer = await agent.AskAsync(q);
    Console.WriteLine($"\n{answer}");
  }
  catch (Exception ex)
  {
    Console.WriteLine($"\n[ERROR] {ex.Message}");
  }
}
