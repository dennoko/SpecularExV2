#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using lilToon;

namespace Dennokoworks.SpecularExV2
{
    // UI strings from Editor/Language/<lang>.json, following lilToon's language setting.
    // Files are located relative to this script (not Resources/), so they never collide with other
    // extensions' Resources/Language files and are never included in player builds.
    internal static class SpecularExLanguage
    {
        static Dictionary<string, string> _table;
        static string _loadedLang;
        static string _languageDir;

        static string LanguageDir()
        {
            if (_languageDir != null) return _languageDir;
            foreach (var guid in AssetDatabase.FindAssets("SpecularExLanguage t:MonoScript"))
            {
                string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(scriptPath) != "SpecularExLanguage") continue;
                _languageDir = Path.Combine(Path.GetDirectoryName(scriptPath), "Language").Replace('\\', '/');
                return _languageDir;
            }
            _languageDir = "Assets/dennokoworks/SpecularExV2/Editor/Language";
            return _languageDir;
        }

        static TextAsset Load(string lang)
            => AssetDatabase.LoadAssetAtPath<TextAsset>(LanguageDir() + "/" + lang + ".json");

        static void Refresh()
        {
            string lang = lilLanguageManager.langSet != null ? lilLanguageManager.langSet.languageName : "en-US";
            if (_table != null && _loadedLang == lang) return;

            _loadedLang = lang;
            var asset = Load(lang);
            if (!asset) asset = Load("en-US");

            _table = new Dictionary<string, string>();
            if (!asset) return;

            foreach (Match m in Regex.Matches(asset.text, "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\""))
                _table[m.Groups[1].Value] = m.Groups[2].Value;
        }

        public static string Get(string key)
        {
            Refresh();
            return _table != null && _table.TryGetValue(key, out var v) ? v : key;
        }

        public static string Format(string key, params object[] args)
        {
            var pattern = Get(key);
            try { return string.Format(pattern, args); }
            catch { return pattern; }
        }
    }
}
#endif
