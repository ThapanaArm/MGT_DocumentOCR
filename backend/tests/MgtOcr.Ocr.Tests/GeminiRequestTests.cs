using System.Net;
using MgtOcr.Core.Config;
using MgtOcr.Ocr;

public sealed class GeminiRequestTests
{
    private static AppConfig TestConfig() => new()
    {
        DbServer = "", DbName = "", DbUser = "", DbPassword = "", DbDriver = "", UploadDir = "",
        GeminiApiKey = "unit-test-key", GeminiEnvironment = "Test"
    };

    [Fact]
    public async Task LogsUsageEvenWhenOcrCannotReadTheResponse()
    {
        using var client = new HttpClient(new FakeGemini("""
            {"responseId":"response-1","usageMetadata":{"promptTokenCount":1000,
              "candidatesTokenCount":100,"thoughtsTokenCount":25,"cachedContentTokenCount":200}}
            """));
        var config = TestConfig();
        using var scope = GeminiRequest.Begin(10, 20, "AP");

        var (status, _) = await GeminiRequest.SendAsync(client, config, "{}", CancellationToken.None,
            "ocr.supporting", [3, 4], 2);

        Assert.Equal(200, status);
        var usage = Assert.Single(scope.Requests);
        Assert.Equal(10L, usage.JobId);
        Assert.Equal(new[] { 3, 4 }, usage.PageNumbers);
        Assert.Equal(2, usage.Attempt);
        Assert.Equal("response-1", usage.ResponseId);
        Assert.Equal(0.0005585m, usage.Cost);
    }

    [Fact]
    public async Task MissingTokenCountLeavesCostUnknown()
    {
        using var client = new HttpClient(new FakeGemini("""{"usageMetadata":{"promptTokenCount":1000}}"""));
        using var scope = GeminiRequest.Begin();
        await GeminiRequest.SendAsync(client, TestConfig(),
            "{}", CancellationToken.None, "ocr.read");

        Assert.Null(Assert.Single(scope.Requests).Cost);
    }

    private sealed class FakeGemini(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("unit-test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.Equal("", request.RequestUri!.Query);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
