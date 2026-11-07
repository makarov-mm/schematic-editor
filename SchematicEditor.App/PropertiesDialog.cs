using System.Windows;
using System.Windows.Controls;

namespace SchematicEditor.App;

/// <summary>Edits a symbol's reference designator and value; two fields need no XAML.</summary>
public sealed class PropertiesDialog : Window
{
    private readonly TextBox _refDesBox;
    private readonly TextBox _valueBox;

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

        _refDesBox = new TextBox { Text = refDes, Margin = new Thickness(6, 2, 0, 2) };
        _valueBox = new TextBox { Text = value, Margin = new Thickness(6, 2, 0, 2) };
        AddRow(grid, 0, "RefDes:", _refDesBox);
        AddRow(grid, 1, "Value:", _valueBox);

        var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { ok, new Button { Content = "Cancel", Width = 70, IsCancel = true } },
        };
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

        Content = grid;
        Loaded += (_, _) =>
        {
            _refDesBox.Focus();
            _refDesBox.SelectAll();
        };
    }

    public string RefDes => _refDesBox.Text.Trim();
    public string Value => _valueBox.Text.Trim();

    private static void AddRow(Grid grid, int row, string label, UIElement editor)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(text, row);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
    }
}
