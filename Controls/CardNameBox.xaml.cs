using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindBotAIEditor.Services;

namespace WindBotAIEditor.Controls
{
    /// <summary>
    /// 카드 ID ↔ 이름 자동완성 입력 상자.
    /// 카드명을 직접 타이핑하면 cards.cdb 에서 검색해 후보를 보여주고, 선택하면 CardId 가 갱신된다.
    /// </summary>
    public partial class CardNameBox : UserControl
    {
        public static readonly DependencyProperty CardIdProperty = DependencyProperty.Register(
            nameof(CardId), typeof(int), typeof(CardNameBox),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnCardIdChanged));

        public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
            nameof(AllowEmpty), typeof(bool), typeof(CardNameBox), new PropertyMetadata(true));

        public static readonly DependencyProperty EmptyLabelProperty = DependencyProperty.Register(
            nameof(EmptyLabel), typeof(string), typeof(CardNameBox), new PropertyMetadata("(전체)"));

        public int CardId
        {
            get => (int)GetValue(CardIdProperty);
            set => SetValue(CardIdProperty, value);
        }

        /// <summary>0 을 허용할지 (행동 규칙은 전체=0 허용)</summary>
        public bool AllowEmpty
        {
            get => (bool)GetValue(AllowEmptyProperty);
            set => SetValue(AllowEmptyProperty, value);
        }

        public string EmptyLabel
        {
            get => (string)GetValue(EmptyLabelProperty);
            set => SetValue(EmptyLabelProperty, value);
        }

        private bool _suppress;
        private List<CardDatabase.CardInfo> _results = new();

        public CardNameBox()
        {
            InitializeComponent();
            Loaded += (_, __) => SyncText();
        }

        private static void OnCardIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CardNameBox box && !box._suppress) box.SyncText();
        }

        private void SyncText()
        {
            var name = CardDatabase.Instance.NameOf(CardId);
            _suppress = true;
            Txt.Text = CardId <= 0 ? EmptyLabel : (name.Length > 0 ? name : CardId.ToString());
            Txt.SetResourceReference(TextBox.ForegroundProperty, CardId <= 0 ? "HintFg" : "TextFg");
            _suppress = false;
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (CardId <= 0) Txt.Text = "";
            Txt.SetResourceReference(TextBox.ForegroundProperty, "TextFg");
            Txt.SelectAll();
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            var keyword = (Txt.Text ?? "").Trim();
            if (keyword.Length < 1 || !CardDatabase.Instance.IsOpen)
            {
                Pop.IsOpen = false;
                return;
            }

            _results = CardDatabase.Instance.Search(keyword, 20);
            if (_results.Count == 0)
            {
                Pop.IsOpen = false;
                return;
            }

            Lst.ItemsSource = _results;
            Lst.SelectedIndex = 0;
            Pop.IsOpen = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && Pop.IsOpen)
            {
                Lst.Focus();
                if (Lst.SelectedIndex < 0 && _results.Count > 0) Lst.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (Pop.IsOpen && Lst.SelectedItem is CardDatabase.CardInfo info) Commit(info);
                else CommitByText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Pop.IsOpen = false;
                SyncText();
                e.Handled = true;
            }
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Lst.SelectedItem is CardDatabase.CardInfo info) Commit(info);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Pop.IsOpen = false;
                Txt.Focus();
                e.Handled = true;
            }
        }

        private void OnItemClick(object sender, MouseButtonEventArgs e)
        {
            if (Lst.SelectedItem is CardDatabase.CardInfo info) Commit(info);
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            // 팝업 클릭으로 포커스가 이동한 경우는 무시
            if (Pop.IsOpen && Pop.Child != null && Pop.Child.IsKeyboardFocusWithin) return;
            if (!Pop.IsOpen) CommitByText();
        }

        private void CommitByText()
        {
            var text = (Txt.Text ?? "").Trim();
            if (text.Length == 0)
            {
                if (AllowEmpty) SetCard(0);
                else SyncText();
                return;
            }
            if (int.TryParse(text, out var direct) && direct > 0)
            {
                SetCard(direct);
                return;
            }

            var id = CardDatabase.Instance.FindIdByName(text);
            if (id > 0) SetCard(id);
            else SyncText();   // 해석 실패 → 원래 값으로 복구
        }

        private void Commit(CardDatabase.CardInfo info)
        {
            Pop.IsOpen = false;
            SetCard(info.Id);
        }

        private void SetCard(int id)
        {
            _suppress = true;
            CardId = id;
            _suppress = false;
            SyncText();
        }
    }
}

