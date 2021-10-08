using System.Text.Json;

namespace WebCrawler;

public class FileIndexWriter : IAsyncDisposable
{
    private readonly string _filePath;
    private readonly FileStream _fileStream;
    private readonly Utf8JsonWriter _writer;

    public FileIndexWriter(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 4096, useAsync: true);
        _writer = new Utf8JsonWriter(_fileStream, new JsonWriterOptions() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _writer.WriteStartObject();
        _writer.WriteNumber("Version", 1);
        _writer.WriteString("CreatedAt", DateTime.UtcNow);
        _writer.WriteStartArray("Pages");
    }

    public void IndexPage(PageData data)
    {
        lock (_writer)
        {
            JsonSerializer.Serialize(_writer, data);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _writer.WriteEndArray();
        _writer.WriteEndObject();

        await _writer.DisposeAsync();
        await _fileStream.DisposeAsync();
    }
}
