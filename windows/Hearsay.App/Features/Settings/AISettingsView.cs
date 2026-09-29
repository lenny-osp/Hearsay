using System.ComponentModel;
using Hearsay.App.Features.Notes;
using Hearsay.Core.Notes;
using Hearsay.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static Hearsay.App.Features.Settings.SettingsLayout;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// Settings > AI (PLAN.md sections 7 and 8). Port of
/// mac/Hearsay/Features/Notes/AISettingsTab.swift: the provider preset;
/// for the CLI presets (GitHub Copilot, Claude Code, Codex, Antigravity) the
/// program path (the detected one as the placeholder), model, reasoning
/// effort, "Check &lt;tool&gt;" through <see cref="NotesPipeline.CheckInstallationAsync"/>
/// and the tool's caption; for the HTTP presets (Ollama / LM Studio, Custom)
/// the endpoint, model, reasoning effort, temperature, the token header
/// (Custom), extra headers, and the token (Credential Manager, through the
/// store's <see cref="ISecretStore"/>); the ask-before-sending switch and
/// "Test connection"; and the prompt templates (the built-in "General
/// meeting" read-only; user templates added, edited, deleted, made the
/// default), all stored in the app's one settings file by
/// <see cref="AIProviderStore"/>.
/// <para>
/// Windows differences: each CLI caption says "on this PC" and adds the
/// tool's Windows install command (<see cref="CliTools.InstallCommand"/>);
/// the path is found in-process (<see cref="CliLocator"/>, no login shell).
/// The Mac has no timeout field and neither has this page (the CLI time
/// limit is <see cref="CliClient.Timeout"/>).
/// </para>
/// </summary>
internal sealed partial class AISettingsView : UserControl
{
    private readonly AIProviderStore store;
    private readonly ComboBox preset;
    private readonly StackPanel providerFields;
    private readonly Border tokenCard;
    private readonly TextBlock tokenHeader;
    private readonly ToggleSwitch askBeforeSending;
    private readonly Button testConnection;
    private readonly StackPanel testResult;
    private readonly ListView templateList;
    private readonly Button editTemplate;
    private readonly Button deleteTemplate;
    private readonly Button setDefaultTemplate;
    private string? builtPresetId;
    private Guid? selectedTemplateId;
    private Guid shownDefaultTemplateId;
    private TextBox? cliPath;
    private TextBox? headers;
    private TextBlock? tokenStatus;
    private Button? tokenRemove;
    private CliTool? checkedTool;
    private bool refreshing;
    private int presetGeneration;

