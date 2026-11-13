using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RB2ModCore;

public sealed record GbImage(string Small, string Medium, string Large);

/// <summary>One mod page on GameBanana (as the lists give it).</summary>
public sealed record GbMod(int Id, string Name, string Url, string Submitter, string Category, string Version,
    DateTimeOffset Added, DateTimeOffset Modified, int Likes, int Views, IReadOnlyList<GbImage> Images, bool Obsolete);

/// <summary>One download of a mod page.</summary>
public sealed record GbFile(int Id, string Name, long Size, string DownloadUrl, string Md5, DateTimeOffset Added, string Version,
    string Description, bool Clean, int Downloads)
{
    public bool IsRb2Mod => Name.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase);
    public bool IsArchive => GameBananaClient.Archives.Contains(Path.GetExtension(Name).ToLowerInvariant());
}

public sealed record GbPage(List<GbMod> Mods, int Total, bool Complete);
public sealed record GbProfile(GbMod Mod, string Text, int Downloads, List<GbFile> Files);
public sealed record GbCategory(int Id, string Name, int Count);

/// <summary>
/// GameBanana's public API for Raging Blast 2 (game 16075): apiv11 for the lists, pages and files (Mod/Index,
/// Util/Search/Results, Mod/Categories, Mod/{id}/ProfilePage, Mod/Multi) and the older Core/Item/Data for what an archive
/// holds (aFlattenedFileList), so a mod is known to carry a .rb2mod before it is downloaded. Nothing about the user is
/// sent: a plain client name as User-Agent, no account.
/// </summary>
public sealed class GameBananaClient
{
    public const int GameId = 16075;
    public const string GamePage = "https://gamebanana.com/games/16075";
    public static readonly string[] Archives = [".zip", ".7z", ".rar"];
    public static readonly (string Key, string Label)[] Sorts =
    [
        ("Generic_NewAndUpdated", "New and updated"), ("Generic_Newest", "Newest"), ("Generic_MostDownloaded", "Most downloaded"),
        ("Generic_MostLiked", "Most liked"), ("Generic_MostViewed", "Most viewed"),
    ];

    private const string Api = "https://gamebanana.com/apiv11";
    private static readonly HttpClient Http = MakeClient();
    private readonly Dictionary<int, IReadOnlyList<string>> contents = [];
    private readonly Dictionary<string, byte[]> images = new(StringComparer.Ordinal);

