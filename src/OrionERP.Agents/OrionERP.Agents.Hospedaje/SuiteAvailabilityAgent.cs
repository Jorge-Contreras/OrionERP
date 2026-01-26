using System.Text.Json;
using OpenAI.Chat;

namespace OrionERP.Agents.Hospedaje;

public sealed class SuiteAvailabilityAgent
{
  private readonly ChatClient _chatClient;
  private readonly CalendarRepository _repo;

  // Tool name exposed to the model
  private const string ToolName = "get_suite_availability";

  // Tool definition (JSON Schema): check-in inclusive, check-out exclusive
  private static readonly ChatTool AvailabilityTool = ChatTool.CreateFunctionTool(
      functionName: ToolName,
      functionDescription:
          "Returns availability for lodging suites based on the OrionERP SQL calendar. " +
          "IMPORTANT: check_in_date is inclusive; check_out_date is exclusive. " +
          "If the user gives a date range, convert it to ISO YYYY-MM-DD and follow the inclusive/exclusive rule. " +
          "Suite codes: BERLIN (Casa Berlin), LONDON (Casa London), MANHATTAN, MOSCU, PARIS, PENTHOUSE, SEUL.",
      functionParameters: BinaryData.FromBytes("""
    {
      "type": "object",
      "properties": {
        "check_in_date": {
          "type": "string",
          "description": "Check-in date in ISO format YYYY-MM-DD (inclusive)"
        },
        "check_out_date": {
          "type": "string",
          "description": "Check-out date in ISO format YYYY-MM-DD (exclusive)"
        },
        "suite": {
          "type": "string",
          "description": "Optional suite code. If omitted, return all suites. Valid codes: BERLIN, LONDON, MANHATTAN, MOSCU, PARIS, PENTHOUSE, SEUL"
        }
      },
      "required": ["check_in_date", "check_out_date"],
      "additionalProperties": false
    }
    """u8.ToArray())
  );


  public SuiteAvailabilityAgent(string model, string openAiApiKey, CalendarRepository repo)
  {
    _chatClient = new ChatClient(model: model, apiKey: openAiApiKey);
    _repo = repo;
  }

  public async Task<string> AskAsync(string userQuestion, CancellationToken ct = default)
  {
    var today = DateOnly.FromDateTime(DateTime.Now);
    var messages = new List<ChatMessage>
        {
            new SystemChatMessage(
                 "You are Nora, a lodging availability assistant for a Bed and Breakfast facility " +
  "Never guess availability. Always call the tool get_suite_availability for availability questions. " +
  "DATE POLICY (must follow exactly): " +
  $"Reference date (today) is {today:yyyy-MM-dd}. " +
  "Use ONLY this reference date for relative expressions. " +
  "Relative date rules: " +
  "- 'today'/'hoy' => check_in = today, check_out = today+1 " +
  "- 'tomorrow'/'mañana' => check_in = today+1, check_out = today+2 " +
  "- 'in N days'/'en N días' => check_in = today+N, check_out = today+N+1 " +
  "- 'next week'/'la próxima semana' => Monday-to-Monday of next week (7 nights), check_out exclusive " +
  "IMPORTANT: check_in_date is inclusive; check_out_date is exclusive. " +
  "Output tool arguments in ISO YYYY-MM-DD."
            ),
            new UserChatMessage(userQuestion)
        };

    var options = new ChatCompletionOptions
    {
      Temperature = 0,
      Tools = { AvailabilityTool }
    };

    bool requiresAction;
    do
    {
      requiresAction = false;

      ChatCompletion completion = await _chatClient.CompleteChatAsync(messages, options, ct);

      switch (completion.FinishReason)
      {
        case ChatFinishReason.Stop:
          messages.Add(new AssistantChatMessage(completion));
          return completion.Content.FirstOrDefault()?.Text ?? "";

        case ChatFinishReason.ToolCalls:
          // Add assistant tool-call message
          messages.Add(new AssistantChatMessage(completion));

          foreach (ChatToolCall toolCall in completion.ToolCalls)
          {
            if (!string.Equals(toolCall.FunctionName, ToolName, StringComparison.Ordinal))
              throw new NotImplementedException($"Unexpected tool: {toolCall.FunctionName}");

            string toolOutput = await HandleAvailabilityToolAsync(toolCall.FunctionArguments, ct);
            messages.Add(new ToolChatMessage(toolCall.Id, toolOutput));
          }

          requiresAction = true;
          break;

        case ChatFinishReason.Length:
          throw new InvalidOperationException("Model output truncated (token limit).");

        default:
          throw new InvalidOperationException($"Unhandled finish reason: {completion.FinishReason}");
      }
    }
    while (requiresAction);

    return "";
  }

