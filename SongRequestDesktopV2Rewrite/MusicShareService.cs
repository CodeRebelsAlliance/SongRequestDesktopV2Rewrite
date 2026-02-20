using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SongRequestDesktopV2Rewrite
{
    public class MusicShareService
    {
        private readonly HttpClient _httpClient;
        private string? _sessionId;
        private CancellationTokenSource? _cancellationTokenSource;
        private ShareStatus _status = ShareStatus.Idle;
        
        // Audio streaming
        private readonly ConcurrentQueue<AudioChunk> _audioBuffer = new();
        private int _sequenceNumber = 0;
        private const int AUDIO_CHUNK_SIZE = 4096; // Samples per chunk
        private const int BUFFER_SIZE_MS = 2000; // 2 seconds buffer for receiver
        
        // Events
        public event EventHandler<ShareStatus>? StatusChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<MusicShareMetadata>? MetadataReceived;
        public event EventHandler<AudioChunk>? AudioChunkReceived;
        public event EventHandler<int>? BufferLevelChanged;

        public ShareStatus Status
        {
            get => _status;
            private set
            {
                if (_status != value)
                {
                    _status = value;
                    StatusChanged?.Invoke(this, value);
                }
            }
        }

        public string? SessionId => _sessionId;

        public MusicShareService()
        {
            var cfg = ConfigService.Instance.Current;
            var baseAddress = cfg?.Address ?? "https://redstefan.software/songrequests";
            // Ensure trailing slash for proper relative URL resolution
            if (!baseAddress.EndsWith("/"))
                baseAddress += "/";

            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(baseAddress),
                Timeout = TimeSpan.FromSeconds(30)
            };


            // Add authorization header using the same bearer token as YouTube fetching
            if (!string.IsNullOrEmpty(cfg?.BearerToken))
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", cfg.BearerToken);
            }
        }

        #region Sharing Mode

        /// <summary>
        /// Start sharing session - generates session ID and begins streaming
        /// </summary>
        public async Task<string> StartSharingAsync()
        {
            if (Status != ShareStatus.Idle && Status != ShareStatus.Disconnected)
            {
                throw new InvalidOperationException("Already sharing or receiving");
            }

            Status = ShareStatus.Connecting;
            
            try
            {
                // Generate 6-digit session ID
                _sessionId = GenerateSessionId();
                
                // Register session with server
                var response = await _httpClient.PostAsJsonAsync("api/share/start", new
                {
                    sessionId = _sessionId
                });

                response.EnsureSuccessStatusCode();
                
                _cancellationTokenSource = new CancellationTokenSource();
                Status = ShareStatus.Connected;
                
                return _sessionId;
            }
            catch (Exception ex)
            {
                Status = ShareStatus.Error;
                ErrorOccurred?.Invoke(this, $"Failed to start sharing: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Send metadata update to server
        /// </summary>
        public async Task SendMetadataAsync(MusicShareMetadata metadata)
        {
            if (Status != ShareStatus.Connected && Status != ShareStatus.Streaming)
                return;

            try
            {
                metadata.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                var response = await _httpClient.PostAsJsonAsync($"api/share/{_sessionId}/metadata", metadata, 
                    _cancellationTokenSource?.Token ?? CancellationToken.None);
                
                response.EnsureSuccessStatusCode();
                
                if (Status == ShareStatus.Connected)
                    Status = ShareStatus.Streaming;
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send metadata: {ex.Message}");
            }
        }

        /// <summary>
        /// Stream audio chunk to server
        /// </summary>
        public async Task SendAudioChunkAsync(float[] samples, int sampleRate, int channels)
        {
            if (Status != ShareStatus.Streaming && Status != ShareStatus.Connected)
                return;

            try
            {
                // Log first chunk to verify what we're actually sending
                if (_sequenceNumber == 0 && samples.Length >= 4)
                {
                    System.Diagnostics.Debug.WriteLine($"📤 SENDING first chunk samples: [{samples[0].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {samples[1].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {samples[2].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}, {samples[3].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)}]");

                    // Check raw bytes
                    byte[] testBytes = new byte[16];
                    Buffer.BlockCopy(samples, 0, testBytes, 0, 16);
                    System.Diagnostics.Debug.WriteLine($"   Raw bytes: [{testBytes[0]:X2} {testBytes[1]:X2} {testBytes[2]:X2} {testBytes[3]:X2}] [{testBytes[4]:X2} {testBytes[5]:X2} {testBytes[6]:X2} {testBytes[7]:X2}]");
                }

                // Convert float samples to bytes
                var bytes = new byte[samples.Length * sizeof(float)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

                var chunk = new AudioChunk
                {
                    Data = Convert.ToBase64String(bytes), // Convert to base64 string
                    SampleRate = sampleRate,
                    Channels = channels,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SequenceNumber = _sequenceNumber++
                };

                var response = await _httpClient.PostAsJsonAsync($"api/share/{_sessionId}/audio", chunk,
                    _cancellationTokenSource?.Token ?? CancellationToken.None);

                response.EnsureSuccessStatusCode();
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to send audio chunk: {ex.Message}");
            }
        }

        /// <summary>
        /// Stop sharing session
        /// </summary>
        public async Task StopSharingAsync()
        {
            if (string.IsNullOrEmpty(_sessionId))
                return;

            try
            {
                _cancellationTokenSource?.Cancel();

                await _httpClient.PostAsync($"api/share/{_sessionId}/stop", null);
                
                _sessionId = null;
                _sequenceNumber = 0;
                Status = ShareStatus.Idle;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to stop sharing: {ex.Message}");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        #endregion

        #region Receiving Mode

        /// <summary>
        /// Start receiving from a session
        /// </summary>
        public async Task StartReceivingAsync(string sessionId)
        {
            if (Status != ShareStatus.Idle && Status != ShareStatus.Disconnected)
            {
                throw new InvalidOperationException("Already sharing or receiving");
            }

            if (string.IsNullOrEmpty(sessionId) || sessionId.Length != 6)
            {
                throw new ArgumentException("Session ID must be 6 digits");
            }

            Status = ShareStatus.Connecting;
            _sessionId = sessionId;
            
            try
            {
                // Verify session exists
                var response = await _httpClient.GetAsync($"api/share/{sessionId}/status");
                response.EnsureSuccessStatusCode();
                
                _cancellationTokenSource = new CancellationTokenSource();
                Status = ShareStatus.Connected;
                
                // Start polling for updates
                _ = Task.Run(() => PollMetadataAsync(_cancellationTokenSource.Token));
                _ = Task.Run(() => PollAudioAsync(_cancellationTokenSource.Token));
                
            }
            catch (Exception ex)
            {
                Status = ShareStatus.Error;
                ErrorOccurred?.Invoke(this, $"Failed to connect: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Poll for metadata updates
        /// </summary>
        private async Task PollMetadataAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var response = await _httpClient.GetAsync($"api/share/{_sessionId}/metadata", cancellationToken);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var metadata = await response.Content.ReadFromJsonAsync<MusicShareMetadata>(cancellationToken: cancellationToken);
                        if (metadata != null)
                        {
                            MetadataReceived?.Invoke(this, metadata);
                        }
                    }
                    
                    await Task.Delay(500, cancellationToken); // Poll every 500ms
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Metadata poll error: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Poll for audio chunks
        /// </summary>
        private async Task PollAudioAsync(CancellationToken cancellationToken)
        {
            int lastSequence = -1;
            int pollCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    pollCount++;
                    var response = await _httpClient.GetAsync(
                        $"api/share/{_sessionId}/audio?since={lastSequence}", 
                        cancellationToken);

                    if (response.IsSuccessStatusCode)
                    {
                        // Check for 204 No Content (no new chunks available)
                        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                        {
                            // No new chunks yet, continue polling
                            if (pollCount % 50 == 0) // Log every 5 seconds
                            {
                                System.Diagnostics.Debug.WriteLine($"🔄 Audio poll #{pollCount}: No new chunks (204), last seq={lastSequence}");
                            }
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }

                        var chunks = await response.Content.ReadFromJsonAsync<AudioChunk[]>(cancellationToken: cancellationToken);

                        if (chunks != null && chunks.Length > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"📥 Received {chunks.Length} audio chunks (seq {chunks[0].SequenceNumber}-{chunks[^1].SequenceNumber})");

                            foreach (var chunk in chunks)
                            {
                                _audioBuffer.Enqueue(chunk);
                                AudioChunkReceived?.Invoke(this, chunk);
                                lastSequence = Math.Max(lastSequence, chunk.SequenceNumber);
                            }

                            BufferLevelChanged?.Invoke(this, _audioBuffer.Count);

                            if (_audioBuffer.Count > 5) // Enough buffer
                            {
                                if (Status != ShareStatus.Streaming)
                                {
                                    System.Diagnostics.Debug.WriteLine($"📡 Status: Buffering → Streaming");
                                    Status = ShareStatus.Streaming;
                                }
                            }
                            else if (_audioBuffer.Count <= 2)
                            {
                                if (Status != ShareStatus.Buffering)
                                {
                                    System.Diagnostics.Debug.WriteLine($"⏳ Status: Streaming → Buffering (low buffer)");
                                    Status = ShareStatus.Buffering;
                                }
                            }
                        }
                        else
                        {
                            if (pollCount % 50 == 0)
                            {
                                System.Diagnostics.Debug.WriteLine($"🔄 Audio poll #{pollCount}: Empty response (200 OK), last seq={lastSequence}");
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"❌ Audio poll failed: {response.StatusCode}");
                    }

                    await Task.Delay(100, cancellationToken); // Poll frequently for low latency
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Audio poll error: {ex.Message}");
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        /// <summary>
        /// Get next audio chunk from buffer
        /// </summary>
        public bool TryDequeueAudioChunk(out AudioChunk? chunk)
        {
            return _audioBuffer.TryDequeue(out chunk);
        }

        /// <summary>
        /// Get current buffer level
        /// </summary>
        public int GetBufferLevel()
        {
            return _audioBuffer.Count;
        }

        /// <summary>
        /// Stop receiving
        /// </summary>
        public async Task StopReceivingAsync()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                
                _audioBuffer.Clear();
                _sessionId = null;
                Status = ShareStatus.Idle;
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            
            await Task.CompletedTask;
        }

        #endregion

        #region Helpers

        private string GenerateSessionId()
        {
            var random = new Random();
            return random.Next(100000, 999999).ToString();
        }

        public static string? BitmapSourceToBase64(BitmapSource? bitmap)
        {
            if (bitmap == null) return null;

            try
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using var stream = new MemoryStream();
                encoder.Save(stream);
                return Convert.ToBase64String(stream.ToArray());
            }
            catch
            {
                return null;
            }
        }

        public static BitmapSource? Base64ToBitmapSource(string? base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;

            try
            {
                var bytes = Convert.FromBase64String(base64);
                var bitmap = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
