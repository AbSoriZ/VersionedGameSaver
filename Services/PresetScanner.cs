using System.IO;
using VersionedGameSaver.Models;

namespace VersionedGameSaver.Services;

public sealed class PresetScanner
{
    public List<GameProfile> Scan()
    {
        var results = new List<GameProfile>();
        var zomboidRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Zomboid",
            "Saves");

        if (Directory.Exists(zomboidRoot))
        {
            var profile = new GameProfile
            {
                GameId = "project-zomboid",
                GameName = "Project Zomboid",
                Label = "Project Zomboid",
                SaveRootPath = zomboidRoot,
                IsPreset = true
            };

            profile.Scopes.Add(new SaveScope
            {
                Label = "All Project Zomboid saves",
                Kind = SaveScopeKind.WholeGame,
                Items = [new ScopeItem { SourcePath = zomboidRoot }]
            });

            foreach (var world in FindProjectZomboidWorlds(zomboidRoot))
            {
                profile.Scopes.Add(new SaveScope
                {
                    Label = Path.GetFileName(world),
                    Kind = SaveScopeKind.DetectedWorld,
                    Items = [new ScopeItem { SourcePath = world }]
                });
            }

            results.Add(profile);
        }

        return results;
    }

    private static IEnumerable<string> FindProjectZomboidWorlds(string saveRoot)
    {
        foreach (var modeDirectory in Directory.EnumerateDirectories(saveRoot))
        {
            foreach (var worldDirectory in Directory.EnumerateDirectories(modeDirectory))
            {
                yield return worldDirectory;
            }
        }
    }
}
