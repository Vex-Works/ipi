using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ipi.Desktop;

public sealed class InlineRunsTextBlock : TextBlock
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.Register(
        nameof(Runs),
        typeof(IEnumerable<ChatMessageRunItem>),
        typeof(InlineRunsTextBlock),
        new PropertyMetadata(null, OnRunsChanged));

    public IEnumerable<ChatMessageRunItem>? Runs
    {
        get => (IEnumerable<ChatMessageRunItem>?)GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    public event EventHandler<string>? LinkClicked;

    public InlineRunsTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    private static void OnRunsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is InlineRunsTextBlock block) block.Rebuild();
    }

    private void Rebuild()
    {
        Inlines.Clear();
        if (Runs is null) return;

        foreach (var item in Runs)
        {
            if (item.IsLink)
            {
                var hyperlink = new Hyperlink(new Run(item.Text))
                {
                    Foreground = TryFindResource("FeedbackAccent") as Brush ?? Brushes.LightGreen,
                    ToolTip = item.Target,
                };
                hyperlink.Click += (_, _) =>
                {
                    if (!string.IsNullOrWhiteSpace(item.Target)) LinkClicked?.Invoke(this, item.Target);
                };
                Inlines.Add(hyperlink);
            }
            else
            {
                Inlines.Add(new Run(item.Text));
            }
        }
    }
}
