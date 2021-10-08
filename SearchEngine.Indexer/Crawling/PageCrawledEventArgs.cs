namespace WebCrawler;

public sealed class PageCrawledEventArgs : EventArgs
{
    public PageCrawledEventArgs(PageData pageData)
    {
        PageData = pageData;
    }

    public PageData PageData { get; }
}
