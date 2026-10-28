using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>카드 ID 변환 1건 (미리보기 표시용)</summary>
    public sealed class PreIdChange
    {
        public int From { get; set; }
        public int To { get; set; }

        /// <summary>카드 이름 (없으면 빈 문자열)</summary>
        public string Name { get; set; } = "";

        /// <summary>어디에 있었는지 — 예: <c>① 행동 #3 · 카드</c>, <c>덱 #main 12번째</c></summary>
        public string Where { get; set; } = "";

        /// <summary>덱(.ydk) 변경인지</summary>
        public bool InDeck { get; set; }

        public string Target => InDeck ? "덱 파일" : "규칙";

        /// <summary>미리보기 한 줄 — <c>101104012 → 92586237 (Bujin Mahitotsu)   · ① 행동 #3 · 카드</c></summary>
        public string Display => From + " → " + To +
                                 (Name.Length > 0 ? "  (" + Name + ")" : "") +
                                 "   · " + Where;
    }

    /// <summary>변환 계획 — 미리보기 목록 + (있으면) 변환된 덱 텍스트</summary>
    public sealed class PreConvertPlan
    {
        public List<PreIdChange> Changes { get; } = new();

        /// <summary>덱 변환이 있었으면 새 .ydk 텍스트, 없으면 null</summary>
        public string? NewDeckText { get; set; }

        /// <summary>규칙에서 변환되는 ID 종류 수</summary>
        public int RuleIdCount => Changes.Where(c => !c.InDeck).Select(c => c.From).Distinct().Count();

        /// <summary>규칙에서 변환되는 자리 수</summary>
        public int RuleChangeCount => Changes.Count(c => !c.InDeck);

        /// <summary>덱에서 변환되는 장 수(중복 포함)</summary>
        public int DeckChangeCount => Changes.Count(c => c.InDeck);

        public bool IsEmpty => Changes.Count == 0;

        public string Summary => "규칙 " + RuleIdCount + "종/" + RuleChangeCount + "곳" +
                                 (DeckChangeCount > 0 ? " · 덱 " + DeckChangeCount + "장" : "");
    }

    /// <summary>
    /// B-24: 프로필(규칙)·덱의 카드 ID 를 프리릴리즈 → 정규로 바꾼다.
    /// <para>
    /// <b>리플렉션 화이트리스트</b>로 "카드 ID 인 필드"만 변환한다.
    /// 프로필 모델의 int 프로퍼티는 대부분이 카드 ID 가 아니므로(Priority/Value/Count/ThemeCode/RaceId/
    /// EffectDesc/OptionDesc/ChainLimit …) 이름 화이트리스트 + 블랙리스트를 명시적으로 둔다.
    /// </para>
    /// <para>
    /// 화이트리스트 근거:
    /// <list type="bullet">
    /// <item><c>Card</c> — ① 행동 / ② 선택(codeMatch) / ③ 금지 / ④ 트리거 · 상대 트리거 / ⑤ 단계 · 순위 / 조건. 모두 카드 ID.</item>
    /// <item><c>WhenCard</c> — ② "이 선택 규칙을 적용할 카드(발동한 카드)".</item>
    /// <item><c>ResponseCard</c> — ④ 상대 트리거가 대응해서 발동할 **내 카드** ID.</item>
    /// <item><c>AvoidColumnCard</c> — 열 회피 기준 **카드** ID.</item>
    /// <item><c>ExcludeCards</c> / <c>FreePlaySetSpellExclude</c> — List&lt;int&gt; 이고 원소가 카드 ID.</item>
    /// </list>
    /// 블랙리스트(절대 변환 금지) 근거: <c>Priority</c>(평가 순서) · <c>Value</c>(레벨/수치) · <c>Count</c>(장수) ·
    /// <c>ThemeCode</c>(setcode) · <c>RaceId</c>(종족 비트) · <c>ChainLimit</c>(체인 횟수) · <c>WhenMax</c>(max 개수) ·
    /// <c>OptionIndex</c>/<c>PlaceIndex</c>(선택 인덱스) · <c>Group</c>/<c>Order</c>/<c>Rank</c>(순서) ·
    /// <c>SchemaVersion</c> · <c>EffectDesc</c>/<c>OptionDesc</c>(효과번호·옵션 desc — long 이라 타입으로도 제외).
    /// </para>
    /// </summary>
    public static class PreReleaseConverter
    {
        // ---------- 화이트리스트 / 블랙리스트 ----------

        /// <summary>변환하는 단일 카드 ID 프로퍼티 (이름 기준)</summary>
        private static readonly HashSet<string> CardIdNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Card",             // ActionRule · Condition · ForbidRule · FinishTrigger · OppTrigger · RouteStep · RouteOption · ChoiceRule
            "WhenCard",         // ChoiceRule — 적용 대상(발동한 카드)
            "ResponseCard",     // OppTrigger — 대응 발동 카드
            "AvoidColumnCard",  // AiProfile — 열 회피 기준 카드
        };

        /// <summary>변환하는 카드 ID 목록 프로퍼티 (이름 기준)</summary>
        private static readonly HashSet<string> CardIdListNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "ExcludeCards",             // ChoiceRule — 선택 후보 제외
            "FreePlaySetSpellExclude",  // AiProfile — 자유 플레이 세트 제외
        };

        /// <summary>
        /// 절대 변환하지 않는 숫자 필드 — 화이트리스트 방식이라 자동으로 제외되지만,
        /// 나중에 누가 "int 니까 카드 ID 겠지" 하고 화이트리스트에 넣지 않도록 근거를 남긴다.
        /// </summary>
        private static readonly HashSet<string> NeverConvert = new HashSet<string>(StringComparer.Ordinal)
        {
            "Priority",       // 규칙 평가 순서
            "Value",          // 레벨/LP/턴 등 수치
            "Count",          // 장수 (B-20)
            "ThemeCode",      // 테마 setcode
            "RaceId",         // 종족 비트 (CardRace)
            "ChainLimit",     // 체인당 허용 횟수
            "WhenMax",        // 적용할 max 선택 수
            "OptionIndex",    // SelectOption 인덱스
            "PlaceIndex",     // SelectPlace 인덱스
            "Group",          // 트리거 그룹 번호
            "Order",          // 단계 순서
            "Rank",           // 순위
            "SchemaVersion",  // 파일 스키마
        };

        // ---------- 수집 ----------

        /// <summary>프로필의 카드 ID 전부 (중복 제거)</summary>
        public static List<int> CollectIds(AiProfile profile)
        {
            var ids = new SortedSet<int>();
            WalkCardFields(profile,
                (target, prop, path, list, index) =>
                {
                    if (list != null)
                    {
                        foreach (var v in list) if (v > 0) ids.Add(v);
                    }
                    else if (prop.GetValue(target) is int v && v > 0) ids.Add(v);
                });
            return ids.ToList();
        }

        /// <summary>ydk 텍스트의 카드 ID (중복 제거)</summary>
        public static List<int> DeckIds(string? deckText)
        {
            var ids = new SortedSet<int>();
            foreach (var line in SplitLines(deckText ?? ""))
                if (TryCardId(line.Text, out var id) && id > 0) ids.Add(id);
            return ids.ToList();
        }

        // ---------- 계획 ----------

        /// <summary>
        /// 변환 계획을 만든다 (파일 쓰기 없음).
        /// <paramref name="map"/> 를 주면 그 매핑을 쓴다 (없으면 <see cref="PreReleaseCatalog.OfficialId"/>).
        /// </summary>
        public static PreConvertPlan Build(AiProfile profile, string? deckText, Func<int, int>? map = null)
        {
            var catalog = PreReleaseCatalog.Instance;
            var lookup = map ?? catalog.OfficialId;
            var plan = new PreConvertPlan();

            WalkCardFields(profile, (target, prop, path, list, index) =>
            {
                if (list != null)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var from = list[i];
                        if (from <= 0) continue;
                        var to = lookup(from);
                        if (to <= 0 || to == from) continue;
                        plan.Changes.Add(new PreIdChange
                        {
                            From = from,
                            To = to,
                            Name = NameOf(from, to),
                            Where = path + "[" + i + "]",
                            InDeck = false,
                        });
                    }
                }
                else if (prop.GetValue(target) is int from && from > 0)
                {
                    var to = lookup(from);
                    if (to <= 0 || to == from) return;
                    plan.Changes.Add(new PreIdChange
                    {
                        From = from,
                        To = to,
                        Name = NameOf(from, to),
                        Where = path,
                        InDeck = false,
                    });
                }
            });

            if (!string.IsNullOrEmpty(deckText))
            {
                var newText = ConvertDeck(deckText!, lookup, plan.Changes);
                if (!string.Equals(newText, deckText, StringComparison.Ordinal)) plan.NewDeckText = newText;
            }
            return plan;
        }

        /// <summary>계획을 실제 모델에 적용한다 (저장/파일 쓰기는 호출자 몫). 반환 = 바뀐 자리 수</summary>
        public static int Apply(AiProfile profile, Func<int, int>? map = null)
        {
            var lookup = map ?? PreReleaseCatalog.Instance.OfficialId;
            var changed = 0;

            WalkCardFields(profile, (target, prop, path, list, index) =>
            {
                if (list != null)
                {
                    // 목록은 중복이 생길 수 있다(제외 목록에 pre/정규가 둘 다 있던 경우) → 순서 유지 + 중복 제거
                    var seen = new HashSet<int>();
                    var dedup = false;
                    for (var i = 0; i < list.Count; i++)
                    {
                        var cur = list[i];
                        var mapped = cur > 0 ? lookup(cur) : 0;
                        if (mapped > 0 && mapped != cur) { list[i] = mapped; changed++; }
                        if (!seen.Add(list[i])) dedup = true;
                    }
                    if (dedup)
                    {
                        var keep = new List<int>();
                        var once = new HashSet<int>();
                        foreach (var v in list) if (once.Add(v)) keep.Add(v);
                        list.Clear();
                        foreach (var v in keep) list.Add(v);
                    }
                    return;
                }

                if (prop.SetMethod == null || !prop.SetMethod.IsPublic) return;
                if (prop.GetValue(target) is not int from || from <= 0) return;
                var newId = lookup(from);
                if (newId <= 0 || newId == from) return;
                try { prop.SetValue(target, newId); changed++; } catch { }
            });

            return changed;
        }

        /// <summary>ydk 텍스트의 카드 ID 를 바꾼다 (섹션/개행/주석 보존). 변경 내역을 <paramref name="changes"/> 에 추가.</summary>
        public static string ConvertDeck(string deckText, Func<int, int> map, List<PreIdChange>? changes)
        {
            var lines = SplitLines(deckText);
            var section = "#main";
            var counter = 0;
            var sb = new System.Text.StringBuilder(deckText.Length + 64);
            foreach (var line in lines)
            {
                var text = line.Text;
                var trimmed = text.Trim();
                if (trimmed.Length == 0)
                {
                    sb.Append(text).Append(line.Ending);
                    continue;
                }
                var sec = WindBotProject.SectionOf(trimmed);
                if (sec != null)
                {
                    section = sec == "extra" ? "#extra" : sec == "side" ? "!side" : "#main";
                    counter = 0;
                    sb.Append(text).Append(line.Ending);
                    continue;
                }
                if (WindBotProject.IsCommentOrMarker(trimmed) || !TryCardId(trimmed, out var id))
                {
                    sb.Append(text).Append(line.Ending);
                    continue;
                }

                counter++;
                var to = id > 0 ? map(id) : 0;
                if (to > 0 && to != id)
                {
                    // 원본 줄의 앞뒤 공백을 유지한다
                    var lead = text.Substring(0, text.Length - text.TrimStart().Length);
                    var trail = text.Substring(text.TrimEnd().Length);
                    sb.Append(lead).Append(to).Append(trail).Append(line.Ending);
                    changes?.Add(new PreIdChange
                    {
                        From = id,
                        To = to,
                        Name = NameOf(id, to),
                        Where = "덱 " + section + " " + counter + "번째",
                        InDeck = true,
                    });
                }
                else sb.Append(text).Append(line.Ending);
            }
            return sb.ToString();
        }

        // ---------- 내부 ----------

        /// <summary>카드 이름 — pre cdb 이름 → (없으면) 카드 DB</summary>
        private static string NameOf(int from, int to)
        {
            var name = PreReleaseCatalog.Instance.NameOf(from);
            if (name.Length == 0) name = CardDatabase.Instance.NameOf(from);
            if (name.Length == 0) name = CardDatabase.Instance.NameOf(to);
            return name;
        }

        private sealed class Line
        {
            public string Text = "";
            public string Ending = "";
        }

        /// <summary>개행을 보존한 줄 분리 (CRLF/LF 혼용 유지)</summary>
        private static List<Line> SplitLines(string text)
        {
            var result = new List<Line>();
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                var end = i;
                if (end > start && text[end - 1] == '\r') end--;
                result.Add(new Line { Text = text.Substring(start, end - start), Ending = text.Substring(end, i + 1 - end) });
                start = i + 1;
            }
            if (start < text.Length) result.Add(new Line { Text = text.Substring(start), Ending = "" });
            return result;
        }

        private static bool TryCardId(string line, out int id)
        {
            id = 0;
            var t = line.Trim();
            if (t.Length == 0 || t.Length > 12) return false;
            foreach (var ch in t) if (ch < '0' || ch > '9') return false;
            return int.TryParse(t, out id);
        }

        private delegate void CardFieldVisitor(object target, PropertyInfo prop, string path, IList<int>? list, int index);

        /// <summary>
        /// 프로필 객체 그래프를 훑어 "카드 ID 필드"만 방문한다.
        /// <paramref name="list"/> 가 null 이 아니면 목록 프로퍼티(List&lt;int&gt;), 아니면 단일 int 프로퍼티.
        /// </summary>
        private static void WalkCardFields(object? obj, CardFieldVisitor visit)
        {
            Walk(obj, "", new HashSet<object>(ReferenceEqualityComparer.Instance));

            void Walk(object? node, string path, HashSet<object> visited)
            {
                if (node == null) return;
                var type = node.GetType();
                if (IsScalar(type)) return;
                if (!type.IsValueType && !visited.Add(node)) return;   // 순환 방지

                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    if (!prop.CanRead) continue;
                    // [JsonIgnore] 는 UI 파생 필드(CardName/Display/TurnScopeName …) 와 편집용 입력 상자 → 건너뛴다
                    if (prop.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;

                    var pt = prop.PropertyType;
                    object? value;
                    try { value = prop.GetValue(node); } catch { continue; }
                    var childPath = path.Length == 0 ? Label(prop.Name) : path + " · " + Label(prop.Name);

                    if (pt == typeof(int) || pt == typeof(int?))
                    {
                        if (CardIdNames.Contains(prop.Name) && !NeverConvert.Contains(prop.Name))
                            visit(node, prop, childPath, null, -1);
                        continue;
                    }

                    if (value is IList<int> ints)
                    {
                        if (CardIdListNames.Contains(prop.Name)) visit(node, prop, childPath, ints, -1);
                        continue;
                    }

                    if (value is string) continue;

                    if (value is IEnumerable seq)
                    {
                        var i = 0;
                        foreach (var item in seq)
                        {
                            // 목록이면 "① 행동 #3" 처럼 1부터 번호를 붙인다
                            var itemPath = value is IList ? childPath + " #" + (i + 1) : childPath;
                            Walk(item, itemPath, visited);
                            i++;
                        }
                        continue;
                    }

                    Walk(value, childPath, visited);
                }
            }
        }

        private static bool IsScalar(Type t) =>
            t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) ||
            t == typeof(DateTime) || t == typeof(Guid);

        /// <summary>경로 표시용 한국어 라벨 (모르는 이름은 그대로)</summary>
        private static string Label(string name) => name switch
        {
            "Actions" => "① 행동",
            "Choices" => "② 선택",
            "Forbids" => "③ 금지",
            "Triggers" => "④ 트리거",
            "OppTriggers" => "④ 상대 트리거",
            "Routes" => "⑤ 선공 루트",
            "RoutesSecond" => "⑤ 후공 루트",
            "Route" => "⑤ 선공 루트(구형)",
            "RouteSecond" => "⑤ 후공 루트(구형)",
            "Steps" => "단계",
            "Options" => "순위",
            "Conditions" => "조건",
            "Card" => "카드",
            "WhenCard" => "발동카드",
            "ResponseCard" => "대응카드",
            "AvoidColumnCard" => "열 회피 카드",
            "ExcludeCards" => "제외 카드",
            "FreePlaySetSpellExclude" => "세트 제외",
            _ => name,
        };
    }
}
