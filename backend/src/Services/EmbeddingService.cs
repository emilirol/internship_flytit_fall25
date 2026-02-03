using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FlytIT.Chatbot.Services;

public interface IEmbeddingService
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly string _apiKey;
    private readonly string _model;
    private static readonly HttpClient _http = new();

    public OpenAiEmbeddingService(string apiKey, string model = "text-embedding-3-small")
    {
        _apiKey = apiKey;
        _model  = model;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var payload = new
        {
            model = _model,
            input = text
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(msg, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return Array.Empty<float>();

        var emb = data[0].GetProperty("embedding");
        var arr = new float[emb.GetArrayLength()];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = emb[i].GetSingle(); // JSON number → float

        return arr;
    }
}
