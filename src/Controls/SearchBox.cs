using Avalonia.Input;

namespace DeadlockAdvisor.Controls;

/// <summary>The app's search field: large text, a clear button, and Escape empties it before it does anything else.</summary>
public class SearchBox : TextBox
{
    public SearchBox()
    {
        Classes.Add("search");
        Classes.Add("clearButton");
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(Text))
        {
            Text = "";
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
