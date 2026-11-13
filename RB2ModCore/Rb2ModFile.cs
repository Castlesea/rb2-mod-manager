using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RB2ModCore;

/// <summary>One form of a character: its ID, its Character Select picture number and the forms it transforms into.</summary>
public sealed record FormInfo(int Id, string Name, int PresentationId, IReadOnlyList<int> Destinations);

/// <summary>One package of a .rb2mod (Characters/&lt;folder&gt;) or one character the game ships.</summary>
public sealed record PackageInfo(string Kind, string Folder, int Id, string Name, bool RetailEdit, IReadOnlyList<FormInfo> Forms);

/// <summary>What a .rb2mod file holds (rb2mod.json and each character's character.json), read without extracting it.</summary>
public sealed record ModInfo(string Path, string Name, string Author, string Version, string Description, string Created,
    IReadOnlyList<PackageInfo> Packages, byte[]? Preview)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The .rb2mod format (the Engine's ModPackage writes it): a ZIP with rb2mod.json at its root ({"format": "rb2mod",
/// "format_version": 1, name, author, version, description, created, packages: [{kind, folder, id, name, retail_edit,
/// forms}], preview}), an optional preview.png and the packages under Characters/&lt;folder&gt;/ (maps.rb2mod: Maps/).
/// Reading checks every entry first: no path leaving the archive, no programs, 8 GB at most once extracted.
/// </summary>
public static class Rb2ModFile
{
    public const string Extension = ".rb2mod";
    public const string ManifestName = "rb2mod.json";
    public const long MaxTotalBytes = 8L << 30;
    /// <summary>Already compressed game files: stored in the archive, the rest deflated.</summary>
    public static readonly string[] Stored = [".zpak", ".pak", ".png", ".jpg", ".dds", ".xma", ".wav"];
    /// <summary>A mod never carries a program.</summary>
    public static readonly string[] Refused = [".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".com", ".scr", ".lnk", ".reg"];

    internal static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Reads and checks a .rb2mod; throws <see cref="InvalidDataException"/> when it is unsafe or broken.</summary>
    public static ModInfo Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        Check(zip);
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Not a .rb2mod: rb2mod.json is missing.");
        var manifest = ReadJson(manifestEntry) ?? throw new InvalidDataException("rb2mod.json is not an object.");
        if (manifest["format"]?.GetValue<string>() != "rb2mod") throw new InvalidDataException("rb2mod.json: format is not \"rb2mod\".");
        if ((Int(manifest["format_version"]) ?? 0) > 1) throw new InvalidDataException("This mod needs a newer Mod Manager.");
        var packages = new List<PackageInfo>();
        foreach (var package in (manifest["packages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var kind = Text(package["kind"]) ?? "character";
            var folder = Text(package["folder"]) ?? "";
            if (kind != "character")
            {
                packages.Add(new PackageInfo(kind, folder, Int(package["id"]) ?? -1, Text(package["name"]) ?? folder, false, []));
                continue;
            }
            if (!Regex.IsMatch(folder, @"^\d{3}_[A-Za-z0-9_\-]+$")) throw new InvalidDataException($"Invalid package folder \"{folder}\".");
            var character = zip.GetEntry($"Characters/{folder}/character.json") ?? throw new InvalidDataException($"{folder}: character.json is missing.");
            var json = ReadJson(character) ?? throw new InvalidDataException($"{folder}: character.json is not an object.");
            var id = Int(package["id"]) ?? Int(json["id"]) ?? int.Parse(folder[..3], CultureInfo.InvariantCulture);
            packages.Add(new PackageInfo(kind, folder, id, Text(package["name"]) ?? Text(json["name"]) ?? folder,
                package["retail_edit"]?.GetValue<bool>() ?? false, Forms(json, id)));
        }
        // maps.rb2mod and other content without a package list: what it carries, by its top folders
        if (packages.Count == 0)
            foreach (var top in zip.Entries.Select(e => e.FullName.Replace('\\', '/').Split('/')[0]).Where(t => t is "Maps" or "Overrides").Distinct())
                packages.Add(new PackageInfo(top == "Maps" ? "maps" : "override", top, -1, top, false, []));
        if (packages.Count == 0) throw new InvalidDataException("The mod has nothing the game loads.");
        byte[]? preview = null;
        if (Text(manifest["preview"]) is { } previewName && zip.GetEntry(previewName) is { Length: < 16 << 20 } previewEntry)
        {
            using var stream = previewEntry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            preview = memory.ToArray();
        }
        return new ModInfo(path, Text(manifest["name"]) ?? Path.GetFileNameWithoutExtension(path), Text(manifest["author"]) ?? "",
            Text(manifest["version"]) ?? "", Text(manifest["description"]) ?? "", Text(manifest["created"]) ?? "", packages, preview);
    }

    /// <summary>Every entry stays inside the archive, no program, the size limit.</summary>
    internal static void Check(ZipArchive zip)
    {
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part == ".."))
                throw new InvalidDataException($"Unsafe path in the mod: {entry.FullName}");
            if (Refused.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant()))
                throw new InvalidDataException($"The mod contains a program ({entry.FullName}); it is not installed.");
            total += entry.Length;
            if (total > MaxTotalBytes) throw new InvalidDataException("The mod is larger than 8 GB once extracted.");
        }
    }

    /// <summary>A character.json's forms (a character without a form list is one form, its own ID).</summary>
    public static IReadOnlyList<FormInfo> Forms(JsonObject json, int id)
    {
        var forms = new List<FormInfo>();
        foreach (var form in (json["forms"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var destinations = (form["transformer"]?["branches"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(b => Int(b["destination_id"])).OfType<int>().ToList();
            forms.Add(new FormInfo(Int(form["id"]) ?? id, Text(form["name"]) ?? "", Int(form["presentation_id"]) ?? -1, destinations));
        }
        if (forms.Count == 0) forms.Add(new FormInfo(id, Text(json["name"]) ?? "", -1, []));
        return forms;
    }

    internal static JsonObject? ReadJson(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return JsonNode.Parse(reader.ReadToEnd(), documentOptions: Lenient) as JsonObject;
    }

    internal static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static int? Int(JsonNode? node) =>
        node is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null : null;
}
