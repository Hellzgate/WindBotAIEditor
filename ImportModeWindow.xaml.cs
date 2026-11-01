using System.Windows;

namespace WindBotAIEditor
{
    /// <summary>2026-11-02: 다른 규칙 JSON 을 불러올 때 **덮어쓰기 / 추가(병합)** 를 고르는 작은 창.</summary>
    public partial class ImportModeWindow : Window
    {
        public enum ImportMode { Cancel, Replace, Merge }

        public ImportMode Mode { get; private set; } = ImportMode.Cancel;

        public ImportModeWindow(string info)
        {
            InitializeComponent();
            if (TxtInfo != null) TxtInfo.Text = info;
        }

        private void OnReplace(object sender, RoutedEventArgs e) { Mode = ImportMode.Replace; DialogResult = true; }
        private void OnMerge(object sender, RoutedEventArgs e) { Mode = ImportMode.Merge; DialogResult = true; }
        private void OnCancel(object sender, RoutedEventArgs e) { Mode = ImportMode.Cancel; DialogResult = false; }
    }
}
