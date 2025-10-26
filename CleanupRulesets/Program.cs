using System.Globalization;
using System.Text.RegularExpressions;
using Realms;
using Realms.Exceptions;

namespace CleanupRulesets
{
    internal class Program
    {
        private const string realmFileName = "client.realm";
        private const string settingFileName = "storage.ini";
        static int Main(string[] args)
        {
            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h"))
            {
                printUsage();
                return 0;
            }

            string? pathArgument = args.FirstOrDefault(a => !a.StartsWith('-'));
            var candidatePaths = enumerateCandidateRealmPaths(pathArgument).Select(normalizeRealmPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? realmPath = candidatePaths.FirstOrDefault(File.Exists);

            if (realmPath == null)
            {
                Console.Error.WriteLine("Could not locate osu! realm database. Checked the following locations:");
                foreach (var candidate in candidatePaths)
                    Console.Error.WriteLine($"  - {candidate}");

                Console.Error.WriteLine("Pass the path to the database (or its directory) as the first argument if it lives elsewhere.");
                return 1;
            }

            var config = buildConfiguration(realmPath);

            using var realm = openRealm(config);
            List<RulesetInfo> rulesets = [.. realm.All<RulesetInfo>().Where(r => r.OnlineID < 0 || r.OnlineID > 3)  // ignore offical
                                              .OrderBy(r => r.OnlineID)
                                              .ThenBy(r => r.ShortName, StringComparer.Ordinal)];

            if (rulesets.Count == 0)
            {
                Console.WriteLine("No rulesets found in the database.");
                return 0;
            }

            Console.WriteLine($"Loaded {rulesets.Count} ruleset(s) from '{realmPath}'.");
            Console.WriteLine();

            for (int i = 0; i < rulesets.Count; i++)
            {
                RulesetInfo ruleset = rulesets[i];
                Console.WriteLine($"[{i}] {ruleset.ShortName,-12} | {ruleset.Name} | OnlineID={ruleset.OnlineID} ");
                if (!string.IsNullOrWhiteSpace(ruleset.InstantiationInfo))
                    Console.WriteLine($"      Instantiation: {ruleset.InstantiationInfo}");
            }

            Console.WriteLine();
            Console.WriteLine("Enter the indices (space/comma separated) of rulesets to delete, or press Enter to keep all:");
            string? input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("No rulesets selected. Exiting.");
                return 0;
            }

            if (string.Equals(input, "all", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("You are about to delete ALL rulesets. Type 'yes' to confirm.");
                string? confirmAll = Console.ReadLine();
                if (!string.Equals(confirmAll, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Deletion cancelled.");
                    return 0;
                }

                deleteRulesets(realm, rulesets);
                Console.WriteLine($"Deleted {rulesets.Count} ruleset(s).");
                return 0;
            }

            if (!tryParseSelection(input, rulesets.Count, out var indexes))
            {
                Console.Error.WriteLine("Could not parse selection. No changes were made.");
                return 1;
            }

            if (indexes.Count == 0)
            {
                Console.WriteLine("No valid indices provided. Exiting.");
                return 0;
            }

            Console.WriteLine($"You selected {indexes.Count} ruleset(s) for deletion: {string.Join(", ", indexes.OrderBy(i => i))}.");
            Console.WriteLine("Type 'yes' to confirm deletion, or anything else to cancel:");
            string? confirmation = Console.ReadLine();

            if (!string.Equals(confirmation, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Deletion cancelled.");
                return 0;
            }

            var targets = indexes.Select(i => rulesets[i]).ToList();
            deleteRulesets(realm, targets);
            Console.WriteLine($"Deleted {targets.Count} ruleset(s).");
            return 0;
        }

        private static void deleteRulesets(Realm realm, IReadOnlyCollection<RulesetInfo> targets)
        {
            realm.Write(() =>
            {
                foreach (var ruleset in targets)
                    realm.Remove(ruleset);
            });
        }

        private static bool tryParseSelection(string input, int maxCount, out HashSet<int> indexes)
        {
            indexes = new HashSet<int>();

            string[] separators = { ",", " ", "\t", ";" };
            string[] tokens = input.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            foreach (string token in tokens)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                    return false;

                if (index < 0 || index >= maxCount)
                    return false;

                indexes.Add(index);
            }

            return true;
        }

        private static Realm openRealm(RealmConfiguration configuration)
        {
            try
            {
                return Realm.GetInstance(configuration);
            }
            catch (RealmException ex) when (tryExtractSchemaVersion(ex, out ulong requiredVersion))
            {
                configuration.SchemaVersion = requiredVersion;
                return Realm.GetInstance(configuration);
            }
        }

        private static RealmConfiguration buildConfiguration(string realmPath)
        {
            string fallbackPipePath = ensureFallbackPipePath();

            return new RealmConfiguration(realmPath)
            {
                Schema = new[] { typeof(RulesetInfo) },
                FallbackPipePath = fallbackPipePath
            };
        }

        private static string ensureFallbackPipePath()
        {
            string path = Path.Combine(Path.GetTempPath(), "lazer");

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return path;
        }

        private static bool tryExtractSchemaVersion(Exception exception, out ulong schemaVersion)
        {
            schemaVersion = 0;

            Match match = Regex.Match(exception.Message, @"last set version (?<version>\d+)");
            if (!match.Success)
                return false;

            string value = match.Groups["version"].Value;
            return ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out schemaVersion);
        }

        private static IEnumerable<string> enumerateCandidateRealmPaths(string? pathArgument)
        {
            if (!string.IsNullOrWhiteSpace(pathArgument))
                yield return pathArgument;

            string? envOverride = Environment.GetEnvironmentVariable("OSU_LAZER_PATH");
            if (!string.IsNullOrWhiteSpace(envOverride))
                yield return envOverride;

            yield return getDefaultRealmPath();
        }

        private static string normalizeRealmPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return Path.GetFullPath(getDefaultRealmPath());

            string trimmed = rawPath.Trim();
            string expanded = expandHome(Environment.ExpandEnvironmentVariables(trimmed));

            if (expanded.EndsWith(Path.DirectorySeparatorChar) || expanded.EndsWith(Path.AltDirectorySeparatorChar))
                expanded = expanded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (Directory.Exists(expanded) || (!File.Exists(expanded) && string.IsNullOrEmpty(Path.GetExtension(expanded))))
                expanded = Path.Combine(expanded, realmFileName);

            return Path.GetFullPath(expanded);
        }

