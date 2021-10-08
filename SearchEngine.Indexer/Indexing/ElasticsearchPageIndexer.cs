using System.Globalization;
using System.Threading.Channels;
using Nest;

namespace WebCrawler
{
    // docker run -p 9200:9200 -p 9300:9300 -e "discovery.type=single-node" docker.elastic.co/elasticsearch/elasticsearch:7.15.0
    public class ElasticsearchPageIndexer : IAsyncDisposable
    {
        private readonly ElasticClient _client;
        private readonly Channel<PageData> _channel;
        private readonly Task _task;

        public ElasticsearchPageIndexer(Uri elasticSearchUrl)
        {
            var settings = new ConnectionSettings(elasticSearchUrl);
            _client = new ElasticClient(settings);
            _channel = Channel.CreateUnbounded<PageData>();
            _task = Task.Run(Index);
        }

        public void IndexPage(PageData pageData)
        {
            // This calls don't fail as we are using an Unbounded channel
            _ = _channel.Writer.TryWrite(pageData);
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            await _task;
        }

        private async Task Index()
        {
            var indexName = "webpages";
            var tempIndexName = indexName + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");

            await _client.Indices.CreateAsync(tempIndexName);

            var reader = _channel.Reader;
            var list = new List<Page>(capacity: 10);
            while (await reader.WaitToReadAsync())
            {
                while (list.Count < list.Capacity && reader.TryRead(out var item))
                {
                    list.Add(new()
                    {
                        Url = item.CanonicalUrl.AbsoluteUri,
                        Title = item.Title,
                        Description = item.Description,
                        Contents = item.MainElementTexts,
                        Headers = item.Headers,
                    });
                }

                if (list.Count > 0)
                {
                    await _client.IndexManyAsync(list, tempIndexName);
                    list.Clear();
                }
            }

            // Swap index
            var aliasActions = new List<IAliasAction>
            {
                new AliasAddAction { Add = new AliasAddOperation { Alias = indexName, Index = tempIndexName } },
            };

            var currentAlias = await _client.Indices.GetAliasAsync(indexName);
            if (currentAlias.IsValid)
            {
                foreach (var index in currentAlias.Indices)
                {
                    aliasActions.Add(new AliasRemoveAction { Remove = new AliasRemoveOperation { Alias = indexName, Index = index.Key } });
                }
            }


            await _client.Indices.BulkAliasAsync(new BulkAliasRequest { Actions = aliasActions });

            // Delete old indices
            if (currentAlias.IsValid)
            {
                foreach (var index in currentAlias.Indices)
                {
                    await _client.Indices.DeleteAsync(index.Key);
                }
            }
        }

        [ElasticsearchType(IdProperty = nameof(Url))]
        public class Page
        {
            [Keyword]
            public string? Url { get; set; }
            [Text]
            public IEnumerable<string>? Contents { get; set; }
            [Text]
            public IEnumerable<string>? Headers { get; set; }
            [Text]
            public string? Title { get; set; }
            [Text]
            public string? Description { get; set; }
        }
    }
}
