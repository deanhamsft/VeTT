using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using YoutubeExplode;
using YoutubeExplode.Converter;
using OpenAI.Audio;
using Whisper.net;
using System.Collections.Generic;  // For List<WhisperResult>

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
        private double _currentDuration = 0;

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
                _currentFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "DownloadedAudio", filename);
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

        private void PreviewTrim_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Preview functionality can be added with NAudio waveform.\nFor now, use the player + trim sliders.");
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

            transcribeProgress.Value = 0;
            string wavPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + "_whisper.wav");

            try
            {
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
                using var processor = whisperFactory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();

                transcribeProgress.Value = 35;

                var segments = new List<dynamic>();
                await foreach (var segment in processor.ProcessAsync(File.OpenRead(wavPath)))
                {
                    segments.Add(segment);
                }

                // === Build nicely formatted Markdown with paragraphs ===
                string mdContent = $"# Transcription ({type})\n\n";
                mdContent += $"**File:** {Path.GetFileName(inputFilePath)}\n";
                mdContent += $"**Date:** {DateTime.UtcNow:yyyy-MM-dd HH:mm UTC}\n\n";

                // Join all text and create paragraphs
                string fullText = string.Join(" ", segments.Select(s => s.Text.Trim()));

                // Split into sentences and group into paragraphs (roughly 3-5 sentences per paragraph)
                var sentences = SplitIntoSentences(fullText);
                string formattedText = "";

                for (int i = 0; i < sentences.Count; i++)
                {
                    formattedText += sentences[i] + " ";

                    // Create new paragraph every 3-4 sentences
                    if ((i + 1) % 4 == 0 && i < sentences.Count - 1)
                    {
                        formattedText += "\n\n";
                    }
                }

                mdContent += formattedText.Trim() + "\n\n";

                // Optional: Keep timestamped version below
                //mdContent += "## Timestamped Segments\n\n";
                //foreach (var segment in segments)
                //{
                //    string start = TimeSpan.FromSeconds(segment.Start).ToString(@"mm\:ss");
                //    string end = TimeSpan.FromSeconds(segment.End).ToString(@"mm\:ss");
                //    mdContent += $"**[{start} → {end}]** {segment.Text.Trim()}\n\n";
                //}

                string mdPath = Path.Combine("Transcripts",
                    Path.GetFileNameWithoutExtension(inputFilePath) + $"_{type.ToLower()}_local.md");

                Directory.CreateDirectory("Transcripts");
                await File.WriteAllTextAsync(mdPath, mdContent);

                transcribeProgress.Value = 100;
                MessageBox.Show($"✅ Transcription completed!\nSaved as:\n{Path.GetFileName(mdPath)}");
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
            if (Directory.Exists("DownloadedAudio"))
                Process.Start("explorer.exe", "DownloadedAudio");
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