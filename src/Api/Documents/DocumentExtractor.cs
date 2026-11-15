using System.Text.Json;
using Anthropic.Models.Messages;
using Api.Services;

namespace Api.Documents;

/// <summary>Lab 6: turn a document into checked data, or send it to a person.</summary>
public class DocumentExtractor(IClaudeChat claude, ClaudeOptions options)
{
    public const int MaxAttempts = 2; // first try + one retry
    public const int MaxDocumentLength = 20_000;

    const string Prompt = """
        Extract the data from this Acme HR document. It is a medical certificate (MC), an expense claim, or something else (kind "other").
        Dates as yyyy-MM-dd. Numeric dates in these documents are day first (dd/MM/yyyy, Malaysian format): 03/10/2026 is 2026-10-03.
        Amounts as numbers without currency symbols. Use null for the part that does not apply.
        Copy values exactly as written. Do not fix or invent numbers.
        """;

    /// <summary>The JSON schema Claude must follow (structured outputs).</summary>
    public static readonly Dictionary<string, JsonElement> Schema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "kind", "medical_certificate", "expense_claim" },
        properties = new
        {
            kind = new { type = "string", @enum = new[] { "medical_certificate", "expense_claim", "other" } },
            medical_certificate = new
            {
                type = new[] { "object", "null" },
                additionalProperties = false,
                required = new[] { "employee_name", "clinic", "date_from", "date_to", "days" },
                properties = new
                {
                    employee_name = new { type = "string" }, clinic = new { type = "string" },
                    date_from = new { type = "string" }, date_to = new { type = "string" }, days = new { type = "integer" },
                },
            },
            expense_claim = new
            {
                type = new[] { "object", "null" },
                additionalProperties = false,
                required = new[] { "claim_no", "vendor", "date", "currency", "subtotal", "tax", "total", "items" },
                properties = new
                {
                    claim_no = new { type = "string" }, vendor = new { type = "string" }, date = new { type = "string" },
                    currency = new { type = "string" }, subtotal = new { type = "number" }, tax = new { type = "number" }, total = new { type = "number" },
                    items = new
                    {
                        type = "array",
                        items = new { type = "object", additionalProperties = false, required = new[] { "description", "amount" }, properties = new { description = new { type = "string" }, amount = new { type = "number" } } },
                    },
                },
            },
        },
    }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

    public async Task<ExtractionResult> ExtractAsync(string documentText, CancellationToken ct = default)
    {
        if (documentText.Length > MaxDocumentLength)
            return new ExtractionResult(null, DocumentStatus.NeedsReview, [$"document is longer than {MaxDocumentLength} characters"], 0);

        var history = new List<MessageParam> { new UserMessageParam { Content = $"<document>\n{documentText}\n</document>" } };
        Extraction? data = null;
        List<string> problems = [];
        var attempts = 0;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            attempts = attempt;
            var res = await claude.SendAsync(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 1024,
                System = Prompt,
                OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = Schema } },
                Messages = history.ToList(), // a copy: the retry adds to history after this request is sent
            }, ct);

            // Cut off or refused: the JSON cannot be trusted, and a retry would hit the same limit.
            if (res.StopReason != StopReason.EndTurn)
                return new ExtractionResult(null, DocumentStatus.NeedsReview, [$"Claude stopped early ({res.StopReason?.Raw() ?? "unknown"})"], attempt);

            var text = string.Concat(res.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            (data, problems) = Parse(text);
            if (problems.Count == 0) return new ExtractionResult(data, DocumentStatus.Valid, [], attempt);
            if (data?.Kind == "other") break; // not an MC or a claim: a retry cannot fix that
            if (attempt == MaxAttempts) break;

            // One retry: Claude sees its own answer and the exact problems found in C#.
            history.Add(new AssistantMessageParam { Content = text });
            history.Add(new UserMessageParam
            {
                Content = "Your answer has these problems:\n- " + string.Join("\n- ", problems) +
                          "\nRead the document again and answer with the corrected JSON. Copy values exactly as written: " +
                          "if the document itself is wrong, keep its values and do not change them to pass these checks.",
            });
        }
        return new ExtractionResult(data, DocumentStatus.NeedsReview, problems, attempts);
    }

    /// <summary>The schema guarantees the shape; the meaning is checked by DocumentValidator in C#.</summary>
    static (Extraction? Data, List<string> Problems) Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Extraction>(text) is { } data
                ? (data, DocumentValidator.Check(data))
                : (null, ["the answer is empty"]);
        }
        catch (JsonException e)
        {
            return (null, [$"the answer is not valid JSON ({e.Message})"]);
        }
    }
}
