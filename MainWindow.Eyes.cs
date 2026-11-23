using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VajraPulse.Core;

namespace VajraPulse;

public partial class MainWindow
{
    private const double EyeLookRange = 4.5;
    private readonly DispatcherTimer _eyeTracker = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _eyeBlink = new();
    private readonly DispatcherTimer _appWatcher = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Random _eyeRandom = new();
    private HashSet<int> _titledApps = new();
    private DateTimeOffset _eyeBusyUntil, _eyeReadyAt, _pendingEmotionUntil;
    private EyeEmotion _pendingEmotion;
    private bool _eyesStarted, _appWatcherBusy;

    private enum EyeEmotion { None, Happy, Sad }

    [StructLayout(LayoutKind.Sequential)]
    private struct EyeCursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out EyeCursorPoint point);

    private void InitializeEyes()
    {
        if (_eyesStarted) return;
        _eyesStarted = true;
        _eyeTracker.Tick += (_, _) => TrackCursor();
        _eyeBlink.Tick += (_, _) => Blink();
        _appWatcher.Tick += async (_, _) => await WatchAppsAsync();
        _eyeTracker.Start();
        ScheduleBlink();
        _ = PrimeAppWatcherAsync();
    }

    private void ShutdownEyes()
    {
        _eyeTracker.Stop();
        _eyeBlink.Stop();
        _appWatcher.Stop();
    }

    private void TrackCursor()
    {
        if (_closed || !_ready || !IsLoaded || !IsVisible || DateTimeOffset.UtcNow < _eyeBusyUntil) return;
        if (LeftEye.ActualWidth <= 0 || RightEye.ActualWidth <= 0) return;
        if (!GetCursorPos(out EyeCursorPoint cursor)) return;

        Point pointer = PointFromScreen(new Point(cursor.X, cursor.Y));
        Point leftCenter = LeftEye.TransformToAncestor(this).Transform(new Point(LeftEye.ActualWidth / 2, LeftEye.ActualHeight / 2));
        Point rightCenter = RightEye.TransformToAncestor(this).Transform(new Point(RightEye.ActualWidth / 2, RightEye.ActualHeight / 2));
        AimPupil(LeftPupilOffset, leftCenter, pointer);
        AimPupil(RightPupilOffset, rightCenter, pointer);
    }

    private static void AimPupil(TranslateTransform pupil, Point center, Point pointer)
    {
        double dx = pointer.X - center.X;
        double dy = pointer.Y - center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double reach = EyeLookRange * distance / (distance + 55);
        double targetX = distance < 0.01 ? 0 : dx / distance * reach;
        double targetY = distance < 0.01 ? 0 : dy / distance * reach;
        pupil.X += (targetX - pupil.X) * 0.45;
        pupil.Y += (targetY - pupil.Y) * 0.45;
    }

    private void ScheduleBlink()
    {
        _eyeBlink.Interval = TimeSpan.FromMilliseconds(_eyeRandom.Next(2800, 6200));
        _eyeBlink.Start();
    }

    private void Blink()
    {
        _eyeBlink.Stop();
        if (!_closed && _ready && IsVisible && !_settings.ReducedMotion && DateTimeOffset.UtcNow >= _eyeBusyUntil)
        {
            BlinkEye(LeftEyeScale);
            BlinkEye(RightEyeScale);
        }
        ScheduleBlink();
    }

    private static void BlinkEye(ScaleTransform scale)
    {
        var blink = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)), new SineEase { EasingMode = EasingMode.EaseIn }));
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new SineEase { EasingMode = EasingMode.EaseOut }));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, blink);
    }

    private void ReactHappy()
    {
        if (_closed || !_ready) return;
        if (!EyesOnScreen())
        {
            QueueEmotion(EyeEmotion.Happy);
            return;
        }
        if (DateTimeOffset.UtcNow < _eyeReadyAt || DateTimeOffset.UtcNow < _eyeBusyUntil) return;
        PlayHappy();
    }

    private void ReactSad()
    {
        if (_closed || !_ready) return;
        if (!EyesOnScreen())
        {
            QueueEmotion(EyeEmotion.Sad);
            return;
        }
        if (DateTimeOffset.UtcNow < _eyeReadyAt || DateTimeOffset.UtcNow < _eyeBusyUntil) return;
        PlaySad();
    }

    private bool EyesOnScreen() => IsVisible && _state.Mode == IslandMode.Compact;

    private void QueueEmotion(EyeEmotion emotion)
    {
        if (DateTimeOffset.UtcNow < _eyeReadyAt) return;
        _pendingEmotion = emotion;
        _pendingEmotionUntil = DateTimeOffset.UtcNow.AddSeconds(4);
    }

    private void FlushEyeEmotion()
    {
        if (_pendingEmotion == EyeEmotion.None || !EyesOnScreen()) return;
        if (DateTimeOffset.UtcNow > _pendingEmotionUntil)
        {
            _pendingEmotion = EyeEmotion.None;
            return;
        }
        EyeEmotion pending = _pendingEmotion;
        _pendingEmotion = EyeEmotion.None;
        if (pending == EyeEmotion.Happy) PlayHappy();
        else PlaySad();
    }

    private void PlayHappy()
    {
        _eyeReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(1700);
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(1000);
        _pendingEmotion = EyeEmotion.None;

        var hidePupils = PhaseFade(1, 0, 780, 980);
        LeftPupil.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)hidePupils.Clone());
        RightPupil.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)hidePupils.Clone());
        var showArcs = PhaseFade(0, 1, 780, 980);
        LeftHappyArc.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)showArcs.Clone());
        RightHappyArc.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)showArcs.Clone());
        HappySparkle.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)showArcs.Clone());

        if (!_settings.ReducedMotion) HopEyes();
    }

    private void PlaySad()
    {
        _eyeReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(1900);
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(1650);
        _pendingEmotion = EyeEmotion.None;

        var brows = PhaseFade(0, 1, 1200, 1600);
        LeftSadBrow.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)brows.Clone());
        RightSadBrow.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)brows.Clone());
        LeftTear.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)brows.Clone());

        if (_settings.ReducedMotion) return;
        DroopPupil(LeftPupilOffset, 1.2);
        DroopPupil(RightPupilOffset, -1.2);
        DroopEye(LeftEyeScale);
        DroopEye(RightEyeScale);
        SlideTear();
    }

    private static DoubleAnimationUsingKeyFrames PhaseFade(double from, double to, int holdMs, int totalMs)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(holdMs))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(totalMs))));
        return animation;
    }

    private void HopEyes()
    {
        var hop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        hop.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-2.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(-1.3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(680)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        EyeClusterOffset.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    private void SlideTear()
    {
        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1550)), new SineEase { EasingMode = EasingMode.EaseIn }));
        LeftTearOffset.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static void DroopPupil(TranslateTransform pupil, double inward)
    {
        double x = pupil.X;
        double y = pupil.Y;
        var xFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        xFrames.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        xFrames.KeyFrames.Add(new EasingDoubleKeyFrame(x + inward, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        xFrames.KeyFrames.Add(new LinearDoubleKeyFrame(x + inward, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))));
        xFrames.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        pupil.BeginAnimation(TranslateTransform.XProperty, xFrames);

        var yFrames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        yFrames.KeyFrames.Add(new LinearDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        yFrames.KeyFrames.Add(new EasingDoubleKeyFrame(y + 2.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        yFrames.KeyFrames.Add(new LinearDoubleKeyFrame(y + 2.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))));
        yFrames.KeyFrames.Add(new EasingDoubleKeyFrame(y, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        pupil.BeginAnimation(TranslateTransform.YProperty, yFrames);
    }

    private static void DroopEye(ScaleTransform scale)
    {
        var frames = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        frames.KeyFrames.Add(new LinearDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1350))));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1600)), new QuadraticEase { EasingMode = EasingMode.EaseInOut }));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, frames);
    }

    private async Task PrimeAppWatcherAsync()
    {
        try { _titledApps = await SnapshotTitledAppsAsync(); }
        catch { _titledApps = new HashSet<int>(); }
        if (_closed) return;
        _appWatcher.Start();
    }

    private async Task WatchAppsAsync()
    {
        if (_closed || _appWatcherBusy) return;
        _appWatcherBusy = true;
        try
        {
            HashSet<int> current;
            try { current = await SnapshotTitledAppsAsync(); }
            catch { return; }
            if (_closed) return;
            bool opened = current.Except(_titledApps).Any();
            bool closed = _titledApps.Except(current).Any();
            _titledApps = current;
            if (opened) ReactHappy();
            else if (closed) ReactSad();
        }
        finally { _appWatcherBusy = false; }
    }

    private static Task<HashSet<int>> SnapshotTitledAppsAsync() => Task.Run(() =>
    {
        var ids = new HashSet<int>();
        int self = Environment.ProcessId;
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id != self && process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    ids.Add(process.Id);
            }
            catch { }
            finally { process.Dispose(); }
        }
        return ids;
    });
}
