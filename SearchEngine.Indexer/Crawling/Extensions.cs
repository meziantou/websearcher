using Microsoft.Playwright;

namespace WebCrawler;

internal static class Extensions
{
    public static async Task<string?> GetAttributeValueOrDefaultAsync(this IElementHandle elementHandle, string attributeName)
    {
        try
        {
            return await elementHandle.GetAttributeAsync(attributeName);
        }
        catch (KeyNotFoundException)
        {
            return default;
        }
    }
}