    public AISettingsView(AppShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        store = shell.AIProviders;
        var page = Page();

        // Provider
        preset = new ComboBox { MinWidth = 280 };
        foreach (var item in ProviderPreset.All) preset.Items.Add(Strings.CoreText(item.Name));
        preset.SelectionChanged += (_, _) =>
        {
            if (refreshing || preset.SelectedIndex < 0) return;
            var chosen = ProviderPreset.All[preset.SelectedIndex];
            if (chosen.Id != store.Configuration.PresetId) store.SelectPreset(chosen);
        };
        providerFields = new StackPanel { Spacing = 10 };
        page.Children.Add(Header(Strings.AISectionProvider));
        page.Children.Add(Card(Field(Strings.AIPreset, preset), providerFields));

        // Token (HTTP presets only)
        tokenHeader = Header(Strings.AISectionToken);
        tokenCard = Card();
        page.Children.Add(tokenHeader);
        page.Children.Add(tokenCard);

        // Sending
        var (askRow, askSwitch) = Toggle(Strings.AIAskBeforeSending);
        askBeforeSending = askSwitch;
        askBeforeSending.Toggled += (_, _) =>
        {
            if (!refreshing) store.Configuration = store.Configuration with { AskBeforeSending = askBeforeSending.IsOn };
        };
        testConnection = new Button { Content = Strings.AITestConnection };
        testConnection.Click += (_, _) => _ = TestConnectionAsync();
        testResult = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        testRow.Children.Add(testConnection);
        testRow.Children.Add(testResult);
        page.Children.Add(Header(Strings.AISectionSending));
        page.Children.Add(Card(askRow, testRow));

        // Prompt templates
        templateList = new ListView { SelectionMode = ListViewSelectionMode.Single, MinHeight = 90 };
        templateList.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            var index = templateList.SelectedIndex;
            selectedTemplateId = index >= 0 && index < store.Templates.Count ? store.Templates[index].Id : null;
            UpdateTemplateButtons();
        };
        var add = new Button { Content = Strings.AITemplateAdd };
        add.Click += (_, _) => _ = EditTemplateAsync(
            new PromptTemplate(Strings.AINewTemplateName, PromptTemplate.GeneralMeeting.Instructions));
        editTemplate = new Button { Content = Strings.AITemplateEdit };
        editTemplate.Click += (_, _) =>
        {
            if (SelectedTemplate is { IsBuiltIn: false } selected) _ = EditTemplateAsync(selected);
        };
        deleteTemplate = new Button { Content = Strings.Delete };
        deleteTemplate.Click += (_, _) =>
        {
            if (SelectedTemplate is { IsBuiltIn: false } selected) store.DeleteTemplate(selected.Id);
            selectedTemplateId = null;
            RefreshTemplates();
        };
        setDefaultTemplate = new Button { Content = Strings.AITemplateSetDefault };
        setDefaultTemplate.Click += (_, _) =>
        {
            if (SelectedTemplate is { } selected) store.SetDefaultTemplate(selected.Id);
        };
        var templateButtons = new Grid { ColumnSpacing = 8 };
        templateButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        templateButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        templateButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        templateButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        templateButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement[] buttons = [add, editTemplate, deleteTemplate, setDefaultTemplate];
        int[] columns = [0, 1, 2, 4];
        for (var index = 0; index < buttons.Length; index++)
        {
            Grid.SetColumn((FrameworkElement)buttons[index], columns[index]);
            templateButtons.Children.Add(buttons[index]);
        }
        page.Children.Add(Header(Strings.AISectionTemplates));
        page.Children.Add(Card(templateList, templateButtons));

