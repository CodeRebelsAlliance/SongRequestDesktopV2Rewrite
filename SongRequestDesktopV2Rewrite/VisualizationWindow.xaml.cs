using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SongRequestDesktopV2Rewrite
{
    public partial class VisualizationWindow : Window
    {
        private DispatcherTimer _renderTimer;
        private VisualizationType _currentType = VisualizationType.Spectrum;
        private float[] _audioSamples = Array.Empty<float>();
        private readonly object _sampleLock = new object();
        
        // FFT data
        private float[] _fftData = new float[512];
        private readonly int _fftSize = 512;
        
        // Smoothing for visualizations
        private float[] _smoothedSpectrum = new float[64];
        private const float SmoothingFactor = 0.7f;
        
        // Particles system
        private List<Particle> _particles = new List<Particle>();
        private Random _random = new Random();

        private enum VisualizationType
        {
            Spectrum,
            Waveform,
            Circular,
            VUMeter,
            Particles
        }

        private class Particle
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double VelocityX { get; set; }
            public double VelocityY { get; set; }
            public double Size { get; set; }
            public double Life { get; set; }
            public Color Color { get; set; }
        }

        public VisualizationWindow()
        {
            InitializeComponent();

            // Setup render timer (60 FPS)
            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
            };
            _renderTimer.Tick += RenderTimer_Tick;
            _renderTimer.Start();

            Loaded += VisualizationWindow_Loaded;
        }

        private void VisualizationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Initial render
            Render();
        }

        /// <summary>
        /// Update audio samples from the music player
        /// </summary>
        public void UpdateAudioSamples(float[] samples)
        {
            lock (_sampleLock)
            {
                // Store a copy of the samples
                if (samples != null && samples.Length > 0)
                {
                    _audioSamples = new float[samples.Length];
                    Array.Copy(samples, _audioSamples, samples.Length);
                }
            }
        }

        private void RenderTimer_Tick(object? sender, EventArgs e)
        {
            Render();
        }

        private void Render()
        {
            VisualizationCanvas.Children.Clear();

            lock (_sampleLock)
            {
                if (_audioSamples == null || _audioSamples.Length == 0)
                {
                    // Show "No audio" message
                    var text = new TextBlock
                    {
                        Text = "♪ No audio playing ♪",
                        Foreground = Brushes.Gray,
                        FontSize = 24,
                        FontWeight = FontWeights.Bold
                    };
                    Canvas.SetLeft(text, (VisualizationCanvas.ActualWidth - 200) / 2);
                    Canvas.SetTop(text, VisualizationCanvas.ActualHeight / 2 - 20);
                    VisualizationCanvas.Children.Add(text);
                    return;
                }

                switch (_currentType)
                {
                    case VisualizationType.Spectrum:
                        RenderSpectrum();
                        break;
                    case VisualizationType.Waveform:
                        RenderWaveform();
                        break;
                    case VisualizationType.Circular:
                        RenderCircular();
                        break;
                    case VisualizationType.VUMeter:
                        RenderVUMeter();
                        break;
                    case VisualizationType.Particles:
                        RenderParticles();
                        break;
                }
            }
        }

        #region Visualization Implementations

        private void RenderSpectrum()
        {
            // Perform FFT and render as bars
            PerformFFT();

            double width = VisualizationCanvas.ActualWidth;
            double height = VisualizationCanvas.ActualHeight;
            
            int barCount = 64;
            double barWidth = width / barCount;
            double spacing = 2;

            for (int i = 0; i < barCount; i++)
            {
                float magnitude = _smoothedSpectrum[i];
                double barHeight = Math.Min(magnitude * height * 2, height);

                var rect = new Rectangle
                {
                    Width = barWidth - spacing,
                    Height = barHeight,
                    Fill = GetSpectrumBrush(magnitude)
                };

                Canvas.SetLeft(rect, i * barWidth);
                Canvas.SetBottom(rect, 0);
                VisualizationCanvas.Children.Add(rect);
            }
        }

        private void RenderWaveform()
        {
            double width = VisualizationCanvas.ActualWidth;
            double height = VisualizationCanvas.ActualHeight;
            double centerY = height / 2;

            var polyline = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromRgb(91, 141, 239)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };

            int sampleStep = Math.Max(1, _audioSamples.Length / (int)width);
            
            for (int x = 0; x < width && x * sampleStep < _audioSamples.Length; x++)
            {
                int sampleIndex = x * sampleStep;
                float sample = _audioSamples[sampleIndex];
                double y = centerY + (sample * height * 0.4);
                polyline.Points.Add(new Point(x, y));
            }

            VisualizationCanvas.Children.Add(polyline);

            // Add center line
            var centerLine = new Line
            {
                X1 = 0,
                Y1 = centerY,
                X2 = width,
                Y2 = centerY,
                Stroke = Brushes.DarkGray,
                StrokeThickness = 1,
                Opacity = 0.3
            };
            VisualizationCanvas.Children.Add(centerLine);
        }

        private void RenderCircular()
        {
            PerformFFT();

            double width = VisualizationCanvas.ActualWidth;
            double height = VisualizationCanvas.ActualHeight;
            double centerX = width / 2;
            double centerY = height / 2;
            double radius = Math.Min(width, height) * 0.3;

            // Draw center circle
            var centerCircle = new Ellipse
            {
                Width = radius * 0.4,
                Height = radius * 0.4,
                Fill = new SolidColorBrush(Color.FromRgb(91, 141, 239))
            };
            Canvas.SetLeft(centerCircle, centerX - radius * 0.2);
            Canvas.SetTop(centerCircle, centerY - radius * 0.2);
            VisualizationCanvas.Children.Add(centerCircle);

            int barCount = 64;
            double angleStep = 360.0 / barCount;

            for (int i = 0; i < barCount; i++)
            {
                float magnitude = _smoothedSpectrum[i];
                double angle = i * angleStep * Math.PI / 180.0;
                double barLength = magnitude * radius * 2;

                double x1 = centerX + Math.Cos(angle) * radius;
                double y1 = centerY + Math.Sin(angle) * radius;
                double x2 = centerX + Math.Cos(angle) * (radius + barLength);
                double y2 = centerY + Math.Sin(angle) * (radius + barLength);

                var line = new Line
                {
                    X1 = x1,
                    Y1 = y1,
                    X2 = x2,
                    Y2 = y2,
                    Stroke = GetSpectrumBrush(magnitude),
                    StrokeThickness = 3,
                    StrokeEndLineCap = PenLineCap.Round
                };

                VisualizationCanvas.Children.Add(line);
            }
        }

        private void RenderVUMeter()
        {
            double width = VisualizationCanvas.ActualWidth;
            double height = VisualizationCanvas.ActualHeight;

            // Calculate left and right channel RMS
            float leftRMS = CalculateRMS(_audioSamples, 0, 2); // Even indices (left channel)
            float rightRMS = CalculateRMS(_audioSamples, 1, 2); // Odd indices (right channel)

            // Convert to dB
            double leftDB = 20 * Math.Log10(Math.Max(leftRMS, 0.00001));
            double rightDB = 20 * Math.Log10(Math.Max(rightRMS, 0.00001));

            // Normalize to 0-1 range (assuming -60dB to 0dB range)
            double leftLevel = Math.Max(0, (leftDB + 60) / 60.0);
            double rightLevel = Math.Max(0, (rightDB + 60) / 60.0);

            double meterWidth = width * 0.7;
            double meterHeight = 60;
            double spacing = 40;
            double startX = (width - meterWidth) / 2;

            // Left channel meter
            DrawVUMeter(startX, height / 2 - meterHeight - spacing / 2, meterWidth, meterHeight, leftLevel, "L");

            // Right channel meter
            DrawVUMeter(startX, height / 2 + spacing / 2, meterWidth, meterHeight, rightLevel, "R");

            // Peak indicators
            DrawPeakIndicator(startX + meterWidth + 20, height / 2 - meterHeight - spacing / 2, leftRMS);
            DrawPeakIndicator(startX + meterWidth + 20, height / 2 + spacing / 2, rightRMS);
        }

        private void DrawVUMeter(double x, double y, double width, double height, double level, string label)
        {
            // Background
            var bg = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                RadiusX = 8,
                RadiusY = 8
            };
            Canvas.SetLeft(bg, x);
            Canvas.SetTop(bg, y);
            VisualizationCanvas.Children.Add(bg);

            // Level bar with gradient
            double barWidth = width * level;
            if (barWidth > 0)
            {
                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = height,
                    Fill = GetVUGradient(level),
                    RadiusX = 8,
                    RadiusY = 8
                };
                Canvas.SetLeft(bar, x);
                Canvas.SetTop(bar, y);
                VisualizationCanvas.Children.Add(bar);
            }

            // Label
            var text = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = 20,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(text, x - 35);
            Canvas.SetTop(text, y + height / 2 - 12);
            VisualizationCanvas.Children.Add(text);

            // Level percentage text
            var percentText = new TextBlock
            {
                Text = $"{(level * 100):F0}%",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(percentText, x + width / 2 - 20);
            Canvas.SetTop(percentText, y + height / 2 - 10);
            VisualizationCanvas.Children.Add(percentText);
        }

        private void DrawPeakIndicator(double x, double y, float level)
        {
            // Draw a circular peak indicator
            Color color = level > 0.9f ? Colors.Red : level > 0.7f ? Colors.Yellow : Colors.Green;
            
            var ellipse = new Ellipse
            {
                Width = 30,
                Height = 30,
                Fill = new SolidColorBrush(color),
                Opacity = Math.Min(1.0, level * 2)
            };
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y + 15);
            VisualizationCanvas.Children.Add(ellipse);
        }

        private void RenderParticles()
        {
            double width = VisualizationCanvas.ActualWidth;
            double height = VisualizationCanvas.ActualHeight;

            // Calculate audio energy
            float energy = CalculateRMS(_audioSamples, 0, 1);

            // Spawn new particles based on energy
            if (energy > 0.1f && _particles.Count < 200)
            {
                int particlesToSpawn = (int)(energy * 10);
                for (int i = 0; i < particlesToSpawn; i++)
                {
                    _particles.Add(new Particle
                    {
                        X = width / 2 + (_random.NextDouble() - 0.5) * 100,
                        Y = height / 2 + (_random.NextDouble() - 0.5) * 100,
                        VelocityX = (_random.NextDouble() - 0.5) * energy * 50,
                        VelocityY = (_random.NextDouble() - 0.5) * energy * 50,
                        Size = _random.NextDouble() * 8 + 2,
                        Life = 1.0,
                        Color = GetRandomColor()
                    });
                }
            }

            // Update and render particles
            var particlesToRemove = new List<Particle>();
            
            foreach (var particle in _particles)
            {
                // Update physics
                particle.X += particle.VelocityX;
                particle.Y += particle.VelocityY;
                particle.VelocityY += 0.5; // Gravity
                particle.Life -= 0.02;

                if (particle.Life <= 0 || particle.Y > height)
                {
                    particlesToRemove.Add(particle);
                    continue;
                }

                // Render particle
                var ellipse = new Ellipse
                {
                    Width = particle.Size,
                    Height = particle.Size,
                    Fill = new SolidColorBrush(particle.Color),
                    Opacity = particle.Life
                };
                Canvas.SetLeft(ellipse, particle.X);
                Canvas.SetTop(ellipse, particle.Y);
                VisualizationCanvas.Children.Add(ellipse);
            }

            // Remove dead particles
            foreach (var p in particlesToRemove)
            {
                _particles.Remove(p);
            }

            // Center text with energy
            var text = new TextBlock
            {
                Text = $"♪ Energy: {(energy * 100):F0}% ♪",
                Foreground = new SolidColorBrush(Color.FromArgb((byte)(energy * 255), 255, 255, 255)),
                FontSize = 32,
                FontWeight = FontWeights.Bold
            };
            Canvas.SetLeft(text, (width - 250) / 2);
            Canvas.SetTop(text, height / 2 - 20);
            VisualizationCanvas.Children.Add(text);
        }

        #endregion

        #region Audio Analysis

        private void PerformFFT()
        {
            if (_audioSamples.Length < _fftSize)
                return;

            // Simple FFT implementation using Complex numbers
            Complex[] fftInput = new Complex[_fftSize];
            
            for (int i = 0; i < _fftSize && i < _audioSamples.Length; i++)
            {
                // Apply Hanning window
                double window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / _fftSize));
                fftInput[i] = new Complex(_audioSamples[i] * window, 0);
            }

            // Perform FFT
            FFT(fftInput);

            // Calculate magnitudes and smooth
            int bins = 64;
            int samplesPerBin = _fftSize / 2 / bins;
            
            for (int i = 0; i < bins; i++)
            {
                float sum = 0;
                for (int j = 0; j < samplesPerBin; j++)
                {
                    int index = i * samplesPerBin + j;
                    if (index < fftInput.Length / 2)
                    {
                        sum += (float)fftInput[index].Magnitude;
                    }
                }
                
                float avgMagnitude = sum / samplesPerBin / 100f; // Normalize
                
                // Smooth the spectrum
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * SmoothingFactor + avgMagnitude * (1 - SmoothingFactor);
            }
        }

        private void FFT(Complex[] data)
        {
            int n = data.Length;
            if (n <= 1) return;

            // Cooley-Tukey FFT algorithm
            if ((n & (n - 1)) != 0) return; // n must be power of 2

            // Bit-reversal permutation
            int bits = (int)Math.Log(n, 2);
            for (int i = 0; i < n; i++)
            {
                int j = BitReverse(i, bits);
                if (j > i)
                {
                    var temp = data[i];
                    data[i] = data[j];
                    data[j] = temp;
                }
            }

            // FFT computation
            for (int len = 2; len <= n; len *= 2)
            {
                double angle = -2 * Math.PI / len;
                var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
                
                for (int i = 0; i < n; i += len)
                {
                    var w = new Complex(1, 0);
                    for (int j = 0; j < len / 2; j++)
                    {
                        var u = data[i + j];
                        var v = data[i + j + len / 2] * w;
                        data[i + j] = u + v;
                        data[i + j + len / 2] = u - v;
                        w *= wlen;
                    }
                }
            }
        }

        private int BitReverse(int n, int bits)
        {
            int reversed = 0;
            for (int i = 0; i < bits; i++)
            {
                reversed = (reversed << 1) | (n & 1);
                n >>= 1;
            }
            return reversed;
        }

        private float CalculateRMS(float[] samples, int offset, int step)
        {
            if (samples == null || samples.Length == 0) return 0;

            double sum = 0;
            int count = 0;
            
            for (int i = offset; i < samples.Length; i += step)
            {
                sum += samples[i] * samples[i];
                count++;
            }

            return count > 0 ? (float)Math.Sqrt(sum / count) : 0;
        }

        #endregion

        #region Color Helpers

        private Brush GetSpectrumBrush(float magnitude)
        {
            // Color based on magnitude: Blue -> Cyan -> Green -> Yellow -> Red
            if (magnitude < 0.2f)
                return new SolidColorBrush(Color.FromRgb(0, 100, 255)); // Blue
            else if (magnitude < 0.4f)
                return new SolidColorBrush(Color.FromRgb(0, 200, 255)); // Cyan
            else if (magnitude < 0.6f)
                return new SolidColorBrush(Color.FromRgb(0, 255, 100)); // Green
            else if (magnitude < 0.8f)
                return new SolidColorBrush(Color.FromRgb(255, 200, 0)); // Yellow
            else
                return new SolidColorBrush(Color.FromRgb(255, 50, 50)); // Red
        }

        private Brush GetVUGradient(double level)
        {
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };

            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(76, 175, 80), 0.0));    // Green
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(255, 193, 7), 0.7));    // Yellow
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(244, 67, 54), 1.0));    // Red

            return gradient;
        }

        private Color GetRandomColor()
        {
            byte r = (byte)_random.Next(100, 255);
            byte g = (byte)_random.Next(100, 255);
            byte b = (byte)_random.Next(100, 255);
            return Color.FromRgb(r, g, b);
        }

        #endregion

        #region Button Handlers

        private void SpectrumButton_Click(object sender, RoutedEventArgs e)
        {
            SetVisualizationType(VisualizationType.Spectrum);
            UpdateButtonStyles();
        }

        private void WaveformButton_Click(object sender, RoutedEventArgs e)
        {
            SetVisualizationType(VisualizationType.Waveform);
            UpdateButtonStyles();
        }

        private void CircularButton_Click(object sender, RoutedEventArgs e)
        {
            SetVisualizationType(VisualizationType.Circular);
            UpdateButtonStyles();
        }

        private void VUMeterButton_Click(object sender, RoutedEventArgs e)
        {
            SetVisualizationType(VisualizationType.VUMeter);
            UpdateButtonStyles();
        }

        private void ParticlesButton_Click(object sender, RoutedEventArgs e)
        {
            SetVisualizationType(VisualizationType.Particles);
            UpdateButtonStyles();
        }

        private void SetVisualizationType(VisualizationType type)
        {
            _currentType = type;
            
            // Clear particles when switching away from particles mode
            if (type != VisualizationType.Particles)
            {
                _particles.Clear();
            }
        }

        private void UpdateButtonStyles()
        {
            // Reset all buttons to default style
            SpectrumButton.Style = (Style)FindResource("VisualizerButton");
            WaveformButton.Style = (Style)FindResource("VisualizerButton");
            CircularButton.Style = (Style)FindResource("VisualizerButton");
            VUMeterButton.Style = (Style)FindResource("VisualizerButton");
            ParticlesButton.Style = (Style)FindResource("VisualizerButton");

            // Highlight active button
            Button activeButton = _currentType switch
            {
                VisualizationType.Spectrum => SpectrumButton,
                VisualizationType.Waveform => WaveformButton,
                VisualizationType.Circular => CircularButton,
                VisualizationType.VUMeter => VUMeterButton,
                VisualizationType.Particles => ParticlesButton,
                _ => SpectrumButton
            };

            activeButton.Style = (Style)FindResource("ActiveVisualizerButton");
        }

        #endregion

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _renderTimer?.Stop();
            _particles.Clear();
        }
    }
}
