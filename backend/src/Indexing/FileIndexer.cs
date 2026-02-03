using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO.Compression;                 // for DOCX (zip)
using System.Net;                            // WebUtility.HtmlDecode

using Elastic.Clients.Elasticsearch;
using UglyToad.PdfPig;

using FlytIT.Chatbot.Models;
using FlytIT.Chatbot.Utils;
using FlytIT.Chatbot.Services;

namespace FlytIT.Chatbot.Indexing
{
    public static class FileIndexer
    {
        /// <summary>
        /// Indekserer en mappe basert på semikolon- ELLER komma-separerte mønstre.
        /// Støttede filtyper: .pdf, .html/.htm, .md, .txt, .docx
        /// </summary>
        public static async Task<int> IndexFolderAsync(
            ElasticsearchClient es,
            IEmbeddingService embeddings,
            string indexName,
            string folder,
            string pattern,
            bool recursive,
            string? site,
            int maxConcurrency = 2)
        {
            // Støtt både ';' og ',' (og whitespace) som separatorer
            var patterns = pattern.Split(new[] { ';', ',', ' ', '\n', '\r', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var files = patterns
                .SelectMany(p => Directory.EnumerateFiles(
                    folder, p,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var throttler = new SemaphoreSlim(maxConcurrency);
            var tasks = files.Select(path => IndexDocumentAsync(es, embeddings, indexName, path, site, throttler));

            await Task.WhenAll(tasks);
            return files.Count;
        }

        private static async Task IndexDocumentAsync(
            ElasticsearchClient es,
            IEmbeddingService embeddings,
            string indexName,
            string path,
            string? site,
            SemaphoreSlim throttler)
        {
            await throttler.WaitAsync();
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                string? content = null;
                int captionCount = 0;

                switch (ext)
                {
                    case ".pdf":
                        (content, captionCount) = await ExtractPdfContentAsync(path, site);
                        break;

                    case ".txt":
                    case ".md":
                        content = await ReadTextFileAsync(path);
                        break;

                    case ".html":
                    case ".htm":
                        content = await ReadHtmlFileAsync(path);
                        break;

                    case ".docx":
                        content = await ReadDocxTextAsync(path);
                        break;

                    default:
                        Console.WriteLine($"[INDEX] Hopper over {path} (ikke støttet filtype: {ext}).");
                        return;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    Console.WriteLine($"[INDEX] {Path.GetFileName(path)}: tomt innhold – skipper.");
                    return;
                }

                // Embedding
                var vec = await embeddings.EmbedAsync(content);

                // Indexer
                var doc = new Doc
                {
                    Title = Path.GetFileNameWithoutExtension(path),
                    Content = content,
                    Embedding = vec,
                    Site = site,
                    SourcePath = path
                };

                var resp = await es.IndexAsync(doc, req => req.Index(indexName));
                if (!resp.IsValidResponse)
                {
                    Console.WriteLine($"[INDEX] FEIL {Path.GetFileName(path)}: {resp.ElasticsearchServerError?.Error?.Reason}");
                }
                else
                {
                    var extra = ext == ".pdf" ? $" (captions: {captionCount})" : "";
                    Console.WriteLine($"[INDEX] {Path.GetFileName(path)}: indeksert{extra}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[INDEX] {Path.GetFileName(path)}: {ex.Message}");
            }
            finally
            {
                throttler.Release();
            }
        }

        // ---------- PDF-spesifikk logikk (tekst per side + valgfri captions) ----------
        private static async Task<(string content, int captions)> ExtractPdfContentAsync(string pdfPath, string? site)
        {
            var pageTexts = await ReadTextPerPageAsync(pdfPath);
            var pageCount = pageTexts.Count;

            var captionsEnabled = ConfigHelper.GetBoolOrDefault("INDEX_IMAGE_CAPTIONS", false);
            var renderPagesEnabled = ConfigHelper.GetBoolOrDefault("INDEX_RENDER_PAGES", false);

            var captionMode = (Environment.GetEnvironmentVariable("CAPTION_MODE") ?? "auto")
                .ToLowerInvariant(); // "always" | "auto" | "never"

            int TEXT_MIN_CHARS = ConfigHelper.GetIntOrDefault("TEXT_MIN_CHARS", 200);
            var FIGURE_HINT_REGEX = new Regex(@"\b(figur|figure|tabell|chart|diagram|graf)\b", RegexOptions.IgnoreCase);

            List<byte[]>? pagePngs = null;

            var combined = new StringBuilder();
            int captionCount = 0;

            for (int i = 0; i < pageCount; i++)
            {
                var txt = pageTexts[i] ?? string.Empty;
                var trimmed = txt.Trim();

                bool hasSomeText = trimmed.Length >= TEXT_MIN_CHARS;
                bool looksLikeFigure = FIGURE_HINT_REGEX.IsMatch(trimmed) || !hasSomeText;

                bool wantCaption = captionsEnabled && renderPagesEnabled &&
                    (captionMode == "always" || (captionMode == "auto" && (looksLikeFigure || !hasSomeText)));

                // Tekst
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    combined.AppendLine($"[Side {i + 1} – tekst]");
                    combined.AppendLine(trimmed);
                    combined.AppendLine();
                }

                // Captions (valgfritt)
                if (wantCaption)
                {
                    try
                    {
                        pagePngs ??= PdfImageExtractor.RenderPagesAsPng(pdfPath, targetWidth: 1280).ToList();
                        if (i < pagePngs.Count)
                        {
                            var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(openAiKey))
                            {
                                var ctx = trimmed;
                                if (ctx.Length > 800) ctx = ctx[..800];

                                var hint =
                                    $"PDF-side {i + 1}. Les bildet og beskriv kort figur/bilde/tabell. " +
                                    $"Knytt beskrivelsen til teksten på denne siden når relevant (terminologi, steg, mål)." +
                                    (string.IsNullOrWhiteSpace(ctx) ? "" : $" Tekst på siden (kontekst): \"{ctx}\"");

                                var caption = await ImageCaptioner.DescribeImageAsync(
                                    pagePngs[i], openAiKey, site: site, taskHint: hint);

                                if (!string.IsNullOrWhiteSpace(caption))
                                {
                                    combined.AppendLine($"[Side {i + 1} – bilde/figur]");
                                    combined.AppendLine(caption.Trim());
                                    combined.AppendLine();
                                    captionCount++;
                                }
                            }
                            else
                            {
                                Console.WriteLine("[CAPTION] OPENAI_API_KEY mangler – skipper caption.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[CAPTION] {Path.GetFileName(pdfPath)} side {i + 1}: {ex.Message} (ignorerer)");
                    }
                }
            }

            return (combined.ToString().Trim(), captionCount);
        }

        private static Task<List<string>> ReadTextPerPageAsync(string pdfPath)
        {
            var perPage = new List<string>();
            try
            {
                using var doc = PdfDocument.Open(pdfPath);
                foreach (var page in doc.GetPages())
                {
                    var txt = page.Text ?? string.Empty;
                    perPage.Add(txt.Trim());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PDFTEXT] PdfPig feilet: {ex.Message}");
            }
            return Task.FromResult(perPage);
        }

        // ---------- Enkle lesere for andre formater ----------
        private static async Task<string> ReadTextFileAsync(string path)
        {
            var txt = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return TextProcessor.NormalizeWhitespace(txt);
        }

        private static async Task<string> ReadHtmlFileAsync(string path)
        {
            var html = await File.ReadAllTextAsync(path, Encoding.UTF8);
            return HtmlProcessor.StripHtml(html);
        }

        /// <summary>
        /// Minimal DOCX-tekstleser uten ekstra pakker:
        /// Leser word/document.xml, stripper tags og HTML-dekoder tekst.
        /// </summary>
        private static async Task<string> ReadDocxTextAsync(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) return string.Empty;

                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var xml = await reader.ReadToEndAsync();

                // Bytt ut avsnitt-markører med linjeskift, fjern tags og dekod
                xml = Regex.Replace(xml, @"</w:p>", "\n", RegexOptions.IgnoreCase);
                xml = Regex.Replace(xml, @"<[^>]+>", " ");
                xml = WebUtility.HtmlDecode(xml);
                return TextProcessor.NormalizeWhitespace(xml);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DOCX] {Path.GetFileName(path)}: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
