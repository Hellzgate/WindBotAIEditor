using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace WindBotAIEditor.Services
{
    /// <summary>카드의 출시 상태 (B-24).</summary>
    public enum ReleaseState
    {
        /// <summary>어느 cdb 에도 없다 (커스텀/삭제 카드)</summary>
        Unknown = 0,
        /// <summary>정규 cdb 에 있다 (변환 불필요)</summary>
        Official = 1,
        /// <summary>프리릴리즈 cdb 에만 있고, 같은 이름의 정규 카드가 아직 없다</summary>
        PreRelease = 2,
        /// <summary>프리릴리즈 cdb 에 있고, 같은 이름의 정규 카드가 있다 (= 변환 가능)</summary>
        PreReleaseConvertible = 3,
    }

    /// <summary>
    /// B-24: 프리릴리즈(pre) 카드 ↔ 정규(release) 카드 매핑.
    /// <para>
    /// EDOPro <c>repositories\**\*.cdb</c> 를 파일 이름으로 분류한다:
    /// <list type="bullet">
    /// <item>파일명에 <c>pre</c> 포함 &amp; <c>fix-errata</c> 아님 → <b>pre</b></item>
    /// <item><c>languages\</c> 하위 → <b>번역</b> (이름 보조용 — 정규/프리 판정에 쓰지 않는다)</item>
    /// <item>그 외 → <b>official</b> (cards.cdb, release-*.cdb, cards.delta.cdb …)</item>
    /// </list>
    /// 같은 이름(Trim + 대소문자 무시)이 official 쪽에 있으면 pre ID → 정규 ID 로 변환할 수 있다.
    /// 후보가 여럿이면 <b>8자리 ID(&lt;100,000,000) 우선</b>, 그 다음 낮은 ID.
    /// </para>
    /// <para>
    /// 9자리 ID(≥100,000,000)를 무조건 pre 로 보는 규칙은 정확도가 매우 낮아(실측 3.7%) 쓰지 않는다.
    /// 스캔은 <see cref="StartScan"/> 으로 백그라운드에서 돌리고, UI 는 <see cref="IsReady"/> 로 상태만 본다.
    /// </para>
    /// </summary>
    public sealed class PreReleaseCatalog
    {
        public static PreReleaseCatalog Instance { get; } = new PreReleaseCatalog();

        private enum Kind { Pre, Official, Translation }

        private sealed class CdbFile
        {
            public string Path = "";
            public Kind Kind;
            public long Ticks;
            public long Size;
        }

        private sealed class Entry
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int[] Official { get; set; } = Array.Empty<int>();
        }

        /// <summary>스캔 결과 한 벌 (불변 — 스캔이 끝나면 통째로 교체한다)</summary>
        private sealed class Snapshot
        {
            public string Repositories = "";
            public HashSet<int> PreOnly = new();
            public HashSet<int> Official = new();
            public Dictionary<int, string> Names = new();                  // pre ID → 카드 이름
            public Dictionary<int, IReadOnlyList<int>> Candidates = new();  // pre ID → 정규 후보 (자기 자신 제외)
            public string[] Fingerprint = Array.Empty<string>();
            public int PreFiles;
            public int OfficialFiles;
            public int TranslationFiles;
            public int PreRows;
            public int PreIds;
            public int Convertible;
            public bool FromCache;
            public long ScanMs;
        }

        // ---------- 캐시 파일 ----------

        private sealed class CacheFile
        {
            public int Version { get; set; }
            public string Edopro { get; set; } = "";
            public string[] Files { get; set; } = Array.Empty<string>();
            public int PreFiles { get; set; }
            public int OfficialFiles { get; set; }
            public int TranslationFiles { get; set; }
            public int PreRows { get; set; }
            public int[] OfficialIds { get; set; } = Array.Empty<int>();
            public List<Entry> Entries { get; set; } = new();
        }

        /// <summary>캐시 형식 버전 — 구조가 바뀌면 올린다.</summary>
        private const int CacheVersion = 1;

        private static readonly JsonSerializerOptions CacheJson = new JsonSerializerOptions
        {
            WriteIndented = false,
        };

        public static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindBotAIEditor", "prerelease-cache.json");

        // ---------- 상태 ----------

        private volatile Snapshot? _snap;
        private int _scanning;                       // 0/1 (Interlocked)
        private string _scanRoot = "";
        private readonly object _gate = new();

        /// <summary>스캔이 끝나면 호출된다 (백그라운드 스레드 — UI 는 Dispatcher 로 옮겨야 한다).</summary>
        public event Action? Ready;

        public bool IsReady => _snap != null;
        public bool IsScanning => Volatile.Read(ref _scanning) != 0;

        /// <summary>스캔한 repositories 폴더</summary>
        public string Repositories => _snap?.Repositories ?? "";

        public int PreFileCount => _snap?.PreFiles ?? 0;
        public int OfficialFileCount => _snap?.OfficialFiles ?? 0;
        public int TranslationFileCount => _snap?.TranslationFiles ?? 0;

        /// <summary>pre cdb 의 카드 행 수 (중복 포함 — 문서의 651)</summary>
        public int PreRowCount => _snap?.PreRows ?? 0;

        /// <summary>pre cdb 에만 있는 고유 ID 수</summary>
        public int PreCardCount => _snap?.PreIds ?? 0;

        /// <summary>정규 변환 가능한 ID 수 (문서의 130)</summary>
        public int ConvertibleCount => _snap?.Convertible ?? 0;

        public bool FromCache => _snap?.FromCache ?? false;
        public long ScanMs => _snap?.ScanMs ?? 0;

        /// <summary>진단용 한 줄 요약</summary>
        public string Stats => _snap == null
            ? "(분석 중)"
            : $"pre 파일 {PreFileCount} / official 파일 {OfficialFileCount} / 번역 파일 {TranslationFileCount}" +
              $" · pre 카드 {PreRowCount}행(고유 {PreCardCount}) · 변환 가능 {ConvertibleCount}" +
              (FromCache ? " (캐시)" : "") + " · " + ScanMs + "ms";

        // ---------- 스캔 ----------

        /// <summary>EDOPro 폴더(또는 WindBot 폴더)로 repositories 경로를 계산한다.</summary>
        public static string RepositoriesOf(string edoproOrWindBot)
        {
            if (string.IsNullOrWhiteSpace(edoproOrWindBot)) return "";
            var p = edoproOrWindBot.TrimEnd('\\', '/');
            var repo = Path.Combine(p, "repositories");
            if (Directory.Exists(repo)) return repo;
            // WindBot 폴더를 받은 경우
            var parent = Directory.GetParent(p)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var repo2 = Path.Combine(parent!, "repositories");
                if (Directory.Exists(repo2)) return repo2;
            }
            return repo;    // 없으면 그대로 (스캔이 0건으로 끝난다)
        }

        /// <summary>
        /// 백그라운드 스캔 시작. 이미 같은 폴더로 준비돼 있으면 아무것도 하지 않는다.
        /// <paramref name="force"/> = 캐시를 무시하고 다시 읽는다.
        /// </summary>
        public void StartScan(string edoproOrWindBot, bool force = false)
        {
            var repo = RepositoriesOf(edoproOrWindBot);
            var snap = _snap;
            if (!force && snap != null && string.Equals(snap.Repositories, repo, StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;   // 이미 스캔 중

            _scanRoot = repo;
            Task.Run(() =>
            {
                try
                {
                    var built = Build(repo, useCache: !force);
                    _snap = built;
                    Ready?.Invoke();
                }
                catch
                {
                    // 스캔 실패는 치명적이지 않다 — 배지만 "(분석 중)" 으로 남는다
                }
                finally
                {
                    Interlocked.Exchange(ref _scanning, 0);
                }
            });
        }

        /// <summary>스캔이 끝날 때까지 기다린다 (CLI 용 — 최대 <paramref name="timeoutMs"/>).</summary>
        public bool WaitScan(int timeoutMs = 30000)
        {
            var start = Environment.TickCount;
            while (_snap == null && IsScanning)
            {
                if (Environment.TickCount - start > timeoutMs) return false;
                Thread.Sleep(50);
            }
            return _snap != null;
        }

        /// <summary>동기 스캔 (CLI 용 — UI 스레드에서 부르지 말 것). 캐시를 쓴다.</summary>
        public bool ScanNow(string edoproOrWindBot, bool force = false)
        {
            var repo = RepositoriesOf(edoproOrWindBot);
            try
            {
                _snap = Build(repo, useCache: !force);
                return true;
            }
            catch { return false; }
        }

        private static Snapshot Build(string repositories, bool useCache)
        {
            var started = Environment.TickCount;
            var files = Enumerate(repositories);
            var fingerprint = files.Select(f => f.Path + "|" + f.Ticks + "|" + f.Size).ToArray();

            if (useCache)
            {
                var cached = LoadCache(repositories, fingerprint, started);
                if (cached != null) return cached;
            }

            var official = new HashSet<int>();
            var officialByName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var preNames = new Dictionary<int, string>();
            var transNames = new Dictionary<int, string>();
            int preRows = 0;

            foreach (var f in files)
            {
                try
                {
                    foreach (var row in ReadNames(f.Path))
                    {
                        switch (f.Kind)
                        {
                            case Kind.Official:
                                official.Add(row.Key);
                                if (row.Value.Length > 0)
                                {
                                    if (!officialByName.TryGetValue(row.Value, out var ids))
                                    {
                                        ids = new List<int>();
                                        officialByName[row.Value] = ids;
                                    }
                                    if (!ids.Contains(row.Key)) ids.Add(row.Key);
                                }
                                break;
                            case Kind.Pre:
                                preRows++;
                                if (!preNames.TryGetValue(row.Key, out var old) || old.Length == 0)
                                    preNames[row.Key] = row.Value;
                                break;
                            case Kind.Translation:
                                if (row.Value.Length > 0 && !transNames.ContainsKey(row.Key))
                                    transNames[row.Key] = row.Value;
                                break;
                        }
                    }
                }
                catch { /* 깨진/잠긴 cdb 는 건너뛴다 */ }
            }

            var preOnly = new HashSet<int>(preNames.Keys.Where(id => !official.Contains(id)));
            var candidates = new Dictionary<int, IReadOnlyList<int>>();
            foreach (var id in preOnly)
            {
                var name = preNames.TryGetValue(id, out var n) && n.Length > 0 ? n : "";
                if (name.Length == 0) transNames.TryGetValue(id, out name);   // 번역 이름 보조
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!officialByName.TryGetValue(name, out var ids)) continue;

                // 후보 여럿 → 8자리 ID(<100,000,000) 우선, 그 다음 낮은 ID
                var list = ids.Where(o => o != id).Distinct()
                              .OrderBy(o => o >= 100000000 ? 1 : 0)
                              .ThenBy(o => o)
                              .ToList();
                if (list.Count > 0) candidates[id] = list;
            }

            var snap = new Snapshot
            {
                Repositories = repositories,
                PreOnly = preOnly,
                Official = official,
                Names = preNames,
                Candidates = candidates,
                Fingerprint = fingerprint,
                PreFiles = files.Count(f => f.Kind == Kind.Pre),
                OfficialFiles = files.Count(f => f.Kind == Kind.Official),
                TranslationFiles = files.Count(f => f.Kind == Kind.Translation),
                PreRows = preRows,
                PreIds = preOnly.Count,
                Convertible = candidates.Count,
                FromCache = false,
                ScanMs = Environment.TickCount - started,
            };

            // 번역에서만 이름을 얻은 카드도 목록에 이름이 남도록 보정
            foreach (var id in snap.PreOnly)
                if ((!snap.Names.TryGetValue(id, out var nm) || nm.Length == 0) && transNames.TryGetValue(id, out var tn))
                    snap.Names[id] = tn;

            SaveCache(snap);
            return snap;
        }

        private static List<CdbFile> Enumerate(string repositories)
        {
            var result = new List<CdbFile>();
            if (string.IsNullOrWhiteSpace(repositories) || !Directory.Exists(repositories)) return result;
            string[] paths;
            try { paths = Directory.GetFiles(repositories, "*.cdb", SearchOption.AllDirectories); }
            catch { return result; }

            foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                Kind kind;
                try
                {
                    var rel = path.Substring(repositories.Length).TrimStart('\\', '/');
                    var segments = rel.Split('\\', '/');
                    var lower = Path.GetFileName(path).ToLowerInvariant();
                    // 삭제 목록(remove.cdb / cards-unofficial-removal.cdb)은 카드 정의가 아니라 "이 카드를 지운다" 표시다.
                    // official 로 세면 pre ID 가 '이미 정규에 있음' 으로 오판돼 실제 쌍이 사라진다(실측 126 → 98쌍,
                    // 예: 100273006 → 5130393 Millennium Seeker). 번역도 아니므로 인덱스에서 아예 뺀다.
                    if (lower.Contains("remove")) continue;
                    if (segments.Any(s => s.Equals("languages", StringComparison.OrdinalIgnoreCase)))
                        kind = Kind.Translation;
                    else if (lower.Contains("pre") && !lower.Contains("fix-errata"))
                        kind = Kind.Pre;
                    else
                        kind = Kind.Official;
                }
                catch { continue; }

                FileInfo fi;
                try { fi = new FileInfo(path); } catch { continue; }
                if (!fi.Exists) continue;
                result.Add(new CdbFile { Path = fi.FullName, Kind = kind, Ticks = fi.LastWriteTimeUtc.Ticks, Size = fi.Length });
            }
            return result;
        }

        /// <summary>cdb 의 (id, 이름) 을 읽는다 — 읽기 전용.</summary>
        private static IEnumerable<KeyValuePair<int, string>> ReadNames(string path)
        {
            var rows = new List<KeyValuePair<int, string>>();
            try
            {
                var cs = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString();
                using var conn = new SqliteConnection(cs);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, name FROM texts WHERE name IS NOT NULL AND name <> ''";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var id = r.GetInt32(0);
                    var name = r.IsDBNull(1) ? "" : (Convert.ToString(r.GetValue(1)) ?? "").Trim();
                    if (id <= 0) continue;
                    rows.Add(new KeyValuePair<int, string>(id, name));
                }
            }
            catch { /* 깨진 cdb */ }
            return rows;
        }

        // ---------- 캐시 ----------

        private static Snapshot? LoadCache(string repositories, string[] fingerprint, int started)
        {
            try
            {
                var path = CachePath;
                if (!File.Exists(path)) return null;
                var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), CacheJson);
                if (cache == null || cache.Version != CacheVersion) return null;
                if (!string.Equals(cache.Edopro, repositories, StringComparison.OrdinalIgnoreCase)) return null;
                if (cache.Files.Length != fingerprint.Length) return null;
                for (var i = 0; i < fingerprint.Length; i++)
                    if (!string.Equals(cache.Files[i], fingerprint[i], StringComparison.OrdinalIgnoreCase)) return null;

                var snap = new Snapshot
                {
                    Repositories = repositories,
                    Official = new HashSet<int>(cache.OfficialIds ?? Array.Empty<int>()),
                    Fingerprint = fingerprint,
                    PreFiles = cache.PreFiles,
                    OfficialFiles = cache.OfficialFiles,
                    TranslationFiles = cache.TranslationFiles,
                    PreRows = cache.PreRows,
                    FromCache = true,
                    ScanMs = Environment.TickCount - started,
                };
                foreach (var e in cache.Entries ?? new List<Entry>())
                {
                    if (e.Id <= 0) continue;
                    snap.PreOnly.Add(e.Id);
                    snap.Names[e.Id] = e.Name ?? "";
                    if (e.Official != null && e.Official.Length > 0)
                        snap.Candidates[e.Id] = e.Official;
                }
                snap.PreIds = snap.PreOnly.Count;
                snap.Convertible = snap.Candidates.Count;
                return snap;
            }
            catch { return null; }
        }

        private static void SaveCache(Snapshot snap)
        {
            try
            {
                var cache = new CacheFile
                {
                    Version = CacheVersion,
                    Edopro = snap.Repositories,
                    Files = snap.Fingerprint,
                    PreFiles = snap.PreFiles,
                    OfficialFiles = snap.OfficialFiles,
                    TranslationFiles = snap.TranslationFiles,
                    PreRows = snap.PreRows,
                    OfficialIds = snap.Official.OrderBy(x => x).ToArray(),
                    Entries = snap.PreOnly.OrderBy(x => x).Select(id => new Entry
                    {
                        Id = id,
                        Name = snap.Names.TryGetValue(id, out var n) ? n : "",
                        Official = snap.Candidates.TryGetValue(id, out var c) ? c.ToArray() : Array.Empty<int>(),
                    }).ToList(),
                };
                var path = CachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(cache, CacheJson), new System.Text.UTF8Encoding(false));
            }
            catch { /* 캐시 실패는 무시 (다음에 다시 스캔) */ }
        }

        /// <summary>캐시 파일을 지운다 (강제 재스캔용).</summary>
        public static void ClearCache()
        {
            try { if (File.Exists(CachePath)) File.Delete(CachePath); } catch { }
        }

        // ---------- 조회 API ----------

        /// <summary>이 ID 가 프리릴리즈에만 있는 카드인가 (정규 cdb 에도 있으면 false)</summary>
        public bool IsPreRelease(int id) => _snap != null && _snap.PreOnly.Contains(id);

        /// <summary>같은 이름의 정규 카드가 있는가 (= 정규로 넘어갔다)</summary>
        public bool IsReleased(int preId) => Candidates(preId).Count > 0;

        /// <summary>변환 결과 (없으면 0)</summary>
        public int OfficialId(int preId)
        {
            var c = Candidates(preId);
            return c.Count > 0 ? c[0] : 0;
        }

        /// <summary>같은 이름의 정규 카드 후보 (자기 자신 제외 · 8자리 우선)</summary>
        public IReadOnlyList<int> Candidates(int preId)
        {
            var s = _snap;
            if (s != null && s.Candidates.TryGetValue(preId, out var list)) return list;
            return Array.Empty<int>();
        }

        /// <summary>카드 이름 (pre cdb 기준 — 없으면 번역 cdb 보조)</summary>
        public string NameOf(int id)
        {
            var s = _snap;
            if (s != null && s.Names.TryGetValue(id, out var n)) return n;
            return "";
        }

        public ReleaseState StateOf(int id)
        {
            var s = _snap;
            if (s == null) return ReleaseState.Unknown;
            if (s.Candidates.ContainsKey(id)) return ReleaseState.PreReleaseConvertible;
            if (s.PreOnly.Contains(id)) return ReleaseState.PreRelease;
            if (s.Official.Contains(id)) return ReleaseState.Official;
            return ReleaseState.Unknown;
        }

        /// <summary>카드 상세 배지 문구 — 프리릴리즈 / 프리릴리즈 → 정규 N (변환 가능) / 정규</summary>
        public string Badge(int id)
        {
            var s = _snap;
            if (s == null) return "(분석 중)";
            if (s.Candidates.TryGetValue(id, out var c) && c.Count > 0)
                return "프리릴리즈 → 정규 " + c[0] + " (변환 가능)" + (c.Count > 1 ? " · 후보 " + c.Count + "개" : "");
            if (s.PreOnly.Contains(id)) return "프리릴리즈";
            if (s.Official.Contains(id)) return "정규";
            return "(DB에 없음)";
        }

        /// <summary>모든 변환 쌍 (pre → 정규 후보). 정렬: pre ID 오름차순.</summary>
        public IReadOnlyList<KeyValuePair<int, IReadOnlyList<int>>> Mappings()
        {
            var s = _snap;
            if (s == null) return Array.Empty<KeyValuePair<int, IReadOnlyList<int>>>();
            return s.Candidates.OrderBy(kv => kv.Key).ToList();
        }

        /// <summary>이름 기준 조회 (CLI 표시용)</summary>
        public string Describe(int id)
        {
            var state = StateOf(id);
            var name = NameOf(id);
            if (name.Length == 0) name = CardDatabase.Instance.NameOf(id);
            return state switch
            {
                ReleaseState.PreReleaseConvertible => "프리릴리즈 → 정규 " + OfficialId(id),
                ReleaseState.PreRelease => "프리릴리즈",
                ReleaseState.Official => "정규",
                _ => "DB에 없음",
            } + (name.Length > 0 ? "  (" + name + ")" : "");
        }
    }
}
