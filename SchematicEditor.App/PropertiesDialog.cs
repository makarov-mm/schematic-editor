using System.Windows;
using System.Windows.Controls;

namespace SchematicEditor.App;

/// <summary>Minimal refdes/value editor, built in code — no XAML needed for two fields.</summary>
public sealed class PropertiesDialog : Window
{
    private readonly TextBox _refBox;
    private readonly TextBox _valBox;

    public string RefDes => _refBox.Text.Trim();
    public string Value => _valBox.Text.Trim();

    public PropertiesDialog(string refDes, string value)
    {
        Title = "Symbol properties";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        for (int i = 0; i < 3; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _refBox = new TextBox { Text = refDes, Margin = new Thickness(6, 2, 0, 2) };
        _valBox = new TextBox { Text = value, Margin = new Thickness(6, 2, 0, 2) };

        AddRow(grid, 0, "RefDes:", _refBox);
        AddRow(grid, 1, "Value:", _valBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) => { _refBox.Focus(); _refBox.SelectAll(); };
    }

    private static void AddRow(Grid grid, int row, string label, UIElement editor)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 0);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
    }
}
