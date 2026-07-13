using System.Collections;
using System.Collections.Generic;
using System.IO;
using Oxide.Core;
using UnityEngine;
using UnityEngine.Networking;

namespace Oxide.Plugins
{
    [Info("ExtractItemIcons", "Codex", "1.1.0")]
    [Description("Downloads the icon PNG for every item in the game into oxide/data/ExtractedIcons")]
    class ExtractItemIcons : RustPlugin
    {
        // Same public source ImageLibrary uses for its stock item icons.
        private const string IconSource = "https://www.rustedit.io/images/imagelibrary/";

        // Categories copied into subfolders by "extracticons sort". Building blocks and
        // deployables live inside Construction and Items, same as the F1 menu buckets.
        private static readonly HashSet<ItemCategory> SortedCategories = new HashSet<ItemCategory>
        {
            ItemCategory.Electrical,
            ItemCategory.Construction,
            ItemCategory.Items,
            ItemCategory.Component,
            ItemCategory.Misc,
            ItemCategory.Traps,
            ItemCategory.Resources,
            ItemCategory.Fun
        };

        private Coroutine routine;

        [ConsoleCommand("extracticons")]
        private void ExtractCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2)
                return;

            if (arg.GetString(0, string.Empty).ToLower() == "sort")
            {
                SortIcons();
                return;
            }

            if (routine != null)
            {
                Puts("Extraction is already running.");
                return;
            }

            routine = ServerMgr.Instance.StartCoroutine(ExtractAll());
        }

        // Copies already-downloaded icons into per-category subfolders; nothing is re-downloaded.
        private void SortIcons()
        {
            string folder = Path.Combine(Interface.Oxide.DataDirectory, "ExtractedIcons");
            if (!Directory.Exists(folder))
            {
                Puts("Nothing to sort - run 'extracticons' first.");
                return;
            }

            int copied = 0;
            int missing = 0;

            foreach (ItemDefinition definition in ItemManager.itemList)
            {
                if (!SortedCategories.Contains(definition.category))
                    continue;

                string source = Path.Combine(folder, definition.shortname + ".png");
                if (!File.Exists(source))
                {
                    missing++;
                    continue;
                }

                string categoryFolder = Path.Combine(folder, definition.category.ToString());
                Directory.CreateDirectory(categoryFolder);

                string target = Path.Combine(categoryFolder, definition.shortname + ".png");
                if (File.Exists(target))
                    continue;

                File.Copy(source, target);
                copied++;
            }

            Puts("Sorted icons into category folders: " + copied + " copied, " + missing + " not downloaded.");
        }

        private void Unload()
        {
            if (routine != null && ServerMgr.Instance != null)
                ServerMgr.Instance.StopCoroutine(routine);
        }

        private IEnumerator ExtractAll()
        {
            string folder = Path.Combine(Interface.Oxide.DataDirectory, "ExtractedIcons");
            Directory.CreateDirectory(folder);

            int saved = 0;
            int skipped = 0;
            int failed = 0;
            int total = ItemManager.itemList.Count;

            Puts("Extracting " + total + " item icons to " + folder + " ...");

            foreach (ItemDefinition definition in ItemManager.itemList)
            {
                string path = Path.Combine(folder, definition.shortname + ".png");
                if (File.Exists(path))
                {
                    skipped++;
                    continue;
                }

                using (UnityWebRequest request = UnityWebRequest.Get(IconSource + definition.shortname + ".png"))
                {
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success || request.downloadHandler == null || request.downloadHandler.data == null || request.downloadHandler.data.Length == 0)
                    {
                        failed++;
                        Puts("Failed: " + definition.shortname);
                    }
                    else
                    {
                        File.WriteAllBytes(path, request.downloadHandler.data);
                        saved++;
                    }
                }

                int processed = saved + skipped + failed;
                if (processed % 50 == 0)
                    Puts("Progress: " + processed + "/" + total);

                yield return new WaitForSeconds(0.05f);
            }

            Puts("Icon extraction complete: " + saved + " saved, " + skipped + " already existed, " + failed + " failed.");
            routine = null;
        }
    }
}
