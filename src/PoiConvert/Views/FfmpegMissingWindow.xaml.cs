using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;

namespace PoiConvert.Views;

public partial class FfmpegMissingWindow : Window
{
    public string? SelectedPath { get; private set; }

    public FfmpegMissingWindow(string? currentPath)
    {
        InitializeComponent();
        PathBox.Text = currentPath ?? "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "ffmpeg.exe を選択",
            Filter = "ffmpeg.exe|ffmpeg.exe|実行ファイル|*.exe",
            CheckFileExists = true,
        };
        var current = PathBox.Text.Trim().Trim('"');
        try
        {
            var dir = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
        }
        catch (ArgumentException) { }

        if (dlg.ShowDialog(this) == true)
            PathBox.Text = dlg.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var p = PathBox.Text.Trim().Trim('"');
        if (p.Length == 0)
        {
            MessageBox.Show(this, "ffmpeg.exe の場所を指定してください。", "PoiConvert", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SelectedPath = p;
        DialogResult = true;
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ブラウザを開けませんでした：{ex.Message}\n{e.Uri}", "PoiConvert");
        }
        e.Handled = true;
    }
}
