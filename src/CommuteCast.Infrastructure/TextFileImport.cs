using System.Text;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public static class TextFileImport
{
    public static bool Supported(string path) => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);
    public static async Task<string> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!Supported(path)) throw new ArgumentException("Import a .txt or .md file.");
        const int maximumBytes = TextPreparation.MaximumCharacters * 4 + 4;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (input.Length > maximumBytes) throw new ArgumentException("The text file exceeds the supported draft size. Your existing draft is retained.");
        using var buffer = new MemoryStream(); var bytes = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + read > maximumBytes) throw new ArgumentException("The text file exceeds the supported draft size. Your existing draft is retained.");
            buffer.Write(bytes, 0, read);
        }
        var data = buffer.ToArray();
        Encoding encoding = new UTF8Encoding(false, true); var offset = 0;
        if (data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) offset = 3;
        else if (data.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
        else if (data.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
        string text;
        try { text = encoding.GetString(data, offset, data.Length - offset); }
        catch (DecoderFallbackException) { throw new ArgumentException("The file contains invalid text. Save it as UTF-8 or UTF-16 with a byte-order mark. Your draft is retained."); }
        if (string.IsNullOrWhiteSpace(text) || text.Length > TextPreparation.MaximumCharacters || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("Import nonempty text within 250,000 characters, without binary control characters. Your draft is retained.");
        return text;
    }
}