        Content = page;
        Refresh();
        store.PropertyChanged += OnStoreChanged;
    }

    /// <summary>The template editor while it is open, for the UI snapshots.</summary>
    public TemplateEditorSheet? PendingTemplateEditor { get; private set; }

    /// <summary>Opens the editor for <paramref name="template"/> (a new one is added on Save, an existing one updated).</summary>
    public async Task EditTemplateAsync(PromptTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (XamlRoot is not { } root) return;
        var sheet = new TemplateEditorSheet(root, template);
        PendingTemplateEditor = sheet;
        var edited = await sheet.AskAsync().ConfigureAwait(true);
        PendingTemplateEditor = null;
        if (edited is null) return;
        if (store.Templates.Any(existing => existing.Id == edited.Id))
        {
            store.UpdateTemplate(edited);
        }
        else
        {
            selectedTemplateId = store.AddTemplate(edited.Name, edited.Instructions).Id;
        }
        RefreshTemplates();
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AIProviderStore.Configuration):
                Refresh();
                break;
            case nameof(AIProviderStore.Templates):
            case nameof(AIProviderStore.SelectedTemplate):
                RefreshTemplates();
                break;
            case nameof(AIProviderStore.TokenRevision):
                RefreshToken();
                break;
        }
    }

    /// <summary>Rebuilds the preset's fields only when the preset changed, so typing keeps its focus.</summary>
    private void Refresh()
    {
        refreshing = true;
        var configuration = store.Configuration;
        preset.SelectedIndex = ProviderPreset.All.ToList().FindIndex(item => item.Id == configuration.PresetId);
        askBeforeSending.IsOn = configuration.AskBeforeSending;
        refreshing = false;
        if (builtPresetId != configuration.PresetId)
        {
            builtPresetId = configuration.PresetId;
            presetGeneration++;
            checkedTool = null;
            testResult.Children.Clear();
            BuildProviderFields();
            BuildTokenCard();
        }
        if (shownDefaultTemplateId != configuration.SelectedTemplateId) RefreshTemplates();
    }

    // Provider fields

    private void BuildProviderFields()
    {
        providerFields.Children.Clear();
        cliPath = null;
        headers = null;
        var configuration = store.Configuration;
        if (configuration.Preset.Kind.CliTool() is { } tool)
        {
            BuildCliFields(tool);
        }
        else
        {
            BuildHttpFields();
        }
    }

    private void BuildCliFields(CliTool tool)
    {
        var configuration = store.Configuration;
        var presetValue = configuration.Preset;
        var path = TextField(configuration.CliPath(tool) ?? "", Strings.AICliNotFound(tool.BinaryName()), value =>
        {
            var trimmed = value.Trim();
            store.Configuration = store.Configuration.WithCliPath(trimmed.Length == 0 ? null : value, tool);
            checkedTool = null;
        });
        cliPath = path;
        providerFields.Children.Add(Field(Strings.AICliPath(tool.ShortName()), path));
        DetectCli(tool, presetGeneration);
        var model = TextField(configuration.Model,
            tool == CliTool.Copilot ? ProviderPreset.DefaultOpenAIModel : Strings.AICliDefault,
            value => store.Configuration = store.Configuration with { Model = value });
        providerFields.Children.Add(Field(Strings.AIModel, model));
        if (presetValue.SupportsReasoningEffort)
        {
            var effort = TextField(configuration.ReasoningEffort ?? "",
                tool == CliTool.Copilot ? ProviderPreset.DefaultReasoningEffort : Strings.AICliDefault,
                SetReasoningEffort);
            providerFields.Children.Add(Field(Strings.AIReasoningEffort, effort));
        }

        var check = new Button { Content = Strings.AICheckCli(tool.ShortName()) };
        var result = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        check.Click += (_, _) => _ = CheckCliAsync(check, result, tool);
        var checkRow = new Grid { ColumnSpacing = 10 };
        checkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        checkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        checkRow.Children.Add(check);
        Grid.SetColumn(result, 1);
        checkRow.Children.Add(result);
        providerFields.Children.Add(checkRow);
        providerFields.Children.Add(Caption(CliCaption(tool)));
        providerFields.Children.Add(Caption(Strings.AIInstallLine(tool.InstallCommand())));
    }

    private static string CliCaption(CliTool tool) => tool switch
    {
        CliTool.Copilot => Strings.AICaptionCopilot,
        CliTool.ClaudeCode => Strings.AICaptionClaudeCode,
        CliTool.Codex => Strings.AICaptionCodex,
        _ => Strings.AICaptionAntigravity,
    };

    private void BuildHttpFields()
    {
        var configuration = store.Configuration;
        var presetValue = configuration.Preset;
        providerFields.Children.Add(Field(Strings.AIEndpointUrl, TextField(configuration.BaseUrl,
            "https://…/chat/completions", value => store.Configuration = store.Configuration with { BaseUrl = value })));
        providerFields.Children.Add(Field(Strings.AIModel, TextField(configuration.Model, "",
            value => store.Configuration = store.Configuration with { Model = value })));
        var effort = TextField(configuration.ReasoningEffort ?? "", Strings.AIOmittedWhenEmpty, SetReasoningEffort);
        effort.IsEnabled = presetValue.SupportsReasoningEffort;
        providerFields.Children.Add(Field(Strings.AIReasoningEffort, effort));
        if (presetValue.SupportsTemperature)
        {
            var temperature = new NumberBox
            {
                Value = configuration.Temperature ?? double.NaN,
                PlaceholderText = Strings.AIOmittedWhenEmpty,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
                NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                {
                    FractionDigits = 0,
                    IntegerDigits = 1,
                    NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = 0.01 },
                },
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            temperature.ValueChanged += (_, e) =>
            {
                double? value = double.IsNaN(e.NewValue) ? null : e.NewValue;
                store.Configuration = store.Configuration with { Temperature = value };
            };
            providerFields.Children.Add(Field(Strings.AITemperature, temperature));
        }
        if (presetValue.Id == ProviderPreset.Custom.Id)
        {
            var header = new ComboBox { MinWidth = 220 };
            foreach (var style in AuthHeaderStyles.All) header.Items.Add(Strings.CoreText(style.DisplayName()));
            header.SelectedIndex = AuthHeaderStyles.All.ToList().IndexOf(configuration.Auth);
            header.SelectionChanged += (_, _) =>
            {
                if (header.SelectedIndex < 0) return;
                store.Configuration = store.Configuration with { Auth = AuthHeaderStyles.All[header.SelectedIndex] };
                BuildTokenCard();
            };
            providerFields.Children.Add(Field(Strings.AITokenHeader, header));
        }
        headers = new TextBox
        {
            // Before Text: a single-line TextBox keeps only the first line.
            AcceptsReturn = true,
            Text = HeadersText(configuration.ExtraHeaders),
            PlaceholderText = Strings.AIExtraHeadersPlaceholder,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 32,
            MaxHeight = 96,
        };
        headers.LostFocus += (_, _) => CommitHeaders();
        providerFields.Children.Add(Field(Strings.AIExtraHeaders, headers));
    }

    private void SetReasoningEffort(string value)
    {
        var trimmed = value.Trim();
        store.Configuration = store.Configuration with { ReasoningEffort = trimmed.Length == 0 ? null : value };
    }

    // Token

    private void BuildTokenCard()
    {
        var configuration = store.Configuration;
        var isHttp = configuration.Preset.Kind == ProviderKind.Http;
        tokenHeader.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
        tokenCard.Visibility = isHttp ? Visibility.Visible : Visibility.Collapsed;
        var rows = new StackPanel { Spacing = 10 };
        tokenCard.Child = rows;
        tokenStatus = null;
        tokenRemove = null;
        if (!isHttp) return;
        if (!configuration.Auth.NeedsToken())
        {
            rows.Children.Add(new TextBlock { Text = Strings.AINoTokenNeeded, Foreground = Brush("TextFillColorSecondaryBrush") });
            return;
        }
        var input = new PasswordBox { PlaceholderText = Strings.AITokenPlaceholder };
        var error = Warning();
        error.Foreground = Brush("SystemFillColorCriticalBrush");
        input.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            if (input.Password.Length == 0) return;
            try
            {
                store.SetToken(input.Password);
                SetWarning(error, null);
            }
            catch (SecretStoreException failure)
            {
                SetWarning(error, Strings.AITokenSaveFailed(NotesFlowViewModel.Describe(failure)));
            }
            input.Password = "";
        };
        rows.Children.Add(Field(Strings.AIApiToken, input));
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        tokenStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(tokenStatus);
        var remove = new Button { Content = Strings.AITokenRemove };
        tokenRemove = remove;
        remove.Click += (_, _) =>
        {
            try
            {
                store.DeleteToken();
                SetWarning(error, null);
            }
            catch (SecretStoreException failure)
            {
                SetWarning(error, Strings.AITokenRemoveFailed(NotesFlowViewModel.Describe(failure)));
            }
        };
        status.Children.Add(remove);
        rows.Children.Add(Field(Strings.AITokenStatus, status));
        rows.Children.Add(error);
        RefreshToken();
    }

    private void RefreshToken()
    {
        if (tokenStatus is null || tokenRemove is null) return;
        var hasToken = store.HasToken;
        tokenStatus.Text = hasToken ? Strings.AITokenSaved : Strings.AITokenNotSet;
        tokenStatus.Foreground = Brush(hasToken ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        tokenRemove.Visibility = hasToken ? Visibility.Visible : Visibility.Collapsed;
    }

    // Extra headers

    private static string HeadersText(IReadOnlyDictionary<string, string> extraHeaders) =>
        string.Join("\n", extraHeaders.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}"));

    /// <summary>"Name: value" per line; lines without a colon or a name are dropped (the Mac's <c>commitHeaders</c>).</summary>
    private void CommitHeaders()
    {
        if (headers is null) return;
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in headers.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length > 0) parsed[name] = value;
        }
        store.Configuration = store.Configuration with { ExtraHeaders = parsed };
        headers.Text = HeadersText(store.Configuration.ExtraHeaders);
    }

    // CLI presets

    /// <summary>Fills the path placeholder with the auto-detected program (no process is started).</summary>
    private void DetectCli(CliTool tool, int generation)
    {
        _ = DetectCliAsync(tool, generation);
    }

    private async Task DetectCliAsync(CliTool tool, int generation)
    {
        string? found;
        try
        {
            found = await Task.Run(() => new CliLocator().Locate(tool, null)).ConfigureAwait(true);
        }
        catch (CliProviderException)
        {
            found = null;
        }
        if (generation == presetGeneration && found is not null && cliPath is { } field) field.PlaceholderText = found;
    }

    /// <summary>Runs <c>--version</c> and the login status command (<c>agy models</c> for Antigravity).</summary>
    private async Task CheckCliAsync(Button check, StackPanel result, CliTool tool)
    {
        check.IsEnabled = false;
        result.Children.Clear();
        result.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left });
        var configuration = store.Configuration;
        var generation = presetGeneration;
        checkedTool = tool;
        UIElement[] lines;
        try
        {
            var installation = await Task.Run(() => NotesFlowViewModel.Pipeline.CheckInstallationAsync(configuration))
                .ConfigureAwait(true);
            List<UIElement> found = [ResultText(Strings.AICliFound(installation.Version, installation.Path), success: true)];
            if (installation.LoginStatus is { } status)
            {
                found.Add(ResultText(Strings.CoreText(status), success: installation.LoggedIn != false));
            }
            lines = [.. found];
        }
        catch (Exception error) when (error is CliProviderException or IOException or UnauthorizedAccessException)
        {
            lines = [ResultText(NotesFlowViewModel.Describe(error), success: false)];
        }
        check.IsEnabled = true;
        if (generation != presetGeneration || checkedTool != tool) return;
        result.Children.Clear();
        foreach (var line in lines) result.Children.Add(line);
    }

    // Test connection

    private async Task TestConnectionAsync()
    {
        CommitHeaders();
        testConnection.IsEnabled = false;
        testResult.Children.Clear();
        testResult.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        var configuration = store.Configuration;
        var token = configuration.Auth.NeedsToken() ? store.CurrentToken : null;
        var generation = presetGeneration;
        UIElement line;
        try
        {
            var reply = await Task.Run(() => NotesFlowViewModel.Pipeline.ClientFor(configuration).CompleteAsync(
                MeetingPrompt.SystemMessage, "Reply with the single word OK.", configuration, token)).ConfigureAwait(true);
            var trimmed = reply.Trim();
            line = ResultText(Strings.AIConnected(trimmed.Length > 60 ? trimmed[..60] : trimmed), success: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            line = ResultText(NotesFlowViewModel.Describe(error), success: false);
        }
        testConnection.IsEnabled = true;
        if (generation != presetGeneration) return;
        testResult.Children.Clear();
        testResult.Children.Add(line);
    }

    // Templates

    private PromptTemplate? SelectedTemplate =>
        selectedTemplateId is { } id ? store.Templates.FirstOrDefault(template => template.Id == id) : null;

    private void RefreshTemplates()
    {
        refreshing = true;
        shownDefaultTemplateId = store.Configuration.SelectedTemplateId;
        templateList.Items.Clear();
        var selectedIndex = -1;
        for (var index = 0; index < store.Templates.Count; index++)
        {
            var template = store.Templates[index];
            templateList.Items.Add(TemplateRow(template, template.Id == shownDefaultTemplateId));
            if (template.Id == selectedTemplateId) selectedIndex = index;
        }
        if (selectedIndex < 0) selectedTemplateId = null;
        templateList.SelectedIndex = selectedIndex;
        refreshing = false;
        UpdateTemplateButtons();
    }

    private void UpdateTemplateButtons()
    {
        var selected = SelectedTemplate;
        editTemplate.IsEnabled = selected is { IsBuiltIn: false };
        deleteTemplate.IsEnabled = selected is { IsBuiltIn: false };
        setDefaultTemplate.IsEnabled = selected is not null && selected.Id != store.Configuration.SelectedTemplateId;
    }

    private static Grid TemplateRow(PromptTemplate template, bool isDefault)
    {
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = template.IsBuiltIn ? Strings.CoreText(template.Name) : template.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (template.IsBuiltIn)
        {
            var builtIn = Caption(Strings.AITemplateBuiltIn);
            builtIn.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(builtIn, 1);
            row.Children.Add(builtIn);
        }
        if (isDefault)
        {
            var marker = Caption(Strings.AITemplateDefault);
            marker.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(marker, 2);
            row.Children.Add(marker);
        }
        return row;
    }

    // Layout helpers

    /// <summary>A label column and a stretched control (the Mac's grouped <c>Form</c> rows).</summary>
    private static Grid Field(string label, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (control is not StackPanel) control.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (control is ComboBox) control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, label.TrimEnd(':'));
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static TextBox TextField(string text, string placeholder, Action<string> changed)
    {
        var box = new TextBox { Text = text, PlaceholderText = placeholder };
        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    private static TextBlock ResultText(string text, bool success) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        MaxLines = 4,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = Brush(success ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"),
    };

    private static Brush Brush(string key) =>
        Application.Current.Resources[key] as Brush ?? throw new InvalidOperationException($"Missing resource {key}.");
}
