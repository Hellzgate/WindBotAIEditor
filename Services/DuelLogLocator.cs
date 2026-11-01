using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-01: duel-log 위치 해석 — 실행기는 ① **WindBot 폴더**(보기/복사 쉬움) ② `%LOCALAPPDATA%\WindBotAIEditor`
    /// 순으로 쓰고, 앱은 **둘 다** 읽는다(각자 오프셋으로 증분).
    /// </summary>
    public static class DuelLogLocator
    {
        private static string? _windBotRoot;

        /// <summary>프로젝트를 열 때 WindBot 폴더 루트(…\WindBot)를 알려준다.</summary>
        public static void SetWindBotRoot(string? root)
        {
            if (!string.IsNullOrWhiteSpace(root)) _windBotRoot = root;
        }

        /// <summary>읽을 로그 경로 후보 (존재 여부와 무관하게 반환 — 읽을 때 확인)</summary>
        public static List<string> Paths()
        {
            var list = new List<string>();

            var root = _windBotRoot;
            if (string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    var edopro = App.EdoproRoot;
                    if (!string.IsNullOrWhiteSpace(edopro)) root = Path.Combine(edopro, "WindBot");
                }
                catch { }
            }
            if (!string.IsNullOrWhiteSpace(root))
            {
                try { list.Add(Path.Combine(root, "duel-log.txt")); } catch { }
            }

            try
            {
                list.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WindBotAIEditor", "duel-log.txt"));
            }
            catch { }

            return list.Distinct().ToList();
        }

        /// <summary>기본(앱 전용) 로그 경로 — 옛 캐시 호환용</summary>
        public static string LocalPath
        {
            get
            {
                try
                {
                    return Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WindBotAIEditor", "duel-log.txt");
                }
                catch { return "duel-log.txt"; }
            }
        }
    }
}
