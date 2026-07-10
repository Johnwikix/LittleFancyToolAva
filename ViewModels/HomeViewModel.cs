using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Models;
using LittleFancyToolAva.Utils;
using System;
using System.IO;
using System.Media;

namespace LittleFancyToolAva.ViewModels
{
    public partial class HomeViewModel : ViewModelBase
    {
        private readonly AppObserveModel _appObserveModel;
        private DispatcherTimer? _rotationTimer;
        private readonly Random _random = new();
        private SoundPlayer? _player;

        [ObservableProperty]
        private double _rotationAngle;

        [ObservableProperty]
        private bool _isRotating;

        public HomeViewModel(AppObserveModel appObserveModel)
        {
            _appObserveModel = appObserveModel;
        }

        [RelayCommand]
        private void ToggleRotation()
        {
            if (IsRotating)
            {
                StopRotation();
            }
            else
            {
                StartRotation();
            }
        }

        private void StartRotation()
        {
            IsRotating = true;
            _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(62.5) };
            _rotationTimer.Tick += (_, _) =>
            {
                RotationAngle += 18;
                if (RotationAngle >= 360)
                    RotationAngle -= 360;
            };
            _rotationTimer.Start();
            PlayRandomSound();
        }

        private void StopRotation()
        {
            IsRotating = false;
            _rotationTimer?.Stop();
            _rotationTimer = null;
            RotationAngle = 0;
        }

        private void PlayRandomSound()
        {
            try
            {
                bool playShort = ToolMethod.GetRandomBoolean(70);
                string wavName = playShort ? "Resources.short114.wav" : "Resources.origin114.wav";
                string baseDir = AppContext.BaseDirectory;
                string filePath = Path.Combine(baseDir, wavName);
                if (!File.Exists(filePath))
                {
                    int idx = baseDir.IndexOf("bin", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        filePath = Path.Combine(baseDir[..idx], wavName);
                    }
                }
                if (File.Exists(filePath))
                {
                    _player?.Dispose();
                    _player = new SoundPlayer(filePath);
                    _player.Play();
                }
            }
            catch
            {
            }
        }
    }
}
