using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private int _eyeReactionVersion;
    private bool _eyesStarted, _appWatcherBusy;

    private enum EyeEmotion { None, Happy, Sad, Hearts, Curious, Surprised, Sleepy }

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
        ApplyEyeStyle();
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
        if (_closed || !_ready || !IsLoaded) return;
        if (!GetCursorPos(out EyeCursorPoint cursor)) return;
        CheckScreenEdgeReveal(cursor.X, cursor.Y);
        if (_edgePhase != EdgePhase.Visible || !IsVisible || DateTimeOffset.UtcNow < _eyeBusyUntil) return;
        if (!AnimationsEnabled)
        {
            LeftPupilOffset.X = LeftPupilOffset.Y = RightPupilOffset.X = RightPupilOffset.Y = 0;
            return;
        }
        if (LeftEye.ActualWidth <= 0 || RightEye.ActualWidth <= 0) return;

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

    private void EyeStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        if (sender is not FrameworkElement { Tag: string style } || style is not ("Googly" or "Kawaii" or "Starry")) return;
        _settings.EyeStyle = style;
        ApplyEyeStyle();
        SavePreferences();
        SettingsStatus.Text = $"{style} eyes selected.";
    }

    private void ApplyEyeStyle()
    {
        ResetEyeMotion();
        string style = _settings.EyeStyle is "Kawaii" or "Starry" ? _settings.EyeStyle : "Googly";
        bool kawaii = style == "Kawaii";
        bool starry = style == "Starry";

        Brush eyeFill = CreateEyeFill(style);
        LeftEyeWhite.Fill = eyeFill;
        RightEyeWhite.Fill = eyeFill;
        LeftEyeWhite.Width = RightEyeWhite.Width = kawaii ? 20 : 22;
        LeftEyeWhite.Height = RightEyeWhite.Height = kawaii ? 23 : 22;
        LeftRoundPupilVisual.Width = RightRoundPupilVisual.Width = kawaii ? 13.5 : 11;
        LeftRoundPupilVisual.Height = RightRoundPupilVisual.Height = kawaii ? 13.5 : 11;
        LeftRoundPupilVisual.Visibility = RightRoundPupilVisual.Visibility = starry ? Visibility.Collapsed : Visibility.Visible;
        LeftStarPupilVisual.Visibility = RightStarPupilVisual.Visibility = starry ? Visibility.Visible : Visibility.Collapsed;
        KawaiiBlush.Visibility = kawaii ? Visibility.Visible : Visibility.Collapsed;

        GooglyEyesButton.IsChecked = style == "Googly";
        KawaiiEyesButton.IsChecked = kawaii;
        StarryEyesButton.IsChecked = starry;
        EyeCluster.ToolTip = style + " eyes";
    }

    private static Brush CreateEyeFill(string style)
    {
        (Color inner, Color middle, Color outer) = style switch
        {
            "Kawaii" => (Color.FromRgb(255, 255, 255), Color.FromRgb(255, 240, 247), Color.FromRgb(244, 205, 224)),
            "Starry" => (Color.FromRgb(255, 255, 247), Color.FromRgb(247, 241, 255), Color.FromRgb(219, 207, 247)),
            _ => (Color.FromRgb(255, 255, 255), Color.FromRgb(244, 244, 248), Color.FromRgb(217, 217, 227))
        };
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.28),
            Center = new Point(0.42, 0.36),
            RadiusX = 0.8,
            RadiusY = 0.8
        };
        brush.GradientStops.Add(new GradientStop(inner, 0));
        brush.GradientStops.Add(new GradientStop(middle, 0.55));
        brush.GradientStops.Add(new GradientStop(outer, 1));
        brush.Freeze();
        return brush;
    }

    private void ScheduleBlink()
    {
        _eyeBlink.Interval = TimeSpan.FromMilliseconds(_eyeRandom.Next(2800, 6200));
        _eyeBlink.Start();
    }

    private void Blink()
    {
        _eyeBlink.Stop();
        if (!_closed && _ready && IsVisible && AnimationsEnabled && DateTimeOffset.UtcNow >= _eyeBusyUntil)
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

    private bool EyesOnScreen() => IsVisible && _edgePhase == EdgePhase.Visible && _state.Mode == IslandMode.Compact && _notice == null && EyeCluster.Visibility == Visibility.Visible;

    private void QueueEmotion(EyeEmotion emotion, DateTimeOffset? expiresAt = null)
    {
        if (DateTimeOffset.UtcNow < _eyeReadyAt && EmotionPriority(emotion) < EmotionPriority(EyeEmotion.Hearts)) return;
        if (_pendingEmotion != EyeEmotion.None && EmotionPriority(emotion) < EmotionPriority(_pendingEmotion)) return;
        _pendingEmotion = emotion;
        _pendingEmotionUntil = expiresAt ?? DateTimeOffset.UtcNow.AddSeconds(4);
    }

    private static int EmotionPriority(EyeEmotion emotion) => emotion switch
    {
        EyeEmotion.Hearts => 4,
        EyeEmotion.Happy or EyeEmotion.Sad => 3,
        EyeEmotion.Surprised or EyeEmotion.Curious => 2,
        EyeEmotion.Sleepy => 1,
        _ => 0
    };

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
        switch (pending)
        {
            case EyeEmotion.Happy: PlayHappy(); break;
            case EyeEmotion.Sad: PlaySad(); break;
            case EyeEmotion.Hearts: PlayHearts(); break;
            case EyeEmotion.Curious: PlayCurious(); break;
            case EyeEmotion.Surprised: PlaySurprised(); break;
            case EyeEmotion.Sleepy: PlaySleepy(); break;
        }
    }

    private void ReactHearts()
    {
        DateTimeOffset expiry = (_noticeUntil > DateTimeOffset.UtcNow ? _noticeUntil : DateTimeOffset.UtcNow).AddSeconds(3);
        if (!EyesOnScreen()) { QueueEmotion(EyeEmotion.Hearts, expiry); return; }
        PlayHearts();
    }

    private void ReactCurious()
    {
        if (_closed || !_ready) return;
        if (!EyesOnScreen()) { QueueEmotion(EyeEmotion.Curious); return; }
        if (DateTimeOffset.UtcNow >= _eyeBusyUntil) PlayCurious();
    }

    private void ReactSurprised()
    {
        if (_closed || !_ready) return;
        if (!EyesOnScreen()) { QueueEmotion(EyeEmotion.Surprised); return; }
        if (DateTimeOffset.UtcNow >= _eyeBusyUntil) PlaySurprised();
    }

    private void Compact_PreviewLeftDown(object sender, MouseButtonEventArgs e) => ReactSurprised();
    private void Compact_PreviewRightDown(object sender, MouseButtonEventArgs e) => ReactCurious();

    private async void PlayHearts()
    {
        ResetEyeMotion();
        int version = _eyeReactionVersion;
        _eyeReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(1550);
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(1450);
        LeftRoundPupilVisual.Opacity = RightRoundPupilVisual.Opacity = 0;
        LeftStarPupilVisual.Opacity = RightStarPupilVisual.Opacity = 0;
        LeftHeartPupilVisual.Opacity = RightHeartPupilVisual.Opacity = 1;
        if (AnimationsEnabled)
        {
            var pop = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }));
            pop.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            LeftPupilScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            LeftPupilScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
            RightPupilScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop.Clone());
            RightPupilScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
        }
        await Task.Delay(1400);
        if (!_closed && version == _eyeReactionVersion) ResetEyeMotion();
    }

    private async void PlayCurious()
    {
        ResetEyeMotion();
        int version = _eyeReactionVersion;
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(650);
        LeftCuriousBrow.Opacity = 1;
        RightEyeScale.ScaleY = 0.86;
        if (AnimationsEnabled)
        {
            LeftPupilOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 2.2, TimeSpan.FromMilliseconds(170)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
            RightPupilOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 2.2, TimeSpan.FromMilliseconds(170)) { AutoReverse = true, FillBehavior = FillBehavior.Stop });
        }
        else LeftPupilOffset.X = RightPupilOffset.X = 2.2;
        await Task.Delay(620);
        if (!_closed && version == _eyeReactionVersion) ResetEyeMotion();
    }

    private async void PlaySurprised()
    {
        ResetEyeMotion();
        int version = _eyeReactionVersion;
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(480);
        if (AnimationsEnabled)
        {
            var grow = new DoubleAnimation(1, 1.1, TimeSpan.FromMilliseconds(120)) { AutoReverse = true, FillBehavior = FillBehavior.Stop };
            var shrink = new DoubleAnimation(1, 0.68, TimeSpan.FromMilliseconds(120)) { AutoReverse = true, FillBehavior = FillBehavior.Stop };
            LeftEyeScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
            RightEyeScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow.Clone());
            RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
            LeftPupilScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            LeftPupilScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
            RightPupilScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink.Clone());
            RightPupilScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink.Clone());
        }
        else
        {
            LeftEyeScale.ScaleX = LeftEyeScale.ScaleY = RightEyeScale.ScaleX = RightEyeScale.ScaleY = 1.1;
            LeftPupilScale.ScaleX = LeftPupilScale.ScaleY = RightPupilScale.ScaleX = RightPupilScale.ScaleY = 0.68;
        }
        await Task.Delay(440);
        if (!_closed && version == _eyeReactionVersion) ResetEyeMotion();
    }

    private void PlaySleepy()
    {
        ResetEyeMotion();
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(500);
        LeftPupil.Opacity = RightPupil.Opacity = 0;
        LeftSleepyLid.Opacity = RightSleepyLid.Opacity = 1;
    }

    private void ResetEyeMotion()
    {
        _eyeReactionVersion++;
        foreach (ScaleTransform scale in new[] { LeftEyeScale, RightEyeScale, LeftPupilScale, RightPupilScale })
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = 1;
        }
        foreach (TranslateTransform offset in new[] { LeftPupilOffset, RightPupilOffset })
        {
            offset.BeginAnimation(TranslateTransform.XProperty, null);
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            offset.X = offset.Y = 0;
        }
        EyeClusterOffset.BeginAnimation(TranslateTransform.YProperty, null);
        EyeClusterOffset.Y = 0;
        LeftTearOffset.BeginAnimation(TranslateTransform.YProperty, null);
        LeftTearOffset.Y = 0;
        LeftPupil.BeginAnimation(OpacityProperty, null);
        RightPupil.BeginAnimation(OpacityProperty, null);
        LeftPupil.Opacity = RightPupil.Opacity = 1;
        foreach (UIElement element in new UIElement[] { LeftHappyArc, RightHappyArc, LeftSadBrow, RightSadBrow, LeftTear, HappySparkle, LeftHeartPupilVisual, RightHeartPupilVisual, LeftSleepyLid, RightSleepyLid, LeftCuriousBrow, RightCuriousBrow })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 0;
        }
        LeftRoundPupilVisual.Opacity = RightRoundPupilVisual.Opacity = 1;
        LeftStarPupilVisual.Opacity = RightStarPupilVisual.Opacity = 1;
    }

    private async void ClearEyePoseAfter(int version, int milliseconds)
    {
        await Task.Delay(milliseconds);
        if (!_closed && version == _eyeReactionVersion) ResetEyeMotion();
    }

    private void PlayHappy()
    {
        ResetEyeMotion();
        int version = _eyeReactionVersion;
        _eyeReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(1700);
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(1000);
        _pendingEmotion = EyeEmotion.None;
        if (!AnimationsEnabled)
        {
            LeftPupil.Opacity = RightPupil.Opacity = 0;
            LeftHappyArc.Opacity = RightHappyArc.Opacity = HappySparkle.Opacity = 1;
            ClearEyePoseAfter(version, 980);
            return;
        }

        var hidePupils = PhaseFade(1, 0, 780, 980);
        LeftPupil.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)hidePupils.Clone());
        RightPupil.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)hidePupils.Clone());
        var showArcs = PhaseFade(0, 1, 780, 980);
        LeftHappyArc.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)showArcs.Clone());
        RightHappyArc.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)showArcs.Clone());
        HappySparkle.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)showArcs.Clone());

        HopEyes();
    }

    private void PlaySad()
    {
        ResetEyeMotion();
        int version = _eyeReactionVersion;
        _eyeReadyAt = DateTimeOffset.UtcNow.AddMilliseconds(1900);
        _eyeBusyUntil = DateTimeOffset.UtcNow.AddMilliseconds(1650);
        _pendingEmotion = EyeEmotion.None;
        if (!AnimationsEnabled)
        {
            LeftSadBrow.Opacity = RightSadBrow.Opacity = LeftTear.Opacity = 1;
            ClearEyePoseAfter(version, 1600);
            return;
        }

        var brows = PhaseFade(0, 1, 1200, 1600);
        LeftSadBrow.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)brows.Clone());
        RightSadBrow.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)brows.Clone());
        LeftTear.BeginAnimation(OpacityProperty, (DoubleAnimationUsingKeyFrames)brows.Clone());

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