        private static string expandHome(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] != '~')
                return path;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
                return path.TrimStart('~');

            if (path.Length == 1)
                return home;

            char separator = path[1];
            if (separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar || separator == '/')
                return Path.Combine(home, path.Substring(2));

            return path;
        }

        private static string getOsuStoragePath()
        {
            string basePath = Environment.CurrentDirectory;
            if (OperatingSystem.IsWindows())
                basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu");

            if (OperatingSystem.IsMacOS())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
                basePath = Path.Combine(home, "Library", "Application Support", "osu");
            }

            if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                basePath = Path.Combine(home, ".local", "share", "osu");
            }

            if (File.Exists(Path.Combine(basePath, settingFileName)))
            {
                using var streamReader = new StreamReader(Path.Combine(basePath, settingFileName));
                string? line;
                while ((line = streamReader.ReadLine()) != null)
                {
                    if (line.StartsWith("FullPath =", StringComparison.OrdinalIgnoreCase))
                    {
                        string newPath = line["FullPath =".Length..].Trim();
                        if (!string.IsNullOrEmpty(newPath))
                            return newPath;
                    }
                }
            }
            return basePath;
        }

        private static string getDefaultRealmPath()
        {
            return Path.Combine(getOsuStoragePath(), realmFileName);
        }

        private static void printUsage()
        {
            Console.WriteLine("Usage: CleanupRulesets [path/to/client.realm]");
            Console.WriteLine("If no path is provided, the tool attempts to locate the osu! client.realm using the default install locations.");
            Console.WriteLine("Once loaded, all rulesets in the database are listed, and you can choose which ones to delete by entering their indices.");
            Console.WriteLine("Type 'all' to remove every ruleset. Confirm deletions by typing 'yes' when prompted.");
            Console.WriteLine("Environment variables: set OSU_LAZER_PATH or OSU_DATA_PATH to point at your osu! data folder or realm file.");
        }
    }
}