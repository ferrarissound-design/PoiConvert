using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PoiConvert.Core;
using PoiConvert.ViewModels;

namespace PoiConvert.Views;

public partial class MainWindow : Window, IUiServices
{
    private readonly MainViewModel _vm;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new SettingsStore(SettingsStore.DefaultPath), Dispatcher);
        _vm.AttachUi(this);
        DataContext = _vm;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.CheckFfmpegOnStartup();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingConfirmed)
            return;

        if (_vm.IsRunning)
        {
            e.Cancel = true;
            if (!Confirm("変換中です。中止して終了しますか？\n（作りかけのファイルは削除されます）"))
                return;
            IsEnabled = false;
            await _vm.ShutdownAsync();
            _closingConfirmed = true;
            Close();
            return;
        }
        _vm.SaveSettings();
    }

    // ───────── D&D

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            _vm.AddPaths(paths);
    }

    // ───────── 一覧操作

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        _vm.RemoveItems(QueueGrid.SelectedItems.Cast<QueueItem>());
    }

    private void QueueGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _vm.IsIdle)
        {
            _vm.RemoveItems(QueueGrid.SelectedItems.Cast<QueueItem>());
            e.Handled = true;
        }
    }

    private void LogBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        LogBox.ScrollToEnd();
    }

    // ───────── IUiServices

    public void ShowError(string message) =>
        MessageBox.Show(this, message, "PoiConvert", MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowInfo(string message) =>
        MessageBox.Show(this, message, "PoiConvert", MessageBoxButton.OK, MessageBoxImage.Information);

    public bool Confirm(string message) =>
        MessageBox.Show(this, message, "PoiConvert", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public string? ShowFfmpegMissing(string? currentPath)
    {
        var dlg = new FfmpegMissingWindow(currentPath) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.SelectedPath : null;
    }

    public string? PickFolder(string? initial)
    {
        var dlg = new OpenFolderDialog { Title = "出力先フォルダを選択" };
        if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
            dlg.InitialDirectory = initial;
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    public string[]? PickFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "変換するファイルを選択",
            Multiselect = true,
            Filter = "動画・音声ファイル|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp;" +
                     "*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.gif|すべてのファイル|*.*",
        };
        return dlg.ShowDialog(this) == true ? dlg.FileNames : null;
    }

    public void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // Windows のファイル名に " は使えないので、この組み立てで壊れることはない
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            ShowError($"フォルダを開けませんでした：{ex.Message}");
        }
    }
}
