using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-01: 실행기가 남긴 <c>destruct-fail</c> 로그를 모아 **파괴 내성 후보**로 보여준다.
    /// <para>실행기는 `OnSelectCard(hint=Destroy)` 로 고른 카드가 **체인 종료 후에도 필드에 있으면**
    /// `[AI-EDITOR] destruct-fail card=&lt;ID&gt; … name=&lt;이름&gt;` 을 duel-log 에 남긴다.</para>
    /// <para>여기서는 그것을 세어 후보 목록으로 만들 뿐, **자동 등록은 하지 않는다**(파괴 무효로 실패한 오탐이 있을 수 있다).</para>
    /// </summary>
    public sealed class DestructFailCatalog
    {
        public sealed class Candidate : INotifyPropertyChanged
        {
            public int CardId { get; set; }
            public string Name { get; set; } = "";
            public int Count { get; set; }

            public string Label
            {
                get
                {
                    var nm = string.IsNullOrWhiteSpace(Name) ? "" : " " + Name;
                    return CardId + nm + " (" + Count + "회)";
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }

        public static DestructFailCatalog Instance { get; } = new DestructFailCatalog();

        /// <summary>후보(무시 목록·이미 등록된 카드는 제외)</summary>
        public ObservableCollection<Candidate> Items { get; } = new ObservableCollection<Candidate>();

        public string Status { get; private set; } = "아직 없음";

        private readonly Dictionary<int, int> _counts = new Dictionary<int, int>();
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private readonly HashSet<int> _ignored = new HashSet<int>();
        // 2026-11-01: 경로별 오프셋 (WindBot 폴더 / %LOCALAPPDATA% 둘 다 읽는다)
        private readonly Dictionary<string, long> _offsets = new Dictionary<string, long>();
        private bool _loaded;

        public static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindBotAIEditor");

        public static string LogPath => Path.Combine(DirPath, "duel-log.txt");
        private static string CachePath => Path.Combine(DirPath, "destruct-candidates.json");

        private sealed class CacheDto
        {
            public long offset { get; set; } = -1;              // 옛 형식 — 읽기만 호환
            public Dictionary<string, long> offsets { get; set; } = new Dictionary<string, long>();
            public Dictionary<int, int> counts { get; set; } = new Dictionary<int, int>();
            public Dictionary<int, string> names { get; set; } = new Dictionary<int, string>();
            public List<int> ignored { get; set; } = new List<int>();
        }

        private void LoadCache()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath, Encoding.UTF8));
                if (dto == null) return;
                if (dto.offsets != null && dto.offsets.Count > 0)
                {
                    foreach (var kv in dto.offsets) _offsets[kv.Key] = kv.Value;
                }
                else if (dto.offset >= 0)
                {
                    _offsets[DuelLogLocator.LocalPath] = dto.offset;
                }
                foreach (var kv in dto.counts ?? new Dictionary<int, int>()) _counts[kv.Key] = kv.Value;
                foreach (var kv in dto.names ?? new Dictionary<int, string>()) _names[kv.Key] = kv.Value ?? "";
                foreach (var id in dto.ignored ?? new List<int>()) _ignored.Add(id);
            }
            catch { }
        }

        private void SaveCache()
        {
            try
            {
                var dto = new CacheDto { offset = -1, offsets = new Dictionary<string, long>(_offsets) };
                dto.counts = new Dictionary<int, int>(_counts);
                dto.names = new Dictionary<int, string>(_names);
                dto.ignored = _ignored.ToList();
                Directory.CreateDirectory(DirPath);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>duel-log 에서 <c>destruct-fail card=&lt;ID&gt;</c> 를 뽑는다 (테스트용 공개).</summary>
        public static List<int> ParseFailIds(string? text)
        {
            var list = new List<int>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (Match m in Regex.Matches(text, @"destruct-fail\s+card=(\d+)"))
            {
                if (!int.TryParse(m.Groups[1].Value, out var id) || id <= 0) continue;
                if (!list.Contains(id)) list.Add(id);
            }
            return list;
        }

        private void Ingest(string text)
        {
            foreach (Match m in Regex.Matches(text, @"destruct-fail\s+card=(\d+)([^\r\n]*)"))
            {
                if (!int.TryParse(m.Groups[1].Value, out var id) || id <= 0) continue;
                _counts.TryGetValue(id, out var n);
                _counts[id] = n + 1;
                var nm = Regex.Match(m.Groups[2].Value ?? "", @"name=([^\r\n]*)");
                if (nm.Success)
                {
                    var name = nm.Groups[1].Value.Trim();
                    if (name.Length > 0) _names[id] = name;
                }
            }
        }

        /// <summary>로그 증분 읽기 + 후보 재구성. listed = 이미 [파괴 내성 카드] 에 들어 있는 카드.</summary>
        public void Refresh(IEnumerable<int>? listed)
        {
            LoadCache();
            foreach (var logPath in DuelLogLocator.Paths())
            {
                try
                {
                    if (!File.Exists(logPath)) continue;
                    _offsets.TryGetValue(logPath, out var off);
                    if (off < 0) off = 0;
                    var fi = new FileInfo(logPath);
                    if (fi.Length < off) off = 0;            // 회전/삭제 → 처음부터
                    if (fi.Length <= off) { _offsets[logPath] = off; continue; }
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(off, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    var text = sr.ReadToEnd();
                    _offsets[logPath] = fs.Position;
                    Ingest(text);
                }
                catch { }
            }

            Rebuild(listed);
            SaveCache();
        }

        private void Rebuild(IEnumerable<int>? listed)
        {
            var keep = listed == null ? new HashSet<int>() : new HashSet<int>(listed);
            Items.Clear();
            foreach (var kv in _counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
            {
                if (kv.Value <= 0) continue;
                if (_ignored.Contains(kv.Key)) continue;
                if (keep.Contains(kv.Key)) continue;
                _names.TryGetValue(kv.Key, out var nm);
                Items.Add(new Candidate { CardId = kv.Key, Name = nm ?? "", Count = kv.Value });
            }
            Status = Items.Count == 0 ? "관찰된 후보 없음" : "관찰된 후보 " + Items.Count + "장";
        }

        /// <summary>현재 후보를 모두 '무시' 로 옮긴다(다시 보지 않음).</summary>
        public void IgnoreAll(IEnumerable<int>? listed)
        {
            foreach (var it in Items) _ignored.Add(it.CardId);
            Rebuild(listed);
            SaveCache();
        }

        /// <summary>관찰 기록을 모두 지운다(캐시 포함).</summary>
        public void ClearAll(IEnumerable<int>? listed)
        {
            _counts.Clear();
            _names.Clear();
            Rebuild(listed);
            SaveCache();
        }
    }
}
