using System;
using System.IO;
using System.Media;
using System.Threading;

namespace Connector_Vision.Services
{
    public class SoundService
    {
        private SoundPlayer _ngPlayer;
        private readonly string _wavPath;
        private Timer _stopTimer;

        public SoundService()
        {
            _wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "ng_alert.wav");
        }

        public void Initialize()
        {
            try
            {
                if (File.Exists(_wavPath))
                {
                    _ngPlayer = new SoundPlayer(_wavPath);
                    _ngPlayer.Load();
                }
            }
            catch
            {
                _ngPlayer = null;
            }
        }

        public void PlayNgAlert()
        {
            try
            {
                _stopTimer?.Dispose();
                _stopTimer = null;

                if (_ngPlayer != null)
                {
                    _ngPlayer.PlayLooping();
                    _stopTimer = new Timer(_ =>
                    {
                        try { _ngPlayer?.Stop(); } catch { }
                    }, null, 3000, Timeout.Infinite);
                }
                else
                {
                    SystemSounds.Exclamation.Play();
                }
            }
            catch
            {
                SystemSounds.Exclamation.Play();
            }
        }

        public void StopNgAlert()
        {
            _stopTimer?.Dispose();
            _stopTimer = null;
            try { _ngPlayer?.Stop(); } catch { }
        }
    }
}
