using System.Windows;
using System.Windows.Controls;
using Magpienest.Sdk.Localization;

namespace Magpienest.Plugins.WebUi;

/// <summary>
/// Dialog content for typing a password: once (a certificate's password) or twice (a new web interface password).
/// Built in code because <see cref="PasswordBox"/> cannot be bound.
/// </summary>
internal sealed class PasswordDialog : StackPanel
{
    private readonly PasswordBox _first = new();
    private readonly PasswordBox? _second;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly bool _isNewPassword;

    /// <summary>Creates the content.</summary>
    /// <param name="hintKey">Text above the boxes.</param>
    /// <param name="isNewPassword">True: two boxes, at least <see cref="PasswordHasher.MinLength"/> characters, must match.</param>
    public PasswordDialog(string hintKey, bool isNewPassword)
    {
        _isNewPassword = isNewPassword;
        Children.Add(Text(hintKey, secondary: true));
        Children.Add(Text(isNewPassword ? "WebUi.Password.New" : "WebUi.Password.Password", secondary: false));
        Children.Add(_first);
        _first.PasswordChanged += (_, _) => Validate();
        if (isNewPassword)
        {
            _second = new PasswordBox();
            Children.Add(Text("WebUi.Password.Repeat", secondary: false));
            Children.Add(_second);
            _second.PasswordChanged += (_, _) => Validate();
        }

        _error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        Children.Add(_error);
        Loaded += (_, _) => _first.Focus();
    }

    /// <summary>Whether the dialog may be accepted.</summary>
    public bool IsValid { get; private set; }

    /// <summary>The typed password.</summary>
    public string Password => _first.Password;

    private void Validate()
    {
        if (!_isNewPassword)
        {
            IsValid = _first.Password.Length > 0;
            return;
        }

        var first = _first.Password;
        var second = _second!.Password;
        IsValid = PasswordHasher.IsAcceptable(first) && first == second;
        _error.Text = first.Length > 0 && !PasswordHasher.IsAcceptable(first)
            ? Loc.Format("WebUi.Password.TooShort", PasswordHasher.MinLength)
            : second.Length > 0 && first != second ? Loc.Get("WebUi.Password.Mismatch") : "";
    }

    private static TextBlock Text(string key, bool secondary)
    {
        var block = new TextBlock { Text = Loc.Get(key), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, secondary ? 0 : 12, 0, 4) };
        if (secondary)
        {
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        }

        return block;
    }
}
