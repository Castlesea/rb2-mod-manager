namespace RB2ModCore;

/// <summary>A newer download of an installed GameBanana mod.</summary>
public sealed record ModUpdate(GbInstall Installed, GbFile File, string Version);

/// <summary>
/// Update checks: every installed mod that came from GameBanana (ModManager\gamebanana.json) against its page's files
/// now. A file uploaded after the installed one (a new upload is a new file on GameBanana) that the Mod Manager can
/// install the same way is an update. Only the mod IDs are sent (Mod/Multi, 40 a call).
/// </summary>
public static class ModUpdates
{
    public static async Task<List<ModUpdate>> Check(GameBananaClient client, GameInstall game, Func<GbInstall, bool> stillInstalled,
        Func<GbInstall, GbFile, CancellationToken, Task<bool>> installable, CancellationToken cancel = default)
    {
        var records = ModDownloads.Sources(game).Where(stillInstalled).ToList();
        if (records.Count == 0) return [];
        var files = await client.Files(records.Select(r => r.ModId), cancel).ConfigureAwait(false);
        var updates = new List<ModUpdate>();
        foreach (var group in records.GroupBy(r => (r.ModId, r.FileId)))
        {
            var record = group.First();
            if (!files.TryGetValue(record.ModId, out var list)) continue;
            foreach (var file in list.Where(f => f.Id != record.FileId && f.Added > record.FileDate && f.Clean).OrderByDescending(f => f.Added))
                if (await installable(record, file, cancel).ConfigureAwait(false))
                {
                    foreach (var installed in group) updates.Add(new ModUpdate(installed, file, file.Version));
                    break;
                }
        }
        return updates;
    }
}
