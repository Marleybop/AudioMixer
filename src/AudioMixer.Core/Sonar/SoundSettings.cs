using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioMixer.Core.Sonar;

/// <summary>Parametric EQ filter shapes, in the APO's numbering.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FilterType>))]
public enum FilterType
{
    [JsonStringEnumMemberName("byPass")] Bypass,
    [JsonStringEnumMemberName("lowPass")] LowPass,
    [JsonStringEnumMemberName("highPass")] HighPass,
    [JsonStringEnumMemberName("bandPassPeakQ")] BandPass,
    [JsonStringEnumMemberName("bandPassPeak0dB")] BandPass0dB,
    [JsonStringEnumMemberName("notchFilter")] Notch,
    [JsonStringEnumMemberName("allPass")] AllPass,
    [JsonStringEnumMemberName("peakingEQ")] Peaking,
    [JsonStringEnumMemberName("lowShelving")] LowShelf,
    [JsonStringEnumMemberName("highShelving")] HighShelf,
}

[JsonConverter(typeof(JsonStringEnumConverter<Loudness>))]
public enum Loudness
{
    [JsonStringEnumMemberName("extreme")] Loud,
    [JsonStringEnumMemberName("balanced")] Balanced,
    [JsonStringEnumMemberName("soft")] Quiet,
}

[JsonConverter(typeof(JsonStringEnumConverter<SpatialMode>))]
public enum SpatialMode
{
    [JsonStringEnumMemberName("headphones")] Headphones,
    [JsonStringEnumMemberName("speakers")] Speakers,
}

/// <summary>An effect that is on or off with one level (Sonar's StateWithFloatValue).</summary>
public sealed class Effect
{
    public bool Enabled { get; set; }
    public float Value { get; set; }

    public Effect() { }
    public Effect(bool enabled, float value) { Enabled = enabled; Value = value; }
    public Effect Clone() => new(Enabled, Value);
}

public sealed class EqFilter
{
    public bool Enabled { get; set; } = true;
    public float QFactor { get; set; } = 0.7071f;
    public float Frequency { get; set; } = 1000;
    public float Gain { get; set; }
    public FilterType Type { get; set; } = FilterType.Peaking;

    public EqFilter() { }
    public EqFilter(bool enabled, float frequency, FilterType type = FilterType.Peaking, float gain = 0, float q = 0.7071f)
    {
        Enabled = enabled;
        Frequency = frequency;
        Type = type;
        Gain = gain;
        QFactor = q;
    }

    public EqFilter Clone() => new(Enabled, Frequency, Type, Gain, QFactor);
}

/// <summary>The 10-band parametric EQ. Stored as filter1…filter10, like Sonar's configs.</summary>
[JsonConverter(typeof(ParametricEqConverter))]
public sealed class ParametricEq
{
    public const int BandCount = 10;

    public bool Enabled { get; set; } = true;
    public EqFilter[] Filters { get; set; } = new EqFilter[BandCount];

    public ParametricEq Clone() => new() { Enabled = Enabled, Filters = Filters.Select(f => f.Clone()).ToArray() };

    /// <summary>Sonar's starting EQ for game-type channels: five bands in use, all flat.</summary>
    public static ParametricEq FlatRender() => new()
    {
        Filters =
        [
            new(true, 35), new(true, 120), new(true, 1000), new(true, 6000), new(true, 18000),
            new(false, 1000), new(false, 2000), new(false, 4000), new(false, 8000), new(false, 16000),
        ],
    };

    /// <summary>Sonar's starting EQ for voice channels: ten flat bands from a low shelf to a high shelf.</summary>
    public static ParametricEq FlatCapture() => new()
    {
        Filters =
        [
            new(true, 31, FilterType.LowShelf), new(true, 62), new(true, 125), new(true, 250), new(true, 500),
            new(true, 1000), new(true, 2000), new(true, 4000), new(true, 8000), new(true, 16000, FilterType.HighShelf),
        ],
    };
}

sealed class ParametricEqConverter : JsonConverter<ParametricEq>
{
    public override ParametricEq Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var eq = new ParametricEq();
        if (TryGet(root, "enabled", out var enabled))
            eq.Enabled = enabled.GetBoolean();
        var flat = ParametricEq.FlatRender();
        for (var i = 0; i < ParametricEq.BandCount; i++)
            eq.Filters[i] = TryGet(root, $"filter{i + 1}", out var f) ? f.Deserialize<EqFilter>(options) ?? flat.Filters[i] : flat.Filters[i];
        return eq;
    }

    public override void Write(Utf8JsonWriter writer, ParametricEq value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        for (var i = 0; i < value.Filters.Length; i++)
        {
            writer.WritePropertyName($"filter{i + 1}");
            JsonSerializer.Serialize(writer, value.Filters[i], options);
        }
        writer.WriteEndObject();
    }

    static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        value = default;
        return false;
    }
}

