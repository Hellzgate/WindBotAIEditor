namespace WindBotAIEditor.Models
{
    /// <summary>덱 안의 카드 한 종류 (검색/집계용)</summary>
    public class DeckCardItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public string Section { get; set; } = "main";

        public string Display => string.IsNullOrEmpty(Name)
            ? $"{Id} ×{Count}  [{Section}]"
            : $"{Name} ×{Count}  [{Section}]  ({Id})";

        public override string ToString() => Display;
    }
}
