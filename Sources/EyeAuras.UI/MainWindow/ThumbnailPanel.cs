﻿using System;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WindowsFormsAero.Dwm;
using EyeAuras.OnTopReplica;
using JetBrains.Annotations;
using log4net;
using PoeShared;
using PoeShared.Native;
using PoeShared.Scaffolding;
using ReactiveUI;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using WinSize = System.Drawing.Size;
using WinPoint = System.Drawing.Point;
using WinRectangle = System.Drawing.Rectangle;

namespace EyeAuras.UI.MainWindow
{
    public class ThumbnailPanel : Panel
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ThumbnailPanel));
        
        private static readonly TimeSpan UpdateLogSamplingInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

        public static readonly DependencyProperty SourceWindowProperty = DependencyProperty.Register(
            "SourceWindow",
            typeof(WindowHandle),
            typeof(ThumbnailPanel),
            new FrameworkPropertyMetadata(default(WindowHandle)));

        public static readonly DependencyProperty SourceRegionProperty = DependencyProperty.Register(
            "SourceRegion",
            typeof(WinRectangle),
            typeof(ThumbnailPanel),
            new FrameworkPropertyMetadata(default(WinRectangle)));

        public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(
            "Owner",
            typeof(Window),
            typeof(ThumbnailPanel),
            new PropertyMetadata(default(Window)));

        public static readonly DependencyProperty SourceRegionSizeProperty = DependencyProperty.Register(
            "SourceRegionSize",
            typeof(WinSize),
            typeof(ThumbnailPanel),
            new PropertyMetadata(default(WinSize)));

        public static readonly DependencyProperty ThumbnailOpacityProperty = DependencyProperty.Register(
            "ThumbnailOpacity",
            typeof(double),
            typeof(ThumbnailPanel),
            new PropertyMetadata((double) 1));

        public static readonly DependencyProperty SourceWindowSizeProperty = DependencyProperty.Register(
            "SourceWindowSize",
            typeof(WinSize),
            typeof(ThumbnailPanel),
            new PropertyMetadata(default(WinSize)));

        private readonly CompositeDisposable anchors = new CompositeDisposable();
        private readonly BehaviorSubject<Size> renderSizeSource = new BehaviorSubject<Size>(Size.Empty);

        public ThumbnailPanel()
        {
            Dpi = VisualTreeHelper.GetDpi(this);
            Loaded += OnLoaded;
        }

        public WinSize SourceWindowSize
        {
            get => (WinSize) GetValue(SourceWindowSizeProperty);
            set => SetValue(SourceWindowSizeProperty, value);
        }

        public double ThumbnailOpacity
        {
            get => (double) GetValue(ThumbnailOpacityProperty);
            set => SetValue(ThumbnailOpacityProperty, value);
        }

        public WinSize SourceRegionSize
        {
            get => (WinSize) GetValue(SourceRegionSizeProperty);
            set => SetValue(SourceRegionSizeProperty, value);
        }

        public Window Owner
        {
            get => (Window) GetValue(OwnerProperty);
            private set => SetValue(OwnerProperty, value);
        }

        public WinRectangle SourceRegion
        {
            get => (WinRectangle) GetValue(SourceRegionProperty);
            set => SetValue(SourceRegionProperty, value);
        }

        public WindowHandle SourceWindow
        {
            get => (WindowHandle) GetValue(SourceWindowProperty);
            set => SetValue(SourceWindowProperty, value);
        }

        public DpiScale Dpi { get; }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log.Debug($"ThumbnailPanel loaded, TargetWindow: {SourceWindow}, parent: {Parent}");
            if (Owner != null)
            {
                throw new InvalidOperationException("ThumbnailPanel must be initialized only once per lifecycle");
            }
            Owner = this.FindVisualAncestor<Window>();
            Guard.ArgumentNotNull(Owner, nameof(Owner));
            
            var thumbnailSource =
                Observable.Merge(
                        this.Observe(OwnerProperty).Select(x => "Owner changed"),
                        this.Observe(IsVisibleProperty).Select(x => "IsVisible changed"),
                        this.Observe(SourceWindowProperty).Select(x => "TargetWindow changed"))
                    .StartWith($"Initial {nameof(CreateThumbnail)} tick")
                    .Select(
                        reason =>
                        {
                            return Observable.Create<Thumbnail>(
                                observer =>
                                {
                                    var args = new ThumbnailArgs()
                                    {
                                        Owner = Owner,
                                        IsVisible = IsVisible,
                                        SourceWindow = SourceWindow
                                    };
                                    Log.Debug($"Recreating thumbnail, reason: {reason}, args: {args}");
                                    
                                    var thumbnailAnchors = new CompositeDisposable();
                                    var result = CreateThumbnail(args);
                                    if (result != null)
                                    {
                                        Disposable.Create(
                                            () =>
                                            {
                                                Log.Debug($"Disposing Thumbnail, args: {args}");
                                                result.Dispose();
                                            }).AddTo(thumbnailAnchors);
                                    }
                                    observer.OnNext(result);
                                    return thumbnailAnchors;
                                });
                        })
                    .Switch();
            
            
            var throttledLogger = new Subject<string>();
            throttledLogger.Subscribe(message => Log.Debug(message)).AddTo(anchors);

            thumbnailSource
                .Select(
                    thumbnail => Observable.Merge(
                            this.Observe(SourceRegionProperty).Select(x => SourceRegion).DistinctUntilChanged().WithPrevious((prev, curr) => new { prev, curr }).Select(x => $"RegionBounds changed {x.prev} => {x.curr}"),
                            this.Observe(ThumbnailOpacityProperty).Select(x => ThumbnailOpacity).DistinctUntilChanged().WithPrevious((prev, curr) => new { prev, curr }).Select(x => $"ThumbnailOpacity changed {x.prev} => {x.curr}"),
                            renderSizeSource.Where(x => !x.IsEmpty).Select(x => x.ToWinSize()).DistinctUntilChanged().WithPrevious((prev, curr) => new { prev, curr }).Select(x => $"RenderSize changed {x.prev} => {x.curr}"))
                        .StartWith($"Initial {nameof(UpdateThumbnail)} tick")
                        .Select(
                            reason =>
                            {
                                ThumbnailUpdateArgs args;
                                var sourceWindow = SourceWindow.Handle;
                                var ownerWindow = new WindowInteropHelper(Owner).Handle;
                                if (sourceWindow != IntPtr.Zero && ownerWindow != IntPtr.Zero && CanUpdateThumbnail(thumbnail))
                                {
                                    var sourceArgs = PrepareSourceRegion(thumbnail, sourceWindow, ownerWindow, SourceRegion);
                                    SourceWindowSize = sourceArgs.sourceSize;
                                    SourceRegionSize = sourceArgs.sourceRegionOriginalSize;

                                    args = PrepareUpdateArgs(
                                            thumbnail,
                                            RenderSize,
                                            this,
                                            Owner,
                                            ThumbnailOpacity,
                                            sourceArgs.sourceRegion,
                                            sourceArgs.sourceRegionOriginalSize,
                                            sourceArgs.sourceSize);
                                }
                                else
                                {
                                    args = default;
                                }
                                
                                
                                return new {args, reason};
                            })
                        .DistinctUntilChanged(x=> x.args)
                        .Select(x => { 
                            throttledLogger.OnNext($"Updating Thumbnail, reason: {x.reason}, state: {new { TargetWindowSize = SourceWindowSize, ThumbnailSize = SourceRegionSize }} args: {(x.args.Thumbnail == null ? "empty" : x.args.ToString())}");
                            UpdateThumbnail(x.args);
                            return x.args;
                        })
                )
                .Switch()
                .RetryWithDelay(RetryInterval, DispatcherScheduler.Current)
                .SubscribeToErrors(Log.HandleUiException)
                .AddTo(anchors);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            renderSizeSource.OnNext(sizeInfo.NewSize);
        }

        private static Thumbnail CreateThumbnail(ThumbnailArgs args)
        {
            try
            {
                if (args.SourceWindow == null || args.Owner == null || !args.IsVisible || args.Owner == null)
                {
                    return null;
                }
                
                Log.Debug($"Creating new Thumbnail, targetWindow: {args.SourceWindow}");
                var ownerFormHelper = new WindowInteropHelper(args.Owner);

                var thumbnail = DwmManager.Register(ownerFormHelper.Handle, args.SourceWindow.Handle);
                thumbnail.ShowOnlyClientArea = true;

                return thumbnail;
            }
            catch (Exception e)
            {
                Log.Warn($"Failed to create Thumbnail Handle for window {args.SourceWindow}", e);
                throw;
            }
        }
        
        private static (WinRectangle sourceRegion, WinSize sourceRegionOriginalSize, WinSize sourceSize) PrepareSourceRegion(
            Thumbnail thumbnail,
            IntPtr ownerWindow,
            IntPtr sourceWindow,
            WinRectangle sourceBounds)
        {
            try
            {
                Guard.ArgumentIsTrue(CanUpdateThumbnail(thumbnail), "CanUpdateThumbnail");

                // GetSourceSize returns DPI-affected Size, so we have to convert it to Screen
                var sourceWindowSize = thumbnail.GetSourceSize();
                var thumbnailSize = !sourceBounds.IsNotEmpty()
                    ? sourceWindowSize
                    : sourceBounds.FitToSize(sourceWindowSize);

                WinRectangle source;
                if (sourceBounds.IsEmpty)
                {
                    source = new WinRectangle(0, 0, thumbnailSize.Width, thumbnailSize.Height);
                }
                else
                {
                    source = new WinRectangle(
                        sourceBounds.X,
                        sourceBounds.Y,
                        sourceBounds.Width > 0 && sourceBounds.Height > 0
                            ? sourceBounds.Width
                            : thumbnailSize.Width,
                        sourceBounds.Width > 0 && sourceBounds.Height > 0
                            ? sourceBounds.Height
                            : thumbnailSize.Height);
                }
                var windowDpi = UnsafeNative.GetDisplayScaleFactor(sourceWindow);
                var ownerDpi = UnsafeNative.GetDisplayScaleFactor(ownerWindow);
                var wpfToScreen = ownerDpi / windowDpi;
                var screenToWpf = windowDpi / ownerDpi;
                return (source.Scale(screenToWpf), thumbnailSize, sourceWindowSize.Scale(wpfToScreen));
            }
            catch (Exception e)
            {
                Log.Warn($"Failed to build ThumbnailUpdateArgs, args: { new { thumbnail, sourceRegion = sourceBounds } }", e);
                throw;
            }
        }
        
        private static ThumbnailUpdateArgs PrepareUpdateArgs(
            Thumbnail thumbnail,
            Size canvasSize,
            UIElement canvas,
            Window owner,
            double opacity,
            WinRectangle sourceRegion,
            WinSize sourceRegionOriginalSize,
            WinSize sourceSize)
        {
            try
            {
                Guard.ArgumentIsTrue(CanUpdateThumbnail(thumbnail), "CanUpdateThumbnail");

                var ownerLocation = canvas.TranslatePoint(new Point(0, 0), owner);
                var destination = new Rect(ownerLocation, canvasSize).ScaleToScreen();
                
                
                var result = new ThumbnailUpdateArgs
                {
                    DestinationRegion = destination,
                    SourceRegion = sourceRegion,
                    SourceRegionOriginalSize = sourceRegionOriginalSize,
                    Thumbnail = thumbnail,
                    Opacity = ToByte(opacity),
                    SourceSize = sourceSize,
                };

                return result;
            }
            catch (Exception e)
            {
                Log.Warn($"Failed to build ThumbnailUpdateArgs, args: { new { thumbnail, sourceRegion, sourceSize, sourceRegionOriginalSize, opacity } }", e);
                throw;
            }
        }
        
        private static bool CanUpdateThumbnail(Thumbnail thumbnail)
        {
            try
            {
                return thumbnail != null && !thumbnail.IsInvalid;
            }
            catch (Exception e)
            {
                Log.Warn("Failed to check CanUpdateThumbnail", e);
                throw;
            }
        }
        
        private static void UpdateThumbnail(ThumbnailUpdateArgs args)
        {
            try
            {
                if (args.Thumbnail == null)
                {
                    return;
                }

                args.Thumbnail.Update(
                    destination: args.DestinationRegion, 
                    source: args.SourceRegion, 
                    opacity: args.Opacity, 
                    visible: true,
                    onlyClientArea: true);
            }
            catch (Exception ex)
            {
                Log.Error($"UpdateThumbnail error, args: {args}", ex);
                throw;
            }
        }

        private struct ThumbnailArgs
        {
            public Window Owner { [CanBeNull] get; [CanBeNull] set; }
            public WindowHandle SourceWindow { [CanBeNull] get; [CanBeNull] set; }
            public bool IsVisible { get; set; }

            public override string ToString()
            {
                return $"{nameof(SourceWindow)}: {SourceWindow}, {nameof(IsVisible)}: {IsVisible}";
            }
        }
        
        private struct ThumbnailUpdateArgs
        {
            public Thumbnail Thumbnail { get; set; }
            public WinSize SourceRegionOriginalSize { get; set; }
            public WinSize SourceSize { get; set; }
            public WinRectangle SourceRegion { get; set; }
            public WinRectangle DestinationRegion { get; set; }
            public byte Opacity { get; set; }

            public override string ToString()
            {
                return $"{nameof(Thumbnail)}(IsInvalid): {Thumbnail}({Thumbnail?.IsInvalid}), {nameof(SourceRegionOriginalSize)}: {SourceRegionOriginalSize}, {nameof(SourceRegion)}: {SourceRegion}, {nameof(DestinationRegion)}: {DestinationRegion},  {nameof(Opacity)}: {Opacity}";
            }
        }

        private static byte ToByte(double value)
        {
            if (value < 0)
            {
                value = 0;
            }
            else if (value > 1)
            {
                value = 1;
            }

            return (byte) (value * byte.MaxValue);
        }
    }
}