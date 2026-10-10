using System.Windows;
using System.Windows.Controls;
using ScMapper.Core;
using ScMapper.Core.Input;
using ScMapper.Core.Model;

namespace ScMapper.App.Controls;

public sealed record BindRow(ScBind Bind, string Title, string Group, string Mode);

/// <summary>Shows and edits everything attached to one physical input of one stick.</summary>
public partial class ButtonInspector : UserControl
{
    private AppController? _c;
    private StickRecord? _stick;
    private string? _input;
    private bool _loading;

    public ButtonInspector()
    {
        InitializeComponent();
        PressKeys.ComboChanged += _ => SaveRule();
        ReleaseKeys.ComboChanged += _ => SaveRule();
    }

    public void Show(AppController c, StickRecord? stick, string? input)
    {
        _c = c; _stick = stick; _input = input;
        Refresh();
    }

    /// <summary>Re-reads the current rule/binds (call after external changes).</summary>
    public void Refresh()
    {
        if (_c == null || _stick == null || _input == null)
        {
            Empty.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Collapsed;
            return;
        }
        _loading = true;
        Empty.Visibility = Visibility.Collapsed;
        Body.Visibility = Visibility.Visible;
        var prof = _c.Store.Profile(_stick.ProfileId);
        Title.Text = $"{_stick.Name} · {InputNames.Pretty(_input)}";
        Subtitle.Text = prof == null ? "No controller profile assigned" : $"Profile: {prof.Name}" +
            (_stick.ScInstance is { } n ? $"   ·   Star Citizen: js{n}_{_input}" : "");
        ButtonRule? rule = null;
        prof?.Rules.TryGetValue(_input, out rule);
        PressKeys.SetComboSilently(rule?.OnPress?.Keys);
        ReleaseKeys.SetComboSilently(rule?.OnRelease?.Keys);
        HoldCheck.IsChecked = rule?.OnPress?.Hold ?? false;
        LabelBox.Text = rule?.Label ?? "";
        KeyError.Visibility = Visibility.Collapsed;
        var rows = (prof?.Binds ?? new List<ScBind>()).Where(b => b.Input == _input).Select(b =>
        {
            var def = _c.Catalog?.Find(b.Map, b.Action);
            var grp = _c.Catalog?.Groups.FirstOrDefault(g => g.Name == b.Map);
            return new BindRow(b, def?.Label ?? b.Action, grp != null ? $"{grp.Category} › {grp.Label}" : b.Map,
                b.Mode ?? "");
        }).ToList();
        BindList.ItemsSource = rows;
        NoBinds.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
    }

    private void SaveRule()
    {
        if (_loading || _c == null || _stick == null || _input == null) return;
        var prof = _c.EnsureProfile(_stick);
        string? error = null;
        foreach (var box in new[] { PressKeys, ReleaseKeys })
            if (!string.IsNullOrWhiteSpace(box.Text) && !KeyCombo.TryParse(box.Text, out _, out error)) break;
            else error = null;
        KeyError.Text = error ?? "";
        KeyError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        var rule = new ButtonRule
        {
            Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
            OnPress = string.IsNullOrWhiteSpace(PressKeys.Text) ? null
                : new KeyAction { Keys = PressKeys.Text, Hold = HoldCheck.IsChecked == true },
            OnRelease = string.IsNullOrWhiteSpace(ReleaseKeys.Text) ? null : new KeyAction { Keys = ReleaseKeys.Text },
        };
        _c.SetRule(prof, _input, rule);
    }

    private void PressClear_Click(object s, RoutedEventArgs e) { PressKeys.SetComboSilently(""); HoldCheck.IsChecked = false; SaveRule(); }
    private void ReleaseClear_Click(object s, RoutedEventArgs e) { ReleaseKeys.SetComboSilently(""); SaveRule(); }
    private void HoldCheck_Click(object s, RoutedEventArgs e) => SaveRule();
    private void LabelBox_TextChanged(object s, TextChangedEventArgs e) => SaveRule();

    private void RemoveBind_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _stick == null) return;
        if (((FrameworkElement)s).Tag is not BindRow row) return;
        var prof = _c.Store.Profile(_stick.ProfileId);
        if (prof != null) _c.RemoveBind(prof, row.Bind);
        Refresh();
    }

    private void ClearAll_Click(object s, RoutedEventArgs e)
    {
        if (_c == null || _stick == null || _input == null) return;
        var prof = _c.Store.Profile(_stick.ProfileId);
        if (prof == null) return;
        prof.Binds.RemoveAll(b => b.Input == _input);
        prof.Rules.Remove(_input);
        _c.Touch();
        Refresh();
    }
}
