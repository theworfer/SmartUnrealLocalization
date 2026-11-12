using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using SmartUnrealLocalization.Models;
using SmartUnrealLocalization.ViewModels;
using SmartUnrealLocalization.Views;
using Xunit;
using static SmartUnrealLocalization.Tests.Ui.UiSession;

namespace SmartUnrealLocalization.Tests.Ui;

/// <summary>
/// The Target editor's keys, through real keyboard input (2026-10-21).
///
/// <para>Found in the real window on 2026-10-20: Enter inserted a line break
/// instead of committing — the TextBox took it before the editor's handler — and
/// Ctrl+Z while typing reverted a committed edit in ANOTHER row, because the
/// window's KeyBinding won over the field's own undo.</para>
/// </summary>
[Collection("AppState")]
public class EditorKeysUiTests
{
    private static async Task<(TextBox Editor, TranslationRow Row)> EditAsync(MainWindow window, MainWindowViewModel vm, int index)
    {
        var row = vm.AllRows[index];
        typeof(MainWindow).GetMethod("StartEditingTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, new object[] { row });
        await Settle();
        var editor = window.GetVisualDescendants().OfType<TextBox>()
            .First(t => t.Classes.Contains("gridEditor") && t.IsEffectivelyVisible);
        editor.Focus();
        editor.CaretIndex = editor.Text?.Length ?? 0;
        await Settle();
        return (editor, row);
    }

    private static void Press(MainWindow w, Key key, RawInputModifiers mods = RawInputModifiers.None, PhysicalKey phys = PhysicalKey.None)
    {
        w.KeyPress(key, mods, phys, null);
        w.KeyRelease(key, mods, phys, null);
    }

    [Fact]
    public Task Enter_commits_and_moves_to_the_next_row()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("keys1.locres", 20));
            await Settle();
            var (_, row) = await EditAsync(window, vm, 0);
            window.KeyTextInput("hello");
            Press(window, Key.Enter, phys: PhysicalKey.Enter);
            await Settle();

            Assert.False(row.IsEditing);
            Assert.Equal("hello", row.Target);
            Assert.Same(vm.Rows[1], vm.FocusedRow);
        });

    [Fact]
    public Task Shift_Enter_is_a_line_break_and_Escape_cancels()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("keys2.locres", 20));
            await Settle();
            var (_, row) = await EditAsync(window, vm, 0);
            window.KeyTextInput("one");
            Press(window, Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter);
            window.KeyTextInput("two");
            await Settle();
            Assert.True(row.IsEditing);
            Assert.Contains("\n", row.EditValue);
            Assert.EndsWith("two", row.EditValue);

            Press(window, Key.Escape, phys: PhysicalKey.Escape);
            await Settle();
            Assert.False(row.IsEditing);
            Assert.Equal("", row.Target ?? "");
        });

    [Fact]
    public Task Ctrl_Z_in_the_editor_undoes_the_typing_and_never_another_rows_edit()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("keys3.locres", 20));
            await Settle();
            vm.AllRows[5].Target = "PRIOR EDIT";          // on the undo stack
            await Settle();

            var (editor, _) = await EditAsync(window, vm, 0);
            window.KeyTextInput("abc");
            await Settle();
            Press(window, Key.Z, RawInputModifiers.Control, PhysicalKey.Z);
            await Settle();

            Assert.Equal("PRIOR EDIT", vm.AllRows[5].Target);
            Assert.NotEqual("abc", editor.Text);
        });

    [Fact]
    public Task Outside_a_text_field_Ctrl_Z_and_Ctrl_Y_still_undo_and_redo_the_grid()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("keys5.locres", 20));
            await Settle();
            vm.AllRows[2].Target = "GRID EDIT";
            await Settle();
            window.FindControl<DataGrid>("MainDataGrid")!.Focus();
            await Settle();

            Press(window, Key.Z, RawInputModifiers.Control, PhysicalKey.Z);
            await Settle();
            Assert.Equal("", vm.AllRows[2].Target ?? "");
            Press(window, Key.Y, RawInputModifiers.Control, PhysicalKey.Y);
            await Settle();
            Assert.Equal("GRID EDIT", vm.AllRows[2].Target);
        });

    [Fact]
    public Task Ctrl_T_in_the_editor_puts_the_source_into_it()
        => WithWindowAsync(async (window, vm) =>
        {
            await vm.OpenFileAsync(MakeLocres("keys4.locres", 20));
            await Settle();
            var (editor, row) = await EditAsync(window, vm, 3);
            Press(window, Key.T, RawInputModifiers.Control, PhysicalKey.T);
            await Settle();

            Assert.True(row.IsEditing);
            Assert.Equal(row.Source, editor.Text);
            Assert.Equal("", row.Target ?? "");   // nothing committed yet
        });
}