/// <summary>One virtual speaker of spatial audio: its angle and its gain (distance).</summary>
public sealed class Speaker
{
    public float Position { get; set; }
    public float Gain { get; set; }

    public Speaker() { }
    public Speaker(float position, float gain = 0) { Position = position; Gain = gain; }
    public Speaker Clone() => new(Position, Gain);
}

public sealed class Speakers
{
    public Speaker FrontLeft { get; set; } = new(30);
    public Speaker FrontRight { get; set; } = new(-30);
    public Speaker Center { get; set; } = new(0);
    public Speaker SubWoofer { get; set; } = new(0);
    public Speaker RearLeft { get; set; } = new(150);
    public Speaker RearRight { get; set; } = new(-150);
    public Speaker SideLeft { get; set; } = new(90);
    public Speaker SideRight { get; set; } = new(-90);

    /// <summary>All eight, in the APO's channel order.</summary>
    [JsonIgnore]
    public Speaker[] All => [FrontLeft, FrontRight, Center, SubWoofer, RearLeft, RearRight, SideLeft, SideRight];

    public Speakers Clone() => new()
    {
        FrontLeft = FrontLeft.Clone(), FrontRight = FrontRight.Clone(), Center = Center.Clone(), SubWoofer = SubWoofer.Clone(),
        RearLeft = RearLeft.Clone(), RearRight = RearRight.Clone(), SideLeft = SideLeft.Clone(), SideRight = SideRight.Clone(),
    };
}

public sealed class SmartVolume
{
    public bool Enabled { get; set; }
    public float VolumeLevel { get; set; }
    public Loudness Loudness { get; set; } = Loudness.Balanced;

    public SmartVolume Clone() => new() { Enabled = Enabled, VolumeLevel = VolumeLevel, Loudness = Loudness };
}

/// <summary>
/// Everything Sonar lets you tune on one channel. The JSON shape matches Sonar's own configs (camelCase), so
/// Sonar presets load as-is. Game, Media and Aux use the playback effects; Chat and Mic use the voice effects.
/// </summary>
public sealed class SoundSettings
{
    public bool GlobalEnableState { get; set; } = true;
    public ParametricEq ParametricEQ { get; set; } = ParametricEq.FlatRender();

    // Playback channels (Game, Media, Aux)
    public Effect BassBoostState { get; set; } = new();
    public Effect VoiceClarityState { get; set; } = new();
    public Effect TrebleBoostState { get; set; } = new();
    public SmartVolume SmartVolume { get; set; } = new();
    /// <summary>"Volume boost" in dB, 0…12.</summary>
    public float GeneralGain { get; set; }
    public bool VirtualSurroundState { get; set; }
    public Speakers VirtualSurroundChannels { get; set; } = new();
    /// <summary>Spatial audio tuning: −12 (performance) … 0 (immersion).</summary>
    public float ReverbGainDB { get; set; } = -6;
    public SpatialMode FormFactor { get; set; }

    // Voice channels (Chat, Mic)
    public Effect NoiseCancelingState { get; set; } = new(false, 0.9f);
    public bool AcousticEchoCancelingState { get; set; }
    public Effect NoiseReductionState { get; set; } = new();
    public Effect ImpactNoiseReductionState { get; set; } = new();
    public Effect NoiseGateState { get; set; } = new(false, -60);
    public Effect AutomaticNoiseGateState { get; set; } = new(false, 3);
    /// <summary>"Compressor" on voice channels.</summary>
    public Effect VolumeStabilizerState { get; set; } = new();

    public static bool IsVoice(Channel channel) => channel is Channel.Chat or Channel.Mic;

    /// <summary>Sonar's "Flat" for a channel: no effects and a flat EQ.</summary>
    public static SoundSettings Flat(Channel channel) => new()
    {
        ParametricEQ = IsVoice(channel) ? ParametricEq.FlatCapture() : ParametricEq.FlatRender(),
        NoiseCancelingState = new(channel == Channel.Mic, 0.9f),
    };

    public SoundSettings Clone() => FromJson(ToJson());

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = false,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SoundSettings FromJson(string json) => JsonSerializer.Deserialize<SoundSettings>(json, Json) ?? new SoundSettings();
}