    private static HttpClient MakeClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RB2ModManager", typeof(GameBananaClient).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        return client;
    }

    private static async Task<JsonNode?> Get(string url, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GameBanana answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
    }

    /// <summary>A page of Raging Blast 2 mods, newest first by default; with <paramref name="search"/>, GameBanana's search.</summary>
    public async Task<GbPage> Browse(int page, string sort, int? category, string? search, CancellationToken cancel = default)
    {
        string url;
        if (!string.IsNullOrWhiteSpace(search))
            url = $"{Api}/Util/Search/Results?_sModelName=Mod&_sOrder=best_match&_idGameRow={GameId}&_sSearchString={Uri.EscapeDataString(search.Trim())}&_nPage={page}";
        else
        {
            url = $"{Api}/Mod/Index?_nPage={page}&_nPerpage=20&_aFilters%5BGeneric_Game%5D={GameId}&_sSort={sort}";
            if (category is { } c) url += $"&_aFilters%5BGeneric_Category%5D={c}";
        }
        var json = await Get(url, cancel).ConfigureAwait(false) as JsonObject ?? throw new InvalidDataException("GameBanana sent no list.");
        var mods = (json["_aRecords"] as JsonArray ?? []).OfType<JsonObject>().Where(r => Text(r["_sModelName"]) is null or "Mod")
            .Select(ParseMod).Where(m => m.Id > 0).ToList();
        var meta = json["_aMetadata"];
        return new GbPage(mods, Int(meta?["_nRecordCount"]) ?? mods.Count, meta?["_bIsComplete"]?.GetValue<bool>() ?? true);
    }

    public async Task<List<GbCategory>> Categories(CancellationToken cancel = default)
    {
        var json = await Get($"{Api}/Mod/Categories?_idGameRow={GameId}&_sSort=a_to_z&_bShowEmpty=false", cancel).ConfigureAwait(false);
        return (json as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => new GbCategory(Int(c["_idRow"]) ?? 0, Text(c["_sName"]) ?? "", Int(c["_nItemCount"]) ?? 0)).Where(c => c.Id > 0).ToList();
    }

    /// <summary>A mod's page: its text (as plain text), downloads and files.</summary>
    public async Task<GbProfile> Profile(int id, CancellationToken cancel = default)
    {
        var json = await Get($"{Api}/Mod/{id}/ProfilePage", cancel).ConfigureAwait(false) as JsonObject ?? throw new InvalidDataException("GameBanana sent no page.");
        return new GbProfile(ParseMod(json), PlainText(Text(json["_sText"]) ?? ""), Int(json["_nDownloadCount"]) ?? 0, ParseFiles(json["_aFiles"]));
    }

    /// <summary>The files of many mods at once (Mod/Multi, 40 a call).</summary>
    public async Task<Dictionary<int, List<GbFile>>> Files(IEnumerable<int> ids, CancellationToken cancel = default)
    {
        var result = new Dictionary<int, List<GbFile>>();
        foreach (var chunk in ids.Distinct().Chunk(40))
        {
            var json = await Get($"{Api}/Mod/Multi?_csvRowIds={string.Join(',', chunk)}&_csvProperties=_idRow,_aFiles", cancel).ConfigureAwait(false);
            foreach (var mod in (json as JsonArray ?? []).OfType<JsonObject>())
                if (Int(mod["_idRow"]) is { } modId) result[modId] = ParseFiles(mod["_aFiles"]);
        }
        return result;
    }

    /// <summary>The files inside an uploaded archive (GameBanana lists them), cached.</summary>
    public async Task<IReadOnlyList<string>> ArchiveContents(int fileId, CancellationToken cancel = default)
    {
        lock (contents)
            if (contents.TryGetValue(fileId, out var known)) return known;
        var json = await Get($"https://api.gamebanana.com/Core/Item/Data?itemtype=File&itemid={fileId}&fields=aFlattenedFileList()", cancel).ConfigureAwait(false);
        var list = (json as JsonArray)?.FirstOrDefault() is JsonArray files
            ? files.Select(f => Text(f) ?? "").Where(f => f.Length > 0).ToList()
            : new List<string>();
        lock (contents) contents[fileId] = list;
        return list;
    }

    /// <summary>The .rb2mod files a download carries: itself, or the ones inside the archive.</summary>
    public async Task<List<string>> Rb2ModsIn(GbFile file, CancellationToken cancel = default)
    {
        if (file.IsRb2Mod) return [file.Name];
        if (!file.IsArchive) return [];
        return (await ArchiveContents(file.Id, cancel).ConfigureAwait(false))
            .Where(f => f.EndsWith(Rb2ModFile.Extension, StringComparison.OrdinalIgnoreCase)).Select(f => f.Replace('\\', '/').Split('/')[^1]).ToList();
    }

    /// <summary>Downloads a file into <paramref name="folder"/> (a .part until it is whole), then checks its MD5.</summary>
    public async Task<string> Download(GbFile file, string folder, IProgress<double>? progress, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(folder);
        var name = string.Concat(Path.GetFileName(file.Name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var target = Path.Combine(folder, $"{file.Id}_{name}");
        var part = target + ".part";
        using (var response = await Http.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"The download failed: GameBanana answered {(int)response.StatusCode}.");
            var total = response.Content.Headers.ContentLength ?? file.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var output = File.Create(part);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, done / (double)total));
            }
        }
        if (file.Md5.Length == 32)
        {
            string md5;
            await using (var stream = File.OpenRead(part))
                md5 = Convert.ToHexStringLower(await MD5.HashDataAsync(stream, cancel).ConfigureAwait(false));
            if (!md5.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                throw new InvalidDataException($"{file.Name} arrived damaged (its checksum doesn't match GameBanana's). Try again.");
            }
        }
        File.Move(part, target, true);
        return target;
    }

    /// <summary>An image (previews, cached for the session).</summary>
    public async Task<byte[]> Image(string url, CancellationToken cancel = default)
    {
        lock (images)
            if (images.TryGetValue(url, out var known)) return known;
        var bytes = await Http.GetByteArrayAsync(url, cancel).ConfigureAwait(false);
        lock (images) images[url] = bytes;
        return bytes;
    }

    // ---- parsing ---------------------------------------------------------------------------------------------------

    private static GbMod ParseMod(JsonObject r)
    {
        var shots = new List<GbImage>();
        foreach (var image in (r["_aPreviewMedia"]?["_aImages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var root = Text(image["_sBaseUrl"]);
            var file = Text(image["_sFile"]);
            if (root is null || file is null) continue;
            string Url(string? sized) => $"{root}/{sized ?? file}";
            shots.Add(new GbImage(Url(Text(image["_sFile220"]) ?? Text(image["_sFile100"])), Url(Text(image["_sFile530"]) ?? Text(image["_sFile220"])), Url(file)));
        }
        var category = Text(r["_aRootCategory"]?["_sName"]) ?? Text(r["_aCategory"]?["_sName"]) ?? "";
        return new GbMod(Int(r["_idRow"]) ?? 0, WebUtility.HtmlDecode(Text(r["_sName"]) ?? ""), Text(r["_sProfileUrl"]) ?? "",
            Text(r["_aSubmitter"]?["_sName"]) ?? "", category, Text(r["_sVersion"]) ?? "",
            Time(r["_tsDateAdded"]), Time(r["_tsDateModified"] ?? r["_tsDateAdded"]), Int(r["_nLikeCount"]) ?? 0, Int(r["_nViewCount"]) ?? 0,
            shots, r["_bIsObsolete"]?.GetValue<bool>() ?? false);
    }

    private static List<GbFile> ParseFiles(JsonNode? node) =>
        (node as JsonArray ?? []).OfType<JsonObject>().Select(f => new GbFile(
            Int(f["_idRow"]) ?? 0, Text(f["_sFile"]) ?? "", Long(f["_nFilesize"]), Text(f["_sDownloadUrl"]) ?? "", Text(f["_sMd5Checksum"]) ?? "",
            Time(f["_tsDateAdded"]), Text(f["_sVersion"]) ?? "", WebUtility.HtmlDecode(Text(f["_sDescription"]) ?? ""),
            Text(f["_sAvResult"]) is null or "clean", Int(f["_nDownloadCount"]) ?? 0)).Where(f => f.Id > 0 && f.DownloadUrl.Length > 0).ToList();

    /// <summary>A page's HTML as readable text (line breaks kept, tags dropped).</summary>
    public static string PlainText(string html)
    {
        var text = Regex.Replace(html, @"<\s*(br|/p|/h\d|/li|/div)\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n");
        return text.Trim();
    }

    private static DateTimeOffset Time(JsonNode? node) => Long(node) is var s and > 0 ? DateTimeOffset.FromUnixTimeSeconds(s) : DateTimeOffset.MinValue;
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? (int)l : v2(node);
    private static int? v2(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && int.TryParse(s, CultureInfo.InvariantCulture, out var i) ? i : null;
    private static long Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
}
