using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using OpenShelf.App.ViewModels;

namespace OpenShelf.App.Views;

/// <summary>
/// Keeps Avalonia's native selectable text surface while painting persisted,
/// character-accurate highlight ranges as inline run backgrounds.
/// </summary>
public sealed class HighlightedSelectableTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<HighlightedSelectableTextBlock, string>(
            nameof(SourceText),
            string.Empty);

    public static readonly StyledProperty<IReadOnlyList<ReaderHighlightRange>>
        HighlightRangesProperty = AvaloniaProperty.Register<
            HighlightedSelectableTextBlock,
            IReadOnlyList<ReaderHighlightRange>>(
                nameof(HighlightRanges),
                Array.Empty<ReaderHighlightRange>());

    public static readonly StyledProperty<ReaderSpokenRange?> SpokenRangeProperty =
        AvaloniaProperty.Register<
            HighlightedSelectableTextBlock,
            ReaderSpokenRange?>(nameof(SpokenRange));

    private bool _isRebuilding;

    public string SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public IReadOnlyList<ReaderHighlightRange> HighlightRanges
    {
        get => GetValue(HighlightRangesProperty);
        set => SetValue(HighlightRangesProperty, value);
    }

    public ReaderSpokenRange? SpokenRange
    {
        get => GetValue(SpokenRangeProperty);
        set => SetValue(SpokenRangeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!_isRebuilding
            && (change.Property == HighlightRangesProperty
                || change.Property == SourceTextProperty
                || change.Property == SpokenRangeProperty))
        {
            RebuildInlines();
        }
    }

    private void RebuildInlines()
    {
        var text = SourceText ?? string.Empty;
        var ranges = HighlightRanges
            .Where(range => range.Length > 0 && range.Start < text.Length)
            .Select(
                range => range with
                {
                    Start = Math.Clamp(range.Start, 0, text.Length),
                    Length = Math.Clamp(
                        range.Length,
                        0,
                        text.Length - Math.Clamp(range.Start, 0, text.Length))
                })
            .Where(range => range.Length > 0)
            .ToArray();
        var spokenRange = ClampSpokenRange(SpokenRange, text.Length);

        _isRebuilding = true;
        try
        {
            var inlines = Inlines ?? new InlineCollection();
            if (!ReferenceEquals(Inlines, inlines))
            {
                Inlines = inlines;
            }

            inlines.Clear();
            if (text.Length == 0)
            {
                return;
            }

            var boundaries = new SortedSet<int> { 0, text.Length };
            foreach (var range in ranges)
            {
                boundaries.Add(range.Start);
                boundaries.Add(range.Start + range.Length);
            }


            if (spokenRange is not null)
            {
                boundaries.Add(spokenRange.Start);
                boundaries.Add(spokenRange.Start + spokenRange.Length);
            }

            var points = boundaries.ToArray();
            for (var index = 0; index + 1 < points.Length; index++)
            {
                var start = points[index];
                var end = points[index + 1];
                var run = new Run { Text = text[start..end] };
                var isSpoken = spokenRange is not null
                    && spokenRange.Start <= start
                    && spokenRange.Start + spokenRange.Length >= end;
                if (isSpoken)
                {
                    run.Background = SpokenHighlightBrush();
                }
                else
                {
                    var active = ranges.LastOrDefault(
                        range => range.Start <= start
                            && range.Start + range.Length >= end);
                    if (active is not null)
                    {
                        run.Background = HighlightBrush(active.ColorName);
                    }
                }

                inlines.Add(run);
            }
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    private static ReaderSpokenRange? ClampSpokenRange(
        ReaderSpokenRange? range,
        int textLength)
    {
        if (range is null || range.Length <= 0 || range.Start >= textLength)
        {
            return null;
        }

        var start = Math.Clamp(range.Start, 0, textLength);
        var length = Math.Clamp(range.Length, 0, textLength - start);
        return length == 0 ? null : new ReaderSpokenRange(start, length);
    }

    private static IBrush SpokenHighlightBrush() =>
        new SolidColorBrush(Color.Parse("#8A9C7BFF"));

    private static IBrush HighlightBrush(string colorName)
    {
        var color = colorName switch
        {
            "Green" => "#7258D68D",
            "Blue" => "#7266A9FF",
            "Pink" => "#72FF78B7",
            _ => "#72FFD166"
        };
        return new SolidColorBrush(Color.Parse(color));
    }
}
