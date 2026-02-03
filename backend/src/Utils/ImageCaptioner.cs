using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FlytIT.Chatbot.Utils;

namespace FlytIT.Chatbot.Utils;

public static class ImageCaptioner
{
    // Vision-modell
    private const string Model = "gpt-4o-mini";
    private static readonly HttpClient Http = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly SemaphoreSlim Gate = new(initialCount:
        ConfigHelper.GetIntOrDefault("CAPTION_MAX_CONCURRENCY", AppConstants.DEFAULT_CAPTION_MAX_CONCURRENCY));

    public static async Task<string> DescribeImageAsync(
        byte[] pngBytes,
        string openAiKey,
        string? site = null,
        string? taskHint = null,
        CancellationToken ct = default)
    {
        // Deaktivert via env?
        if ((Environment.GetEnvironmentVariable("INDEX_IMAGE_CAPTIONS") ?? "false")
            .Equals("false", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        await Gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(
                ConfigHelper.GetIntOrDefault("CAPTION_TIMEOUT_SECONDS", AppConstants.DEFAULT_CAPTION_TIMEOUT_SECONDS)));

            // Downscale for mindre payload
            var maxW = ConfigHelper.GetIntOrDefault("CAPTION_MAX_WIDTH", AppConstants.DEFAULT_CAPTION_MAX_WIDTH);
            pngBytes = PngDownscaleIfWider(pngBytes, maxW);

            var attempts = ConfigHelper.GetIntOrDefault("CAPTION_MAX_RETRIES", AppConstants.DEFAULT_CAPTION_MAX_RETRIES);
            for (int i = 0; i <= attempts; i++)
            {
                try
                {
                    return await DescribeOnceAsync(pngBytes, openAiKey, site, taskHint, cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (i == attempts) throw;
                }
                catch (HttpRequestException ex) when (i < attempts)
                {
                    Console.WriteLine($"[CAPTION] HTTP feil, retry {i + 1}/{attempts}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1 + i * 2), cts.Token);
                }
                catch (Exception ex) when (i < attempts)
                {
                    Console.WriteLine($"[CAPTION] Ukjent feil, retry {i + 1}/{attempts}: {ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(400 + i * 600), cts.Token);
                }
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CAPTION] Feil: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ✨ Ny implementasjon: Chat Completions for bilde + bedre feillogging
    private static async Task<string> DescribeOnceAsync(
        byte[] pngBytes,
        string openAiKey,
        string? site,
        string? taskHint,
        CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(pngBytes);

        var system = """
            Du er en assistent som beskriver bilder, figurer og tabeller fra dokumenter på norsk.
            Vær kort og presis. Skriv kun fakta du kan se. Ta med synlig tekst når relevant.
            """;
        if (!string.IsNullOrWhiteSpace(site)) system += $" Nettsidens kontekst er: {site}.";
        if (!string.IsNullOrWhiteSpace(taskHint)) system += $" Oppgave: {taskHint}.";

        var req = new
        {
            model = Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new {
                    role = "user",
                    content = new object[] {
                        new { type = "text", text = "Beskriv bildet kort. Ta med synlig tekst ved behov." },
                        new { type = "image_url", image_url = new { url = $"data:image/png;base64,{b64}" } }
                    }
                }
            },
            temperature = 0.2,
            max_tokens = 200
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openAiKey);
        msg.Content = new StringContent(JsonSerializer.Serialize(req, JsonOpts), Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[CAPTION] HTTP {(int)resp.StatusCode}: {err}");
            return string.Empty;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // choices[0].message.content
        try
        {
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(content) ? string.Empty : content.Trim();
        }
        catch
        {
            return TryExtractTextFromResponse(doc.RootElement)?.Trim() ?? string.Empty;
        }
    }

    private static byte[] PngDownscaleIfWider(byte[] input, int maxWidth)
    {
        try
        {
            using var src = SkiaSharp.SKBitmap.Decode(input);
            if (src == null || src.Width <= maxWidth) return input;

            var newW = maxWidth;
            var newH = Math.Max(1, (int)Math.Round(src.Height * (newW / (double)src.Width)));
            using var resized = new SkiaSharp.SKBitmap(newW, newH);
            using (var canvas = new SkiaSharp.SKCanvas(resized))
            {
                canvas.DrawBitmap(src, new SkiaSharp.SKRect(0, 0, newW, newH));
                canvas.Flush();
            }
            using var img = SkiaSharp.SKImage.FromBitmap(resized);
            using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        catch { return input; }
    }

    // Fallback-parser (hvis OpenAI endrer respons-format)
    private static string? TryExtractTextFromResponse(JsonElement root)
    {
        try
        {
            return root
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }
        catch { return null; }
    }
}
