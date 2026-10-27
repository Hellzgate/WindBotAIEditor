using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// cards.cdb (SQLite) 읽기 — 여러 DB 를 동시에 참조한다.
    /// EDOPro 표준 스키마: texts(id, name, desc), datas(id, ot, alias, setcode, type, atk, def, level, race, attribute, category)
    /// </summary>
    public sealed class CardDatabase : IDisposable
    {
        private readonly List<SqliteConnection> _conns = new();
        private readonly Dictionary<int, string> _nameCache = new();

        /// <summary>B-12: 카드 ID → 같은 이름 ID 목록 (지연 생성 · cdb 재로드 시 무효화)</summary>
        private Dictionary<int, IReadOnlyList<int>>? _aliasIndex;

        public IReadOnlyList<string> Paths => _paths;
        private readonly List<string> _paths = new();

        public bool IsOpen => _conns.Count > 0;

        public static CardDatabase Instance { get; } = new CardDatabase();

        /// <summary>여러 cdb 를 우선순위 순으로 연다. 성공한 것만 유지.</summary>
        public bool OpenMany(IEnumerable<string> paths)
        {
            Close();
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_conns.Count >= 64) break;
                try
                {
                    if (!File.Exists(path)) continue;
                    var cs = new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Shared
                    }.ToString();
                    var conn = new SqliteConnection(cs);
                    conn.Open();
                    // 스키마 확인
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='texts' LIMIT 1";
                        if (cmd.ExecuteScalar() == null) { conn.Dispose(); continue; }
                    }
                    _conns.Add(conn);
                    _paths.Add(path);
                }
                catch { /* 다음 후보 */ }
            }
            return _conns.Count > 0;
        }

        public void Close()
        {
            foreach (var c in _conns) { try { c.Dispose(); } catch { } }
            _conns.Clear();
            _paths.Clear();
            _nameCache.Clear();
            _aliasIndex = null;
        }

        public void Dispose() => Close();

        public string NameOf(int id)
        {
            if (id <= 0) return "";
            if (_nameCache.TryGetValue(id, out var cached)) return cached;

            string name = "";
            foreach (var conn in _conns)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT name FROM texts WHERE id=$id LIMIT 1";
                    cmd.Parameters.AddWithValue("$id", id);
                    var o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value)
                    {
                        name = Convert.ToString(o) ?? "";
                        if (name.Length > 0) break;
                    }
                }
                catch { }
            }
            _nameCache[id] = name;
            return name;
        }

        /// <summary>
        /// B-12: 같은 이름 카드(대체 일러 / 재록)의 ID 목록. 첫 번째는 항상 요청한 <paramref name="id"/>.
        /// 로드된 모든 cdb 에서 name → id[] 맵을 만들고, 이름 집합이 하나라도 겹치면 같은 그룹으로 본다(전이 1단계).
        /// 이름 비교는 Trim() + 대소문자 무시. 이름이 없는 카드는 자기 자신만.
        /// </summary>
        public IReadOnlyList<int> AliasesOf(int id)
        {
            if (id <= 0) return Array.Empty<int>();
            var index = EnsureAliasIndex();
            if (index.TryGetValue(id, out var list)) return list;
            list = new[] { id };          // 이름이 없는 카드(커스텀) — 자기 자신만
            index[id] = list;
            return list;
        }

        /// <summary>별칭 인덱스가 만들어졌는지 (진단용)</summary>
        public bool HasAliasIndex => _aliasIndex != null;

        /// <summary>별칭 그룹이 2개 이상인 카드 수 (진단용 — 인덱스가 없으면 만든다)</summary>
        public int AliasGroupCount
        {
            get
            {
                var index = EnsureAliasIndex();
                return index.Values.Count(v => v.Count > 1);
            }
        }

        /// <summary>
        /// name → id[] + id → name[] 를 한 번만 만들어 캐시한다.
        /// (같은 이름이 여러 언어 cdb 에 흩어져 있어도 합집합으로 묶는다)
        /// </summary>
        private Dictionary<int, IReadOnlyList<int>> EnsureAliasIndex()
        {
            if (_aliasIndex != null) return _aliasIndex;

            var namesById = new Dictionary<int, HashSet<string>>();
            var idsByName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var conn in _conns)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT id, name FROM texts WHERE name IS NOT NULL AND name <> ''";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var cid = r.GetInt32(0);
                        var name = (r.IsDBNull(1) ? "" : (Convert.ToString(r.GetValue(1)) ?? "")).Trim();
                        if (name.Length == 0) continue;

                        if (!namesById.TryGetValue(cid, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            namesById[cid] = set;
                        }
                        set.Add(name);

                        if (!idsByName.TryGetValue(name, out var ids))
                        {
                            ids = new List<int>();
                            idsByName[name] = ids;
                        }
                        if (!ids.Contains(cid)) ids.Add(cid);
                    }
                }
                catch { /* 깨진 cdb 는 건너뛴다 */ }
            }

            var index = new Dictionary<int, IReadOnlyList<int>>(namesById.Count);
            foreach (var kv in namesById)
            {
                var group = new SortedSet<int> { kv.Key };
                foreach (var name in kv.Value)
                {
                    if (!idsByName.TryGetValue(name, out var ids)) continue;
                    foreach (var other in ids) group.Add(other);
                }
                index[kv.Key] = group.Count == 1 ? new[] { kv.Key } : group.ToList();
            }
            _aliasIndex = index;
            return index;
        }

        /// <summary>이름으로 카드 ID 역검색 — 원본(alias=0) 우선, 그 다음 낮은 ID</summary>
        public int FindIdByName(string exactOrPartial)
        {
            if (string.IsNullOrWhiteSpace(exactOrPartial)) return 0;
            var key = exactOrPartial.Trim();

            foreach (var conn in _conns)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT t.id FROM texts t LEFT JOIN datas d ON d.id=t.id
                                        WHERE t.name=$n
                                        ORDER BY (CASE WHEN d.alias IS NULL OR d.alias=0 THEN 0 ELSE 1 END), t.id
                                        LIMIT 1";
                    cmd.Parameters.AddWithValue("$n", key);
                    var o = cmd.ExecuteScalar();
                    if (o != null && o != DBNull.Value) return Convert.ToInt32(o);
                }
                catch { }
            }
            var hit = Search(key, 5);
            return hit.Count > 0 ? hit[0].Id : 0;
        }

        public class CardInfo
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Desc { get; set; } = "";
            public int Type { get; set; }
            public int Atk { get; set; }
            public int Def { get; set; }
            public int Level { get; set; }
            public long Race { get; set; }
            public int Attribute { get; set; }
            public int Alias { get; set; }
            /// <summary>어느 cdb 에서 찾았는지</summary>
            public string Source { get; set; } = "";
        }

        public CardInfo? Info(int id)
        {
            if (id <= 0) return null;
            foreach (var conn in _conns)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT t.id, t.name, t.desc, d.type, d.atk, d.def, d.level, d.race, d.attribute, d.alias
                                        FROM texts t LEFT JOIN datas d ON d.id = t.id WHERE t.id=$id LIMIT 1";
                    cmd.Parameters.AddWithValue("$id", id);
                    using var r = cmd.ExecuteReader();
                    if (!r.Read()) continue;
                    return new CardInfo
                    {
                        Id = r.GetInt32(0),
                        Name = r.IsDBNull(1) ? "" : r.GetString(1),
                        Desc = r.IsDBNull(2) ? "" : r.GetString(2),
                        Type = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                        Atk = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                        Def = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                        Level = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                        Race = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                        Attribute = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                        Alias = r.IsDBNull(9) ? 0 : r.GetInt32(9),
                        Source = conn.DataSource
                    };
                }
                catch { }
            }
            return null;
        }

        /// <summary>이름(부분)으로 검색 — 여러 DB 결과를 병합.</summary>
        public List<CardInfo> Search(string keyword, int limit = 200)
        {
            var results = new List<CardInfo>();
            var seen = new HashSet<int>();
            if (!IsOpen) return results;

            foreach (var conn in _conns)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    if (string.IsNullOrWhiteSpace(keyword))
                    {
                        cmd.CommandText = @"SELECT t.id, t.name, d.type, d.atk, d.def, d.level
                                            FROM texts t LEFT JOIN datas d ON d.id=t.id
                                            WHERE t.name IS NOT NULL AND t.name <> '' ORDER BY t.id LIMIT $lim";
                    }
                    else
                    {
                        cmd.CommandText = @"SELECT t.id, t.name, d.type, d.atk, d.def, d.level
                                            FROM texts t LEFT JOIN datas d ON d.id=t.id
                                            WHERE t.name LIKE $kw ORDER BY length(t.name), t.id LIMIT $lim";
                        cmd.Parameters.AddWithValue("$kw", "%" + keyword.Trim() + "%");
                    }
                    cmd.Parameters.AddWithValue("$lim", limit);

                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int id = r.GetInt32(0);
                        if (!seen.Add(id)) continue;
                        results.Add(new CardInfo
                        {
                            Id = id,
                            Name = r.IsDBNull(1) ? "" : r.GetString(1),
                            Type = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                            Atk = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                            Def = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                            Level = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                            Source = Path.GetFileName(Path.GetDirectoryName(conn.DataSource) ?? "") + "/" + Path.GetFileName(conn.DataSource)
                        });
                        if (results.Count >= limit) return results;
                    }
                }
                catch { }
            }
            return results;
        }

        /// <summary>
        /// 후보 cdb 목록 (우선순위 순).
        /// 1) EDOPro/repositories/languages/**/cards.cdb (한국어 폴더 우선)
        /// 2) EDOPro/languages/Korean/cards.cdb
        /// 3) WindBot/cards.cdb, EDOPro/expansions/*.cdb
        /// </summary>
        public static List<string> FindCandidates(string windBotRoot, string? preferred = null)
        {
            var result = new List<string>();
            void Add(string? p)
            {
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p) && !result.Contains(p!, StringComparer.OrdinalIgnoreCase))
                    result.Add(p!);
            }

            Add(preferred);

            var edopro = Directory.GetParent(windBotRoot)?.FullName ?? windBotRoot;

            // 1) repositories/languages 전부
            try
            {
                var repoLangs = Path.Combine(edopro, "repositories", "languages");
                if (Directory.Exists(repoLangs))
                {
                    var files = Directory.GetFiles(repoLangs, "*.cdb", SearchOption.AllDirectories)
                        .Where(f => !Path.GetFileName(f).StartsWith("__empty", StringComparison.OrdinalIgnoreCase))
                        .Where(f => !Path.GetFileName(f).Equals("EDITME.cdb", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => Path.GetFileName(f).Equals("cards.cdb", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(f => ScoreKorean(f))
                        .ThenByDescending(f => new FileInfo(f).Length)
                        .ToList();
                    foreach (var f in files) Add(f);
                }
            }
            catch { }

            // 2) EDOPro/languages/Korean
            Add(Path.Combine(edopro, "languages", "Korean", "cards.cdb"));
            Add(Path.Combine(windBotRoot, "languages", "Korean", "cards.cdb"));

            // 3) 기타
            Add(Path.Combine(windBotRoot, "cards.cdb"));
            Add(Path.Combine(edopro, "cards.cdb"));

            try
            {
                var exp = Path.Combine(edopro, "expansions");
                if (Directory.Exists(exp))
                    foreach (var f in Directory.GetFiles(exp, "*.cdb").OrderBy(x => x))
                        Add(f);
            }
            catch { }

            return result;
        }

        private static int ScoreKorean(string path)
        {
            var p = path.Replace("/", "\\").ToLowerInvariant();
            if (p.Contains("한국어")) return 0;
            if (p.Contains("korean")) return 1;
            return 2;
        }
    }
}

