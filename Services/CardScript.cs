using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// EDOPro 카드 스크립트(.lua) 조회.
    /// 표준 위치: script/c&lt;id&gt;.lua, script/official/c&lt;id&gt;.lua, script/local/c&lt;id&gt;.lua
    /// </summary>
    public static class CardScript
    {
        public static string[] SearchDirs(string edoproRoot)
        {
            var script = Path.Combine(edoproRoot, "script");
            return new[]
            {
                Path.Combine(script, "local"),
                Path.Combine(script, "official"),
                script,
                Path.Combine(script, "pre-release"),
                Path.Combine(script, "goat"),
            };
        }

        /// <summary>카드 ID → .lua 경로 (없으면 null)</summary>
        public static string? Find(string edoproRoot, int cardId)
        {
            if (cardId <= 0 || !Directory.Exists(edoproRoot)) return null;
            var file = "c" + cardId + ".lua";
            foreach (var dir in SearchDirs(edoproRoot))
            {
                var p = Path.Combine(dir, file);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string? Read(string edoproRoot, int cardId)
        {
            var p = Find(edoproRoot, cardId);
            if (p == null) return null;
            try { return File.ReadAllText(p); } catch { return null; }
        }

        public class EffectDesc
        {
            public string Raw { get; set; } = "";
            public int Value { get; set; } = -1;
            public bool Dynamic { get; set; }
            public string Text { get; set; } = "";
            public override string ToString() =>
                Dynamic ? $"동적 (aux.Stringid #{Value}) — 런타임 기록으로 확인"
                        : $"{Value} — {(string.IsNullOrEmpty(Text) ? "(설명 없음)" : Text)}";
        }

        /// <summary>Lua 의 SetDescription(...) 값을 뽑아 효과번호 후보를 만든다.</summary>
        public static List<EffectDesc> Descriptions(string? lua, Services.EffectCatalog? catalog = null)
        {
            var list = new List<EffectDesc>();
            if (string.IsNullOrEmpty(lua)) return list;

            foreach (var raw in lua.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                var idx = line.IndexOf("SetDescription", StringComparison.Ordinal);
                if (idx < 0) continue;

                var open = line.IndexOf('(', idx);
                var close = line.LastIndexOf(')');
                if (open < 0 || close <= open) continue;
                var arg = line.Substring(open + 1, close - open - 1).Trim();

                if (int.TryParse(arg, out var fixedVal))
                {
                    list.Add(new EffectDesc
                    {
                        Raw = line,
                        Value = fixedVal,
                        Dynamic = false,
                        Text = catalog?.TextOf(fixedVal) ?? ""
                    });
                }
                else if (arg.StartsWith("aux.Stringid", StringComparison.OrdinalIgnoreCase))
                {
                    // aux.Stringid(id, n) → 카드 텍스트 항목 n (런타임 계산값)
                    var inner = arg.Substring(arg.IndexOf('(') + 1).TrimEnd(')');
                    var parts2 = inner.Split(',');
                    var n = parts2.Length > 1 && int.TryParse(parts2[1].Trim(), out var v) ? v : 0;
                    list.Add(new EffectDesc { Raw = line, Dynamic = true, Value = n });
                }
            }
            return list;
        }
        /// <summary>스크립트에서 이 카드가 실행하는 effect 종류를 대략 요약</summary>
        public static List<string> Summarize(string lua)
        {
            var found = new List<string>();
            var patterns = new (string token, string label)[]
            {
                ("EFFECT_TYPE_ACTIVATE", "발동형 효과"),
                ("EFFECT_TYPE_IGNITION", "기동 효과"),
                ("EFFECT_TYPE_TRIGGER_O", "유발 즉시 효과"),
                ("EFFECT_TYPE_TRIGGER_F", "유발 효과"),
                ("EFFECT_TYPE_QUICK_O", "패스트 효과(즉시)"),
                ("EFFECT_TYPE_QUICK_F", "패스트 효과"),
                ("EFFECT_TYPE_CONTINUOUS", "지속 효과"),
                ("EFFECT_TYPE_SINGLE", "단일(소환/표시) 효과"),
                ("EFFECT_TYPE_FIELD", "필드 효과"),
                ("EFFECT_TYPE_EQUIP", "장착 효과"),
                ("EFFECT_SUMMON_PROC", "소환 절차"),
                ("EFFECT_SPSUMMON_PROC", "특수소환 절차"),
                ("EFFECT_SPSUMMON_CONDITION", "특수소환 조건"),
                ("EFFECT_SET_PROC", "세트 절차"),
                ("EFFECT_CANNOT", "금지 계열"),
                ("EFFECT_UPDATE_ATTACK", "공격력 변경"),
                ("EFFECT_UPDATE_DEFENSE", "수비력 변경"),
                ("EFFECT_DESTROY_SUBSTITUTE", "파괴 대체"),
                ("EFFECT_INDESTRUCTABLE", "파괴 내성"),
                ("EFFECT_NEGATE", "무효화"),
                ("EFFECT_DAMAGE_STEP", "데미지 스텝 관련"),
                ("EFFECT_CHAIN", "체인 관련"),
            };
            foreach (var (token, label) in patterns)
                if (lua.Contains(token) && !found.Contains(label)) found.Add(label);
            return found;
        }
    }
}

