using System.Windows;
using System.Windows.Input;
using WindBotAIEditor.Services;

namespace WindBotAIEditor
{
    public partial class CardPickerWindow : Window
    {
        public int SelectedCardId { get; private set; }
        public string SelectedCardName { get; private set; } = "";

        public CardPickerWindow(int initialCard = 0, string keyword = "")
        {
            InitializeComponent();
            TxtKeyword.Text = keyword;
            Loaded += (_, __) =>
            {
                if (initialCard > 0)
                {
                    var name = CardDatabase.Instance.NameOf(initialCard);
                    if (!string.IsNullOrEmpty(name)) TxtKeyword.Text = name;
                }
                RunSearch();
                TxtKeyword.Focus();
                TxtKeyword.SelectAll();
            };
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) RunSearch();
        }

        private void OnSearch(object sender, RoutedEventArgs e) => RunSearch();

        private void RunSearch()
        {
            if (!CardDatabase.Instance.IsOpen)
            {
                MessageBox.Show("카드 DB 가 열려 있지 않습니다.", "카드 선택");
                return;
            }
            var list = CardDatabase.Instance.Search(TxtKeyword.Text, 300);
            GridResults.ItemsSource = list;
            LblInfo.Text = list.Count + "건" + (CardDatabase.Instance.Paths.Count > 1
                ? $" · DB {CardDatabase.Instance.Paths.Count}개 참조" : "");
            if (list.Count > 0) GridResults.SelectedIndex = 0;
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

        private void OnOk(object sender, RoutedEventArgs e) => Commit();

        private void Commit()
        {
            if (GridResults.SelectedItem is CardDatabase.CardInfo info)
            {
                SelectedCardId = info.Id;
                SelectedCardName = info.Name;
                DialogResult = true;
            }
        }
    }
}
