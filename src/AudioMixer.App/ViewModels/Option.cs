namespace AudioMixer.App.ViewModels;

/// <summary>One choice in a row of option buttons (bound through SelectedValuePath="Value").</summary>
public sealed record Option(object Value, string Label);

/// <summary>A label/value line in a status list; <see cref="Good"/> shows the value in the "OK" colour.</summary>
public sealed record StatusRow(string Label, string Value, bool Good = false);
