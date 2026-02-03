using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using FlytIT.Chatbot.Models;
using FlytIT.Chatbot.Utils;

namespace FlytIT.Chatbot.Services;

public class RetrievalService
{
    private readonly string _indexName;
    public RetrievalService(string indexName) => _indexName = indexName;

    public async Task<string> GetContextAsync(
        ElasticsearchClient es,
        IEmbeddingService embeddings,
        string query,
        string? site)
    {
        var qvec = await embeddings.EmbedAsync(query);

        // ---------- KNN ----------
        try
        {
            SearchResponse<Doc> knn;

            if (string.IsNullOrEmpty(site))
            {
                knn = await es.SearchAsync<Doc>(s => s
                    .Indices(_indexName)
                    .Size(5)
                    .Knn(k => k
                        .Field(f => f.Embedding)
                        .QueryVector(qvec)
                        .K(20)
                        .NumCandidates(100)
                    )
                );
            }
            else
            {
                knn = await es.SearchAsync<Doc>(s => s
                    .Indices(_indexName)
                    .Size(5)
                    .Knn(k => k
                        .Field(f => f.Embedding)
                        .QueryVector(qvec)
                        .K(20)
                        .NumCandidates(100)
                        .Filter(q => q.Term(t => t.Field(f => f.Site).Value(site)))
                    )
                );
            }

            if (knn.IsValidResponse && knn.Hits.Count > 0)
            {
                return string.Join("\n\n", knn.Hits.Select((h, i) =>
                    $"[{i + 1}] {h.Source!.Title}\n{TextProcessor.TrimSnippet(h.Source!.Content, 700)}"));
            }
        }
        catch
        {
            // faller til BM25
        }

        // ---------- BM25 fallback ----------
        SearchResponse<Doc> bm25;

        if (string.IsNullOrEmpty(site))
        {
            bm25 = await es.SearchAsync<Doc>(s => s
                .Indices(_indexName)
                .Size(5)
                .Query(q => q.Match(m => m.Field(f => f.Content).Query(query)))
            );
        }
        else
        {
            bm25 = await es.SearchAsync<Doc>(s => s
                .Indices(_indexName)
                .Size(5)
                .Query(q => q.Bool(b => b
                    .Must(mu => mu.Match(m => m.Field(f => f.Content).Query(query)))
                    .Filter(fi => fi.Term(t => t.Field(f => f.Site).Value(site)))
                ))
            );
        }

        if (bm25.IsValidResponse && bm25.Hits.Count > 0)
        {
            return string.Join("\n\n", bm25.Hits.Select((h, i) =>
                $"[{i + 1}] {h.Source!.Title}\n{TextProcessor.TrimSnippet(h.Source!.Content, 700)}"));
        }

        return string.Empty;
    }
}
