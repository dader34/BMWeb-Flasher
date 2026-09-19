using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace BmwebFlasher
{
    /// <summary>
    /// First-boot setup. Lets the user download the SGBD data, point at an
    /// existing EDIABAS folder, or skip. Closes with <see cref="Result"/> set to
    /// the ECU folder to use, or null when skipped/cancelled.
    /// </summary>
    public partial class SetupWindow : Window
    {
        /// <summary>The chosen ECU folder, or null if the user skipped.</summary>
        public string Result { get; private set; }

        public SetupWindow()
        {
            InitializeComponent();
        }

        private void ShowDownloadView()
        {
            ChoicePanel.IsVisible = false;
            DownloadPanel.IsVisible = true;
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            ShowDownloadView();

            var progress = new Progress<int>(p =>
            {
                if (p < 0)
                {
                    DownloadStatus.Text = "Downloading…";
                    DownloadProgress.IsIndeterminate = true;
                    DownloadPercent.Text = string.Empty;
                }
                else
                {
                    DownloadProgress.IsIndeterminate = false;
                    DownloadProgress.Value = p;
                    DownloadPercent.Text = p + "%";
                    if (p >= 100)
                        DownloadStatus.Text = "Extracting files…";
                    else
                        DownloadStatus.Text = "Downloading E46 data set (~60 MB)…";
                }
            });

            try
            {
                string dir = await EcuBootstrap.DownloadAndExtractAsync(progress);
                Result = dir;
                Close();
            }
            catch (Exception ex)
            {
                // Surface the failure and return to the choice view so the user
                // can retry or pick a folder instead.
                DownloadPanel.IsVisible = false;
                ChoicePanel.IsVisible = true;
                await ShowError("Download failed", Unwrap(ex));
            }
        }

        private async void Folder_Click(object sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose your EDIABAS ECU folder",
                AllowMultiple = false,
            });

            var folder = folders?.FirstOrDefault();
            string path = folder?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
                return;

            if (!EcuBootstrap.HasEcuData(path))
            {
                await ShowError("Folder missing SGBDs",
                    "That folder does not contain the expected SGBD files " +
                    "(ms450ds0.prg / D_MOTOR.grp). Pick the EDIABAS 'Ecu' folder, " +
                    "or download the data set instead.");
                return;
            }

            Result = path;
            Close();
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            Close();
        }

        private async Task ShowError(string title, string message)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false
            };
            var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true };
            ok.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Children = { ok },
                    },
                },
            };
            await dialog.ShowDialog(this);
        }

        private static string Unwrap(Exception ex)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (Exception e = ex; e != null; e = e.InnerException)
                parts.Add(e.Message);
            return string.Join("\n\n", parts);
        }
    }
}
