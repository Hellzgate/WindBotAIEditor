using System.Linq;
using System.Windows;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    /// <summary>
    /// B-24: 프리릴리즈 → 정규 카드 ID 변환 **미리보기**.
    /// [적용] 을 누르면 DialogResult=true 만 돌려주고, 실제 변환/저장은 MainWindow 가 한다
    /// (실수 방지 2단계 — 여기서는 아무 파일도 쓰지 않는다).
    /// </summary>
    public partial class PreReleasePreviewWindow : Window
    {
        public PreReleasePreviewWindow(PreConvertPlan plan)
        {
            InitializeComponent();

            GridChanges.ItemsSource = plan.Changes.OrderBy(c => c.InDeck).ThenBy(c => c.From).ToList();
            TxtSummary.Text = plan.Summary + " · 총 " + plan.Changes.Count + "건";

            TxtInfo.Text = "같은 이름의 정규 카드가 있는 프리릴리즈 ID 를 바꿉니다. " +
                           "적용하면 규칙 JSON 과 덱 파일(.ydk)을 .bak-<시각> 으로 백업한 뒤 덮어씁니다.";

            var warn = "";
            if (plan.DeckChangeCount > 0)
                warn = "덱 파일도 바뀝니다 — 이 PC 의 카드 DB 에 정규 카드가 있어야 게임에서 인식됩니다. " +
                       "프리릴리즈 DB 로만 플레이한다면 변환하지 마세요.";
            else
                warn = "규칙(카드 ID)만 바뀝니다. 되돌리려면 생성된 .bak 파일을 원래 이름으로 복사하세요.";
            TxtWarn.Text = warn;
        }

        private void OnApply(object sender, RoutedEventArgs e) => DialogResult = true;

        /// <summary>--uicheck 확인용: 미리보기 목록(DataGrid)에 실제로 들어간 항목 수</summary>
        public int GridChangeCount => GridChanges?.Items.Count ?? 0;
    }
}
