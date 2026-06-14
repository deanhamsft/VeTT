using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YoutubeExplode;
using YoutubeExplode.Converter;
using Whisper.net;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Vett
{
    public partial class MainWindow : Window
    {
        private MediaPlayer _mediaPlayer = new MediaPlayer();
        private DispatcherTimer _timer = new DispatcherTimer();
        private string _currentFile = string.Empty;
        private static readonly string AudioOutputDir = "DownloadedAudio";
        private static readonly string TranscriptsDir = "Transcripts";
        private bool _isMediaLoaded = false;
        private IProgress<double> _downloadProgressReporter;
        private IProgress<int> _transcribeProgressReporter;

        // Dictionary: Full name / common variations → Blue Letter Bible short code
        private static readonly Dictionary<string, string> BibleBooks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    // Old Testament
    {"Genesis", "gen"}, {"Gen", "gen"}, {"Ge", "gen"},
    {"Exodus", "exo"}, {"Exod", "exo"}, {"Exo", "exo"},
    {"Leviticus", "lev"}, {"Lev", "lev"},
    {"Numbers", "num"}, {"Num", "num"},
    {"Deuteronomy", "deu"}, {"Deut", "deu"}, {"Dt", "deu"},
    {"Joshua", "jos"}, {"Josh", "jos"},
    {"Judges", "jdg"}, {"Jdg", "jdg"},
    {"Ruth", "rth"},
    {"1 Samuel", "1sa"}, {"1 Sam", "1sa"}, {"1Sa", "1sa"},
    {"2 Samuel", "2sa"}, {"2 Sam", "2sa"}, {"2Sa", "2sa"},
    {"1 Kings", "1ki"}, {"1 Ki", "1ki"},
    {"2 Kings", "2ki"}, {"2 Ki", "2ki"},
    {"1 Chronicles", "1ch"}, {"1 Chr", "1ch"},
    {"2 Chronicles", "2ch"}, {"2 Chr", "2ch"},
    {"Ezra", "ezr"},
    {"Nehemiah", "neh"},
    {"Esther", "est"},
    {"Job", "job"},
    {"Psalms", "psa"}, {"Psalm", "psa"}, {"Psa", "psa"},
    {"Proverbs", "pro"}, {"Prov", "pro"},
    {"Ecclesiastes", "ecc"}, {"Eccl", "ecc"},
    {"Song of Solomon", "sng"}, {"Song of Songs", "sng"}, {"Canticles", "sng"},
    {"Isaiah", "isa"}, {"Isa", "isa"},
    {"Jeremiah", "jer"}, {"Jer", "jer"},
    {"Lamentations", "lam"},
    {"Ezekiel", "eze"}, {"Ezek", "eze"},
    {"Daniel", "dan"}, {"Dan", "dan"},
    {"Hosea", "hos"},
    {"Joel", "joe"},
    {"Amos", "amo"},
    {"Obadiah", "oba"},
    {"Jonah", "jon"},
    {"Micah", "mic"},
    {"Nahum", "nah"},
    {"Habakkuk", "hab"},
    {"Zephaniah", "zep"},
    {"Haggai", "hag"},
    {"Zechariah", "zec"},
    {"Malachi", "mal"},

    // New Testament
    {"Matthew", "mat"}, {"Matt", "mat"},
    {"Mark", "mrk"},
    {"Luke", "luk"},
    {"John", "jhn"},
    {"Acts", "act"},
    {"Romans", "rom"}, {"Rom", "rom"},
    {"1 Corinthians", "1co"}, {"1 Cor", "1co"},
    {"2 Corinthians", "2co"}, {"2 Cor", "2co"},
    {"Galatians", "gal"},
    {"Ephesians", "eph"},
    {"Philippians", "php"},
    {"Colossians", "col"},
    {"1 Thessalonians", "1th"}, {"1 Thess", "1th"},
    {"2 Thessalonians", "2th"}, {"2 Thess", "2th"},
    {"1 Timothy", "1ti"}, {"1 Tim", "1ti"},
    {"2 Timothy", "2ti"}, {"2 Tim", "2ti"},
    {"Titus", "tit"},
    {"Philemon", "phm"},
    {"Hebrews", "heb"},
    {"James", "jas"},
    {"1 Peter", "1pe"}, {"1 Pet", "1pe"},
    {"2 Peter", "2pe"}, {"2 Pet", "2pe"},
    {"1 John", "1jn"}, {"2 John", "2jn"}, {"3 John", "3jn"},
    {"Jude", "jud"},
    {"Revelation", "rev"}
};

        public MainWindow()
        {
            InitializeComponent();

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += Timer_Tick;

            // Hook into MediaOpened event
            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

            // Progress reporters
            _downloadProgressReporter = new Progress<double>(p =>
                Dispatcher.Invoke(() => downloadProgress.Value = p * 100));

            _transcribeProgressReporter = new Progress<int>(p =>
                Dispatcher.Invoke(() => transcribeProgress.Value = p));

            // Set initial slider values safely
            startSlider.Value = 0;
            endSlider.Value = 300;

            Directory.CreateDirectory(AudioOutputDir);
            Directory.CreateDirectory(TranscriptsDir);

            LoadAudioFiles();
        }

        private void LoadAudioFiles()
        {
            lstFiles.Items.Clear();
            var dir = "DownloadedAudio";
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.mp3").OrderByDescending(f => File.GetCreationTime(f)))
                {
                    lstFiles.Items.Add(Path.GetFileName(file));
                }
            }
        }

        // ====================== SELECTION ======================
        private void lstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstFiles.SelectedItem is string filename)
            {
                var assemblyLocation = Assembly.GetExecutingAssembly().Location;
                var baseDir = Path.GetDirectoryName(assemblyLocation) ?? AppDomain.CurrentDomain.BaseDirectory;
                _currentFile = Path.Combine(baseDir, "DownloadedAudio", filename);
                txtNowPlaying.Text = $"Now Playing: {filename}";
                _isMediaLoaded = false;

                try
                {
                    _mediaPlayer.Open(new Uri("file:///" + _currentFile.Replace("\\", "/")));
                    txtNowPlaying.Text = $"Loading: {filename}";
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to load file:\n" + ex.Message);
                }
            }
        }

        // ====================== PLAYER ======================
        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFile) || !File.Exists(_currentFile))
            {
                MessageBox.Show("Please select an audio file first.", "No File Selected");
                return;
            }

            try
            {
                if (!_isMediaLoaded)
                {
                    // Force reload if needed
                    _mediaPlayer.Open(new Uri("file:///" + _currentFile.Replace("\\", "/")));
                    // Give it a tiny moment to load
                    System.Threading.Tasks.Task.Delay(300).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _mediaPlayer.Play();
                            _timer.Start();
                        });
                    });
                }
                else
                {
                    _mediaPlayer.Play();
                    _timer.Start();
                }

                txtNowPlaying.Text = $"▶ Playing: {Path.GetFileName(_currentFile)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Playback error:\n" + ex.Message);
            }
        }

        private void MediaPlayer_MediaFailed(object? sender, ExceptionEventArgs e)
        {
            _isMediaLoaded = false;
            MessageBox.Show($"Media failed to load:\n{e.ErrorException?.Message}", "Playback Error");
        }

        // Keep your existing Pause, Timer_Tick, etc.
        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Pause();
            _timer.Stop();
            txtNowPlaying.Text = $"⏸ Paused: {Path.GetFileName(_currentFile)}";
        }

        private void MediaPlayer_MediaOpened(object? sender, EventArgs e)
        {
            _isMediaLoaded = true;
            Dispatcher.Invoke(() =>
            {
                if (_mediaPlayer.NaturalDuration.HasTimeSpan)
                {
                    seekSlider.Maximum = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                    endSlider.Maximum = Math.Max(endSlider.Maximum, seekSlider.Maximum);
                }
                txtNowPlaying.Text = $"▶ Playing: {Path.GetFileName(_currentFile)}";
            });
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_mediaPlayer.NaturalDuration.HasTimeSpan)
            {
                seekSlider.Maximum = _mediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                seekSlider.Value = _mediaPlayer.Position.TotalSeconds;

                txtTime.Text = $"{_mediaPlayer.Position:mm\\:ss} / {_mediaPlayer.NaturalDuration.TimeSpan:mm\\:ss}";
            }
        }

        private void seekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (Math.Abs(seekSlider.Value - _mediaPlayer.Position.TotalSeconds) > 0.5)
                _mediaPlayer.Position = TimeSpan.FromSeconds(seekSlider.Value);
        }

        private void volumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
                _mediaPlayer.Volume = e.NewValue;
        }

        // ====================== TRIM SLIDERS (Safe) ======================
        private void startSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtStartTime != null)
            {
                TimeSpan ts = TimeSpan.FromSeconds(e.NewValue);
                txtStartTime.Text = ts.ToString(@"mm\:ss");
            }
        }

        private void endSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (txtEndTime != null)
            {
                TimeSpan ts = TimeSpan.FromSeconds(e.NewValue);
                txtEndTime.Text = ts.ToString(@"mm\:ss");
            }
        }

        // ====================== DOWNLOAD ======================
        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtYouTubeUrl.Text) ||
                txtYouTubeUrl.Text.Contains("...")) return;

            downloadProgress.Value = 0;

            try
            {
                var youtube = new YoutubeClient();
                string? customName = txtYouTubeUrl.Text;
                var video = await youtube.Videos.GetAsync(txtYouTubeUrl.Text);

                string safeTitle = Path.GetInvalidFileNameChars()
                          .Aggregate(video.Title, (current, c) => current.Replace(c, '_'));

                string outputPath = Path.Combine("DownloadedAudio", $"{safeTitle}.mp3");

                await youtube.Videos.DownloadAsync(
                    txtYouTubeUrl.Text,
                    outputPath,
                    o => o.SetFFmpegPath("ffmpeg").SetContainer("mp3"),
                    progress: _downloadProgressReporter
                );

                downloadProgress.Value = 100;
                MessageBox.Show("✅ Download completed!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadAudioFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Download failed:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ====================== TRIM ======================
        private void SaveTrim_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFile))
            {
                MessageBox.Show("Please select an audio file first.");
                return;
            }

            double startSec = startSlider.Value;
            double endSec = endSlider.Value;

            if (endSec <= startSec)
            {
                MessageBox.Show("End time must be after Start time.");
                return;
            }

            string output = Path.Combine("DownloadedAudio",
                Path.GetFileNameWithoutExtension(_currentFile) + "_trimmed.mp3");

            TrimAudio(_currentFile, output, startSec, endSec - startSec);

            MessageBox.Show($"✅ Trimmed file saved!\n{Path.GetFileName(output)}", "Success");
            LoadAudioFiles();
        }

        private void TrimAudio(string input, string output, double startSeconds, double durationSeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -ss {startSeconds} -t {durationSeconds} -i \"{input}\" -c copy \"{output}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();
        }

        // ====================== TRANSCRIBE ======================
        private async void TranscribeOriginal_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFile)) return;
            await TranscribeFileAsync(_currentFile, "Original");
        }

        private async void TranscribeTrimmed_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentFile)) return;

            string trimmed = Path.Combine("DownloadedAudio", "temp_trimmed_for_transcription.mp3");
            TrimAudio(_currentFile, trimmed, startSlider.Value, endSlider.Value - startSlider.Value);

            await TranscribeFileAsync(trimmed, "Trimmed");
        }



        private async Task TranscribeFileAsync(string inputFilePath, string type)
        {
            if (!File.Exists(inputFilePath))
            {
                MessageBox.Show("File not found.");
                return;
            }
            txtDetectedBooks.Text = "Beginning transform to .wav file for transcription...";
            transcribeProgress.Value = 0;
            string wavPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_whisper.wav");

            try
            {
                txtDetectedBooks.Text = "Beginning transcription... This may take a few minutes.";
                transcribeProgress.Value = 15;
                ConvertToWhisperWav(inputFilePath, wavPath);

                transcribeProgress.Value = 25;
                string modelPath = "Models/ggml-base.bin";
                if (!File.Exists(modelPath))
                {
                    MessageBox.Show("Whisper model not found.");
                    return;
                }

                using var whisperFactory = WhisperFactory.FromPath(modelPath);
                using var processor = whisperFactory.CreateBuilder().WithLanguage("auto").Build();

                transcribeProgress.Value = 35;

                var segments = new List<dynamic>();
                using var wavStream = File.OpenRead(wavPath);
                await foreach (var segment in processor.ProcessAsync(wavStream))
                {
                    segments.Add(segment);
                }

                string fullText = string.Join(" ", segments.Select(s => s.Text.Trim()));
                // string linkedText = AddBibleLinks(fullText);
                var detectedBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Extract unique books found
                foreach (var book in BibleBooks.Keys)
                {
                    if (Regex.IsMatch(fullText, $@"\b{Regex.Escape(book)}\b", RegexOptions.IgnoreCase))
                        detectedBooks.Add(book);
                }

                // Update UI with detected books
                Dispatcher.Invoke(() =>
                {
                    if (detectedBooks.Count > 0)
                    {
                        txtDetectedBooks.Text = string.Join("\n", detectedBooks.OrderBy(b => b));
                        txtDetectedBooks.Foreground = Brushes.DarkGreen;
                    }
                    else
                    {
                        txtDetectedBooks.Text = "(No Bible books detected in this transcription)";
                        txtDetectedBooks.Foreground = Brushes.Gray;
                    }
                });
                // === Generate Professional PDF with Clickable Bible Links ===
                string pdfPath = Path.Combine("Transcripts",
                    Path.GetFileNameWithoutExtension(inputFilePath) + $".pdf");

                Directory.CreateDirectory("Transcripts");
                QuestPDF.Settings.License = LicenseType.Community; // Ensure license is set for QuestPDF

                Directory.CreateDirectory("Transcripts");

                Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(40);
                        page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Arial"));

                        // ====================== HEADER (Only on First Page) ======================
                        page.Header().Column(headerCol =>
                        {
                            headerCol.Item().ShowOnce().Column(col =>   // ← Only appears on page 1
                            {
                                col.Item().Text(Path.GetFileName(inputFilePath))
                                    .FontSize(13).SemiBold().FontColor(QuestPDF.Helpers.Colors.Grey.Darken3);

                                col.Item().Text($"Generated on: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC")
                                    .FontSize(10).Italic().FontColor(QuestPDF.Helpers.Colors.Grey.Medium);

                                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(QuestPDF.Helpers.Colors.Grey.Lighten2);
                            });
                        });

                        // ====================== MAIN CONTENT ======================
                        page.Content().Column(col =>
                        {
                            col.Item().PaddingTop(10);

                            // Main transcription text with clickable Bible links
                            var sentences = SplitIntoSentences(fullText);

                            foreach (var sentence in sentences)
                            {
                                if (string.IsNullOrWhiteSpace(sentence)) continue;

                                col.Item().Text(text =>
                                {
                                    string remaining = sentence;
                                    int lastIndex = 0;

                                    foreach (var book in BibleBooks.OrderByDescending(b => b.Key.Length))
                                    {
                                        var matches = Regex.Matches(remaining, $@"\b({Regex.Escape(book.Key)})\b", RegexOptions.IgnoreCase);

                                        foreach (Match match in matches.Cast<Match>().OrderBy(m => m.Index))
                                        {
                                            if (match.Index > lastIndex)
                                                text.Span(remaining.Substring(lastIndex, match.Index - lastIndex));

                                            string url = $"https://www.blueletterbible.org/nkjv/{book.Value}/";
                                            text.Hyperlink(book.Key, url)
                                                .FontColor(QuestPDF.Helpers.Colors.Blue.Darken1)
                                                .Underline();

                                            lastIndex = match.Index + match.Length;
                                        }
                                    }

                                    if (lastIndex < remaining.Length)
                                        text.Span(remaining.Substring(lastIndex));
                                });
                            }
                        });

                        // Footer on all pages
                        page.Footer().AlignCenter().Text(x =>
                        {
                            x.Span("Page ");
                            x.CurrentPageNumber();
                            x.Span(" of ");
                            x.TotalPages();
                        });
                    });
                }).GeneratePdf(pdfPath);

                transcribeProgress.Value = 100;
                MessageBox.Show($"✅ PDF Transcription completed!\n\nSaved as:\n{Path.GetFileName(pdfPath)}",
                    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Transcription failed:\n{ex.Message}");
            }
            finally
            {
                if (File.Exists(wavPath)) try { File.Delete(wavPath); } catch { }
                await Task.Delay(800);
                transcribeProgress.Value = 0;
            }
        }

        // Helper: Split text into sentences
        private List<string> SplitIntoSentences(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            // Simple sentence splitting
            var delimiters = new char[] { '.', '!', '?' };
            var sentences = new List<string>();
            int start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (delimiters.Contains(text[i]) && i < text.Length - 1)
                {
                    string sentence = text.Substring(start, i - start + 1).Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                        sentences.Add(sentence);

                    start = i + 1;
                }
            }

            // Add the last sentence
            string last = text.Substring(start).Trim();
            if (!string.IsNullOrWhiteSpace(last))
                sentences.Add(last);

            return sentences;
        }

        // Helper: Convert any audio to Whisper-compatible WAV
        private void ConvertToWhisperWav(string inputPath, string outputWavPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{outputWavPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            if (process?.ExitCode != 0)
                throw new Exception("FFmpeg failed to convert audio to WAV.");
        }

        private async Task DownloadWhisperModelAsync(string destinationPath)
        {
            string url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

            transcribeProgress.Value = 0;   // Reuse the transcription progress bar or add a dedicated one

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? 0;
                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];  // 80 KB buffer
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        double progress = (double)totalRead / totalBytes * 100;
                        Dispatcher.Invoke(() => transcribeProgress.Value = progress);
                    }
                }

                await fileStream.FlushAsync();
                transcribeProgress.Value = 100;

                MessageBox.Show("✅ Whisper model downloaded successfully!", "Success");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Download failed:\n{ex.Message}\n\nTry downloading manually from:\nhttps://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
                    "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
        {
            string modelPath = "Models/ggml-base.bin";
            Directory.CreateDirectory("Models");

            if (File.Exists(modelPath))
            {
                if (MessageBox.Show("Model already exists. Download again?", "Confirm",
                    MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                    return;
            }

            await DownloadWhisperModelAsync(modelPath);
        }

        private void RefreshList_Click(object sender, RoutedEventArgs e) => LoadAudioFiles();
        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Directory.Exists("Transcripts"))
            {
                Process.Start("explorer.exe", "Transcripts");
            }
            else
            {
                MessageBox.Show("Transcripts folder not found.");
            }
        }

        // Placeholder helpers
        private void txtYouTubeUrl_GotFocus(object sender, RoutedEventArgs e)
        {
            if (txtYouTubeUrl.Text.Contains("..."))
                txtYouTubeUrl.Clear();
        }

        private void txtYouTubeUrl_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtYouTubeUrl.Text))
                txtYouTubeUrl.Text = "https://www.youtube.com/watch?v=...";
        }
    }
}