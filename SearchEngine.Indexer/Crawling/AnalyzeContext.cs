using Microsoft.Playwright;

namespace WebCrawler;

internal record AnalyzeContext(Uri Uri, IBrowserContext BrowserContext, CancellationToken CancellationToken);
