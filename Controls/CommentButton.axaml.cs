using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>
    /// Comment indicator + editor for one Target cell. The icon is faint at rest,
    /// revealed on row hover, and shown filled-accent when the row HAS a comment
    /// (driven by <see cref="TranslationRow.CommentIconOpacity"/> / HasComment).
    /// Clicking opens a small popup editor. Save paths converge on the Flyout's
    /// Closed handler: Save / Ctrl+Enter / ✕ / click-outside all persist; Esc
    /// cancels; empty text or Delete removes the comment. Persistence goes through
    /// <see cref="CommentService"/> (per-file sidecar), keyed by the row's Key.
    /// </summary>
    public partial class CommentButton : UserControl
    {
        private Flyout? _flyout;
        private TextBox? _editor;
        private TranslationRow? _row;
        private bool _cancel;
        private bool _delete;

        public CommentButton() => InitializeComponent();
        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        // The Button.Flyout opens itself on click; nothing else to do here. Mark
        // handled so the press doesn't also drive the DataGrid row selection.
        private void OnClick(object? sender, RoutedEventArgs e) => e.Handled = true;

        private void OnFlyoutOpened(object? sender, EventArgs e)
        {
            _flyout = sender as Flyout;
            _cancel = false;
            _delete = false;
            _row = DataContext as TranslationRow;

            _editor = (_flyout?.Content as Control)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (_editor != null)
            {
                _editor.Text = _row?.Comment ?? "";
                _editor.CaretIndex = _editor.Text.Length;
                Dispatcher.UIThread.Post(() => _editor?.Focus(), DispatcherPriority.Loaded);
            }
        }

        // Single save point — covers Save button, ✕, Ctrl+Enter and click-outside
        // (all just hide the flyout). Esc / Delete set a flag first.
        private void OnFlyoutClosed(object? sender, EventArgs e)
        {
            if (_row == null) return;
            if (_cancel) return; // Esc — discard edits made this session

            var text = _editor?.Text?.Trim() ?? "";
            if (_delete || text.Length == 0)
            {
                _row.Comment = null;
                CommentService.Instance.Remove(_row.Namespace, _row.Key ?? "");
            }
            else
            {
                _row.Comment = text;
                CommentService.Instance.Set(_row.Namespace, _row.Key ?? "", text);
            }
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                _flyout?.Hide(); // → Closed saves
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _cancel = true;
                _flyout?.Hide();
            }
        }

        private void OnSaveClick(object? sender, RoutedEventArgs e) => _flyout?.Hide();
        private void OnCloseClick(object? sender, RoutedEventArgs e) => _flyout?.Hide();

        private void OnDeleteClick(object? sender, RoutedEventArgs e)
        {
            _delete = true;
            _flyout?.Hide();
        }
    }
}
