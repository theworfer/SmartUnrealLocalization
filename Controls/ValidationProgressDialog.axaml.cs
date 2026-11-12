using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Modal blocking dialog shown while ValidationService.RunInitialAsync
    /// chews through every row of a freshly-opened file. Owns a single
    /// CancellationTokenSource; Cancel / title-bar X both cancel the work.
    ///
    /// <para>
    /// Usage from the caller:
    /// <code>
    ///   var dialog = new ValidationProgressDialog();
    ///   var workTask = ValidationService.Instance.RunInitialAsync(
    ///       rows, dialog.Progress, dialog.CancellationToken);
    ///   var dialogTask = dialog.ShowDialog(parentWindow);
    ///   try { await workTask; }
    ///   catch (OperationCanceledException) { /* WasCancelled is true */ }
    ///   finally { dialog.Close(); await dialogTask; }
    /// </code>
    /// </para>
    /// </summary>
    public partial class ValidationProgressDialog : Window
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _closing;

        public ValidationProgressDialog()
        {
            InitializeComponent();
            Closing += OnClosing;
            ProgressSink = new Progress<ValidationProgressUpdate>(OnProgress);
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        /// <summary>Cancellation token wired into Cancel button + title-bar X.</summary>
        public CancellationToken CancellationToken => _cts.Token;

        /// <summary>
        /// Progress sink for ValidationService.RunInitialAsync. Renamed from
        /// "Progress" to dodge a collision with the auto-generated x:Name field
        /// for the ProgressBar inside the XAML (Avalonia source-gen creates a
        /// private ProgressBar Progress field when x:Name="Progress" is set).
        /// </summary>
        public IProgress<ValidationProgressUpdate> ProgressSink { get; }

        /// <summary>True if the user pressed Cancel (or closed the dialog) before completion.</summary>
        public bool WasCancelled { get; private set; }

        private void OnProgress(ValidationProgressUpdate update)
        {
            // Progress fires from a background continuation; marshal to UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                if (_closing) return;
                var pb = this.FindControl<ProgressBar>("Progress");
                var label = this.FindControl<TextBlock>("ProgressLabel");
                if (pb != null && update.Total > 0)
                {
                    pb.Maximum = update.Total;
                    pb.Value = update.Current;
                }
                if (label != null)
                {
                    // Format: "1234 / 50000 rows · light"
                    var fmt = Loc.T("validation.progressFormat") ?? "{0} / {1} rows";
                    label.Text = string.Format(fmt, update.Current, update.Total);
                }
            });
        }

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            RequestCancel();
        }

        private void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            // If the dialog is being closed while validation is still running,
            // treat that as Cancel — otherwise the work task would keep
            // mutating row.Problems against a window the user already dismissed.
            // RequestClose (the work finished) closes the dialog too — that is not
            // a cancel. Treating it as one marked every open as cancelled, and the
            // caller then wiped the problems the validation had just found.
            if (_closing) return;
            if (!_cts.IsCancellationRequested)
                RequestCancel();
        }

        private void RequestCancel()
        {
            if (_cts.IsCancellationRequested) return;
            WasCancelled = true;
            _cts.Cancel();
            // The caller will call Close() once the work task observes the
            // cancellation, so we don't Close here — closing twice in flight
            // races with the awaiting dialogTask.
        }

        /// <summary>
        /// Caller's clean exit. Idempotent — safe to call after Closing has
        /// already fired (e.g. user dismissed mid-flight).
        /// </summary>
        public void RequestClose()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }
    }
}
