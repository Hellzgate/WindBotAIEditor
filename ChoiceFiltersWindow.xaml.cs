using System.Windows;
using System.Windows.Controls;
using WindBotAIEditor.Models;

namespace WindBotAIEditor;

/// <summary>
/// 2026-11-07: ② 선택 규칙의 **필터 6종**(내성/무효/뒷면/앞면/효과 사용/상대만)을 한 창에서 켜고 끈다.
///
/// 왜: 예전에는 이 6개를 표의 열(체크박스 6개)로 두어 ② 표가 창보다 넓어졌다(가로 스크롤 + 헤더 잘림).
/// 이제 표에는 **필터 요약 한 열**만 있고(예: `내성 무효`), 이 창에서 실제 값을 바꾼다.
/// 값은 바인딩(TwoWay)으로 **즉시** 모델에 반영되고, 표의 요약은 `FilterSummary` 알림으로 갱신된다.
/// </summary>
public partial class ChoiceFiltersWindow : Window
{
    private readonly ChoiceRule _rule;

    public ChoiceFiltersWindow(ChoiceRule rule)
    {
        InitializeComponent();
        _rule = rule;
        DataContext = rule;

        TxtHeader.Text = Describe(rule);

        ChkResistant.DataContext = rule;
        ChkResistant.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(ChoiceRule.ResistantModeName))
            {
                Mode = System.Windows.Data.BindingMode.OneWay,
                Converter = new ResistantOnConverter()
            });

        CmbResistant.ItemsSource = MainWindow.ResistantModes;
        CmbResistant.SelectedItem = rule.ResistantModeName;
        CmbResistant.SelectionChanged += (_, __) =>
        {
            if (CmbResistant.SelectedItem is ResistantModeOption opt)
            {
                rule.ResistantModeName = opt;
                rule.NotifyFilters();
            }
        };

        BindCheck(ChkDisabled, nameof(ChoiceRule.SkipDisabled));
        BindCheck(ChkFaceDown, nameof(ChoiceRule.SkipFaceDown));
        BindCheck(ChkFaceUp, nameof(ChoiceRule.OnlyFaceUp));
        BindCheck(ChkEffectUsed, nameof(ChoiceRule.SkipEffectUsed));
        BindCheck(ChkOnlyEnemy, nameof(ChoiceRule.OnlyEnemy));

        foreach (var chk in new[] { ChkDisabled, ChkFaceDown, ChkFaceUp, ChkEffectUsed, ChkOnlyEnemy })
            chk.Checked += (_, __) => UpdateSummary();
        foreach (var chk in new[] { ChkDisabled, ChkFaceDown, ChkFaceUp, ChkEffectUsed, ChkOnlyEnemy })
            chk.Unchecked += (_, __) => UpdateSummary();

        ChkResistant.Checked += (_, __) => OnResistantToggle(true);
        ChkResistant.Unchecked += (_, __) => OnResistantToggle(false);

        UpdateSummary();
    }

    private void BindCheck(CheckBox box, string path)
    {
        box.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(path)
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
    }

    private void OnResistantToggle(bool on)
    {
        // 체크 = "항상 제외(on)", 해제 = "안 함(off)" — 3번째 상태(전역 따름)는 콤보로 고른다
        _rule.ResistantModeName = ResistantModeOption.Find(on ? "on" : "off");
        CmbResistant.SelectedItem = _rule.ResistantModeName;
        _rule.NotifyFilters();
        UpdateSummary();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _rule.ResistantModeName = ResistantModeOption.Find("auto");
        CmbResistant.SelectedItem = _rule.ResistantModeName;
        _rule.SkipDisabled = false;
        _rule.SkipFaceDown = false;
        _rule.OnlyFaceUp = false;
        _rule.SkipEffectUsed = false;
        _rule.OnlyEnemy = false;
        _rule.NotifyFilters();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        TxtSummary.Text = "지금 적용: " + _rule.FilterSummary;
    }

    /// <summary>--uicheck: 지정 속성에 바인딩된 체크박스가 있는지(창을 띄우지 않고 확인).</summary>
    public bool HasCheckBoxBinding(string path)
    {
        foreach (var chk in new[] { ChkDisabled, ChkFaceDown, ChkFaceUp, ChkEffectUsed, ChkOnlyEnemy })
        {
            var b = System.Windows.Data.BindingOperations.GetBinding(
                chk, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty);
            if (b?.Path?.Path == path) return true;
        }
        return false;
    }

    private static string Describe(ChoiceRule rule)
    {
        var on = string.IsNullOrWhiteSpace(rule.On) ? "?" : rule.On;
        var card = rule.WhenCard > 0 ? " · 발동카드 " + rule.WhenCard : "";
        return "필터 — 우선 " + rule.Priority + " · " + on + card;
    }

    /// <summary>내성 모드 → 체크박스 표시 (auto/off = 해제, on = 체크).</summary>
    private sealed class ResistantOnConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => value is ResistantModeOption opt && opt.Id == "on";

        public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }
}
