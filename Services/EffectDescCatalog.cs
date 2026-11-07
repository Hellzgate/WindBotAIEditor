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
    public sealed class EffectDescOption : System.ComponentModel.INotifyPropertyChanged
    {
        public long Value { get; set; }

        private string _label = "";
        /// <summary>2026-11-01: 라벨을 제자리에서 갱신해도 콤보 표시가 바뀌도록 알림 구현</summary>
        public string Label
        {
            get => _label;
            set
            {
                if (_label == value) return;
                _label = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Label)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
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
        // 2026-11-01: 로그가 2곳(WindBot 폴더 / %LOCALAPPDATA%)에 있을 수 있어 **경로별** 오프셋을 둔다
        private readonly Dictionary<string, long> _logOffsets = new Dictionary<string, long>();
        private bool _loaded;

        public static string DirPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindBotAIEditor");

        public static string LogPath => Path.Combine(DirPath, "duel-log.txt");
        private static string CachePath => Path.Combine(DirPath, "effectdesc-cache.json");

        /// <summary>2026-11-01: 라벨 포맷 버전 — 올리면 옛 캐시 라벨을 버리고 새로 만든다.</summary>
        private const int LabelVersion = 2;

        private sealed class CacheDto
        {
            public int version { get; set; }
            public long logOffset { get; set; } = -1;          // 옛 형식(단일 경로) — 읽기만 호환
            public Dictionary<string, long> offsets { get; set; } = new Dictionary<string, long>();
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
                if (!File.Exists(CachePath))
                {
                    _logOffsets.Clear();   // 2026-11-01: 캐시가 없으면 로그를 처음부터 다시 훑어 후보를 채운다
                    return;
                }
                var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(CachePath, Encoding.UTF8));
                if (dto == null) return;
                // 2026-11-01: 경로별 오프셋 (옛 캐시는 단일 logOffset → LOCALAPPDATA 경로로 이관)
                if (dto.offsets != null && dto.offsets.Count > 0)
                {
                    foreach (var kv in dto.offsets) _logOffsets[kv.Key] = kv.Value;
                }
                else if (dto.logOffset >= 0)
                {
                    _logOffsets[DuelLogLocator.LocalPath] = dto.logOffset;
                }
                if (dto.version != LabelVersion)
                {
                    // 라벨 형식이 바뀐 캐시 — offset 만 살리고 라벨은 버린다(다음 Refresh 에서 새 형식으로 생성)
                    _map.Clear();
                    Items.Clear();      // 2026-11-01: 옛 라벨이 남지 않게 목록도 비운다(콤보 재구성)
                    _logOffsets.Clear();   // 로그를 처음부터 다시 훑어 후보를 채운다
                    Rebuild();
                    Status = "캐시 라벨이 구버전 → 새로 생성 예정";
                    return;
                }
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

            // 1) 관측(duel-log) — 증분. 경로가 2개일 수 있다(WindBot 폴더 / %LOCALAPPDATA%)
            foreach (var logPath in DuelLogLocator.Paths())
            {
                try
                {
                    if (!File.Exists(logPath)) continue;
                    _logOffsets.TryGetValue(logPath, out var off);
                    if (off < 0) off = 0;
                    var fi = new FileInfo(logPath);
                    if (fi.Length < off) off = 0;                 // 회전/삭제 → 처음부터
                    if (fi.Length <= off) { _logOffsets[logPath] = off; continue; }
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(off, SeekOrigin.Begin);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    string text = sr.ReadToEnd();
                    _logOffsets[logPath] = fs.Position;
                    foreach (Match m in Regex.Matches(text, @"activate\s+(\d+)[^\r\n]*?desc=(-?\d+)"))
                    {
                        if (!long.TryParse(m.Groups[2].Value, out var v)) continue;
                        if (v < 0) continue;
                        int cardId = int.TryParse(m.Groups[1].Value, out var c) ? c : 0;
                        _map[v] = Describe(cardId, v) + "  [관측]";
                        ObservedCount++;
                    }
                }
                catch { }
            }

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
                // 2026-11-01: 효과별 설명 문자열(texts.strN)을 붙여 사람이 읽을 수 있게 한다
                var effectText = CardDatabase.Instance.EffectText(cid, (int)idx);
                if (!string.IsNullOrWhiteSpace(effectText))
                {
                    var t = effectText.Replace("\r", " ").Replace("\n", " ").Trim();
                    if (t.Length > 60) t = t.Substring(0, 60) + "…";
                    return "#" + idx + " · " + t + "  [" + cid + " " + name + " / " + value + "]";
                }
                return cid + " · #" + idx + " = " + value + " (" + name + ")";
            }
            return value.ToString();
        }

        /// <summary>
        /// 2026-11-01: 규칙에 이미 들어 있는 효과번호가 목록에 없으면 추가한다
        /// (콤보에서 선택값이 안 보이는 것을 방지). 값이 −1 이면 무시.
        /// </summary>
        public void EnsureValues(IEnumerable<long> values)
        {
            var added = false;
            foreach (var v in values)
            {
                if (v < 0) continue;
                if (_map.ContainsKey(v)) continue;
                _map[v] = Describe(0, v);
                added = true;
            }
            if (added) Rebuild();
        }

        /// <summary>
        /// 2026-11-01: `옵션desc` 콤보 후보 — whenCard 카드의 cdb `texts.strN`(= `aux.Stringid` 선택지 문자열).
        /// 첫 항목은 항상 `(무관) = 0`. 규칙에 이미 들어 있는 값이 목록에 없으면 뒤에 덧붙인다.
        /// </summary>
        public static List<EffectDescOption> BuildChoices(int cardId, long current)
        {
            var list = new List<EffectDescOption>();
            list.Add(new EffectDescOption { Value = 0, Label = "(무관 — 모든 목록에 적용)" });

            if (cardId > 0 && CardDatabase.Instance.IsOpen)
            {
                for (var n = 0; n < 16; n++)
                {
                    var text = CardDatabase.Instance.EffectText(cardId, n);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var v = ((long)cardId << 20) | (long)n;
                    list.Add(new EffectDescOption { Value = v, Label = Describe(cardId, v) });
                }
            }

            if (current > 0)
            {
                var hit = false;
                foreach (var it in list) if (it.Value == current) hit = true;
                if (!hit) list.Add(new EffectDescOption { Value = current, Label = Describe(cardId, current) + "  [규칙 값]" });
            }
            return list;
        }

        private void Rebuild()
        {
            // 2026-11-01: 목록을 **지우지 않고** 갱신한다 — Clear() 하면 콤보의 선택이 풀려 칸이 빈칸으로 보인다.
            if (Items.Count == 0 || Items[0].Value != -1)
                Items.Insert(0, new EffectDescOption { Value = -1, Label = "—" });   // 무관(효과번호 없음) — 표에서 반복되는 긴 문구를 줄인다

            foreach (var kv in _map.OrderBy(k => k.Key))
            {
                var hit = false;
                foreach (var it in Items)
                {
                    if (it.Value != kv.Key) continue;
                    hit = true;
                    if (it.Label != kv.Value) it.Label = kv.Value;
                    break;
                }
                if (!hit) Items.Add(new EffectDescOption { Value = kv.Key, Label = kv.Value });
            }
        }

        /// <summary>
        /// 2026-11-01: 라벨을 비우고 다시 만들 준비 — 카드 DB 가 열린 뒤(효과 텍스트를 읽을 수 있을 때) 호출한다.
        /// </summary>
        public void ResetLabels()
        {
            try
            {
                _map.Clear();
                Items.Clear();      // 2026-11-01: 옛 라벨 목록을 비우고 새로 만든다(다음 Refresh/Rebuild)
                _logOffsets.Clear();   // 로그를 처음부터 다시 훑어 관측 후보를 되살린다
                Rebuild();
            }
            catch { }
        }

        private void SaveCache()
        {
            // 2026-11-01: 카드 DB 가 닫혀 있으면 효과 텍스트(strN)를 못 읽어 라벨이 빈약해진다 → 저장하지 않는다
            if (!CardDatabase.Instance.IsOpen) return;
            if (_map.Count == 0) return;    // 빈 캐시로 기존 후보를 덮어쓰지 않는다
            try
            {
                Directory.CreateDirectory(DirPath);
                var dto = new CacheDto
                {
                    version = LabelVersion,
                    logOffset = -1,
                    offsets = new Dictionary<string, long>(_logOffsets),
                    items = _map.OrderBy(kv => kv.Key).Select(kv => new CacheItem { value = kv.Key, label = kv.Value }).ToList()
                };
                File.WriteAllText(CachePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
