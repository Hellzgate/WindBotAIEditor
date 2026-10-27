using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// EDOPro strings.conf 의 !system 항목(효과 설명 번호 등)을 읽는다.
    /// 예: "!system 1102 덱 파괴"  →  1102 = 덱 파괴
    /// 카드 Lua 의 SetDescription(1102) 같은 고정 효과번호를 해석할 때 사용.
    /// </summary>
    public sealed class EffectCatalog
    {
        private readonly SortedDictionary<int, string> _map = new();
        public string? SourcePath { get; private set; }
        public bool Loaded => _map.Count > 0;

        public static EffectCatalog Instance { get; } = new EffectCatalog();

        public bool Load(string edoproRoot)
        {
            _map.Clear();
            SourcePath = null;

            foreach (var path in Candidates(edoproRoot))
            {
                try
                {
                    var count = 0;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (!line.StartsWith("!system", StringComparison.OrdinalIgnoreCase)) continue;
                        var parts = line.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 3) continue;
                        if (!int.TryParse(parts[1], out var id)) continue;
                        _map[id] = parts[2].Trim();
                        count++;
                    }
                    if (count > 0)
                    {
                        SourcePath = path;
                        return true;
                    }
                }
                catch { /* 다음 후보 */ }
            }
            return false;
        }

        private static IEnumerable<string> Candidates(string edoproRoot)
        {
            // 한국어 우선
            var list = new List<string>();
            void AddDir(string dir)
            {
                if (!Directory.Exists(dir)) return;
                list.AddRange(Directory.GetFiles(dir, "strings.conf", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.Contains("한국어") || f.Contains("Korean"))
                    .ThenBy(f => f));
            }

            AddDir(Path.Combine(edoproRoot, "languages"));
            AddDir(Path.Combine(edoproRoot, "repositories", "languages"));
            return list.OrderByDescending(p => p.Contains("한국어") || p.Contains("Korean"));
        }

        public string TextOf(int id) => _map.TryGetValue(id, out var s) ? s : "";

        /// <summary>효과 설명 번호대(1100~1200) 목록</summary>
        public List<KeyValuePair<int, string>> EffectDescriptions(int from = 1100, int to = 1299) =>
            _map.Where(kv => kv.Key >= from && kv.Key <= to).ToList();

        public List<KeyValuePair<int, string>> Search(string keyword, int limit = 200)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return _map.Take(limit).ToList();
            return _map.Where(kv => kv.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                                    || kv.Key.ToString().Contains(keyword))
                       .Take(limit).ToList();
        }
    }
}
