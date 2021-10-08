namespace WebCrawler;

public sealed class UrlFilter : IUrlFilter
{
    public UrlFilter(string authority, string pathPrefix)
    {
        Authority = authority;
        PathPrefix = pathPrefix;
    }

    public string Authority { get; }
    public string PathPrefix { get; }

    public bool Match(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(uri.Authority, Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!uri.PathAndQuery.StartsWith(PathPrefix, StringComparison.Ordinal))
            return false;

        return true;
    }
}
