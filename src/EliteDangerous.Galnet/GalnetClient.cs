using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EliteDangerous.Galnet;

/// <summary>
/// Reads the Galnet news feed and parses it into plain article records. This is pure transport: it
/// does not decide how many articles to keep, how long to cache them, or how to present them — that
/// policy belongs to the caller. Following the EDSM and Spansh clients' convention it never throws
/// for network/HTTP/parse problems; failures surface as <see cref="GalnetResult{T}.Failure"/>. A
/// single instance is safe to reuse across fetches.
/// </summary>
/// <remarks>
/// The feed is RSS 2.0 with one <c>item</c> per article carrying <c>guid</c>, <c>title</c>,
/// <c>pubDate</c> and a CDATA <c>description</c> of HTML. There is no per-article link, category or
/// image, so an article is only ever id + headline + text + date.
/// </remarks>
public sealed class GalnetClient : IDisposable
{
    private readonly GalnetClientOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GalnetClient(GalnetClientOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(Sanitize(_options.SoftwareName), Sanitize(_options.SoftwareVersion)));
    }

    /// <summary>
    /// Fetch the current feed, newest first. An OK result with an empty list means the feed was
    /// well-formed but held no articles.
    /// </summary>
    public async Task<GalnetResult<IReadOnlyList<GalnetArticle>>> GetLatestAsync(CancellationToken ct = default)
    {
        try
        {
            // Headers first, so an oversized body can be refused before any of it is buffered.
            using var response = await _http.GetAsync(_options.FeedUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return GalnetResult<IReadOnlyList<GalnetArticle>>.Failure($"HTTP {(int)response.StatusCode}");

            // Read as bytes and decode leniently: the live feed occasionally carries a stray byte
            // that is not valid UTF-8, and one bad apostrophe should not cost the whole news card.
            var bytes = await ReadCappedAsync(response.Content, ct).ConfigureAwait(false);
            return bytes is null
                ? GalnetResult<IReadOnlyList<GalnetArticle>>.Failure($"feed larger than {_options.MaxResponseBytes} bytes")
                : Parse(Decode(bytes));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return GalnetResult<IReadOnlyList<GalnetArticle>>.Failure(ex.Message); }   // incl. an HttpClient timeout
    }

    /// <summary>The body bytes, or null if the body exceeds <see cref="GalnetClientOptions.MaxResponseBytes"/>.</summary>
    private async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        var cap = _options.MaxResponseBytes;
        if (content.Headers.ContentLength is { } declared && declared > cap) return null;

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Parse a feed document that has already been fetched. Exposed for tests and offline replay.</summary>
    public static GalnetResult<IReadOnlyList<GalnetArticle>> Parse(string feed)
    {
        if (string.IsNullOrWhiteSpace(feed))
            return GalnetResult<IReadOnlyList<GalnetArticle>>.Failure("empty feed");

        XDocument doc;
        try
        {
            // A feed has no business carrying a DTD: refuse one (no entity expansion, no external
            // resolution) rather than rely on the framework defaults.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(feed), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            return GalnetResult<IReadOnlyList<GalnetArticle>>.Failure("unparseable feed: " + ex.Message);
        }

        var articles = new List<GalnetArticle>();
        foreach (var item in doc.Descendants("item"))
        {
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;   // an article with no headline is unusable

            articles.Add(new GalnetArticle(
                Id: Text(item, "guid") is { Length: > 0 } id ? id : title,
                Title: PlainText(title),
                Body: PlainText(ExtractCmsBody(Text(item, "description"))),
                Published: ParseDate(Text(item, "pubDate"))));
        }

        return GalnetResult<IReadOnlyList<GalnetArticle>>.Ok(articles);
    }

    private static string Text(XElement item, string name) => item.Element(name)?.Value?.Trim() ?? "";

    /// <summary>
    /// RFC-1123-ish dates as the feed writes them ("Sat, 15 Aug 2026 22:10:41 +0100"). Anything that
    /// will not parse becomes null rather than a wrong date.
    /// </summary>
    private static DateTimeOffset? ParseDate(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// The CMS-backed feed wraps each article's real text in a Drupal "Body" field, followed by more
    /// field blocks that are metadata, not article content (an in-lore date, GUID, image key, slug) —
    /// all inside the same <c>&lt;description&gt;</c>. Slice out just the body field's value so that
    /// metadata doesn't leak into the article as extra lines. A description that isn't shaped this way
    /// (the plain CDATA body the classic feed used) has no such marker and passes through unchanged.
    /// </summary>
    private static string ExtractCmsBody(string html)
    {
        const string bodyField = "field--name-body";
        var bodyStart = html.IndexOf(bodyField, StringComparison.OrdinalIgnoreCase);
        if (bodyStart < 0) return html;

        // Skip past the field's own "Body" label div to where its actual value starts.
        var labelEnd = html.IndexOf("</div>", bodyStart, StringComparison.OrdinalIgnoreCase);
        if (labelEnd < 0) return html;
        var contentStart = labelEnd + "</div>".Length;

        // Everything up to the next Drupal field group (the date/guid/image/slug fields that follow
        // the body in this feed) is the real article text. The marker sits inside that field's own
        // *class attribute*, not at its tag's start, so back up to the enclosing "<div" to cut on a
        // clean tag boundary — cutting at the marker itself would leave a dangling "<div class=..."
        // fragment that the tag-stripping pass downstream can't clean up (no closing '>' left to match).
        var fieldMarker = html.IndexOf("field--name-field-", contentStart, StringComparison.OrdinalIgnoreCase);
        var contentEnd = html.Length;
        if (fieldMarker >= 0)
        {
            var tagStart = html.LastIndexOf("<div", fieldMarker, StringComparison.OrdinalIgnoreCase);
            contentEnd = tagStart >= contentStart ? tagStart : fieldMarker;
        }
        return contentEnd > contentStart ? html[contentStart..contentEnd] : html;
    }

    /// <summary>
    /// Turn the feed's HTML into text: line breaks survive as newlines, every other tag is dropped and
    /// entities decoded. Consecutive blank lines collapse so the reader pane has no gaping holes.
    /// </summary>
    private static string PlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // The feed writes each break as "<br />" followed by a real newline. In HTML that trailing
        // whitespace is insignificant, so swallow it — keeping it would double-space every article.
        try
        {
            var text = LineBreak.Replace(html, "\n");
            text = ParagraphEnd.Replace(text, "\n\n");
            text = StripTags(text);
            text = System.Net.WebUtility.HtmlDecode(text);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = TrimLineEnds(text);
            text = BlankRun.Replace(text, "\n\n");
            return text.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return "";   // pathological input: an unreadable article beats a hung fetch
        }
    }

    // Every pattern carries a timeout, so a crafted body can never pin a thread on regex backtracking.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex LineBreak = new(@"<br\s*/?>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex ParagraphEnd = new(@"</p\s*>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex BlankRun = new(@"\n{3,}", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// Drops every <c>&lt;...&gt;</c> tag in one linear pass. (The obvious <c>&lt;[^&gt;]+&gt;</c> regex is
    /// quadratic on a long run of '&lt;' with no closing '&gt;'.) A '&lt;' with no matching '&gt;' is kept as text.
    /// </summary>
    private static string StripTags(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }

            var close = text.IndexOf('>', open + 1);
            if (close < 0) { sb.Append(text, i, text.Length - i); break; }   // no tag can end: the rest is text

            sb.Append(text, i, open - i);
            i = close + 1;
        }
        return sb.ToString();
    }

    /// <summary>Trims trailing spaces/tabs from every line, in one linear pass (no regex backtracking).</summary>
    private static string TrimLineEnds(string text)
    {
        var lines = text.Split('\n');
        for (var n = 0; n < lines.Length; n++) lines[n] = lines[n].TrimEnd(' ', '\t');
        return string.Join('\n', lines);
    }

    /// <summary>UTF-8 with replacement rather than throwing, so one malformed byte cannot fail a fetch.</summary>
    private static string Decode(byte[] bytes) => new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);

    /// <summary>User-Agent product tokens can't contain whitespace or separators; collapse them.</summary>
    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "app" : cleaned;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
