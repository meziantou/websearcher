using System.Runtime.Serialization;

namespace WebCrawler;

public class CrawlerException : Exception
{
    public CrawlerException()
    {
    }

    public CrawlerException(string? message) : base(message)
    {
    }

    public CrawlerException(string? message, Exception? innerException) : base(message, innerException)
    {
    }

    protected CrawlerException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}