# Raging Blast 2 Mod Manager

The mod manager of the **Dragon Ball: Raging Blast 2 PC Port**. It installs `.rb2mod` packages, turns mods on and off,
and keeps character IDs from clashing. It also browses GameBanana and installs from there in one click.

It is one self-contained `win-x64` program that sits in the game folder. It also runs under Wine / Proton (Steam Deck).

## What it does

- **Installed**: every mod in the game, with its picture, author and the character IDs it uses. You can turn a mod on or
  off, show it in its folder, remove it (a copy is kept in `Mods\Removed`) or delete it (to the Recycle Bin). The list
  can be sorted by ID, name, enabled first, disabled first or author.
- **Character IDs**: installing a character whose ID is already taken moves it to a free one. *Change ID* moves an
  installed character by hand. *Organize* moves the add-ons whose IDs clash and can also pack the IDs together. The
  game's own characters and its Raging Blast 1 roster never move.
- **Browse**: GameBanana's Raging Blast 2 section, with its categories, sorting and search. *Install* downloads the
  file and installs every `.rb2mod` inside the archive. Mods installed from GameBanana can be checked for updates.
- **1-click install**: GameBanana's mod manager buttons open the program through the `rb2mm:` link scheme. It is
  registered for the current user, with no admin rights needed. A click while the window is open goes to that window.
- **Replacement mods**: the older kind of mod, which replaces a game character's files, is installed as its own folder
  in `Modloader\Characters`, never over the game's files, and can be turned on and off like the others. Two folders
  that change the same file are kept apart.
- **Character Select pictures**: when it opens, it tidies pictures left behind by characters that moved or were
  removed.

## Building

You need the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). Then run:

```bash
dotnet publish RB2ModManager/RB2ModManager.csproj -c Release
```

The single-file exe (`Raging Blast 2 Mod Manager.exe`) is written to
`RB2ModManager/bin/x64/Release/net9.0/win-x64/publish/`. Put it in the game folder, next to `Raging Blast 2.exe`.

| Folder | What it holds |
|---|---|
| `RB2ModManager/` | The window (Avalonia), the 1-click handler and the `--cli` commands |
| `RB2ModCore/` | The `.rb2mod` rules with no UI: reading a package, IDs, install / enable / remove, GameBanana |

### Command line

`"Raging Blast 2 Mod Manager.exe" --cli <command> GAME ...` does the same work as the window. It is useful for tests
on a copy of a game folder:

| Command | What it does |
|---|---|
| `map GAME` | The ID map, with its issues and the free IDs |
| `list GAME` | The installed mods |
| `plan GAME FILE` / `install GAME FILE` | What installing `FILE` would do / install it |
| `change-id GAME MODFILE FOLDER ID` | Move one character of an installed mod |
| `enable GAME MODFILE on\|off` | Turn a mod on or off |
| `remove GAME MODFILE` / `delete GAME MODFILE` | Take a mod out (kept) / delete it (Recycle Bin) |
| `chips GAME [tidy]` | Character Select pictures left behind (`tidy` moves them aside) |

## Notes

- Downloads use GameBanana's public API (`apiv11`). No account is used, and the only identification sent is the client
  name `RB2ModManager/<version>`.
- Dragon Ball and Raging Blast belong to their owners (Bandai Namco, Bird Studio / Shueisha, Toei Animation). The icon
  is the game's artwork. This program does not include any game files.
- Game releases and the launcher: [Castlesea/rb2-launcher](https://github.com/Castlesea/rb2-launcher).
