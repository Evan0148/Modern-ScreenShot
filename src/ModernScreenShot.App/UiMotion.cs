using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ModernScreenShot.App;

/// <summary>
/// Shared motion tokens and animation factories (motion audit batch 1). All entrance, panel and
/// window animations go through here so curves, durations and fallbacks stay consistent app-wide.
///
/// Contract shared by every factory:
///  - <see cref="Suppress"/>: diagnostic render paths (--render-*) set it before opening any
///    window; factories then snap all touched properties to their final values synchronously so
///    snapshots stay deterministic and are never caught mid-flight. The probe process hard-exits
///    after rendering, so the flag is never reset there.
///  - Reduced motion (SystemParameters.ClientAreaAnimation == false): transform components are
///    dropped and opacity fades are kept as the accessible fallback. A factory never leaves an
///    element invisible — whenever it does not animate a property, it sets the final value at once.
///  - Timelines are DoubleAnimationUsingKeyFrames with a single frozen spline; the current property
///    value is the start keyframe, and on completion the final value is committed as the plain
///    property value so a held animation never blocks later direct sets (hover opacity,
///    Ctrl+wheel opacity, ...).
///  - Never call these from a CompositionTarget.Rendering handler (overlay frame loop): every
///    call allocates timeline objects and is meant for low-frequency entrance paths only.
/// </summary>
internal static class UiMotion
{
    /// <summary>Enter curve — strong ease-out for entrances/exits: cubic-bezier(0.23,1,0.32,1).</summary>
    internal static readonly KeySpline EnterSpline = FrozenSpline(0.23, 1.0, 0.32, 1.0);

    /// <summary>On-screen curve for moves/deforms while already visible: cubic-bezier(0.77,0,0.175,1).
    /// Reserved for later batches; frozen now so future call sites share one token.</summary>
    internal static readonly KeySpline OnScreenSpline = FrozenSpline(0.77, 0.0, 0.175, 1.0);

    /// <summary>
    /// Global kill switch for diagnostic render paths (--render-*). True → every factory applies
    /// final values synchronously instead of animating.
    /// </summary>
    public static bool Suppress { get; set; }

    private static bool ReducedMotion => SystemParameters.ClientAreaAnimation == false;

