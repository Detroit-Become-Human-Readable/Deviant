using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Deviant.Assets;
using Deviant.Browser;

namespace Deviant.Desktop;

/// <summary>Displays original localization occurrences linked to one audio resource.</summary>
public partial class AudioDialogueView : UserControl
{
    private LocalizationAudioLookupResult? result;
    private LocalizationAudioTextOccurrence[] occurrences = [];
    private ICollectionView? occurrenceView;
    private Func<LocalizationAudioTextOccurrence, CancellationToken, Task>? textNavigator;
    private CancellationToken textNavigationToken;

    public AudioDialogueView()
    {
        InitializeComponent();
        Clear();
    }

    public LocalizationAudioLookupResult? CurrentResult => result;
    public LocalizationAudioTextOccurrence? SelectedOccurrence => Occurrences.SelectedItem as LocalizationAudioTextOccurrence;

    /// <summary>The root-provided explicit navigation action for the selected localization record.</summary>
    public Func<LocalizationAudioTextOccurrence, CancellationToken, Task>? TextNavigator
    {
        get => textNavigator;
        set
        {
            textNavigator = value;
            UpdateOpenButton();
        }
    }

    /// <summary>The root's current token for the explicit Open text action.</summary>
    public CancellationToken TextNavigationToken
    {
        get => textNavigationToken;
        set => textNavigationToken = value;
    }

    public event EventHandler? SelectedOccurrenceChanged;

    public void SetResult(LocalizationAudioLookupResult value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Clear();

        result = value;
        occurrences = value.Occurrences.ToArray();
        var languages = occurrences.Select(item => item.TextOccurrence.Language)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(language => language, StringComparer.OrdinalIgnoreCase)
            .Select(language => new LanguageChoice(language, language));
        Languages.ItemsSource = new[] { new LanguageChoice(null, "Any") }.Concat(languages).ToArray();
        Languages.SelectedIndex = 0;

        occurrenceView = CollectionViewSource.GetDefaultView(occurrences);
        occurrenceView.Filter = MatchesLanguage;
        Occurrences.ItemsSource = occurrenceView;
        UpdateEmptyMessage();
        Advanced.Text = BuildAdvanced(value, null);
    }

    public void SetStatus(string message)
    {
        Status.Text = message ?? string.Empty;
        Status.Visibility = Status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateEmptyMessage();
    }

    public void Clear()
    {
        result = null;
        occurrences = [];
        occurrenceView = null;
        Languages.ItemsSource = null;
        Occurrences.ItemsSource = null;
        Occurrences.SelectedItem = null;
        EmptyMessage.Text = string.Empty;
        EmptyMessage.Visibility = Visibility.Collapsed;
        SelectedLanguage.Text = "Select dialogue";
        SelectedText.Clear();
        Advanced.Clear();
        AdvancedPanel.IsExpanded = false;
        Status.Text = string.Empty;
        Status.Visibility = Visibility.Collapsed;
        UpdateOpenButton();
        SelectedOccurrenceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Invokes the root-provided navigation callback for the full selected occurrence.</summary>
    public Task OpenSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        var selected = SelectedOccurrence;
        var navigate = textNavigator;
        return selected is null || navigate is null ? Task.CompletedTask : navigate(selected, cancellationToken);
    }

    private bool MatchesLanguage(object value)
    {
        var filter = (Languages.SelectedItem as LanguageChoice)?.Language;
        return filter is null || value is LocalizationAudioTextOccurrence occurrence &&
            string.Equals(occurrence.TextOccurrence.Language, filter, StringComparison.Ordinal);
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (occurrenceView is null) return;
        if (SelectedOccurrence is { } selected &&
            (Languages.SelectedItem as LanguageChoice)?.Language is { } language &&
            !string.Equals(selected.TextOccurrence.Language, language, StringComparison.Ordinal))
            Occurrences.SelectedItem = null;

        occurrenceView.Refresh();
        UpdateEmptyMessage();
    }

    private void OccurrenceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedOccurrence is { } selected)
        {
            SelectedLanguage.Text = selected.TextOccurrence.Language;
            SelectedText.Text = selected.TextOccurrence.Text ?? string.Empty;
        }
        else
        {
            SelectedLanguage.Text = "Select dialogue";
            SelectedText.Clear();
        }

        UpdateOpenButton();
        if (result is { } current) Advanced.Text = BuildAdvanced(current, SelectedOccurrence);
        SelectedOccurrenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEmptyMessage()
    {
        var visible = result is not null && Status.Text.Length == 0 && Occurrences.Items.Count == 0;
        EmptyMessage.Text = result is null ? string.Empty :
            occurrences.Length == 0 ? "No dialogue linked." : "No matches for this language.";
        EmptyMessage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateOpenButton() => OpenText.IsEnabled = SelectedOccurrence is not null && textNavigator is not null;

    private async void OpenTextClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await OpenSelectedTextAsync(TextNavigationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus(error.Message);
        }
    }

    private static string BuildAdvanced(LocalizationAudioLookupResult current,
        LocalizationAudioTextOccurrence? selected)
    {
        var lines = new List<string>
        {
            $"Audio resource: {current.AudioIdentity}",
            $"Dialogue headers: {string.Join(", ", current.DialogueHeaderIdentities)}"
        };

        if (selected is { } occurrence)
        {
            var text = occurrence.TextOccurrence;
            var pointer = occurrence.PointerOccurrence;
            var relationship = occurrence.Relationship;
            lines.Add("Selected occurrence");
            lines.Add($"Text identity: {text.Identity}");
            lines.Add($"Text owner: {text.ResourceIdentity} · resource {text.ResourceOrdinal}");
            lines.Add($"Text key: {text.Key ?? "(no key)"} · language {text.Language} · raw {text.RawLanguageCode}");
            lines.Add($"Pointer identity: {pointer.Identity}");
            lines.Add($"Pointer owner: {pointer.ResourceIdentity} · resource {pointer.ResourceOrdinal}");
            lines.Add($"Pointer key: {pointer.Key ?? "(no key)"} · language {pointer.Language} · raw {pointer.RawLanguageCode}");
            lines.Add($"Relationship key: {relationship.Key} · raw language {relationship.RawLanguageCode}");
            lines.Add($"Dialogue header: {relationship.DialogueHeaderIdentity?.ToString() ?? "unresolved"}");
            lines.Add($"Audio resource: {relationship.AudioResourceIdentity?.ToString() ?? "unresolved"}");
            lines.Add($"Dialogue event: {relationship.DialogueEventIdentity?.ToString() ?? "unresolved"}");
            foreach (var evidence in relationship.EvidencePath)
                lines.Add($"Evidence: {evidence.Kind} · {evidence.Label} · {evidence.Evidence.Source} · {evidence.Evidence.Description}");
        }

        lines.Add("Lookup diagnostics");
        if (current.Diagnostics.Count == 0)
        {
            lines.Add("None");
        }
        else
        {
            foreach (var diagnostic in current.Diagnostics)
            {
                lines.Add($"{diagnostic.Code} · {diagnostic.Status} · count {diagnostic.Count}: {diagnostic.Message}");
                if (diagnostic.PointerOccurrenceIdentity is { } pointerIdentity)
                    lines.Add($"Pointer occurrence: {pointerIdentity}");
                if (diagnostic.SourceResources.Count > 0)
                    lines.Add($"Sources: {string.Join(", ", diagnostic.SourceResources)}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record LanguageChoice(string? Language, string Label);
}
