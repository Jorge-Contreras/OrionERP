using OpenAI.Responses;

var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
    ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");

var client = new OpenAIResponseClient(
    model: "gpt-4o-mini",   // good low-cost starter; you can switch later
    apiKey: apiKey);

var response = await client.CreateResponseAsync(
    userInputText: "Say 'ready' and explain what a tool call is in one sentence.",
    new ResponseCreationOptions());

Console.WriteLine(response.GetOutputText());
