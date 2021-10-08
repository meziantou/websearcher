using Microsoft.Playwright;

namespace WebCrawler;

public sealed class PageCrawledContext
{
    public PageCrawledContext(PageData pageData, IResponse response, IPage page)
    {
        PageData = pageData;
        Response = response;
        Page = page;
    }

    public PageData PageData { get; }
    public IResponse Response { get; }
    public IPage Page { get; }
}
