// WindBot AI Editor — rule-generated executor
// engine : TestRulesExecutor
// note   : DSL 컴파일 검증용
using System;
using System.Collections.Generic;
using WindBot.Game;
using WindBot.Game.AI;

namespace WindBot.Game.AI.Decks
{
    public class TestRulesExecutor : Executor
    {
        public TestRulesExecutor(GameAI ai, Duel duel) : base(ai, duel)
        {
            // 상대 몬스터가 있으면 아무 몬스터나 일반 소환
            AddExecutor(ExecutorType.Summon, () => (Enemy.GetMonsterCount() >= 1));
            // 상대 패에 특정 카드가 있으면 발동
            AddExecutor(ExecutorType.Activate, 14558127, () => (Enemy.HasInHand(23434538)));
            // 상대 필드가 비면 배틀 페이즈
            AddExecutor(ExecutorType.GoToBattlePhase, () => (Enemy.IsFieldEmpty()));
        }

        public override IList<ClientCard> OnSelectCard(IList<ClientCard> cards, int min, int max, long hint, bool cancelable)
        {
            List<ClientCard> pool = new List<ClientCard>(cards);
            pool.Sort(delegate(ClientCard a, ClientCard b) { return b.Attack.CompareTo(a.Attack); });
            List<ClientCard> picked = new List<ClientCard>();
            for (int i = 0; i < pool.Count && picked.Count < max; i++)
                picked.Add(pool[i]);
            return picked;
        }

        public override bool OnSelectYesNo(long desc)
        {
            return true;
        }
    }
}
