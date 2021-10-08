namespace WebCrawler;

public record CrawlerConfiguration
{
    public IList<Uri> RootUrls { get; } = new List<Uri>();
    public IList<IUrlFilter> AnalyzeUrlFilters { get; } = new List<IUrlFilter>();
    public int DegreeOfParallelism { get; set; } = 16;

    public static CrawlerConfiguration FromUrls(params Uri[] urls)
    {
        var configuration = new CrawlerConfiguration();
        foreach (var url in urls)
        {
            if (!url.IsAbsoluteUri)
                throw new ArgumentException($"Uri '{url}' must be absolute", nameof(urls));

            configuration.RootUrls.Add(url);
            var parentUrl = new Uri(url, "."); // http://test/index.html => http://test/
            var urlFilter = new UrlFilter(parentUrl.Authority, parentUrl.AbsolutePath);
            configuration.AnalyzeUrlFilters.Add(urlFilter);

            if (!string.Equals(parentUrl.Host, "localhost", StringComparison.OrdinalIgnoreCase) && !parentUrl.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                urlFilter = new UrlFilter("www." + parentUrl.Authority, parentUrl.AbsolutePath);
                configuration.AnalyzeUrlFilters.Add(urlFilter);
            }
        }

        return configuration;
    }

    internal bool MustAnalyzeUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            throw new ArgumentException("Uri must be absolute", nameof(uri));

        foreach (var pattern in AnalyzeUrlFilters)
        {
            if (pattern.Match(uri))
                return true;
        }

        return false;
    }
}
