// Requires: Oxide/uMod for Rust
// Place in: <server>/oxide/plugins/GitHubSync.cs
//
// On load (and on demand), this plugin fetches the file list of a GitHub repo,
// compares each tracked file against the copy on your server using the same
// SHA-1 "blob" hash git uses, and downloads any file that is missing or out of
// date. Downloads are fetched as base64 blobs (so binary files are byte-exact),
// and every downloaded file is re-hashed to verify integrity before it is saved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("GitHubSync", "DoubleBarrel", "1.0.0")]
    [Description("Compares server files against a GitHub repo on load and auto-downloads outdated ones.")]
    public class GitHubSync : CovalencePlugin
    {
        #region Configuration

        private Configuration config;

        private class SyncMapping
        {
            [JsonProperty("Repo path (folder inside the GitHub repo)")]
            public string RepoPath = "Building/oxide";

            [JsonProperty("Local path (folder on the server, relative to the server root)")]
            public string LocalPath = "oxide";
        }

        private class Configuration
        {
            [JsonProperty("GitHub owner (user or org)")]
            public string Owner = "doublebarrelrust";

            [JsonProperty("GitHub repo name")]
            public string Repo = "Double-Barrel-Servers";

            [JsonProperty("Branch (use a branch WITHOUT slashes once merged, e.g. 'main')")]
            public string Branch = "main";

            [JsonProperty("GitHub token (optional; raises the rate limit and enables private repos)")]
            public string Token = "";

            [JsonProperty("Folder mappings to sync")]
            public List<SyncMapping> Mappings = new List<SyncMapping> { new SyncMapping() };

            [JsonProperty("Only sync files with these extensions (empty list = all files)")]
            public List<string> Extensions = new List<string>
            {
                ".cs", ".json", ".js", ".html", ".md", ".txt", ".cfg", ".service"
            };

            [JsonProperty("Never touch files under these local paths")]
            public List<string> ExcludedPaths = new List<string>
            {
                // Protect server-specific settings and stored data from being overwritten.
                "oxide/config",
                "oxide/data"
            };

            [JsonProperty("Automatically download outdated files (false = report only)")]
            public bool AutoDownload = true;

            [JsonProperty("Run the check automatically when the plugin loads")]
            public bool CheckOnLoad = true;

            [JsonProperty("Back up files before overwriting them")]
            public bool BackupBeforeOverwrite = true;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception("config was null");
            }
            catch (Exception ex)
            {
                PrintWarning($"Config file is invalid ({ex.Message}); loading defaults.");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        #endregion

        #region Lifecycle & command

        private const string PermAdmin = "githubsync.admin";
        private bool isSyncing;

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            AddCovalenceCommand("githubsync", nameof(CmdSync));
        }

        private void OnServerInitialized(bool initial)
        {
            // Delay so other plugins have finished loading before we potentially replace them.
            if (config.CheckOnLoad)
                timer.Once(5f, () => RunSync());
        }

        // Console:  githubsync
        // Chat:     /githubsync   (requires the githubsync.admin permission)
        private void CmdSync(IPlayer player, string command, string[] args)
        {
            if (!player.IsServer && !player.HasPermission(PermAdmin))
            {
                player.Reply("You don't have permission to use this command.");
                return;
            }
            RunSync(player);
        }

        #endregion

        #region Sync

        private class RemoteFile { public string Path; public string Sha; }
        private class PendingFile { public string Sha; public string LocalFull; public string LocalRel; public bool IsNew; }

        private void RunSync(IPlayer requester = null)
        {
            if (isSyncing)
            {
                Reply(requester, "A sync is already in progress.");
                return;
            }
            isSyncing = true;
            Log(requester, $"Checking {config.Owner}/{config.Repo}@{config.Branch} for updates...");
            ResolveBranch(requester, commitSha => FetchTree(requester, commitSha));
        }

        // Step 1: resolve the branch name to a commit SHA (handles branch names safely).
        private void ResolveBranch(IPlayer requester, Action<string> onResolved)
        {
            string url = $"https://api.github.com/repos/{config.Owner}/{config.Repo}/git/ref/heads/{config.Branch}";
            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    isSyncing = false;
                    LogError(requester, $"Could not resolve branch '{config.Branch}' (HTTP {code}). {DescribeHttp(code)}");
                    return;
                }
                try
                {
                    string sha = (string)JObject.Parse(response)["object"]?["sha"];
                    if (string.IsNullOrEmpty(sha))
                    {
                        isSyncing = false;
                        LogError(requester, "GitHub did not return a commit for that branch.");
                        return;
                    }
                    onResolved(sha);
                }
                catch (Exception ex)
                {
                    isSyncing = false;
                    LogError(requester, $"Failed to parse branch info: {ex.Message}");
                }
            }, this, RequestMethod.GET, GitHubHeaders());
        }

        // Step 2: fetch the full recursive file tree for that commit and diff it against local files.
        private void FetchTree(IPlayer requester, string commitSha)
        {
            string url = $"https://api.github.com/repos/{config.Owner}/{config.Repo}/git/trees/{commitSha}?recursive=1";
            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    isSyncing = false;
                    LogError(requester, $"Failed to fetch file tree (HTTP {code}). {DescribeHttp(code)}");
                    return;
                }

                List<RemoteFile> remote;
                try
                {
                    var json = JObject.Parse(response);
                    if (json["truncated"]?.Value<bool>() == true)
                        LogError(requester, "Warning: GitHub truncated the file tree; some files may be skipped. Sync smaller folders or use a token.");

                    remote = ((JArray)json["tree"])
                        .Where(t => (string)t["type"] == "blob")
                        .Select(t => new RemoteFile { Path = (string)t["path"], Sha = (string)t["sha"] })
                        .ToList();
                }
                catch (Exception ex)
                {
                    isSyncing = false;
                    LogError(requester, $"Failed to parse file tree: {ex.Message}");
                    return;
                }

                var toUpdate = new List<PendingFile>();
                foreach (var rf in remote)
                {
                    var mapping = config.Mappings.FirstOrDefault(m => PathStartsWith(rf.Path, m.RepoPath));
                    if (mapping == null) continue;

                    string relative = rf.Path.Substring(mapping.RepoPath.Trim('/').Length).TrimStart('/');
                    string localRel = CombineRel(mapping.LocalPath, relative);

                    if (!MatchesExtension(localRel) || IsExcluded(localRel)) continue;

                    string localFull = Path.Combine(Interface.Oxide.RootDirectory, localRel);
                    string localSha = File.Exists(localFull) ? GitBlobSha1(File.ReadAllBytes(localFull)) : null;
                    if (string.Equals(localSha, rf.Sha, StringComparison.OrdinalIgnoreCase)) continue; // up to date

                    toUpdate.Add(new PendingFile
                    {
                        Sha = rf.Sha,
                        LocalFull = localFull,
                        LocalRel = localRel,
                        IsNew = localSha == null
                    });
                }

                // Update the plugin itself last, so a self-reload doesn't cut the batch short.
                toUpdate = toUpdate
                    .OrderBy(pf => pf.LocalRel.EndsWith($"{Name}.cs", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ToList();

                if (toUpdate.Count == 0)
                {
                    isSyncing = false;
                    Log(requester, "All files are up to date.");
                    return;
                }

                Log(requester, $"{toUpdate.Count} file(s) out of date:");
                foreach (var pf in toUpdate)
                    Log(requester, $"  {(pf.IsNew ? "[new]    " : "[update] ")}{pf.LocalRel}");

                if (!config.AutoDownload)
                {
                    isSyncing = false;
                    Log(requester, "Auto-download is disabled (report only). Enable it in the config to download.");
                    return;
                }

                DownloadNext(toUpdate, 0, 0, requester);
            }, this, RequestMethod.GET, GitHubHeaders());
        }

        // Step 3: download each outdated file as a base64 blob (byte-exact), verify, then write.
        private void DownloadNext(List<PendingFile> files, int index, int updated, IPlayer requester)
        {
            if (index >= files.Count)
            {
                isSyncing = false;
                Log(requester, $"Sync complete: {updated}/{files.Count} file(s) updated.");
                if (updated > 0)
                    Log(requester, "Oxide will hot-reload any changed plugins automatically.");
                return;
            }

            var pf = files[index];
            string url = $"https://api.github.com/repos/{config.Owner}/{config.Repo}/git/blobs/{pf.Sha}";
            webrequest.Enqueue(url, null, (code, response) =>
            {
                if (code != 200 || string.IsNullOrEmpty(response))
                {
                    LogError(requester, $"  Failed to download {pf.LocalRel} (HTTP {code})");
                    DownloadNext(files, index + 1, updated, requester);
                    return;
                }

                try
                {
                    var json = JObject.Parse(response);
                    string encoding = (string)json["encoding"];
                    string content = (string)json["content"] ?? "";
                    byte[] bytes = encoding == "base64"
                        ? Convert.FromBase64String(content.Replace("\n", "").Replace("\r", ""))
                        : Encoding.UTF8.GetBytes(content);

                    // Integrity check: the bytes we got must hash back to the SHA we asked for.
                    if (!string.Equals(GitBlobSha1(bytes), pf.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        LogError(requester, $"  Checksum mismatch for {pf.LocalRel}; skipping to avoid corruption.");
                        DownloadNext(files, index + 1, updated, requester);
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(pf.LocalFull));
                    if (config.BackupBeforeOverwrite && File.Exists(pf.LocalFull))
                        BackupFile(pf.LocalFull, pf.LocalRel);

                    File.WriteAllBytes(pf.LocalFull, bytes);
                    Log(requester, $"  Updated {pf.LocalRel}");
                    DownloadNext(files, index + 1, updated + 1, requester);
                }
                catch (Exception ex)
                {
                    LogError(requester, $"  Error saving {pf.LocalRel}: {ex.Message}");
                    DownloadNext(files, index + 1, updated, requester);
                }
            }, this, RequestMethod.GET, GitHubHeaders());
        }

        #endregion

        #region Helpers

        private void BackupFile(string localFull, string localRel)
        {
            try
            {
                string dest = Path.Combine(Interface.Oxide.DataDirectory, "GitHubSync", "backups",
                    localRel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(localFull, dest, true);
            }
            catch (Exception ex)
            {
                PrintWarning($"Could not back up {localRel}: {ex.Message}");
            }
        }

        private Dictionary<string, string> GitHubHeaders()
        {
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "GitHubSync-Oxide-Plugin",
                ["Accept"] = "application/vnd.github+json"
            };
            if (!string.IsNullOrEmpty(config.Token))
                headers["Authorization"] = $"Bearer {config.Token}";
            return headers;
        }

        // Reproduces git's blob object id: sha1("blob <byteLength>\0" + contentBytes).
        private static string GitBlobSha1(byte[] content)
        {
            byte[] header = Encoding.UTF8.GetBytes($"blob {content.Length}\0");
            byte[] combined = new byte[header.Length + content.Length];
            Buffer.BlockCopy(header, 0, combined, 0, header.Length);
            Buffer.BlockCopy(content, 0, combined, header.Length, content.Length);
            using (var sha1 = SHA1.Create())
            {
                var sb = new StringBuilder(40);
                foreach (byte b in sha1.ComputeHash(combined)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static bool PathStartsWith(string path, string prefix)
        {
            prefix = prefix.Trim('/');
            return path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        }

        private static string CombineRel(string a, string b)
        {
            a = a.Trim('/'); b = b.Trim('/');
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "/" + b;
        }

        private bool MatchesExtension(string localRel) =>
            config.Extensions == null || config.Extensions.Count == 0 ||
            config.Extensions.Any(ext => localRel.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        private bool IsExcluded(string localRel) =>
            config.ExcludedPaths != null && config.ExcludedPaths.Any(ex => PathStartsWith(localRel, ex));

        private static string DescribeHttp(int code)
        {
            switch (code)
            {
                case 0:   return "No response - check the server's internet connection.";
                case 401: return "Unauthorized - check the GitHub token in the config.";
                case 403: return "Forbidden - likely the GitHub rate limit (60/hour without a token). Add a token.";
                case 404: return "Not found - check the owner, repo and branch in the config.";
                default:  return "";
            }
        }

        private void Log(IPlayer requester, string msg)
        {
            Puts(msg);
            if (requester != null && !requester.IsServer) requester.Reply(msg);
        }

        private void LogError(IPlayer requester, string msg)
        {
            PrintWarning(msg);
            if (requester != null && !requester.IsServer) requester.Reply(msg);
        }

        private void Reply(IPlayer requester, string msg)
        {
            if (requester != null) requester.Reply(msg); else Puts(msg);
        }

        #endregion
    }
}
