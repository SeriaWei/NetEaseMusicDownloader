using Id3;
using Id3.Frames;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace NetEaseMusicDownloader
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/80.0.3987.149 Safari/537.36";
        private const string TempFile = "temp.buf";
        //private Regex _regex_Title = new Regex("<meta +property=\"og:title\" content=\"(.+)\" ?/>", RegexOptions.Compiled);
        //private Regex _regex_Author = new Regex("<meta +property=\"og:music:artist\" content=\"(.+)\" ?/>", RegexOptions.Compiled);
        private Regex _regex_Title = new Regex("data-res-name=\"(.+)\"", RegexOptions.Compiled);
        private Regex _regex_Author = new Regex("data-res-author=\"(.+)\"", RegexOptions.Compiled);

        private Regex _regex_Album = new Regex("<meta +property=\"og:music:album\" content=\"(.+)\" ?/>", RegexOptions.Compiled);
        private Regex _regex_AlbumImage = new Regex("<meta +property=\"og:image\" content=\"(.+)\" ?/>", RegexOptions.Compiled);
        private Regex _regex_SongUrl = new Regex(@"https?://music.163.com/song\?id=(\d+)", RegexOptions.Compiled);
        private Regex _regex_albumUrl = new Regex(@"https?://music.163.com/album\?id=(\d+)", RegexOptions.Compiled);
        private Regex _regex_playlistUrl = new Regex(@"https?://music.163.com/playlist\?id=(\d+)", RegexOptions.Compiled);
        private Regex _regex_SongId = new Regex(@"/song\?id=(\d+)", RegexOptions.Compiled);
        private char[] _authorSplitChars = new char[] { '/', '&', ';', ',' };
        private Task _downloadTask;
        private Task _getUrlTask;

        private Stack<string> pendingTasks = new Stack<string>();
        public MainWindow()
        {
            InitializeComponent();
            _downloadTask = StartDownload();
            _getUrlTask = StartGetUrl();
        }
        private Task StartGetUrl()
        {
            return Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(500);
                    this.Dispatcher.Invoke(() =>
                    {
                        string url = Clipboard.GetText();
                        if (!string.IsNullOrEmpty(url) && (url.StartsWith("https://music.163.com/") || url.StartsWith("http://music.163.com/")))
                        {
                            if (_regex_SongUrl.IsMatch(url))
                            {
                                pendingTasks.Push(url);
                            }
                            else if (_regex_albumUrl.IsMatch(url) || _regex_playlistUrl.IsMatch(url))
                            {
                                GetSongs(url);
                            }
                            Clipboard.Clear();
                        }
                    });
                }
            });
        }
        private Task StartDownload()
        {
            return Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    if (pendingTasks.Count == 0)
                    {
                        System.Threading.Thread.Sleep(1000);
                        continue;
                    }
                    string musicUrl = pendingTasks.Pop();
                    MusicTag musicTag = ParseMusicTag(musicUrl);
                    this.Dispatcher.Invoke(new Action<MusicTag>(DisplaySongInfo), musicTag);

                    string id = System.Web.HttpUtility.ParseQueryString(new Uri(musicUrl).Query)["id"];
                    WebRequest request = WebRequest.Create($"http://music.163.com/song/media/outer/url?id={id}.mp3");
                    request.Headers["User-Agent"] = UserAgent;
                    try
                    {
                        using (WebResponse webResponse = request.GetResponse())
                        {
                            if (webResponse.ContentType.IndexOf("text/html") >= 0)
                            {
                                this.Dispatcher.Invoke(new Action<string>(DisplayMessage), "未找到歌曲资源！");
                            }
                            else
                            {
                                this.Dispatcher.Invoke(new Action<long>(SetProgressBarMaximum), webResponse.ContentLength);
                                using (Stream responseStream = webResponse.GetResponseStream())
                                {
                                    using (FileStream fileStream = new FileStream(TempFile, FileMode.Create, FileAccess.Write))
                                    {
                                        byte[] buffer = new byte[10240];
                                        int length = 0;
                                        while ((length = responseStream.Read(buffer, 0, 10240)) > 0)
                                        {
                                            this.Dispatcher.BeginInvoke(new Action<long>(SetProgressBarValue), length);
                                            fileStream.Write(buffer, 0, length);
                                        }
                                    }
                                    string fileName = $"{musicTag.Author} - {musicTag.Title}.mp3";


                                    using (var mp3 = new Mp3(TempFile, Mp3Permissions.ReadWrite))
                                    {
                                        try
                                        {
                                            Id3Tag tag = mp3.GetTag(Id3TagFamily.Version2X);
                                            if (tag == null)
                                            {
                                                tag = new Id3Tag();
                                            }
                                            tag.Title.EncodingType = Id3TextEncoding.Unicode;
                                            tag.Artists.EncodingType = Id3TextEncoding.Unicode;
                                            tag.Album.EncodingType = Id3TextEncoding.Unicode;
                                            tag.Title.Value = musicTag.Title;
                                            tag.Artists.Value.Clear();
                                            foreach (var item in musicTag.Author.Split(_authorSplitChars, StringSplitOptions.RemoveEmptyEntries))
                                            {
                                                tag.Artists.Value.Add(item.Trim());
                                            }
                                            tag.Album.Value = musicTag.Album;
                                            if (musicTag.AlbumImg != null)
                                            {
                                                PictureFrame pictureFrame = new PictureFrame();
                                                pictureFrame.PictureData = musicTag.AlbumImg;
                                                pictureFrame.PictureType = PictureType.FrontCover;
                                                tag.Pictures.Add(pictureFrame);
                                            }
                                            mp3.WriteTag(tag, Id3Version.V23, WriteConflictAction.Replace);
                                        }
                                        catch (Exception ex)
                                        {
                                            this.Dispatcher.Invoke(new Action<string>(DisplayMessage), ex.Message);
                                        }
                                    }

                                    fileName = fileName.Replace("\0", "");
                                    foreach (var item in System.IO.Path.GetInvalidFileNameChars())
                                    {
                                        fileName = fileName.Replace(item, '-');
                                    }
                                    string dir = System.IO.Path.Combine("Music", musicTag.Author.Split(_authorSplitChars, StringSplitOptions.RemoveEmptyEntries)[0].Trim());
                                    if (!Directory.Exists(dir))
                                    {
                                        Directory.CreateDirectory(dir);
                                    }

                                    string target = System.IO.Path.Combine(dir, fileName);
                                    if (File.Exists(target))
                                    {
                                        File.Delete(target);
                                    }
                                    File.Move(TempFile, target);
                                    this.Dispatcher.Invoke(() =>
                                    {
                                        ProgressBar_Download.Value = 0;
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.Dispatcher.Invoke(new Action(Reset));
                        this.Dispatcher.Invoke(new Action<string>(DisplayMessage), ex.Message);
                    }
                }
            });
        }
        private void GetSongs(string url)
        {
            this.Dispatcher.Invoke(new Action<string>(DisplayMessage), "正在获取 播放列表/专辑 的歌曲信息...");
            using (WebClient webClient = new WebClient())
            {
                webClient.Headers["User-Agent"] = UserAgent;
                string html = webClient.DownloadString(url);
                HashSet<string> ids = new HashSet<string>();
                _regex_SongId.Replace(html, evaluator =>
                {
                    string id = evaluator.Groups[1].Value;
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }
                    return string.Empty;
                });
                this.Dispatcher.Invoke(new Action<string>(DisplayMessage), $"解析到 {ids.Count} 首歌曲。");
                foreach (var id in ids)
                {
                    pendingTasks.Push($"https://music.163.com/song?id={id}");
                }
            }
        }
        private MusicTag ParseMusicTag(string url)
        {
            this.Dispatcher.Invoke(new Action<string>(SetUrl), url);
            this.Dispatcher.Invoke(new Action<string>(DisplayMessage), "正在解析歌曲信息...");
            using (WebClient webClient = new WebClient())
            {
                webClient.Headers["User-Agent"] = UserAgent;
                string html = webClient.DownloadString(url);
                MusicTag tag = new MusicTag();
                _regex_Title.Replace(html, evaluator =>
                {
                    if (tag.Title == null)
                    {
                        tag.Title = HttpUtility.HtmlDecode(evaluator.Groups[1].Value.Trim());
                    }
                    return string.Empty;
                });
                _regex_Author.Replace(html, evaluator =>
                {
                    if (tag.Author == null)
                    {
                        tag.Author = HttpUtility.HtmlDecode(evaluator.Groups[1].Value.Trim());
                    }
                    return string.Empty;
                });
                _regex_Album.Replace(html, evaluator =>
                {
                    if (tag.Album == null)
                    {
                        tag.Album = HttpUtility.HtmlDecode(evaluator.Groups[1].Value.Trim());
                    }
                    return string.Empty;
                });
                _regex_AlbumImage.Replace(html, evaluator =>
                {
                    if (tag.AlbumImg == null)
                    {
                        tag.AlbumImg = webClient.DownloadData(HttpUtility.HtmlDecode(evaluator.Groups[1].Value.Trim() + "?param=130y130"));
                    }
                    return string.Empty;
                });
                return tag;
            }
        }
        private void SetProgressBarMaximum(long value)
        {
            ProgressBar_Download.Maximum = value;
        }
        private void SetProgressBarValue(long value)
        {
            ProgressBar_Download.Value += value;
        }
        private void Reset()
        {
            BitmapImage bitImage = new BitmapImage();
            bitImage.BeginInit();
            bitImage.UriSource = new Uri("/07.jpg", UriKind.Relative);
            bitImage.EndInit();
            AlbumFrontCovert.Source = bitImage;
            Label_Info.Content = "等待中...";
            ProgressBar_Download.Value = 0;
        }
        private void DisplaySongInfo(MusicTag tag)
        {
            Label_Info.Content = "歌曲：" + tag.Author + " - " + tag.Title + "\r\n专辑：" + tag.Album;
            if (tag.AlbumImg != null)
            {
                BitmapImage bitImage = new BitmapImage();
                bitImage.BeginInit();
                bitImage.StreamSource = new MemoryStream(tag.AlbumImg);
                bitImage.EndInit();
                AlbumFrontCovert.Source = bitImage;
            }
        }
        private void DisplayMessage(string message)
        {
            Label_Info.Content = message;
        }
        private void SetUrl(string url)
        {
            TextBox_Url.Text = url;
            ProgressBar_Download.Value = 0;
        }
    }
}
