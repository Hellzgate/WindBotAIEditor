// WindBot AI Editor — rule-generated executor (schema v2)
// engine : TestRules2Executor
// note   : schema v2 — OR 조건 + 소재/릴리스 선택 + 카드별 대상 지정
using System;
using System.Collections.Generic;
using WindBot.Game;
using WindBot.Game.AI;

namespace WindBot.Game.AI.Decks
{
    public class TestRules2Executor : Executor
    {
        public TestRules2Executor(GameAI ai, Duel duel) : base(ai, duel)
        {
            // 상대 몬스터가 2마리 이상이거나 상대 LP가 3000 이하일 때 일반 소환
            AddExecutor(ExecutorType.Summon, () => (Enemy.GetMonsterCount() >= 2 || Enemy.LifePoints <= 3000));
            // 패/묘지 조건 OR
            AddExecutor(ExecutorType.Activate, 14558127, () => (Enemy.HasInHand(23434538) || Bot.HasInGraveyard(14558127)));
            // 직공 가능
            AddExecutor(ExecutorType.GoToBattlePhase, () => Enemy.GetMonsterCount() == 0);
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            if (Card != null)
            {
                if (Card.IsCode(14558127)) return Pick(cards, max, 1, 0);
            }
            return Pick(cards, max, 0, 0);
        }

        public override IList<ClientCard> OnSelectSum(IList<ClientCard> cards, int sum, int min, int max, long hint, bool mode)
        {
            return Pick(cards, max, 4, 0);
        }

        public override IList<ClientCard> OnSelectFusionMaterial(IList<ClientCard> cards, int min, int max)
        {
            return Pick(cards, max, 1, 89631139);
        }

        public override IList<ClientCard> OnSelectXyzMaterial(IList<ClientCard> cards, int min, int max)
        {
            return Pick(cards, max, 4, 0);
        }

        public override bool OnSelectYesNo(long desc)
        {
            return true;
        }

        // policy: 0=목록순 1=공격력↓ 2=공격력↑ 3=수비력↓ 4=레벨↑ 5=특정카드우선
        private static IList<ClientCard> Pick(IList<ClientCard> cards, int max, int policy, int code)
        {
            List<ClientCard> pool = new List<ClientCard>(cards);
            if (policy == 1) pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Attack.CompareTo(a.Attack); });
            else if (policy == 2) pool.Sort(delegate(ClientCard a, ClientCard b) { return a.Attack.CompareTo(b.Attack); });
            else if (policy == 3) pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Defense.CompareTo(a.Defense); });
            else if (policy == 4) pool.Sort(delegate(ClientCard a, ClientCard b) { return a.Level.CompareTo(b.Level); });
            List<ClientCard> picked = new List<ClientCard>();
            if (code > 0)
            {
                for (int i = 0; i < pool.Count && picked.Count < max; i++)
                    if (pool[i].IsCode(code)) picked.Add(pool[i]);
            }
            for (int i = 0; i < pool.Count && picked.Count < max; i++)
                if (!picked.Contains(pool[i])) picked.Add(pool[i]);
            return picked;
        }
    }
}
