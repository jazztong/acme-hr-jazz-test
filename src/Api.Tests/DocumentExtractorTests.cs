using System.Text.Json;
using Api.Documents;
using Api.Services;
using Xunit;

namespace Api.Tests;

public class DocumentExtractorTests
{
    static readonly ClaudeOptions Opts = new("claude-sonnet-5-5");
    const string GoodMc = """{"kind":"medical_certificate","medical_certificate":{"employee_name":"Aisyah Rahman","clinic":"Klinik Mesra","date_from":"2026-10-05","date_to":"2026-10-06","days":2},"expense_claim":null}""";
    const string BadMc = """{"kind":"medical_certificate","medical_certificate":{"employee_name":"Aisyah Rahman","clinic":"Klinik Mesra","date_from":"2026-10-05","date_to":"2026-10-06","days":3},"expense_claim":null}""";
    const string GoodClaim = """{"kind":"expense_claim","medical_certificate":null,"expense_claim":{"claim_no":"EC-1","vendor":"Kedai","date":"2026-10-11","currency":"MYR","subtotal":100.00,"tax":6.00,"total":106.00,"items":[{"description":"Book","amount":100.00}]}}""";

    [Fact]
    public async Task Valid_first_time_is_Valid_with_one_call_and_schema_sent()
    {
        var fake = new FakeClaude(FakeClaude.Text(GoodMc));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("MC text");
        Assert.Equal(DocumentStatus.Valid, r.Status);
        Assert.Equal(1, r.Attempts);
        Assert.Single(fake.Requests);
        Assert.NotNull(fake.Requests[0].OutputConfig);
    }

    [Fact]
    public async Task Invalid_then_fixed_retries_once_with_the_error()
    {
        var fake = new FakeClaude(FakeClaude.Text(BadMc), FakeClaude.Text(GoodMc));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("MC text");
        Assert.Equal(DocumentStatus.Valid, r.Status);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(3, fake.Requests[1].Messages.Count); // document, Claude's answer, the error
    }

    [Fact]
    public async Task Invalid_twice_goes_to_NeedsReview_and_stops()
    {
        var fake = new FakeClaude(FakeClaude.Text(BadMc), FakeClaude.Text(BadMc), FakeClaude.Text(GoodMc));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("MC text");
        Assert.Equal(DocumentStatus.NeedsReview, r.Status);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains(r.Problems, p => p.Contains("days"));
    }

    [Fact]
    public async Task Not_json_goes_to_NeedsReview()
    {
        var fake = new FakeClaude(FakeClaude.Text("sorry"), FakeClaude.Text("still not json"));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("x");
        Assert.Equal(DocumentStatus.NeedsReview, r.Status);
    }

