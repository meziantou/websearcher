namespace WebCrawler;

public record PageData(Uri CanonicalUrl)
{
    public string? MimeType { get; set; }
    public string? Content { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public List<PageLink> Links { get; } = new();
    public List<Uri> Feeds { get; } = new();
    public List<Uri> Sitemaps { get; } = new();
    public List<string> MainElementTexts { get; } = new();
    public List<string> Headers { get; } = new();
    public RobotConfiguration? RobotsConfiguration { get; set; }
    public DateTime CrawledAt { get; } = DateTime.UtcNow;
}
