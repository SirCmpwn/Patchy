using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Shell;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Common;

namespace Patchy
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private System.Windows.Forms.NotifyIcon NotifyIcon { get; set; }
        private PeriodicTorrent BalloonTorrent { get; set; }
        private string IgnoredClipboardValue { get; set; }
        private bool AllowClose { get; set; } // TODO: Refactor this away

        public MainWindow()
        {
            InitializeComponent();
            InitializeNotifyIcon();
            
            Client = new ClientManager();
            Initialize();
            torrentGrid.ItemsSource = Client.Torrents;
        }

        private void InitializeNotifyIcon()
        {
            NotifyIcon = new System.Windows.Forms.NotifyIcon 
            {
                Text = "Patchy",
                Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/Patchy;component/Images/patchy.ico" )).Stream),
                Visible = true
            };
            NotifyIcon.DoubleClick += NotifyIconClick;
            NotifyIcon.BalloonTipClicked += NotifyIconBalloonTipClicked;
            var menu = new System.Windows.Forms.ContextMenu();
            menu.MenuItems.Add("Add Torrent", (s, e) => ExecuteNew(null, null));
            menu.MenuItems.Add("Exit", (s, e) =>
            {
                NotifyIcon.Dispose();
                AllowClose = true;
                Close();
            });
            NotifyIcon.ContextMenu = menu;
        }

        private void UpdateNotifyIcon()
        {
            if (Client.Torrents.Count == 0)
            {
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                NotifyIcon.Text = "Patchy";
            }
            else if (Client.Torrents.Any(t => !t.Complete))
            {
                int progress = (int)(Client.Torrents.Where(t => !t.Complete).Select(t => t.Progress)
                        .Aggregate((t, n) => t + n) / Client.Torrents.Count(t => !t.Complete));
                NotifyIcon.Text = string.Format(
                    "Patchy - {0} torrent{3}, {1} downloading at {2}%",
                    Client.Torrents.Count,
                    Client.Torrents.Count(t => !t.Complete),
                    progress,
                    Client.Torrents.Count == 1 ? "" : "s");
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
                TaskbarItemInfo.ProgressValue = progress / 100.0;
            }
            else
            {
                NotifyIcon.Text = string.Format(
                    "Patchy - Seeding {0} torrent{1}",
                    Client.Torrents.Count,
                    Client.Torrents.Count == 1 ? "" : "s");
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
            }
        }

        private void NotifyIconClick(object sender, EventArgs e)
        {
            if (Visibility == Visibility.Hidden)
            {
                Visibility = Visibility.Visible;
                Focus();
            }
            else
                Visibility = Visibility.Hidden;
        }

        private void NotifyIconBalloonTipClicked(object sender, EventArgs e)
        {
            Process.Start("explorer", BalloonTorrent.Torrent.SavePath);
        }

        private void WindowClosing(object sender, CancelEventArgs e)
        {
            if (AllowClose)
                return;
            e.Cancel = true;
            Visibility = Visibility.Hidden;
        }

        private void WindowClosed(object sender, EventArgs e)
        {
            var resume = new BEncodedDictionary();
            foreach (var torrent in Client.Torrents)
            {
                torrent.Torrent.Stop();
                while (torrent.Torrent.State != TorrentState.Stopped && torrent.Torrent.State != TorrentState.Error)
                    Thread.Sleep(100);
                // TODO: Notify users on error? The application is shutting down here, it wouldn't be particualry
                // easy to get information to the user
                resume.Add(torrent.Torrent.InfoHash.ToHex(), torrent.Torrent.SaveFastResume().Encode());
            }
            File.WriteAllBytes(SettingsManager.FastResumePath, resume.Encode());
            Client.Shutdown();
        }

        private void TorrentGridSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (torrentGrid.SelectedItems.Count != 1)
            {
                lowerFill.Visibility = Visibility.Visible;
                lowerGrid.DataContext = null;
            }
            else
            {
                lowerFill.Visibility = Visibility.Collapsed;
                lowerGrid.DataContext = torrentGrid.SelectedItem;
            }
        }

        private void QuickAddClicked(object sender, RoutedEventArgs e)
        {
            IgnoredClipboardValue = Clipboard.GetText();
            CheckMagnetLinks();

            var link = new MagnetLink(IgnoredClipboardValue);
            var name = HttpUtility.HtmlDecode(HttpUtility.UrlDecode(link.Name));

            var path = Path.Combine(SettingsManager.DefaultDownloadLocation, 
                ClientManager.CleanFileName(name));

            AddTorrent(link, path);
        }

        private void QuickAddDismissClicked(object sender, RoutedEventArgs e)
        {
            IgnoredClipboardValue = Clipboard.GetText();
            CheckMagnetLinks();
        }

        private void QuickAddAdvancedClciked(object sender, RoutedEventArgs e)
        {
            IgnoredClipboardValue = Clipboard.GetText();
            CheckMagnetLinks();
            ExecuteNew(sender, null);
        }

        private void FilePriorityBoxChanged(object sender, SelectionChangedEventArgs e)
        {
            var source = sender as ComboBox;
            var file = source.Tag as PeriodicFile;
            file.Priority = (Priority)source.SelectedIndex;
        }

        private void FileListGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            foreach (PeriodicFile item in fileListGrid.SelectedItems)
            {
                var extension = Path.GetExtension(item.File.Path);

                // TODO: Expand list of naughty file extensions

                bool open = true;
                if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    open = MessageBox.Show("This file could be dangerous. Are you sure you want to open it?",
                        "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
                }
                if (open)
                {
                    var torrent = torrentGrid.SelectedItem as PeriodicTorrent;
                    Process.Start(Path.Combine(torrent.Torrent.SavePath, item.File.Path));
                }
            }
        }

        private void TorrentGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            foreach (PeriodicTorrent item in torrentGrid.SelectedItems)
                Process.Start("explorer", item.Torrent.SavePath);
        }

        private void torrentGridContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = torrentGrid.SelectedItems.Count == 0;
        }

        private void torrentGridOpenFolder(object sender, RoutedEventArgs e)
        {
            foreach (PeriodicTorrent torrent in torrentGrid.SelectedItems)
                Process.Start("explorer", torrent.Torrent.SavePath);
        }

        private void torrentGridRemoveTorrent(object sender, RoutedEventArgs e)
        {
            foreach (PeriodicTorrent torrent in torrentGrid.SelectedItems)
            {
                if (torrent.State == TorrentState.Downloading)
                {
                    if (MessageBox.Show(torrent.Name + " is not complete. Are you sure you want to remove it?",
                        "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                        continue;
                }
                Client.RemoveTorrent(torrent);
            }
        }

        private void torrentGridRemoveTorrentAndData(object sender, RoutedEventArgs e)
        {
            foreach (PeriodicTorrent torrent in torrentGrid.SelectedItems)
            {
                if (torrent.State == TorrentState.Downloading)
                {
                    if (MessageBox.Show(torrent.Name + " is not complete. Are you sure you want to remove it?",
                        "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.No)
                        continue;
                }
                Client.RemoveTorrentAndFiles(torrent);
            }
        }
    }
}
