using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Ipi.Desktop;

public sealed class SelectableAssistantMessageBox : RichTextBox
{
    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines),
        typeof(IEnumerable<ChatMessageLineItem>),
        typeof(SelectableAssistantMessageBox),
        new PropertyMetadata(null, OnLinesChanged));

    public IEnumerable<ChatMessageLineItem>? Lines
    {
        get => (IEnumerable<ChatMessageLineItem>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public event EventHandler<string>? LinkClicked;

    public SelectableAssistantMessageBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        Cursor = Cursors.IBeam;
        FocusVisualStyle = null;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        AcceptsTab = false;
        Document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            Background = Brushes.Transparent,
        };
    }

    private static void OnLinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SelectableAssistantMessageBox box) box.Rebuild();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        IsReadOnlyCaretVisible = true;
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        IsReadOnlyCaretVisible = false;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty ||
            e.Property == FontFamilyProperty ||
            e.Property == FontSizeProperty ||
            e.Property == FontWeightProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Document.Blocks.Clear();
        Document.FontFamily = FontFamily;
        Document.FontSize = FontSize;
        Document.FontWeight = FontWeight;
        Document.Foreground = Foreground;
        Document.PagePadding = new Thickness(0);

        if (Lines is null) return;

        foreach (var line in Lines)
        {
            var paragraph = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(0),
                LineHeight = 21,
                FontFamily = FontFamily,
                FontSize = FontSize,
                FontWeight = FontWeight,
                Foreground = Foreground,
            };

            if (line.IsBullet)
            {
                paragraph.Inlines.Add(new Run("• ")
                {
                    Foreground = TryFindResource("FeedbackAccent") as Brush ?? Brushes.LightGreen,
                    FontWeight = FontWeights.SemiBold,
                });
            }

            foreach (var run in line.Runs)
            {
                if (run.IsLink)
                {
                    var hyperlink = new Hyperlink(new Run(run.Text))
                    {
                        Foreground = TryFindResource("FeedbackAccent") as Brush ?? Brushes.LightGreen,
                        ToolTip = run.Target,
                    };
                    hyperlink.Click += (_, _) =>
                    {
                        if (!string.IsNullOrWhiteSpace(run.Target)) LinkClicked?.Invoke(this, run.Target);
                    };
                    paragraph.Inlines.Add(hyperlink);
                }
                else
                {
                    paragraph.Inlines.Add(new Run(run.Text));
                }
            }

            Document.Blocks.Add(paragraph);
        }
    }
}