  private async Task<string> HandleAvailabilityToolAsync(BinaryData functionArguments, CancellationToken ct)
  {
    // Parse + validate args (models can hallucinate inputs; validate defensively)
    using var doc = JsonDocument.Parse(functionArguments.ToString());
    var root = doc.RootElement;

    var checkInStr = root.GetProperty("check_in_date").GetString();
    var checkOutStr = root.GetProperty("check_out_date").GetString();
    var suiteRaw = root.TryGetProperty("suite", out var suiteEl) ? suiteEl.GetString() : null;

    if (!DateOnly.TryParse(checkInStr, out var checkIn))
    {
      return JsonSerializer.Serialize(new
      {
        error = "Invalid check_in_date. Expected YYYY-MM-DD.",
        received = checkInStr
      });
    }

    if (!DateOnly.TryParse(checkOutStr, out var checkOut))
    {
      return JsonSerializer.Serialize(new
      {
        error = "Invalid check_out_date. Expected YYYY-MM-DD.",
        received = checkOutStr
      });
    }

    // Enforce: check-out is exclusive => must be strictly greater than check-in
    if (checkOut <= checkIn)
    {
      return JsonSerializer.Serialize(new
      {
        error = "Invalid date range. check_out_date must be AFTER check_in_date (exclusive check-out).",
        check_in_date = checkIn.ToString("yyyy-MM-dd"),
        check_out_date = checkOut.ToString("yyyy-MM-dd")
      });
    }

    var suite = SuiteCatalog.NormalizeSuite(suiteRaw);
    if (!string.IsNullOrWhiteSpace(suiteRaw) && suite is null)
    {
      return JsonSerializer.Serialize(new
      {
        error = "Unknown suite code.",
        received = suiteRaw,
        valid = SuiteCatalog.SuiteCodes
      });
    }

    // Inclusive/Exclusive rule:
    // Scan occupancy from check_in_date through (check_out_date - 1 day)
    var scanStart = checkIn;
    var scanEnd = checkOut.AddDays(-1);

    var calendarRows = await _repo.GetFullCalendarAsync(scanStart, scanEnd);

    // availability scan
    var suiteCodes = suite is null ? SuiteCatalog.SuiteCodes : new[] { suite };

    var availability = new List<object>();

    foreach (var suiteCode in suiteCodes)
    {
      var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (var row in calendarRows)
      {
        if (!row.TryGetValue(suiteCode, out var value))
          continue;

        if (value is null || value is DBNull)
          continue;

        // After your SP fix, empty should be NULL. Still keep a defensive guard:
        if (value is string strVal)
        {
          var s = strVal.Trim();
          if (string.IsNullOrEmpty(s)) continue;
          if (string.Equals(s, "NULL", StringComparison.OrdinalIgnoreCase)) continue;
          conflicts.Add(s);
          continue;
        }

        var asText = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(asText))
          continue;

        conflicts.Add(asText);
      }

      availability.Add(new
      {
        suite = suiteCode,
        available = conflicts.Count == 0,
        conflict_reservation_ids = conflicts.Count == 0
              ? Array.Empty<string>()
              : conflicts.OrderBy(x => x).ToArray()
      });
    }

    return JsonSerializer.Serialize(new
    {
      check_in_date = checkIn.ToString("yyyy-MM-dd"),
      check_out_date = checkOut.ToString("yyyy-MM-dd"),
      // Explicitly echo the scan window we used so there is no ambiguity
      scan_start_date = scanStart.ToString("yyyy-MM-dd"),
      scan_end_date = scanEnd.ToString("yyyy-MM-dd"),
      availability
    });
  }


}
