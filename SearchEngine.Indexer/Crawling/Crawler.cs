using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace WebCrawler;

public class Crawler
{
    private readonly HashSet<Uri> _allUrls = new();
    private readonly Channel<Uri> _pendingUrls = Channel.CreateUnbounded<Uri>();
    private readonly ILogger _logger;

    public Crawler(ILogger logger, CrawlerConfiguration configuration)
    {
        _logger = logger;
        Configuration = configuration;
    }

    public event EventHandler<PageCrawledEventArgs>? PageCrawled;

    public string? DebugFolder { get; set; }
    public CrawlerConfiguration Configuration { get; }

    private static Uri NormalizeUri(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.Fragment))
            return new Uri(uri.GetLeftPart(UriPartial.Query), UriKind.Absolute);

        return uri;
    }

    private async ValueTask EnqueueAsync(Uri url, CancellationToken cancellationToken)
    {
        url = NormalizeUri(url);
        if (!AddVisitedUrl(url))
            return;

        if (!MustAnalyzeUrl(url))
            return;

        await _pendingUrls.Writer.WriteAsync(url, cancellationToken);
    }

    private bool AddVisitedUrl(Uri uri)
    {
        uri = NormalizeUri(uri);
        lock (_pendingUrls)
        {
            return _allUrls.Add(uri);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // Enqueue all RootUrls
        foreach (var url in Configuration.RootUrls)
        {
            await EnqueueAsync(url, cancellationToken);
        }

        // Initialize playwright
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var options = new BrowserNewContextOptions
        {
            AcceptDownloads = false,
            BypassCSP = false,
            HttpCredentials = null,
            JavaScriptEnabled = true,
            IgnoreHTTPSErrors = false,
            Locale = "en-US",
            ScreenSize = new ScreenSize() { Width = 1920, Height = 1080 },
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
        };

        if (DebugFolder != null)
        {
            options.RecordHarOmitContent = false;
            options.RecordHarPath = Path.Combine(DebugFolder, "log.har");
        }

        await using var browserContext = await browser.NewContextAsync(options);

        // Start crawling pages
        var tasks = new List<Task>();
        var maxConcurrency = Configuration.DegreeOfParallelism;
        var remainingConcurrency = maxConcurrency;
        while (await _pendingUrls.Reader.WaitToReadAsync(cancellationToken))
        {
            while (TryGetUrl(out var url))
            {
                // If we reach the maximum number of concurrent tasks, wait for one to finish
                while (Volatile.Read(ref remainingConcurrency) < 0)
                {
                    // The tasks collection can change while Task.WhenAny enumerates the collection
                    // so, we need to clone the collection to avoid issues
                    Task[]? clone = null;
                    lock (tasks)
                    {
                        if (tasks.Count > 0)
                        {
                            clone = tasks.ToArray();
                        }
                    }

                    if (clone != null)
                    {
                        await Task.WhenAny(clone);
                    }
                }

                var task = Task.Run(async () =>
                {
                    try
                    {
                        _logger.LogTrace("Processing {URL}", url);
                        await ProcessUrlAsync(new AnalyzeContext(url, browserContext, cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error while processing {URL}", url);
                    }
                }, cancellationToken);

                lock (tasks)
                {
                    tasks.Add(task);
                    task.ContinueWith(task => OnTaskCompleted(task), cancellationToken);
                }
            }

            bool TryGetUrl([NotNullWhen(true)] out Uri? result)
            {
                lock (tasks)
                {
                    if (_pendingUrls.Reader.TryRead(out var url))
                    {
                        remainingConcurrency--;
                        result = url;
                        return true;
                    }

                    result = null;
                    return false;
                }
            }

            void OnTaskCompleted(Task completedTask)
            {
                lock (tasks)
                {
                    if (!tasks.Remove(completedTask))
                        throw new CrawlerException("An unexpected error occured");

                    remainingConcurrency++;

                    // There is no active tasks, so we are sure we are at the end
                    if (maxConcurrency == remainingConcurrency && !_pendingUrls.Reader.TryPeek(out _))
                    {
                        _pendingUrls.Writer.Complete();
                    }
                }
            }
        }
    }

    private bool MustAnalyzeUrl(Uri uri)
    {
        return Configuration.MustAnalyzeUrl(uri);
    }

    private async Task ProcessUrlAsync(AnalyzeContext context)
    {
        var data = await AnalyzeUrlAsync(context);
        if (data == null)
            return;

        foreach (var link in data.Links)
        {
            if (!link.FollowLink)
                continue;

            await EnqueueAsync(link.Url, context.CancellationToken);
        }

        foreach (var feed in data.Feeds)
        {
            await EnqueueAsync(feed, context.CancellationToken);
        }

        foreach (var sitemap in data.Sitemaps)
        {
            await EnqueueAsync(sitemap, context.CancellationToken);
        }
    }

    private async Task<PageData?> AnalyzeUrlAsync(AnalyzeContext context)
    {
        var page = await context.BrowserContext.NewPageAsync();
        try
        {
            // perf: Enabling routing disables http cache
            await page.RouteAsync("**", async route =>
            {
                if (route.Request.ResourceType is "image" or "media" or "font")
                {
                    await route.AbortAsync();
                }
                else
                {
                    await route.ContinueAsync();
                }
            });

            page.Response += (sender, e) =>
            {
                if (Uri.TryCreate(e.Url, UriKind.Absolute, out var uri))
                {
                    AddVisitedUrl(uri);
                }
            };

            var response = await page.GotoAsync(context.Uri.AbsoluteUri, new PageGotoOptions() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
            if (response == null)
            {
                _logger.LogWarning("Cannot get the page {URL}", context.Uri);
                return null;
            }
            else if (response.Status is StatusCodes.Status301MovedPermanently or StatusCodes.Status302Found)
            {
                _logger.LogError("The page {URL} is a redirection", context.Uri);
                throw new CrawlerException($"Url '{context.Uri}' return with status code '{response.Status}'");
            }
            else if (!response.Ok)
            {
                _logger.LogWarning("The page {URL} return an error: {StatusCode}", context.Uri, response.Status);
                return null;
            }

            var pageUri = await GetCanonicalUrl(page);
            var contentType = await response.HeaderValueAsync("content-type");
            var pageData = new PageData(pageUri);

            if (IsMimeTypeOneOf(contentType, "application/atom+xml", "application/xml"))
            {
                try
                {
                    var responseContent = await response.TextAsync();
                    var document = XDocument.Parse(responseContent);

                    var nsmgr = new XmlNamespaceManager(new NameTable());
                    nsmgr.AddNamespace("atom", "http://www.w3.org/2005/Atom");

                    foreach (var link in document.XPathSelectElements("/atom:feed/atom:entry/atom:link[@rel='alternate']", nsmgr))
                    {
                        if (Uri.TryCreate(pageUri, link.Attribute("href")!.Value, out var uri))
                        {
                            pageData.Links.Add(new PageLink(uri, Text: null, FollowLink: true));
                        }
                    }

                }
                catch (XmlException)
                {
                    _logger.LogWarning("The page {URL} is not a valid xml file", context.Uri);
                }
            }

            if (IsMimeTypeOneOf(contentType, "application/rss+xml", "application/xml"))
            {
                try
                {
                    var responseContent = await response.TextAsync();
                    var document = XDocument.Parse(responseContent);
                    foreach (var link in document.XPathSelectElements("/rss/channel/item/link"))
                    {
                        if (Uri.TryCreate(pageUri, link.Value, out var uri))
                        {
                            pageData.Links.Add(new PageLink(uri, Text: null, FollowLink: true));
                        }
                    }

                }
                catch (XmlException)
                {
                    _logger.LogWarning("The page {URL} is not a valid xml file", context.Uri);
                }
            }

            // Parse html page anyway
            {
                // Extract data
                var title = await page.TitleAsync();
                var description = await GetFirstAttributeValue(page, ("meta[name=description]", "content"),
                                                                     ("meta[name='twitter:description']", "content"),
                                                                     ("meta[property='og:description']", "content"));
                pageData.Content = await page.ContentAsync();
                pageData.Title = title;
                pageData.MimeType = contentType;
                pageData.Description = description;
                pageData.RobotsConfiguration = await GetRobotConfiguration(response, page);
                pageData.Links.AddRange(await GetLinks(page, pageData.RobotsConfiguration));
                pageData.MainElementTexts.AddRange(await GetMainContents(page));
                pageData.Headers.AddRange(await GetHeaders(page));
                pageData.Feeds.AddRange(await GetFeeds(page));
                pageData.Sitemaps.AddRange(await GetSitemaps(page));
                await OnPageCrawled(new(pageData, response, page));
                return pageData;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing page {URL}", context.Uri);
            return null;
        }
        finally
        {
            await page.CloseAsync(new PageCloseOptions { RunBeforeUnload = false });
        }

        async Task<Uri> GetCanonicalUrl(IPage page)
        {
            var pageUrl = new Uri(page.Url, UriKind.Absolute);
            var link = await page.QuerySelectorAsync("link[rel=canonical]");
            if (link != null)
            {
                var href = await link.GetAttributeAsync("href");
                if (Uri.TryCreate(pageUrl, href, out var result))
                    return result;
            }

            return pageUrl;
        }

        async Task<IReadOnlyCollection<PageLink>> GetLinks(IPage page, RobotConfiguration robotsConfiguration)
        {
            var result = new List<PageLink>();
            var anchors = await page.QuerySelectorAllAsync("a[href]");
            foreach (var anchor in anchors)
            {
                var href = await anchor.EvaluateAsync<string>("node => node.href");
                if (!Uri.TryCreate(href, UriKind.Absolute, out var url))
                    continue;

                var innerText = await anchor.EvaluateAsync<string>("node => node.innerText"); // use innerText to resolve CSS and remove hidden text
                var rel = ParseRobotsValue(await anchor.GetAttributeValueOrDefaultAsync("rel"));
                result.Add(new PageLink(url, innerText, rel.Follow ?? robotsConfiguration.FollowLinks));
            }

            return result;
        }

        async Task<IReadOnlyCollection<string>> GetMainContents(IPage page)
        {
            var result = new List<string>();
            var elements = await page.QuerySelectorAllAsync("main, *[role=main]");
            if (elements.Any())
            {
                foreach (var element in elements)
                {
                    var innerText = await element.EvaluateAsync<string>("node => node.innerText"); // use innerText to resolve CSS
                    result.Add(innerText);
                }
            }
            else
            {
                var innerText = await page.InnerTextAsync("body"); // use innerText to resolve CSS
                result.Add(innerText);
            }

            return result;
        }

        async Task<IReadOnlyCollection<Uri>> GetFeeds(IPage page)
        {
            var result = new List<Uri>();
            var elements = await page.QuerySelectorAllAsync("link[rel=alternate]");
            foreach (var element in elements)
            {
                var type = await element.GetAttributeValueOrDefaultAsync("type");
                if (IsMimeTypeOneOf(type, "application/atom+xml", "application/rss+xml", "application/xml"))
                {
                    var link = await element.GetAttributeValueOrDefaultAsync("href");
                    if (link != null && Uri.TryCreate(new Uri(page.Url), link, out var uri))
                    {
                        result.Add(uri);
                    }
                }
            }

            return result;
        }

        async Task<IReadOnlyCollection<Uri>> GetSitemaps(IPage page)
        {
            var result = new List<Uri>();
            var elements = await page.QuerySelectorAllAsync("link[rel=sitemap]");
            foreach (var element in elements)
            {
                var link = await element.GetAttributeValueOrDefaultAsync("href");
                if (link != null && Uri.TryCreate(new Uri(page.Url), link, out var uri))
                {
                    result.Add(uri);
                }
            }

            return result;
        }

        async Task<IReadOnlyCollection<string>> GetHeaders(IPage page)
        {
            var result = new List<string>();
            var elements = await page.QuerySelectorAllAsync("h1,h2,h3,h4,h5,h6");
            foreach (var element in elements)
            {
                var innerText = await element.EvaluateAsync<string>("node => node.innerText"); // use innerText to resolve CSS
                result.Add(innerText);
            }

            return result;
        }

        async Task<RobotConfiguration> GetRobotConfiguration(IResponse response, IPage page)
        {
            bool? follow = null;
            bool? index = null;
            // Use header
            var values = await response.HeaderValuesAsync("X-robots-tag");
            if (values != null)
            {
                foreach (var value in values)
                {
                    ParseValue(value);
                }
            }

            // Use meta
            foreach (var tag in await page.QuerySelectorAllAsync("meta[name=robots]"))
            {
                var content = await tag.GetAttributeValueOrDefaultAsync("content");
                ParseValue(content);
            }

            return new RobotConfiguration(index ?? true, follow ?? true);

            void ParseValue(string? value)
            {
                var result = ParseRobotsValue(value);
                follow ??= result.Follow;
                index ??= result.Index;
            }
        }

        async Task<string?> GetFirstAttributeValue(IPage page, params (string Element, string AttributeName)[] elements)
        {
            foreach (var (elementSelector, attributeName) in elements)
            {
                var link = await page.QuerySelectorAsync(elementSelector);
                if (link != null)
                    return await link.GetAttributeAsync(attributeName);
            }

            return null;
        }
    }

    protected virtual ValueTask OnPageCrawled(PageCrawledContext context)
    {
        PageCrawled?.Invoke(this, new PageCrawledEventArgs(context.PageData));
        return ValueTask.CompletedTask;
    }

    private static (bool? Index, bool? Follow) ParseRobotsValue(string? value)
    {
        if (value == null)
            return default;

        bool? follow = null;
        bool? index = null;

        var parts = value.Split(new char[] { ' ', ',' }, StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Equals("nofollow", StringComparison.OrdinalIgnoreCase))
            {
                follow = false;
            }
            else if (part.Equals("follow", StringComparison.OrdinalIgnoreCase))
            {
                follow = true;
            }
            else if (part.Equals("noindex", StringComparison.OrdinalIgnoreCase))
            {
                index = false;
            }
            else if (part.Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                index = true;
            }
            else if (part.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                follow = false;
                index = false;
            }
        }

        return (index, follow);
    }

    private static bool AreMimeTypeEqual(string? left, string right)
    {
        return left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMimeTypeOneOf(string? value, params string[] mimeTypes)
    {
        if (value is null)
            return false;

        foreach (var item in mimeTypes)
        {
            if (AreMimeTypeEqual(value, item))
                return true;
        }

        return false;
    }
}
