namespace WebCrawler;

public interface IUrlFilter
{
    bool Match(Uri uri);
}
