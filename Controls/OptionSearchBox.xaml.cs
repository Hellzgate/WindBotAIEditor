using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WindBotAIEditor.Models;

namespace WindBotAIEditor.Controls
{
    /// <summary>
    /// 코드 + 이름 목록(종족/테마 등)을 타이핑으로 검색해 고르는 입력 상자.
    /// 선택 결과는 SelectedCode (int) 로 바인딩된다. 0 은 "전체" 항목.
    /// </summary>
    public partial class OptionSearchBox : UserControl
    {
        public static readonly DependencyProperty SelectedCodeProperty = DependencyProperty.Register(
            nameof(SelectedCode), typeof(int), typeof(OptionSearchBox),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedCodeChanged));

        public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
            nameof(Options), typeof(IEnumerable), typeof(OptionSearchBox),
            new PropertyMetadata(null, OnOptionsChanged));

        public static readonly DependencyProperty AllowZeroProperty = DependencyProperty.Register(
            nameof(AllowZero), typeof(bool), typeof(OptionSearchBox), new PropertyMetadata(true));

        public static readonly DependencyProperty ZeroLabelProperty = DependencyProperty.Register(
            nameof(ZeroLabel), typeof(string), typeof(OptionSearchBox), new PropertyMetadata("전체"));

        public int SelectedCode
        {
            get => (int)GetValue(SelectedCodeProperty);
            set => SetValue(SelectedCodeProperty, value);
        }

        /// <summary>ICodeOption 목록 (Code/Name)</summary>
        public IEnumerable? Options
        {
            get => (IEnumerable?)GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        public bool AllowZero
        {
            get => (bool)GetValue(AllowZeroProperty);
            set => SetValue(AllowZeroProperty, value);
        }

        public string ZeroLabel
        {
            get => (string)GetValue(ZeroLabelProperty);
            set => SetValue(ZeroLabelProperty, value);
        }

        private bool _suppress;
        private List<ICodeOption> _results = new();

        public OptionSearchBox()
        {
            InitializeComponent();
            Loaded += (_, __) => SyncText();
        }

        private static void OnSelectedCodeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OptionSearchBox box && !box._suppress) box.SyncText();
        }

        private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is OptionSearchBox box && !box._suppress) box.SyncText();
        }

        private IEnumerable<ICodeOption> AllOptions() =>
            (Options ?? Array.Empty<object>()).OfType<ICodeOption>();

        private void SyncText()
        {
            var code = SelectedCode;
            var name = AllOptions().FirstOrDefault(o => o.Code == code)?.Name ?? "";
            _suppress = true;
            Txt.Text = code <= 0 ? ZeroLabel : (name.Length > 0 ? name : code.ToString());
            Txt.SetResourceReference(TextBox.ForegroundProperty, code <= 0 ? "HintFg" : "TextFg");
            _suppress = false;
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (SelectedCode <= 0) Txt.Text = "";
            Txt.SetResourceReference(TextBox.ForegroundProperty, "TextFg");
            Txt.SelectAll();
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            var keyword = (Txt.Text ?? "").Trim();
            if (keyword.Length == 0)
            {
                Pop.IsOpen = false;
                return;
            }

            _results = AllOptions()
                .Where(o => o.Code > 0 && o.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(30)
                .ToList();

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
                if (Pop.IsOpen && Lst.SelectedItem is ICodeOption item) Commit(item);
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
                if (Lst.SelectedItem is ICodeOption item) Commit(item);
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
            if (Lst.SelectedItem is ICodeOption item) Commit(item);
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            if (Pop.IsOpen && Pop.Child != null && Pop.Child.IsKeyboardFocusWithin) return;
            if (!Pop.IsOpen) CommitByText();
        }

        private void CommitByText()
        {
            var text = (Txt.Text ?? "").Trim();
            if (text.Length == 0)
            {
                if (AllowZero) SetCode(0);
                else SyncText();
                return;
            }

            // 완전 일치 → 부분 일치 순서로 찾는다
            var exact = AllOptions().FirstOrDefault(o => o.Code > 0 && string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase));
            if (exact != null) { SetCode(exact.Code); return; }

            var partial = AllOptions().FirstOrDefault(o => o.Code > 0 && o.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            if (partial != null) { SetCode(partial.Code); return; }

            SyncText();   // 해석 실패 → 원래 값으로 복구
        }

        private void Commit(ICodeOption item)
        {
            Pop.IsOpen = false;
            SetCode(item.Code);
        }

        private void SetCode(int code)
        {
            _suppress = true;
            SelectedCode = code;
            _suppress = false;
            SyncText();
        }
    }
}
