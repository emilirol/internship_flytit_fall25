using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;
using Elastic.Clients.Elasticsearch.Mapping;

namespace FlytIT.Chatbot.Services;

public class IndexBootstrapper
{
    private readonly string _index;
    public IndexBootstrapper(string index) => _index = index;

    public async Task EnsureIndexAsync(ElasticsearchClient es)
    {
        var exists = await es.Indices.ExistsAsync(_index);
        if (exists.Exists) return;

        var req = new CreateIndexRequest(_index)
        {
            Settings = new IndexSettings { NumberOfShards = 1, NumberOfReplicas = 1 },
            Mappings = new TypeMapping
            {
                Properties = new Properties
                {
                    { "title",      new KeywordProperty() },
                    { "site",       new KeywordProperty() },
                    { "sourcePath", new KeywordProperty() },
                    { "content",    new TextProperty { Analyzer = "norwegian" } },
                    { "embedding",  new DenseVectorProperty { Dims = 1536, Index = true, Similarity = DenseVectorSimilarity.Cosine } }
                }
            }
        };

        var create = await es.Indices.CreateAsync(req);
        if (!create.IsValidResponse)
            throw new Exception($"Kunne ikke opprette index '{_index}'. {create.DebugInformation}");
    }
}
