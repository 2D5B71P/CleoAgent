using System;

namespace CleoAgent.Core.Env
{
    // Loads KEY=VALUE pairs from %APPDATA%\CleoAgent\env into the process
    // environment at startup (via System.Environment). The file is the agent's
    // own plaintext config store, outside the repo, so secrets never get
    // committed. Process-level variables always win: the file only supplies
    // values that are not already set.
    internal sealed class EnvLoader
    {
        private static readonly string s_DirectoryName = "CleoAgent";
        private static readonly string s_FileName      = "env";

        // Names of the variables defined in the env file (or already set in the
        // process environment under the same name). Values are deliberately NOT
        // retained here — callers that need names must never see the secrets.
        private static readonly List<string> s_RegisteredKeys = new();

        // Returns only the key NAMES of the configured variables, never values.
        // Used by get_environment_info so it can report what is configured
        // without ever shipping credential values to the model.
        public static IReadOnlyList<string> RegisteredKeys()
        {
            lock (s_RegisteredKeys)
            {
                return s_RegisteredKeys.ToArray();
            }
        }

        public static string GetEnvPath()
        {
            string? appData = System.Environment.GetEnvironmentVariable("APPDATA");

            string directory =
                appData is { Length: > 0 }
                    ? string.Concat(appData, "\\", s_DirectoryName)
                    : string.Concat(
                        System.Environment.GetEnvironmentVariable("USERPROFILE") is { Length: > 0 }
                            ? System.Environment.GetEnvironmentVariable("USERPROFILE")
                            : ".",
                        "\\AppData\\Roaming\\", s_DirectoryName);

            return string.Concat(directory, "\\", s_FileName);
        }

        public static void Load()
        {
            string path = GetEnvPath();

            if (!File.Exists(path))
            {
                Console.Error.WriteLine(
                    $"[env] {path} not found. Create it with KEY=VALUE lines to supply settings.");
                return;
            }

            int loaded = 0;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;

                string key   = Unquote(line.Substring(0, eq).Trim());
                string value = Unquote(line.Substring(eq + 1).Trim());

                if (key.Length == 0)
                    continue;

                // Record the name regardless of whether the process environment
                // already supplied the value — the variable is still "registered".
                lock (s_RegisteredKeys)
                {
                    if (!s_RegisteredKeys.Contains(key))
                        s_RegisteredKeys.Add(key);
                }

                // A variable already present in the process environment wins.
                if (System.Environment.GetEnvironmentVariable(key) is { Length: > 0 })
                    continue;

                System.Environment.SetEnvironmentVariable(key, value);
                loaded++;
            }

            if (loaded > 0)
            {
                Console.Error.WriteLine($"[env] loaded {loaded} variable(s) from {path}.");
            }
        }

        private static string Unquote(string value)
        {
            if (value.Length >= 2 &&
                ((value.StartsWith("\"") && value.EndsWith("\"")) ||
                 (value.StartsWith("'")  && value.EndsWith("'"))))
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }
    }
}