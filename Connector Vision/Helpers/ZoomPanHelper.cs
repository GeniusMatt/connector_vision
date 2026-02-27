using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Connector_Vision.Helpers
{
    public static class ZoomPanHelper
    {
        private class PanState
        {
            public Point Start;
            public Point Origin;
        }

        private static readonly Dictionary<Image, PanState> _panStates = new Dictionary<Image, PanState>();

        public static void Setup(Image img)
        {
            img.RenderTransform = new TransformGroup
            {
                Children = { new ScaleTransform(1, 1), new TranslateTransform(0, 0) }
            };
            img.MouseWheel += OnMouseWheel;
            img.MouseRightButtonDown += OnRightButtonDown;
            img.MouseRightButtonUp += OnRightButtonUp;
            img.MouseMove += OnMouseMove;
            img.MouseLeftButtonDown += OnLeftButtonDown;
        }

        private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var img = (Image)sender;
            var tg = img.RenderTransform as TransformGroup;
            if (tg == null || tg.Children.Count < 2) return;
            var st = (ScaleTransform)tg.Children[0];
            var tt = (TranslateTransform)tg.Children[1];

            double newScale = st.ScaleX * (e.Delta > 0 ? 1.15 : 1.0 / 1.15);
            if (newScale < 1.0) newScale = 1.0;
            if (newScale > 20.0) newScale = 20.0;

            if (newScale <= 1.0)
            {
                // At minimum zoom, reset position to fit the full image
                st.ScaleX = 1.0;
                st.ScaleY = 1.0;
                tt.X = 0;
                tt.Y = 0;
            }
            else
            {
                // Zoom to cursor: keep the point under mouse at the same screen position
                var pos = e.GetPosition(img);
                tt.X += pos.X * (st.ScaleX - newScale);
                tt.Y += pos.Y * (st.ScaleY - newScale);

                st.ScaleX = newScale;
                st.ScaleY = newScale;
            }
            e.Handled = true;
        }

        private static void OnRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var img = (Image)sender;
            var tg = img.RenderTransform as TransformGroup;
            if (tg == null || tg.Children.Count < 2) return;
            var tt = (TranslateTransform)tg.Children[1];

            var parent = img.Parent as UIElement ?? img;
            if (!_panStates.ContainsKey(img))
                _panStates[img] = new PanState();
            _panStates[img].Start = e.GetPosition(parent);
            _panStates[img].Origin = new Point(tt.X, tt.Y);
            img.CaptureMouse();
            e.Handled = true;
        }

        private static void OnRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            ((Image)sender).ReleaseMouseCapture();
        }

        private static void OnMouseMove(object sender, MouseEventArgs e)
        {
            var img = (Image)sender;
            if (!img.IsMouseCaptured) return;
            var tg = img.RenderTransform as TransformGroup;
            if (tg == null || tg.Children.Count < 2) return;
            var tt = (TranslateTransform)tg.Children[1];

            PanState state;
            if (!_panStates.TryGetValue(img, out state)) return;
            var parent = img.Parent as UIElement ?? img;
            var pos = e.GetPosition(parent);
            tt.X = state.Origin.X + (pos.X - state.Start.X);
            tt.Y = state.Origin.Y + (pos.Y - state.Start.Y);
        }

        private static void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            var img = (Image)sender;
            var tg = img.RenderTransform as TransformGroup;
            if (tg == null || tg.Children.Count < 2) return;
            var st = (ScaleTransform)tg.Children[0];
            var tt = (TranslateTransform)tg.Children[1];
            st.ScaleX = 1;
            st.ScaleY = 1;
            tt.X = 0;
            tt.Y = 0;
        }
    }
}