    /// <summary>
    /// Fades an element's (or window's) opacity from its current value to 1. With reduced motion
    /// the fade is kept when <paramref name="allowFadeWhenReduced"/> (the accessible fallback),
    /// otherwise the opacity is snapped to 1 synchronously.
    /// </summary>
    public static void FadeIn(UIElement e, int ms = 150, bool allowFadeWhenReduced = true)
    {
        if (e is null) return;
        if (Suppress || ms <= 0 || (ReducedMotion && !allowFadeWhenReduced))
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            return;
        }
        AnimateDouble(e, UIElement.OpacityProperty, e.Opacity, 1, ms);
    }

    /// <summary>
    /// Entrance for panels and staggered groups: opacity 0→1 plus a TranslateTransform slide
    /// (dx,dy)→(0,0). An existing TranslateTransform is reused (also when nested in a
    /// TransformGroup); a new one is only installed when the transform slot is empty — any other
    /// transform is never clobbered (the slide is skipped, the fade kept). <paramref name="delayMs"/>
    /// paces group entrances (30–80ms steps) and does not block interaction.
    /// </summary>
    public static void FadeSlideIn(FrameworkElement e, double dx = 0, double dy = 6, int ms = 150, int delayMs = 0)
    {
        if (e is null) return;
        if (Suppress || ms <= 0)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            if (e.RenderTransform is TranslateTransform snapped)
            {
                snapped.BeginAnimation(TranslateTransform.XProperty, null);
                snapped.BeginAnimation(TranslateTransform.YProperty, null);
                snapped.X = 0;
                snapped.Y = 0;
            }
            return;
        }
        if (delayMs < 0) delayMs = 0;

        // Slide component: reduced motion drops it entirely (fade-only).
        var translate = ReducedMotion ? null : AcquireTranslateTransform(e);
        if (translate is not null)
        {
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.X = dx; // base values hold the start pose while a stagger delay runs
            translate.Y = dy;
            AnimateDouble(translate, TranslateTransform.XProperty, dx, 0, ms, delayMs);
            AnimateDouble(translate, TranslateTransform.YProperty, dy, 0, ms, delayMs);
        }
        if (delayMs > 0)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null); // clear any held animation so the seed isn't masked
            e.Opacity = 0; // base holds the start pose while a stagger delay runs (parity with the transform leg)
        }
        AnimateDouble(e, UIElement.OpacityProperty, 0, 1, ms, delayMs);
    }

    /// <summary>
    /// Popup entrance: ScaleTransform <paramref name="fromScale"/>→1 plus opacity 0→1. The caller
    /// sets RenderTransformOrigin; never pops from scale(0). Reduced motion — or a transform slot
    /// occupied by something other than a ScaleTransform, which is never clobbered — falls back to
    /// the opacity fade only.
    /// </summary>
    public static void PopIn(FrameworkElement e, double fromScale = 0.96, int ms = 150)
    {
        if (e is null) return;
        if (Suppress || ms <= 0)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            if (e.RenderTransform is ScaleTransform snapped)
            {
                snapped.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                snapped.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                snapped.ScaleX = 1;
                snapped.ScaleY = 1;
            }
            return;
        }
        var scale = ReducedMotion ? null : AcquireScaleTransform(e);
        if (scale is null)
        {
            AnimateDouble(e, UIElement.OpacityProperty, e.Opacity, 1, ms);
            return;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = fromScale;
        scale.ScaleY = fromScale;
        AnimateDouble(scale, ScaleTransform.ScaleXProperty, fromScale, 1, ms);
        AnimateDouble(scale, ScaleTransform.ScaleYProperty, fromScale, 1, ms);
        AnimateDouble(e, UIElement.OpacityProperty, 0, 1, ms);
    }

    /// <summary>
    /// Window entrance fade. Usage: preset the window's Opacity to 0, Show() it, then call this —
    /// the whole preset→fade block runs before the first frame paints, so nothing flashes. Windows
    /// that are not visible yet, or already fully opaque (re-shown reused window), are untouched.
    /// </summary>
    public static void WindowFadeIn(Window w, int ms = 180)
    {
        if (w is null || !w.IsVisible) return;
        if (Suppress || ms <= 0)
        {
            w.BeginAnimation(UIElement.OpacityProperty, null);
            w.Opacity = 1;
            return;
        }
        if (w.Opacity > 0.99) return; // already on screen (re-shown reuse, or fade nearly done)
        AnimateDouble(w, UIElement.OpacityProperty, w.Opacity, 1, ms);
    }

    /// <summary>
    /// Transform-only entrance: TranslateTransform (dx,dy)→(0,0), with no opacity leg — for elements
    /// whose opacity is owned by a parent (pair with <see cref="FadeIn"/> there) or needs separate
    /// control. Same transform discipline as <see cref="FadeSlideIn"/>: reuse an existing
    /// TranslateTransform, never clobber any other transform, reduced motion drops the slide
    /// entirely (the element simply rests at its final position).
    /// </summary>
    public static void SlideIn(FrameworkElement e, double dx = 0, double dy = 6, int ms = 150)
    {
        if (e is null) return;
        if (Suppress || ms <= 0)
        {
            if (e.RenderTransform is TranslateTransform snapped)
            {
                snapped.BeginAnimation(TranslateTransform.XProperty, null);
                snapped.BeginAnimation(TranslateTransform.YProperty, null);
                snapped.X = 0;
                snapped.Y = 0;
            }
            return;
        }
        var translate = ReducedMotion ? null : AcquireTranslateTransform(e);
        if (translate is null) return;
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = dx; // base values hold the start pose
        translate.Y = dy;
        AnimateDouble(translate, TranslateTransform.XProperty, dx, 0, ms);
        AnimateDouble(translate, TranslateTransform.YProperty, dy, 0, ms);
    }

    /// <summary>
    /// Grows an element vertically from 0 to full height (ScaleY 0→1) — selection cues such as a
    /// nav accent bar. The caller sets RenderTransformOrigin so the growth reads from the intended
    /// edge. Reuses/installs a ScaleTransform under the same no-clobber rule as
    /// <see cref="PopIn"/>; the start pose is seeded, so every reselection replays the grow.
    /// Reduced motion — or a transform slot occupied by another transform — snaps the element to
    /// full height with no motion (the appearing element itself is the state indication).
    /// </summary>
    public static void GrowY(FrameworkElement e, int ms = 160)
    {
        if (e is null) return;
        void SnapFull()
        {
            if (e.RenderTransform is ScaleTransform snapped)
            {
                snapped.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                snapped.ScaleY = 1;
            }
        }
        if (Suppress || ms <= 0 || ReducedMotion)
        {
            SnapFull();
            return;
        }
        var scale = AcquireScaleTransform(e);
        if (scale is null) return;
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleY = 0; // seed the start pose so reselection replays the grow
        AnimateDouble(scale, ScaleTransform.ScaleYProperty, 0, 1, ms);
    }

    /// <summary>
    /// Exit fade: opacity from its current value to 0, then the animation is removed, 0 is
    /// committed and <paramref name="onCompleted"/> fires — put window Close() there. The callback
    /// is dispatched off the completion pass so a Close() inside it can never re-enter the
    /// animation machinery. Reduced motion keeps the fade (a short opacity exit with no movement is
    /// the accessible fallback). Suppress — diagnostic render paths must never see content vanish:
    /// the current (fully visible) value is kept, NOT snapped to 0, and the callback fires at once
    /// so probe windows still close.
    /// </summary>
    public static void FadeOut(UIElement e, int ms = 120, Action? onCompleted = null)
    {
        if (e is null) return;
        if (Suppress)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null); // keep visible; never snap to 0
            onCompleted?.Invoke();
            return;
        }
        if (ms <= 0)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 0;
            onCompleted?.Invoke();
            return;
        }
        var from = e.Opacity; // current composed value (also correct when replacing a running fade)
        int generation = TouchGeneration(e, UIElement.OpacityProperty);
        var key = ((DependencyObject)e, UIElement.OpacityProperty);
        var timeline = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EnterSpline),
            },
        };
        void Commit()
        {
            bool stillCurrent;
            lock (AnimationGenerationLock)
            {
                stillCurrent = AnimationGeneration.TryGetValue(key, out int current) && current == generation;
                if (stillCurrent) AnimationGeneration.Remove(key); // no orphaned strong ref to the element
            }
            if (!stillCurrent) return; // a newer animation owns the property; it commits itself
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 0;
            // Defer out of the completion pass: onCompleted may Close() the hosting window.
            if (onCompleted is not null) e.Dispatcher.BeginInvoke(onCompleted);
        }
        timeline.Completed += (_, _) => Commit();
        // Wall-clock commit: Completed shares the project's demonstrated can-stall-forever failure
        // mode, and FadeOut gates Close() — without this a stalled clock leaves the window (or a
        // pin toast) on screen topmost indefinitely.
        var commitTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(2.0 * ms + 150),
        };
        commitTimer.Tick += (_, _) => { commitTimer.Stop(); Commit(); };
        commitTimer.Start();
        e.BeginAnimation(UIElement.OpacityProperty, timeline, HandoffBehavior.SnapshotAndReplace);
    }

    // ---- helpers ----

    private static KeySpline FrozenSpline(double x1, double y1, double x2, double y2)
    {
        var spline = new KeySpline(x1, y1, x2, y2);
        spline.Freeze();
        return spline;
    }

    /// <summary>
    /// Returns the TranslateTransform to slide with: reuses an existing one (also nested in a
    /// TransformGroup), installs a new one only when the slot is empty (null or identity matrix),
    /// and returns null when any other transform is installed — it must never be clobbered.
    /// </summary>
    private static TranslateTransform? AcquireTranslateTransform(FrameworkElement e)
    {
        switch (e.RenderTransform)
        {
            case TranslateTransform existing:
                return existing;
            case TransformGroup group when !group.IsFrozen:
                foreach (var child in group.Children)
                    if (child is TranslateTransform nested) return nested;
                var added = new TranslateTransform();
                group.Children.Add(added);
                return added;
            default:
                if (e.RenderTransform is null || e.RenderTransform.Value.IsIdentity)
                {
                    var created = new TranslateTransform();
                    e.RenderTransform = created;
                    return created;
                }
                return null;
        }
    }

    /// <summary>
    /// Returns the ScaleTransform to pop with, using the same no-clobber rule as
    /// <see cref="AcquireTranslateTransform"/>.
    /// </summary>
    private static ScaleTransform? AcquireScaleTransform(FrameworkElement e)
    {
        switch (e.RenderTransform)
        {
            case ScaleTransform existing:
                return existing;
            case TransformGroup group when !group.IsFrozen:
                foreach (var child in group.Children)
                    if (child is ScaleTransform nested) return nested;
                var added = new ScaleTransform();
                group.Children.Add(added);
                return added;
            default:
                if (e.RenderTransform is null || e.RenderTransform.Value.IsIdentity)
                {
                    var created = new ScaleTransform();
                    e.RenderTransform = created;
                    return created;
                }
                return null;
        }
    }

    /// <summary>
    /// Runs one DoubleAnimationUsingKeyFrames (start keyframe → one frozen-spline keyframe) with
    /// SnapshotAndReplace, and commits the final value as the plain property value when it
    /// completes so the animation never lingers on the property.
    /// A wall-clock timer repeats that commit as a fallback: this project has live evidence that a
    /// WPF animation clock can stall with Completed never firing (the OOBE empty-window bug), and a
    /// window whose opacity never leaves 0 reads as a "black window" over a dark desktop. The
    /// generation counter keeps the fallback from clobbering a NEWER animation on the same
    /// target+property (SnapshotAndReplace makes a superseded clock's Completed never fire — the
    /// timer must obey the same rule).
    /// </summary>
    private static readonly Dictionary<(DependencyObject Owner, DependencyProperty Property), int> AnimationGeneration = new();
    private static readonly object AnimationGenerationLock = new();

    /// <summary>Registers a new clock on (owner, property) and invalidates any pending wall-clock
    /// commit from an older animation. Every hand-rolled BeginAnimation path (not just
    /// AnimateDouble) must call this before starting its timeline.</summary>
    private static int TouchGeneration(DependencyObject owner, DependencyProperty property)
    {
        lock (AnimationGenerationLock)
        {
            var key = (owner, property);
            AnimationGeneration.TryGetValue(key, out int current);
            AnimationGeneration[key] = current + 1;
            return current + 1;
        }
    }

    private static void AnimateDouble(IAnimatable target, DependencyProperty property,
        double from, double to, int ms, int delayMs = 0)
    {
        var owner = (DependencyObject)target;
        if (ms <= 0)
        {
            target.BeginAnimation(property, null);
            owner.SetValue(property, to);
            return;
        }
        int generation = TouchGeneration(owner, property);
        var key = (owner, property);
        var timeline = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EnterSpline),
            },
        };
        if (delayMs > 0) timeline.BeginTime = TimeSpan.FromMilliseconds(delayMs); // stagger: hold the start pose
        timeline.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            owner.SetValue(property, to);
        };
        target.BeginAnimation(property, timeline, HandoffBehavior.SnapshotAndReplace);
        // Wall-clock commit at ~2× the animation budget: if the clock stalled before reaching the
        // end, fail forward to the final pose. Committing while a newer animation runs is skipped
        // via the generation check; committing while THIS clock still runs (it finished late) is
        // harmless — the same value Completed writes.
        var commitTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Send)
        {
            Interval = TimeSpan.FromMilliseconds(delayMs + 2.0 * ms + 150),
        };
        commitTimer.Tick += (_, _) =>
        {
            commitTimer.Stop();
            bool stillCurrent;
            lock (AnimationGenerationLock)
            {
                stillCurrent = AnimationGeneration.TryGetValue(key, out int current) && current == generation;
                if (stillCurrent) AnimationGeneration.Remove(key);
            }
            if (!stillCurrent) return;
            target.BeginAnimation(property, null);
            owner.SetValue(property, to);
        };
        commitTimer.Start();
    }
}
