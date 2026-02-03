using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

using DotNetEnv;

using Elastic.Clients.Elasticsearch;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using UglyToad.PdfPig;

using FlytIT.Chatbot.Indexing;      // FileIndexer
using FlytIT.Chatbot.Models;        // Doc
using FlytIT.Chatbot.Services;
using FlytIT.Chatbot.Utils;

namespace FlytIT.Chatbot;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 1) Last config (.env + appsettings + env + cmdline)
        Env.TraversePath().Load();
        var cfgBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production")}.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args, new Dictionary<string, string>
            {
                ["--cmd"] = "Command",
                ["--folder"] = "Indexer:Folder"
            });
        var config = cfgBuilder.Build();

        // 2) Felles konfig
        string command = (config["Command"] ?? "web").ToLowerInvariant();
        string esUrl = config["Elasticsearch:Url"] ?? "http://localhost:9200";
        string esIndex = config["Elasticsearch:Index"] ?? "flytit-chatbot";
        bool deleteIdx = bool.TryParse(config["Elasticsearch:DeleteIndexFirst"], out var d) && d;

        string? envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? cfgKey = config["OpenAI:ApiKey"];
        string openAiKey = !string.IsNullOrWhiteSpace(envKey) ? envKey! : (cfgKey ?? string.Empty);

        string embeddingModel = config["OpenAI:EmbeddingModel"] ?? "text-embedding-3-small";
        string chatModel = config["OpenAI:ChatModel"] ?? "gpt-4o-mini";

        // 3) Velg modus
        switch (command)
        {
            case "index":
                await RunIndexMode(config, esUrl, esIndex, deleteIdx, openAiKey, embeddingModel);
                break;

            case "siteindex":
                await RunSiteIndexMode(config, esUrl, esIndex, openAiKey, embeddingModel);
                break;

            case "web":
            default:
                await RunWebHostAsync(config, esUrl, esIndex, openAiKey, chatModel, embeddingModel, args);
                break;
        }
    }

    // >>> Program.cs (legg dette inne i Program-klassen) <<<

    private static async Task StartupIndexingAsync(
        IConfiguration config,
        string esUrl,
        string indexName,
        string openAiKey,
        string embeddingModel)
    {
        var es = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(esUrl)));

        // 1) Sørg for at index finnes
        var bootstrap = new FlytIT.Chatbot.Services.IndexBootstrapper(indexName);
        await bootstrap.EnsureIndexAsync(es);

        // 2) Mappe-indeksering ved oppstart (valgfritt)
        // Slås på hvis Indexer:RunOnStartup=true OG Indexer:Folder er satt
        var runFolder = bool.TryParse(config["Indexer:RunOnStartup"], out var runIdx) && runIdx;
        var folder = config["Indexer:Folder"];
        if (runFolder && !string.IsNullOrWhiteSpace(folder))
        {
            var pattern = config["Indexer:Pattern"] ?? "*.pdf;*.PDF";
            var recursive = !bool.TryParse(config["Indexer:Recursive"], out var r) || r;
            var site = config["Indexer:Site"];
            var maxConc = int.TryParse(config["Indexer:MaxConcurrency"], out var mc) ? mc : 2;

            // respekter miljøflagg om captions/rendering hvis satt i appsettings
            Environment.SetEnvironmentVariable("INDEX_RENDER_PAGES", config["Indexer:RenderPages"] ?? "true");
            Environment.SetEnvironmentVariable("INDEX_IMAGE_CAPTIONS", config["Indexer:ImageCaptions"] ?? "true");
            Environment.SetEnvironmentVariable("CAPTION_MODE", config["Indexer:CaptionMode"] ?? "auto");

            var embeddings = new FlytIT.Chatbot.Services.OpenAiEmbeddingService(openAiKey, embeddingModel);

            Console.WriteLine($"[STARTUP] Indekserer mappe: {folder} (pattern={pattern}, recursive={recursive})");
            var count = await FlytIT.Chatbot.Indexing.FileIndexer.IndexFolderAsync(
                es: es,
                embeddings: embeddings,
                indexName: indexName,
                folder: folder!,
                pattern: pattern,
                recursive: recursive,
                site: site,
                maxConcurrency: maxConc);
            Console.WriteLine($"[STARTUP] Mappe-indeksering ferdig. Filer: {count}");
        }

        // 3) Site-indeksering ved oppstart (valgfritt)
        // Slås på hvis Crawler:RunOnStartup=true OG Crawler:StartUrl er satt
        var runSite = bool.TryParse(config["Crawler:RunOnStartup"], out var runSiteIdx) && runSiteIdx;
        var startUrl = config["Crawler:StartUrl"];
        if (runSite && !string.IsNullOrWhiteSpace(startUrl))
        {
            Console.WriteLine($"[STARTUP] Site-indekserer fra sitemap: {startUrl}");
            await RunSiteIndexMode(config, esUrl, indexName, openAiKey, embeddingModel);
        }
    }


    // ===== WEB-HOST (Minimal API + SignalR + RAG) =====
    private static async Task RunWebHostAsync(
        IConfiguration config,
        string esUrl,
        string esIndex,
        string openAiKey,
        string chatModel,
        string embeddingModel,
        string[] args)
    {
        var esClient = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(esUrl)));
        await EnsureIndexAsync(esClient, esIndex);

        var builder = WebApplication.CreateBuilder(args);

        // CORS: bruk appsettings hvis satt, ellers localhost:5173 som default
        var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();

        builder.Services.AddCors(o =>
        {
            o.AddPolicy("dev", p =>
            {
                var origins = (allowedOrigins.Length > 0)
                    ? allowedOrigins
                    : new[] { "http://localhost:5173", "http://127.0.0.1:5173" };
                p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
            });
        });

        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);

        // OpenAI + Embeddings
        builder.Services.AddHttpClient("OpenAI", c =>
        {
            c.BaseAddress = new Uri("https://api.openai.com/");
            if (!string.IsNullOrWhiteSpace(openAiKey))
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openAiKey);
            c.Timeout = TimeSpan.FromSeconds(90);
        });
        builder.Services.AddSingleton<IEmbeddingService>(sp => new OpenAiEmbeddingService(openAiKey, embeddingModel));

        // Elasticsearch HTTP for søk
        builder.Services.AddHttpClient("ES", c =>
        {
            c.BaseAddress = new Uri(esUrl.TrimEnd('/') + "/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        // RAG + generator
        builder.Services.AddSingleton<IRagRetriever>(sp =>
            new RagRetriever(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IEmbeddingService>(),
                esIndex));

        builder.Services.AddSingleton<IChatTextGenerator>(sp =>
            new OpenAiChatTextGenerator(
                sp.GetRequiredService<IHttpClientFactory>(),
                chatModel,
                hasKey: !string.IsNullOrWhiteSpace(openAiKey),
                rag: sp.GetRequiredService<IRagRetriever>()
            ));

        var app = builder.Build();
        app.UseCors("dev");

        // Health
        app.MapGet("/", () => Results.Text("FlytIT Chatbot backend OK. Bruk POST /chat eller /api/chat, eller SignalR /hub/chat."));

        // Felles handler som returnerer { reply, sources }
        static async Task<IResult> ChatEndpoint(IChatTextGenerator svc, ChatReq req)
        {
            var result = await svc.GetReplyWithLinksAsync(req.Message ?? string.Empty, req.History, req.Site);
            return Results.Json(new { reply = result.Text, sources = result.Sources });
        }

        // REST-chat → to paths, samme handler
        app.MapPost("/chat", ([FromServices] IChatTextGenerator svc, [FromBody] ChatReq req) => ChatEndpoint(svc, req));
        app.MapPost("/api/chat", ([FromServices] IChatTextGenerator svc, [FromBody] ChatReq req) => ChatEndpoint(svc, req));

        // SignalR live (tekst-stream)
        app.MapHub<ChatHub>("/hub/chat");

        var url = config["Urls"] ?? "http://0.0.0.0:5000";
        app.Urls.Add(url);
        Console.WriteLine($"WebHost lytter på {url} (tillatte origins: {(allowedOrigins.Length > 0 ? string.Join(", ", allowedOrigins) : "localhost:5173")})");
        await app.RunAsync();
    }

    // ===== INDEXER =====
    private static async Task RunIndexMode(
        IConfiguration config,
        string esUrl,
        string indexName,
        bool deleteIdx,
        string openAiKey,
        string embeddingModel)
    {
        var folder = config["Indexer:Folder"] ?? throw new Exception("Indexer:Folder mangler i appsettings.");
        var pattern = config["Indexer:Pattern"] ?? "*.pdf;*.PDF";
        var recursive = !bool.TryParse(config["Indexer:Recursive"], out var r) || r;
        var site = config["Indexer:Site"];
        var maxConc = int.TryParse(config["Indexer:MaxConcurrency"], out var mc) ? mc : 2;

        Environment.SetEnvironmentVariable("INDEX_RENDER_PAGES", config["Indexer:RenderPages"] ?? "true");
        Environment.SetEnvironmentVariable("INDEX_IMAGE_CAPTIONS", config["Indexer:ImageCaptions"] ?? "true");
        Environment.SetEnvironmentVariable("CAPTION_MODE", config["Indexer:CaptionMode"] ?? "auto");

        var es = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(esUrl)));

        if (deleteIdx)
        {
            try { await es.Indices.DeleteAsync(indexName); } catch { /* ok hvis ikke finnes */ }
        }

        await EnsureIndexAsync(es, indexName);

        IEmbeddingService embeddings = new OpenAiEmbeddingService(openAiKey, embeddingModel);

        Console.WriteLine($"Indekserer: folder=\"{folder}\", pattern=\"{pattern}\", recursive={recursive}, index=\"{indexName}\"");

        var count = await FileIndexer.IndexFolderAsync(
            es: es,
            embeddings: embeddings,
            indexName: indexName,
            folder: folder,
            pattern: pattern,
            recursive: recursive,
            site: site,
            maxConcurrency: maxConc);

        Console.WriteLine($"Ferdig: {count} filer prosessert.");
    }

    // ===== SITEINDEX (sitemap) =====
    private static async Task RunSiteIndexMode(
        IConfiguration config,
        string esUrl,
        string indexName,
        string openAiKey,
        string embeddingModel)
    {
        var startUrl = config["Crawler:StartUrl"] ?? throw new Exception("Crawler:StartUrl mangler i appsettings.");
        var maxPages = int.TryParse(config["Crawler:MaxPages"], out var mp) ? mp : 500;
        var siteLabel = config["Indexer:Site"]; // f.eks. "intranett"

        var es = new Elastic.Clients.Elasticsearch.ElasticsearchClient(
            new Elastic.Clients.Elasticsearch.ElasticsearchClientSettings(new Uri(esUrl)));

        await EnsureIndexAsync(es, indexName);

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FlytIT-Indexer/1.0");

        var embeddings = new OpenAiEmbeddingService(openAiKey, embeddingModel);

        // 1) Hent sitemap
        var sitemapUrl = startUrl.TrimEnd('/') + "/sitemap.xml";
        Console.WriteLine($"[SITE] Henter sitemap: {sitemapUrl}");
        string sitemapXml;
        try
        {
            sitemapXml = await http.GetStringAsync(sitemapUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SITE] Fant ikke sitemap ({ex.Message}).");
            return;
        }

        // 2) Ekstraher URL-er fra <loc>…</loc>
        var locMatches = Regex.Matches(
            sitemapXml,
            @"<loc>\s*(.*?)\s*</loc>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var urls = locMatches
            .Select(m => m.Groups[1].Value.Trim())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxPages)
            .ToList();

        Console.WriteLine($"[SITE] Fant {urls.Count} URL-er i sitemap.");

        int ok = 0, fail = 0;
        foreach (var url in urls)
        {
            try
            {
                var html = await http.GetStringAsync(url);
                var title = TryExtractTitle(html) ?? url;
                var text = HtmlProcessor.StripHtml(html);

                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine($"[SITE] Tomt innhold: {url}");
                    continue;
                }

                var vec = await embeddings.EmbedAsync(text);

                var doc = new Doc
                {
                    Title = title,
                    Content = text,
                    Embedding = vec,
                    Site = siteLabel,
                    SourcePath = url
                };

                // Finn PDF-er på siden og indekser dem også
                var pdfUrls = ExtractPdfLinks(url, html).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
                foreach (var pdfUrl in pdfUrls)
                {
                    try
                    {
                        var pdfBytes = await http.GetByteArrayAsync(pdfUrl);
                        var pdfText = ReadPdfTextFromBytes(pdfBytes);
                        if (string.IsNullOrWhiteSpace(pdfText)) continue;

                        var pdfTitle = Path.GetFileNameWithoutExtension(new Uri(pdfUrl).AbsolutePath);
                        var vecPdf = await embeddings.EmbedAsync(pdfText);

                        var pdfDoc = new Doc
                        {
                            Title = string.IsNullOrWhiteSpace(pdfTitle) ? "Monteringsanvisning" : pdfTitle,
                            Content = pdfText,
                            Embedding = vecPdf,
                            Site = siteLabel,
                            SourcePath = pdfUrl
                        };

                        var pdfResp = await es.IndexAsync(pdfDoc, i => i.Index(indexName).Id(pdfUrl));
                        if (pdfResp.IsValidResponse)
                            Console.WriteLine($"[SITE][PDF] OK: {pdfUrl}");
                        else
                            Console.WriteLine($"[SITE][PDF] FAIL {pdfUrl}: {pdfResp.ElasticsearchServerError?.Error?.Reason}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SITE][PDF] {pdfUrl}: {ex.Message}");
                    }
                }

                // Bruk URL som dokument-ID for å unngå duplikater ved reindeksering
                var resp = await es.IndexAsync(doc, i => i.Index(indexName).Id(url));
                if (resp.IsValidResponse)
                {
                    ok++;
                    Console.WriteLine($"[SITE] OK: {url}");
                }
                else
                {
                    fail++;
                    Console.WriteLine($"[SITE] FAIL {url}: {resp.ElasticsearchServerError?.Error?.Reason}");
                }
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"[SITE] {url}: {ex.Message}");
            }
        }

        Console.WriteLine($"[SITE] Ferdig. OK={ok}, FAIL={fail}");
    }
    private static async Task EnsureIndexAsync(ElasticsearchClient es, string indexName)
    {
        var bootstrap = new IndexBootstrapper(indexName);
        await bootstrap.EnsureIndexAsync(es);
    }
    // === Hjelpere for HTML ===

    private static string? TryExtractTitle(string html)
    {
        var m = Regex.Match(
            html,
            @"<title>\s*(.*?)\s*</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    private static IEnumerable<string> ExtractPdfLinks(string pageUrl, string html)
    {
        foreach (Match m in Regex.Matches(html, "href\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Value.Trim();
            if (href.StartsWith("#") || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var abs = ToAbsoluteUrl(pageUrl, href);
                if (!string.IsNullOrWhiteSpace(abs)) yield return abs!;
            }
        }
    }

    private static string? ToAbsoluteUrl(string pageUrl, string href)
    {
        try
        {
            var uri = new Uri(href, UriKind.RelativeOrAbsolute);
            if (!uri.IsAbsoluteUri) uri = new Uri(new Uri(pageUrl), uri);
            return uri.ToString();
        }
        catch { return null; }
    }

    private static string ReadPdfTextFromBytes(byte[] data)
    {
        var sb = new StringBuilder();
        using var ms = new MemoryStream(data);
        using var pdf = PdfDocument.Open(ms);
        foreach (var page in pdf.GetPages())
        {
            var txt = page.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(txt))
            {
                sb.AppendLine(txt.Trim());
                sb.AppendLine();
            }
        }
        return sb.ToString().Trim();
    }
}

// ===== SignalR Hub + kontrakter =====
public interface IChatClient
{
    Task ReceiveBotDelta(string token);
    Task BotCompleted();
    Task ReceiveError(string message);
}

public class ChatHub : Hub<IChatClient>
{
    private readonly IChatTextGenerator _chat;
    public ChatHub(IChatTextGenerator chat) => _chat = chat;

    // SendUserMessage kan få med 'site' for å avgrense søk
    public async Task SendUserMessage(string conversationId, string text, string? site = null)
    {
        string full = text ?? string.Empty;
        try
        {
            full = await _chat.GetReplyAsync(full, null, site);
        }
        catch (Exception ex)
        {
            await Clients.Caller.ReceiveError("Generator error: " + ex.Message);
        }

        foreach (var w in (full ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            await Clients.Caller.ReceiveBotDelta(w);
            await Task.Delay(40);
        }
        await Clients.Caller.BotCompleted();
    }
}

// ===== Chat generator =====
public interface IChatTextGenerator
{
    Task<string> GetReplyAsync(string userMessage, List<Msg>? history = null, string? site = null);
    Task<ChatAnswer> GetReplyWithLinksAsync(string userMessage, List<Msg>? history = null, string? site = null);
}

public record SourceLink(string Title, string Url);
public record ChatAnswer(string Text, List<SourceLink> Sources);

public sealed class OpenAiChatTextGenerator : IChatTextGenerator
{
    private readonly IHttpClientFactory _http;
    private readonly string _model;
    private readonly bool _hasKey;
    private readonly IRagRetriever _rag;

    public OpenAiChatTextGenerator(IHttpClientFactory http, string model, bool hasKey, IRagRetriever rag)
    {
        _http = http;
        _model = model;
        _hasKey = hasKey;
        _rag = rag;
    }

    // Beholdt for SignalR / bakoverkomp
    public async Task<string> GetReplyAsync(string userMessage, List<Msg>? history = null, string? site = null)
    {
        var res = await GetReplyWithLinksAsync(userMessage, history, site);
        return res.Text;
    }

    // ---------- PUNKT 1: lenker med rensing + #page for PDF ----------
    private static string CleanUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        // Encode vanlige “problemtegn” uten å knekke query/fragments
        return url.Replace(" ", "%20")
                  .Replace("(", "%28")
                  .Replace(")", "%29");
    }

    public async Task<ChatAnswer> GetReplyWithLinksAsync(string userMessage, List<Msg>? history = null, string? site = null)
    {
        var ragRes = await _rag.RetrieveAsync(userMessage, site, take: 8);

        // Bygg link-objekter fra kildene (unik URL inkl. #page for PDF, maks 5). Bare http/https.
        var links = ragRes.Sources
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath)
                && (s.SourcePath!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || s.SourcePath!.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            .Select(s =>
            {
                var raw = s.SourcePath!;
                if (s.Page.HasValue && raw.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    raw = $"{raw}#page={s.Page.Value}";

                var url = CleanUrl(raw);

                string title = s.Title;
                if (string.IsNullOrWhiteSpace(title))
                {
                    try
                    {
                        var u = new Uri(url);
                        title = Uri.UnescapeDataString(u.Segments.LastOrDefault()?.Trim('/') ?? u.Host);
                    }
                    catch { title = url; }
                }

                return new { title, url };
            })
            .GroupBy(x => x.url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => x.url.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.title, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(x => new SourceLink(x.title, x.url))
            .ToList();

        // Uten API-nøkkel → enkel fallbacktekst + kilder
        if (!_hasKey)
        {
            string text;
            if (ragRes.Contexts.Count > 0)
            {
                var titles = ragRes.Sources
                    .Select(s => s.Title)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct()
                    .Take(5);
                text = "(Lokalt søk) " + userMessage + "\n\nBasert på dokumenter:\n- " + string.Join("\n- ", titles);
            }
            else
            {
                text = "Echo: " + userMessage;
            }
            return new ChatAnswer(text, links);
        }

        // Med nøkkel → prompt til LLM
        var sep = "\n\n---\n\n";
        string prompt =
        $@"Du er en hjelpsom assistent. Svar KORT og PRESIST på norsk.
        Bruk KUN informasjon i utdragene under. Hvis svaret ikke finnes der, si: ""Jeg vet dessverre ikke"".
        Ikke skriv kildehenvisninger i selve teksten; de legges til automatisk.

        Utdrag:
        {string.Join(sep, ragRes.Contexts)}

        Spørsmål: {userMessage}
        Svar:";

        var http = _http.CreateClient("OpenAI");
        var body = new
        {
            model = _model,
            input = new object[]
            {
                new { role = "user", content = new object[] { new { type = "input_text", text = prompt } } }
            },
            temperature = 0.2
        };

        string answerText;
        using (var resp = await http.PostAsync("v1/responses",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")))
        {
            resp.EnsureSuccessStatusCode();
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var json = await JsonDocument.ParseAsync(stream);
            answerText = json.RootElement
                .GetProperty("output")[0]
                .GetProperty("content")[0]
                .GetProperty("text").GetString() ?? string.Empty;
        }

        return new ChatAnswer(answerText, links);
    }
    // ---------- /punkt 1 ----------
}

// ===== RAG =====
public interface IRagRetriever
{
    Task<RagResult> RetrieveAsync(string question, string? site, int take = 8);
}

public record RagChunk(string Id, string Title, string? SourcePath, int? Page, double? Score, string Snippet);
public record RagResult(List<string> Contexts, List<RagChunk> Sources);

public sealed class RagRetriever : IRagRetriever
{
    private readonly IHttpClientFactory _http;
    private readonly IEmbeddingService _embeddings;
    private readonly string _indexName;

    public RagRetriever(IHttpClientFactory http, IEmbeddingService embeddings, string indexName)
    {
        _http = http; _embeddings = embeddings; _indexName = indexName;
    }

    public async Task<RagResult> RetrieveAsync(string question, string? site, int take = 8)
    {
        var es = _http.CreateClient("ES");

        // 1) Embedding for KNN
        var qVec = await _embeddings.EmbedAsync(question);

        // helper: POST JSON, return raw text
        static async Task<string> PostJsonAsync(HttpClient client, string path, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            using var resp = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"ES {resp.StatusCode}: {text}");
            return text;
        }

        // 2) BM25 (med highlight) + boost på montering/veiledning
        var boolQuery = new Dictionary<string, object?>
        {
            ["must"] = new Dictionary<string, object?>
            {
                ["match"] = new Dictionary<string, object?>
                {
                    ["content"] = new Dictionary<string, object?>
                    {
                        ["query"] = question,
                        ["operator"] = "and"
                    }
                }
            }
        };
        if (!string.IsNullOrWhiteSpace(site))
        {
            boolQuery["filter"] = new object[] { new Dictionary<string, object?> { ["term"] = new Dictionary<string, object?> { ["site"] = site } } };
        }

        // BOOST: ord som pleier å peke på monteringsanvisninger
        var montWords = new[] { "monter", "montering", "monteringsanvisning", "montasje", "installasjon", "installasjonsveiledning", "manual", "veiledning" };
        var shouldClauses = new List<object>();
        foreach (var w in montWords)
        {
            shouldClauses.Add(new Dictionary<string, object?>
            {
                ["multi_match"] = new Dictionary<string, object?>
                {
                    ["query"] = w,
                    ["fields"] = new[] { "title^3", "content", "sourcePath^2" },
                    ["type"] = "best_fields"
                }
            });
        }
        boolQuery["should"] = shouldClauses;
        boolQuery["minimum_should_match"] = 0;

        var bm25Payload = new Dictionary<string, object?>
        {
            ["size"] = 20,
            ["query"] = new Dictionary<string, object?> { ["bool"] = boolQuery },
            ["highlight"] = new Dictionary<string, object?>
            {
                ["fields"] = new Dictionary<string, object?>
                {
                    ["content"] = new Dictionary<string, object?> { ["fragment_size"] = 300, ["number_of_fragments"] = 1 }
                }
            },
            ["_source"] = new[] { "title", "content", "sourcePath", "page" }
        };

        // 3) KNN
        var knnCore = new Dictionary<string, object?>
        {
            ["field"] = "embedding",
            ["query_vector"] = qVec,
            ["k"] = 50,
            ["num_candidates"] = 1000
        };
        var knnPayload = new Dictionary<string, object?>
        {
            ["knn"] = knnCore,
            ["_source"] = new[] { "title", "content", "sourcePath", "page" }
        };
        if (!string.IsNullOrWhiteSpace(site))
        {
            knnPayload["query"] = new Dictionary<string, object?>
            {
                ["bool"] = new Dictionary<string, object?>
                {
                    ["filter"] = new object[] { new Dictionary<string, object?> { ["term"] = new Dictionary<string, object?> { ["site"] = site } } }
                }
            };
        }

        // 4) Kjør spørringer, materialiser
        var bm25Text = await PostJsonAsync(es, $"{_indexName}/_search", bm25Payload);
        using var bm25Doc = JsonDocument.Parse(bm25Text);
        var bm25Array = bm25Doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray().ToList();

        var bm25List = new List<(string id, double? score, string title, string content, int? page, string? sourcePath, string? highlight)>();
        foreach (var h in bm25Array)
        {
            var id = h.GetProperty("_id").GetString()!;
            double? score = h.TryGetProperty("_score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : (double?)null;
            var src = h.GetProperty("_source");
            var title = src.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
            var content = src.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
            var page = src.TryGetProperty("page", out var p) ? p.GetInt32() : (int?)null;
            var sourcePath = src.TryGetProperty("sourcePath", out var sp) ? sp.GetString() : null;
            string? highlight = null;
            if (h.TryGetProperty("highlight", out var hl) && hl.TryGetProperty("content", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                highlight = arr[0].GetString();
            bm25List.Add((id, score, title, content, page, sourcePath, highlight));
        }

        var knnList = new List<(string id, double? score, string title, string content, int? page, string? sourcePath)>();
        bool knnOk = true;
        try
        {
            var knnText = await PostJsonAsync(es, $"{_indexName}/_search", knnPayload);
            using var knnDoc = JsonDocument.Parse(knnText);
            var knnArray = knnDoc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray().ToList();
            foreach (var h in knnArray)
            {
                var id = h.GetProperty("_id").GetString()!;
                double? score = h.TryGetProperty("_score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : (double?)null;
                var src = h.GetProperty("_source");
                var title = src.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
                var content = src.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                var page = src.TryGetProperty("page", out var p) ? p.GetInt32() : (int?)null;
                var sourcePath = src.TryGetProperty("sourcePath", out var sp) ? sp.GetString() : null;
                knnList.Add((id, score, title, content, page, sourcePath));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[RAG] kNN search failed: " + ex.Message);
            knnOk = false;
        }

        // 5) RRF-fusjon
        var scoreMap = new Dictionary<string, double>();
        var hitMap = new Dictionary<string, (string title, string content, int? page, string? sourcePath, string? highlight)>();

        void Accumulate<T>((IEnumerable<T> list, Func<T, string> getId) data)
        {
            int rank = 0;
            foreach (var item in data.list)
            {
                rank++;
                var id = data.getId(item);
                var add = 1.0 / (60 + rank);
                if (scoreMap.TryGetValue(id, out var cur)) scoreMap[id] = cur + add; else scoreMap[id] = add;
            }
        }

        Accumulate((bm25List, x => x.id));
        if (knnOk) Accumulate((knnList, x => x.id));

        var fusedIds = scoreMap.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(take).ToList();

        foreach (var h in bm25List) hitMap[h.id] = (h.title, h.content, h.page, h.sourcePath, h.highlight);
        foreach (var h in knnList) if (!hitMap.ContainsKey(h.id)) hitMap[h.id] = (h.title, h.content, h.page, h.sourcePath, null);

        static string Trim(string s, int n) => s.Length > n ? s[..n] : s;

        var contexts = new List<string>();
        var sources = new List<RagChunk>();
        foreach (var id in fusedIds)
        {
            var (title, content, page, sourcePath, highlight) = hitMap[id];
            var snippet = !string.IsNullOrWhiteSpace(highlight) ? highlight! : Trim(content ?? string.Empty, 800);
            contexts.Add(snippet);
            sources.Add(new RagChunk(id, title, sourcePath, page, null, snippet));
        }

        return new RagResult(contexts, sources);
    }
}

// ===== DTO-er =====
public record ChatReq(string Message, List<Msg>? History, string? Site);
public record Msg(string Role, string Content);
