using System.Net.Http.Headers;
using System.Text.Json;

public static class ModelCalls
{
    public static HttpClient CreateClient(IHttpClientFactory clients)
    {
        var c = clients.CreateClient("model");
        var key = Environment.GetEnvironmentVariable("CCTV_API_KEY");
        if (!string.IsNullOrWhiteSpace(key)) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    public static async Task<JsonDocument> Ask(HttpClient client, ModelSettings config, Func<bool, object> payload, bool scores, CancellationToken ct)
    {
        try
        {
            using var response = await DescriptionWorker.SendModel(client, config, payload(scores), ct);
            return await DescriptionWorker.ReadResponse(response, ct);
        }
        catch (UserError ex) when (scores && ex.Message.Contains("logprob", StringComparison.OrdinalIgnoreCase))
        {
            using var response = await DescriptionWorker.SendModel(client, config, payload(false), ct);
            return await DescriptionWorker.ReadResponse(response, ct);
        }
    }

    public static string Content(JsonElement message)
    {
        var raw = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(raw))
            foreach (var key in new[] { "reasoning_content", "reasoning", "thinking" })
                if (message.TryGetProperty(key, out var alternate) && alternate.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alternate.GetString()))
                {
                    raw = alternate.GetString()!; break;
                }
        return raw;
    }
}