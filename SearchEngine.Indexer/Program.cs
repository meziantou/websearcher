using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCrawler;

var configuration = CrawlerConfiguration.FromUrls(new Uri("https://www.meziantou.net"));

var services = new ServiceCollection();
services.AddLogging(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Trace);
});
services.BuildServiceProvider();
var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Crawler>>();

await using var fileIndexer = new FileIndexWriter("index.json");
await using var esIndexer = new ElasticsearchPageIndexer(new Uri("http://localhost:9200"));
var crawl = new Crawler(logger, configuration);
crawl.PageCrawled += (sender, e) =>
 {
     var page = e.PageData;

     fileIndexer.IndexPage(e.PageData); // For debug purpose
     if (page.RobotsConfiguration == null || page.RobotsConfiguration.IndexPage)
     {
         esIndexer.IndexPage(e.PageData);
     }
 };

await crawl.RunAsync();
