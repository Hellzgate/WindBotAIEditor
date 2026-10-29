using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindBotAIEditor.Services
{
    /// <summary>효과번호 후보 한 개 (콤보 표시용)</summary>
    public sealed class EffectDescOption
    {
        public long Value { get; set; }
        public string Label { get; set; } = "";
        public override string ToString() => Label;
    }

    /// <summary>
    /// B-45: 효과번호(ActivateDescription / desc) 후보 카탈로그.
    ///  · 정적: 카드 Lua 의 SetDescription(...) — CardScript.Descriptions 재사용
    ///  · 관측: duel-log.txt 의 "activate &lt;카드ID&gt; ... desc=&lt;N&gt;" (증분 스캔)
    ///  · [효과번호 갱신] 버튼 + 5분 자동 갱신 → 그리드 콤보가 바로 갱신됨
    /// </summary>
    public sealed class EffectDescCatalog
    {
        public static EffectDescCatalog Instance { get; } = new EffectDescCatalog();

        /// <summary>그리드 콤보의 ItemsSource (인스턴스 유지, 내용만 갱신)</summary>
        public ObservableCollection<EffectDescOption> Items { get; } = new ObservableCollection<EffectDescOption>();

        public string Status { get; private set; } = "아직 갱신 안 함";
        public int ObservedCount { get; private set; }

        private readonly Dictionary<long, string> _map = new Dictionary<long, string>();   // value → label
        private long _logOffset = -1;
        private bool _loaded;

        public static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindBotAIEditor");

        public static string LogPath => Path.Combine(DirPath, "duel-log.txt");
        private static string CachePath => Path.Combine(DirPath, "effectdesc-cache.json");

        private sealed class CacheDto
        {
            public long logOffset { get; set; } = -1;
            public List<CacheItem> items { get; set; } = new List<CacheItem>();
        }
        private sealed class CacheItem
        {
            public long value { get; set; }
            public string label { get; set; } = "";
        }

        /// <summary>캐시 로드 (앱 시작 시 1회)</summary>
        public void LoadCache()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath, Encoding.UTF8));
                if (dto == null) return;
                _logOffset = dto.logOffset;
                foreach (var it in dto.items) _map[it.value] = it.label;
                Rebuild();
                Status = "캐시 " + _map.Count + "개";
            }
            catch { }
        }

        /// <summary>
        /// 후보 갱신. cardIds = 현재 규칙에서 참조하는 카드 ID(정적 스캔 대상).
        /// </summary>
        public void Refresh(string? edoproRoot, IEnumerable<int> cardIds)
        {
            LoadCache();
            int before = _map.Count;

            // 1) 관측(duel-log) — 증분
            try
            {
                if (File.Exists(LogPath))
                {
                    var fi = new FileInfo(LogPath);
                    if (_logOffset < 0 || fi.Length < _logOffset) _logOffset = 0;
                    if (fi.Length > _logOffset)
                    {
                        using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        fs.Seek(_logOffset, SeekOrigin.Begin);
                        using var sr = new StreamReader(fs, Encoding.UTF8);
                        string text = sr.ReadToEnd();
                        _logOffset = fs.Position;
                        foreach (Match m in Regex.Matches(text, @"activate\s+(\d+)[^\r\n]*?desc=(-?\d+)"))
                        {
                            if (!long.TryParse(m.Groups[2].Value, out var v)) continue;
                            if (v < 0) continue;
                            int cardId = int.TryParse(m.Groups[1].Value, out var c) ? c : 0;
                            _map[v] = Describe(cardId, v) + "  [관측]";
                            ObservedCount++;
                        }
                    }
                }
            }
            catch { }

            // 2) 정적(Lua) — 규칙에서 참조하는 카드만
            if (!string.IsNullOrWhiteSpace(edoproRoot))
            {
                foreach (var id in cardIds.Distinct().Where(x => x > 0).Take(400))
                {
                    try
                    {
                        var lua = CardScript.Read(edoproRoot, id);
                        if (string.IsNullOrEmpty(lua)) continue;
                        foreach (var d in CardScript.Descriptions(lua, EffectCatalog.Instance))
                        {
                            long v = d.Dynamic ? (((long)id << 20) | (uint)(d.Value & 0xFFFFF)) : d.Value;
                            if (v < 0) continue;
                            if (!_map.ContainsKey(v)) _map[v] = Describe(id, v) + (d.Dynamic ? "  [동적]" : "");
                        }
                    }
                    catch { }
                }
            }

            Rebuild();
            SaveCache();
            Status = "후보 " + _map.Count + "개 (관측 " + ObservedCount + "건·새 " + (_map.Count - before) + ")";
        }

        /// <summary>desc 값을 사람이 읽을 수 있게: Stringid(code, idx) = (code&lt;&lt;20)|idx 해석</summary>
        public static string Describe(int cardId, long value)
        {
            // (code << 20) | idx 형태면 카드/인덱스로 분해
            long code = value >> 20;
            long idx = value & 0xFFFFF;
            if (code > 0 && code <= int.MaxValue && idx >= 0 && idx < 64)
            {
                int cid = (int)code;
                var name = CardDatabase.Instance.NameOf(cid);
                if (string.IsNullOrWhiteSpace(name)) name = "(cdb 없음)";
                return cid + " · #" + idx + " = " + value + " (" + name + ")";
            }
            return value.ToString();
        }

        private void Rebuild()
        {
            var ordered = _map.OrderBy(kv => kv.Key).ToList();
            Items.Clear();
            foreach (var kv in ordered)
                Items.Add(new EffectDescOption { Value = kv.Key, Label = kv.Value });
        }

        private void SaveCache()
        {
            try
            {
                Directory.CreateDirectory(DirPath);
                var dto = new CacheDto
                {
                    logOffset = _logOffset,
                    items = _map.OrderBy(kv => kv.Key).Select(kv => new CacheItem { value = kv.Key, label = kv.Value }).ToList()
                };
                File.WriteAllText(CachePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