    [Fact]
    public async Task Other_document_is_NeedsReview_without_retry()
    {
        var fake = new FakeClaude(FakeClaude.Text("""{"kind":"other","medical_certificate":null,"expense_claim":null}"""));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("memo");
        Assert.Equal(DocumentStatus.NeedsReview, r.Status);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Valid_claim_passes()
    {
        var r = await new DocumentExtractor(new FakeClaude(FakeClaude.Text(GoodClaim)), Opts).ExtractAsync("claim");
        Assert.Equal(DocumentStatus.Valid, r.Status);
        Assert.Equal(106.00m, r.Data!.ExpenseClaim!.Total);
    }

    const string BadClaim = """{"kind":"expense_claim","medical_certificate":null,"expense_claim":{"claim_no":"EC-2","vendor":"Kedai","date":"2026-10-11","currency":"MYR","subtotal":100.00,"tax":6.00,"total":107.00,"items":[{"description":"Book","amount":100.00}]}}""";

    [Fact]
    public async Task Schema_is_sent_as_the_json_output_format()
    {
        var fake = new FakeClaude(FakeClaude.Text(GoodMc));
        await new DocumentExtractor(fake, Opts).ExtractAsync("MC text");
        var schema = JsonSerializer.SerializeToElement(fake.Requests[0].OutputConfig).GetProperty("format").GetProperty("schema");
        Assert.Equal(["kind", "medical_certificate", "expense_claim"], schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Retry_sends_Claudes_own_answer_and_the_exact_problems()
    {
        var fake = new FakeClaude(FakeClaude.Text(BadMc), FakeClaude.Text(GoodMc));
        await new DocumentExtractor(fake, Opts).ExtractAsync("MC text");
        var retry = JsonSerializer.SerializeToElement(fake.Requests[1].Messages).EnumerateArray().ToList();
        Assert.Equal("assistant", retry[1].GetProperty("role").GetString());
        Assert.Contains("\"days\":3", retry[1].GetRawText().Replace("\\u0022", "\"").Replace("\\\"", "\""));
        Assert.Equal("user", retry[2].GetProperty("role").GetString());
        Assert.Contains("days is 3 but the date range is 2 days", retry[2].GetRawText());
    }

    [Fact]
    public async Task Stop_reason_other_than_end_turn_is_NeedsReview_without_retry()
    {
        var fake = new FakeClaude(FakeClaude.Text(GoodMc, stop: "max_tokens"), FakeClaude.Text(GoodMc));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("MC text");
        Assert.Equal(DocumentStatus.NeedsReview, r.Status);
        Assert.Single(fake.Requests);
        Assert.Contains(r.Problems, p => p.Contains("max_tokens"));
    }

    [Fact]
    public async Task Not_json_retries_once_then_NeedsReview_with_the_reason()
    {
        var fake = new FakeClaude(FakeClaude.Text("sorry"), FakeClaude.Text("still not json"), FakeClaude.Text(GoodMc));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("x");
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains(r.Problems, p => p.Contains("not valid JSON"));
    }

    [Fact]
    public async Task Claim_wrong_on_paper_stays_NeedsReview()
    {
        var fake = new FakeClaude(FakeClaude.Text(BadClaim), FakeClaude.Text(BadClaim));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync("claim with a wrong total");
        Assert.Equal(DocumentStatus.NeedsReview, r.Status);
        Assert.Equal(2, r.Attempts);
        Assert.Contains(r.Problems, p => p.Contains("total"));
    }

    [Fact]
    public async Task Document_over_20000_characters_is_NeedsReview_without_calling_Claude()
    {
        var fake = new FakeClaude(FakeClaude.Text(GoodMc));
        var r = await new DocumentExtractor(fake, Opts).ExtractAsync(new string('x', DocumentExtractor.MaxDocumentLength + 1));
        Assert.Equal(DocumentStatus.NeedsReview, r.Status);
        Assert.Empty(fake.Requests);
        Assert.Equal(0, r.Attempts);
    }
}

public class DocumentValidatorTests
{
    static Extraction Claim(decimal sub, decimal tax, decimal total) =>
        new("expense_claim", null, new ExpenseClaim("EC-1", "V", "2026-10-01", "MYR", sub, tax, total, [new("x", sub)]));

    [Fact] public void Total_must_equal_subtotal_plus_tax() => Assert.Contains(DocumentValidator.Check(Claim(24, 1.44m, 26.44m)), p => p.Contains("total"));
    [Fact] public void Correct_claim_has_no_problems() => Assert.Empty(DocumentValidator.Check(Claim(24, 1.44m, 25.44m)));
    [Fact] public void Date_to_before_date_from_is_a_problem() =>
        Assert.Contains(DocumentValidator.Check(new("medical_certificate", new MedicalCertificate("A", "K", "2026-10-30", "2026-10-29", 1), null)), p => p.Contains("before"));
    [Fact] public void Bad_date_format_is_a_problem() =>
        Assert.Contains(DocumentValidator.Check(new("medical_certificate", new MedicalCertificate("A", "K", "30/10/2026", "2026-10-30", 1), null)), p => p.Contains("yyyy-MM-dd"));
}
