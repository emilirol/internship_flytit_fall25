using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using FlytIT.Chatbot.Services;
using FlytIT.Chatbot.Utils;

namespace FlytIT.Chatbot.Services
{
    public sealed class LiveSiteQA
    {
        private readonly IEmbeddingService _embeddings;
        private readonly string _openAiKey;
        private readonly string _chatModel;
        private readonly HttpClient _http;

        public LiveSiteQA(IEmbeddingService embeddings, string openAiKey, string chatModel = "gpt-4o-mini")
        {
            _embeddings = embeddings;
            _openAiKey = openAiKey;
            _chatModel = chatModel;
            _http = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            })
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("FlytIT-LiveSiteQA/1.0");
        }

        public sealed class PageDoc
        {
            public required string Url { get; init; }
            public required string Title { get; init; }
            public required string Content { get; init; }  // hovedtekst + ev. captions
            public float[]? Embedding { get; set; }        // fylles etter fetch
        }

        /// <summary>
        /// Hent et sett med sider fra et nettsted (uten å indeksere ES).
        /// </summary>
        public async Task<List<PageDoc>> BuildCorpusAsync(
            string startUrl,
            IEnumerable<string>? allowedHosts = null,
            int maxPages = 200,
            bool useSitemap = true,
            bool includeImages = true,
            CancellationToken ct = default)
        {
            var startUri = new Uri(startUrl);
            var hostAllow = new HashSet<string>((allowedHosts ?? new[] { startUri.Host })
                                                .Select(h => h.ToLowerInvariant()));

            var q = new Queue<Uri>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Enqueue(Uri u)
            {
                var key = u.GetLeftPart(UriPartial.Path).TrimEnd('/');
                if (!seen.Contains(key)) q.Enqueue(u);
            }
            Enqueue(startUri);

            if (useSitemap)
            {
                try
                {
                    var smUri = new Uri(startUri.GetLeftPart(UriPartial.Authority) + "/sitemap.xml");
                    var xml = await _http.GetStringAsync(smUri, ct);
                    foreach (var loc in ExtractSitemapLocs(xml))
                    {
                        if (TryNormalizeUrl(startUri, loc, out var u) && IsAllowed(u, hostAllow))
                            Enqueue(u);
                    }
                }
                catch { /* ok om ingen sitemap */ }
            }

            var pages = new List<PageDoc>(capacity: Math.Min(maxPages, 1024));
            while (q.Count > 0 && pages.Count < maxPages)
            {
                ct.ThrowIfCancellationRequested();
                var url = q.Dequeue();
                var key = url.GetLeftPart(UriPartial.Path).TrimEnd('/');
                if (!seen.Add(key)) continue;

                PageDoc? pd = null;
                try
                {
                    pd = await FetchOneAsync(url, includeImages, ct);
                    if (pd is not null && !string.IsNullOrWhiteSpace(pd.Content))
                        pages.Add(pd);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LIVEQA][fetch] {url} ✖ {ex.Message}");
                }

                // Finn nye lenker fra siden (enkel a[href])
                try
                {
                    var html = await _http.GetStringAsync(url, ct);
                    foreach (var href in HtmlProcessor.ExtractAllHrefs(html))
                    {
                        if (TryNormalizeUrl(url, href, out var u) && IsAllowed(u, hostAllow))
                            Enqueue(u);
                    }
                }
                catch { /* ignorer */ }
            }

            // Lag embeddings (i minne)
            foreach (var p in pages)
            {
                p.Embedding = await _embeddings.EmbedAsync(p.Content);
            }

            // Pre-calc IDF for BM25-light
            _idf = BuildIdf(pages.Select(p => p.Content));

            Console.WriteLine($"[LIVEQA] Bygget korpus: {pages.Count} sider.");
            return pages;
        }

        /// <summary>
        /// Svar på et spørsmål gitt et korpus (uten ES): hybrid RRF (kNN + BM25-light)
        /// </summary>
        public async Task<string> AskAsync(string question, List<PageDoc> corpus, int take = 6, CancellationToken ct = default)
        {
            if (corpus.Count == 0) return "Jeg vet dessverre ikke.";

            // 1) kNN på embeddings
            var qVec = await _embeddings.EmbedAsync(question);
            var knn = corpus
                .Select((p, idx) => new { idx, score = Cosine(qVec, p.Embedding!) })
                .OrderByDescending(x => x.score)
                .Take(50)
                .ToList();

            // 2) BM25-light (tf * idf, enkel)
            var bm25 = corpus
                .Select((p, idx) => new { idx, score = KeywordScore(p.Content, question) })
                .OrderByDescending(x => x.score)
                .Take(50)
                .ToList();

            // 3) RRF-fusjon
            var fused = RrfFuse(knn.Select(x => x.idx), bm25.Select(x => x.idx), k: 60)
                .Take(take)
                .Select(i => corpus[i])
                .ToList();

            // Bygg kontekst
            static string Trim(string s, int n) => s.Length > n ? s[..n] : s;
            var ctxBlocks = fused.Select(p =>
                $"[Kilde] {p.Title} — {p.Url}\n{Trim(p.Content, 1200)}").ToList();

            var prompt = $"""
            Du er en hjelpsom assistent. Bruk kun konteksten under for å svare kort og presist på norsk.
            Oppgi konkrete mål/enheter hvis de fremgår (mm, cm, m). Hvis svaret ikke finnes i konteksten,
            svar "Jeg vet dessverre ikke". Inkluder gjerne hvilken kilde(URL) du brukte.

            Kontekst:
            {string.Join("\n\n---\n\n", ctxBlocks)}

            Spørsmål: {question}
            Svar:
            """;

            // Chat Completions
            var body = new
            {
                model = _chatModel,
                messages = new object[]
                {
                    new { role = "user", content = prompt }
                },
                temperature = 0.2,
                max_tokens = 300
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _openAiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return $"(Feil fra OpenAI: {err})";
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var ans = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ans ?? "Jeg vet dessverre ikke.";
        }

        // ------------------ Helpers ------------------

        private async Task<PageDoc?> FetchOneAsync(Uri url, bool includeImages, CancellationToken ct)
        {
            using var r = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(r, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            if (!resp.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) ?? true)
                return null;

            var html = await resp.Content.ReadAsStringAsync(ct);
            var (title, text, headings, imageUrls) = SimpleExtract(html, url);

            var sb = new StringBuilder();
            if (headings.Count > 0)
            {
                sb.AppendLine(string.Join(" · ", headings.Take(5)));
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text.Trim());
                sb.AppendLine();
            }

            if (includeImages)
            {
                var captionsEnabled = (Environment.GetEnvironmentVariable("INDEX_IMAGE_CAPTIONS") ?? "false")
                    .Equals("true", StringComparison.OrdinalIgnoreCase);

                if (captionsEnabled && imageUrls.Count > 0 && !string.IsNullOrWhiteSpace(_openAiKey))
                {
                    int maxImgs = ConfigHelper.GetIntOrDefault("CRAWL_CAPTION_MAX_IMAGES", AppConstants.DEFAULT_CRAWL_CAPTION_MAX_IMAGES);
                    int count = 0;
                    foreach (var img in imageUrls)
                    {
                        if (count >= maxImgs) break;
                        try
                        {
                            var bytes = await _http.GetByteArrayAsync(img, ct);
                            var ctx = text;
                            if (ctx.Length > 800) ctx = ctx[..800];
                            var hint = $"Side: {url}. Beskriv bildet kort i kontekst av teksten.";

                            var cap = await ImageCaptioner.DescribeImageAsync(bytes, _openAiKey, site: url.Host, taskHint: hint, ct: ct);
                            if (!string.IsNullOrWhiteSpace(cap))
                            {
                                if (count == 0) sb.AppendLine("### Bildebeskrivelser");
                                sb.AppendLine($"[{Path.GetFileName(img.LocalPath)}] {cap.Trim()}");
                                count++;
                            }
                        }
                        catch { /* ignorer enkeltbilder */ }
                    }
                }
            }

            var content = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(content)) return null;

            return new PageDoc
            {
                Url = url.ToString(),
                Title = string.IsNullOrWhiteSpace(title) ? url.ToString() : title,
                Content = content
            };
        }

        // Enkel HTML-”readability”
        private static (string Title, string Text, List<string> Headings, List<Uri> Images) SimpleExtract(string html, Uri pageUrl)
        {
            string title = HtmlProcessor.ExtractBetween(html, "<title", "</title>")?.Replace("\n", " ").Replace("\r", " ").Trim() ?? "";
            var body = HtmlProcessor.ExtractBetween(html, "<body", "</body>") ?? html;
            body = HtmlProcessor.StripTag(body, "script");
            body = HtmlProcessor.StripTag(body, "style");
            body = HtmlProcessor.StripTag(body, "noscript");

            var headings = new List<string>();
            foreach (var tag in new[] { "h1", "h2", "h3" })
                headings.AddRange(HtmlProcessor.ExtractAllTexts(body, tag).Select(TextProcessor.NormalizeWhitespace));

            var main = HtmlProcessor.ExtractBetween(body, "<main", "</main>")
                       ?? HtmlProcessor.ExtractBetween(body, "<article", "</article>")
                       ?? body;

            var text = HtmlProcessor.StripHtml(main);
            text = TextProcessor.NormalizeWhitespace(text);

            var imgs = HtmlProcessor.ExtractAllImgSrc(body).Select(src =>
            {
                try { return src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(src) : new Uri(pageUrl, src); }
                catch { return null; }
            }).Where(u => u is not null).Cast<Uri>().ToList();

            return (title, text, headings.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList(), imgs);
        }


        // Legg denne inni LiveSiteQa-klassen (sammen med andre helpers)


        private static bool TryNormalizeUrl(Uri baseUri, string href, out Uri abs)
        {
            abs = null!;
            try
            {
                if (string.IsNullOrWhiteSpace(href)) return false;
                href = href.Trim();
                if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return false;
                if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) return false;
                if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;

                var u = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? new Uri(href) : new Uri(baseUri, href);
                var b = new UriBuilder(u) { Fragment = "" }.Uri;
                abs = b;
                return true;
            }
            catch { return false; }
        }
        private static bool IsAllowed(Uri uri, HashSet<string> allowedHosts) => allowedHosts.Contains(uri.Host.ToLowerInvariant());
        private static IEnumerable<string> ExtractSitemapLocs(string xml)
        {
            try
            {
                var locs = new List<string>();
                using var sr = new StringReader(xml);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    var i = line.IndexOf("<loc>", StringComparison.OrdinalIgnoreCase);
                    if (i >= 0)
                    {
                        var j = line.IndexOf("</loc>", i, StringComparison.OrdinalIgnoreCase);
                        if (j > i) locs.Add(line.Substring(i + 5, j - (i + 5)).Trim());
                    }
                }
                return locs;
            }
            catch { return Array.Empty<string>(); }
        }

        // ------- Scoring / RRF -------

        private Dictionary<string, double> _idf = new();

        private static IEnumerable<string> Tokenize(string s)
        {
            var word = new StringBuilder();
            foreach (var ch in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch) || "æøå".Contains(ch))
                    word.Append(ch);
                else
                {
                    if (word.Length > 0) { yield return word.ToString(); word.Clear(); }
                }
            }
            if (word.Length > 0) yield return word.ToString();
        }

        private Dictionary<string, double> BuildIdf(IEnumerable<string> docs)
        {
            var df = new Dictionary<string, int>();
            int n = 0;
            foreach (var d in docs)
            {
                n++;
                foreach (var tok in Tokenize(d).Distinct())
                {
                    df.TryGetValue(tok, out int c);
                    df[tok] = c + 1;
                }
            }
            var idf = new Dictionary<string, double>(df.Count);
            foreach (var kv in df)
                idf[kv.Key] = Math.Log((n + 1.0) / (kv.Value + 1.0)) + 1.0; // smoothed
            return idf;
        }

        private double KeywordScore(string doc, string query)
        {
            var qToks = Tokenize(query).Where(t => t.Length >= 2).ToList();
            if (qToks.Count == 0) return 0;

            var tf = new Dictionary<string, int>();
            foreach (var tok in Tokenize(doc))
            {
                if (qToks.Contains(tok))
                {
                    tf.TryGetValue(tok, out int c);
                    tf[tok] = c + 1;
                }
            }
            double score = 0;
            foreach (var t in qToks)
            {
                tf.TryGetValue(t, out int f);
                _idf.TryGetValue(t, out double idf);
                score += f * (idf == 0 ? 1.0 : idf);
            }
            return score;
        }

        private static double Cosine(float[] a, float[] b)
        {
            double dot = 0, na = 0, nb = 0;
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            if (na == 0 || nb == 0) return 0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        private static IEnumerable<int> RrfFuse(IEnumerable<int> rankA, IEnumerable<int> rankB, int k = 60)
        {
            var dict = new Dictionary<int, double>();
            int r = 0;
            foreach (var i in rankA)
            {
                r++;
                dict[i] = dict.TryGetValue(i, out var s) ? s + 1.0 / (k + r) : 1.0 / (k + r);
            }
            r = 0;
            foreach (var i in rankB)
            {
                r++;
                dict[i] = dict.TryGetValue(i, out var s) ? s + 1.0 / (k + r) : 1.0 / (k + r);
            }
            return dict.OrderByDescending(x => x.Value).Select(x => x.Key);
        }
    }
}
