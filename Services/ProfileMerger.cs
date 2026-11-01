using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Services
{
    /// <summary>
    /// 2026-11-02: 다른 프로필을 **추가(병합)** 한다 — 기존 규칙은 그대로 두고 뒤에 이어 붙인다.
    /// <para>· 중복(같은 타입+카드 / 같은 선택 콜백+카드+힌트+정책 / 같은 금지 카드+행동+효과번호)은 **건너뛴다**</para>
    /// <para>· 우선순위는 "기존 최대 + 10" 부터 이어서 매긴다(기존 우선순위 불변)</para>
    /// <para>· **전역 설정(옵션·카드 목록·루트)은 건드리지 않는다** — 덮어쓰기 모드에서만 교체된다</para>
    /// </summary>
    public static class ProfileMerger
    {
        public sealed class Counts
        {
            public int Actions, Choices, Forbids, Triggers, Opps, Routes, Skipped;
            public string Summary => "행동 +" + Actions + " · 선택 +" + Choices + " · 금지 +" + Forbids +
                                     " · 트리거 +" + Triggers + " · 상대트리거 +" + Opps +
                                     (Routes > 0 ? " · 루트 +" + Routes : "") +
                                     (Skipped > 0 ? " (중복 " + Skipped + "건 건너뜀)" : "");
        }

        public static Counts MergeInto(AiProfile target, AiProfile src)
        {
            var c = new Counts();
            if (target == null || src == null) return c;

            target.Actions ??= new List<ActionRule>();
            target.Choices ??= new List<ChoiceRule>();
            target.Forbids ??= new List<ForbidRule>();
            target.Triggers ??= new List<FinishTrigger>();
            target.OppTriggers ??= new List<OppTrigger>();

            // ---- ① 행동 ----
            var pa = target.Actions.Count == 0 ? 0 : target.Actions.Max(x => x.Priority);
            foreach (var a in src.Actions ?? new List<ActionRule>())
            {
                if (a == null) continue;
                if (target.Actions.Any(x => x.Type == a.Type && x.Card == a.Card && x.EffectDesc == a.EffectDesc))
                { c.Skipped++; continue; }
                var cp = Clone(a);
                cp.Priority = pa + 10 * (++c.Actions);
                target.Actions.Add(cp);
            }

            // ---- ② 선택 규칙 ----
            var pc = target.Choices.Count == 0 ? 0 : target.Choices.Max(x => x.Priority);
            foreach (var ch in src.Choices ?? new List<ChoiceRule>())
            {
                if (ch == null) continue;
                if (target.Choices.Any(x => x.On == ch.On && x.WhenCard == ch.WhenCard && x.WhenHint == ch.WhenHint
                                            && x.Pick == ch.Pick && x.Card == ch.Card))
                { c.Skipped++; continue; }
                var cp = Clone(ch);
                cp.Priority = pc + 10 * (++c.Choices);
                target.Choices.Add(cp);
            }

            // ---- ③ 금지 ----
            var pf = target.Forbids.Count == 0 ? 0 : target.Forbids.Max(x => x.Priority);
            foreach (var f in src.Forbids ?? new List<ForbidRule>())
            {
                if (f == null) continue;
                if (target.Forbids.Any(x => x.Card == f.Card && x.Action == f.Action && x.EffectDesc == f.EffectDesc))
                { c.Skipped++; continue; }
                var cp = Clone(f);
                cp.Priority = pf + 10 * (++c.Forbids);
                target.Forbids.Add(cp);
            }

            // ---- ④ 트리거 ----
            var pt = target.Triggers.Count == 0 ? 0 : target.Triggers.Max(x => x.Priority);
            foreach (var t in src.Triggers ?? new List<FinishTrigger>())
            {
                if (t == null) continue;
                if (target.Triggers.Any(x => x.Card == t.Card && x.Zone == t.Zone && x.WhenDone == t.WhenDone))
                { c.Skipped++; continue; }
                var cp = Clone(t);
                cp.Priority = pt + 10 * (++c.Triggers);
                target.Triggers.Add(cp);
            }

            var po = target.OppTriggers.Count == 0 ? 0 : target.OppTriggers.Max(x => x.Priority);
            foreach (var o in src.OppTriggers ?? new List<OppTrigger>())
            {
                if (o == null) continue;
                if (target.OppTriggers.Any(x => x.Card == o.Card && x.ResponseCard == o.ResponseCard))
                { c.Skipped++; continue; }
                var cp = Clone(o);
                cp.Priority = po + 10 * (++c.Opps);
                target.OppTriggers.Add(cp);
            }

            // ---- ⑤ 루트 (벌 단위로 뒤에 붙인다) ----
            target.Routes ??= new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
            foreach (var r in src.Routes ?? new System.Collections.ObjectModel.ObservableCollection<RouteSet>())
            {
                if (r == null) continue;
                target.Routes.Add(Clone(r));
                c.Routes++;
            }
            target.RoutesSecond ??= new System.Collections.ObjectModel.ObservableCollection<RouteSet>();
            foreach (var r in src.RoutesSecond ?? new System.Collections.ObjectModel.ObservableCollection<RouteSet>())
            {
                if (r == null) continue;
                target.RoutesSecond.Add(Clone(r));
                c.Routes++;
            }

            return c;
        }

        private static T Clone<T>(T item)
        {
            var json = JsonSerializer.Serialize(item, WindBotProject.JsonOpts);
            var copy = JsonSerializer.Deserialize<T>(json, WindBotProject.JsonOpts);
            return copy!;
        }
    }
}

