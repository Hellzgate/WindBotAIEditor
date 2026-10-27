using System;
using System.IO;
using System.Text.Json;

namespace WindBotAIEditor.Services
{
    /// <summary>%APPDATA%\WindBotAIEditor\settings.json 에 경로 기억.</summary>
    public class AppSettings
    {
        public string EdoproPath { get; set; } = "";

        /// <summary>다크 모드 사용 여부</summary>
        public bool DarkMode { get; set; }

        private static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindBotAIEditor");

        private static string FilePath => Path.Combine(Dir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>EDOPro 폴더인지 확인 (EDOPro.exe 또는 WindBot 하위 폴더)</summary>
        public static bool LooksLikeEdopro(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            if (File.Exists(Path.Combine(path, "EDOPro.exe"))) return true;
            if (Directory.Exists(Path.Combine(path, "WindBot"))) return true;
            return false;
        }
    }
}

