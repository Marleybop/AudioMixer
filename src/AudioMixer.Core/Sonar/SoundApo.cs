namespace AudioMixer.Core.Sonar;

/// <summary>
/// Maps <see cref="SoundSettings"/> onto the Sonar APO's settings, the same way Sonar applies its configs
/// (decomp: RenderConfigSettings / CaptureConfigSettings / AudioRepository).
/// </summary>
public static class SoundApo
{
    // Smart Volume's loudness is the threshold of the first two compressor bands.
    static float LoudnessThreshold(Loudness loudness) => loudness switch
    {
        Loudness.Loud => -9f,
        Loudness.Quiet => -27f,
        _ => -18f,
    };

    static Loudness LoudnessFromThreshold(float db) => db <= -27f ? Loudness.Quiet : db <= -18f ? Loudness.Balanced : Loudness.Loud;

    static readonly ApoSetting[] SpeakerPositions =
    [
        ApoSetting.ChannelPositionFrontLeft71, ApoSetting.ChannelPositionFrontRight71, ApoSetting.ChannelPositionCenter71,
        ApoSetting.ChannelPositionSubWoofer71, ApoSetting.ChannelPositionRearLeft71, ApoSetting.ChannelPositionRearRight71,
        ApoSetting.ChannelPositionSideLeft71, ApoSetting.ChannelPositionSideRight71,
    ];

    static readonly ApoSetting[] SpeakerGains =
    [
        ApoSetting.GainDBFrontLeft, ApoSetting.GainDBFrontRight, ApoSetting.GainDBCenter, ApoSetting.GainDBSubwoofer,
        ApoSetting.GainDBRearLeft, ApoSetting.GainDBRearRight, ApoSetting.GainDBSideLeft, ApoSetting.GainDBSideRight,
    ];

    /// <summary>The APO settings of one EQ band: State, FreqHz, Q, GainDb, Type are consecutive in the enum.</summary>
    static (ApoSetting State, ApoSetting Freq, ApoSetting Q, ApoSetting Gain, ApoSetting Type) Band(bool voice, int index)
    {
        var first = (voice ? ApoSetting.CaptureParametricEqFilter1State : ApoSetting.ParametricEqFilter1State) + index * 5;
        return (first, first + 1, first + 2, first + 3, first + 4);
    }

    /// <summary>Every APO value that <paramref name="s"/> sets on <paramref name="channel"/>.</summary>
    public static List<(ApoSetting Setting, object Value)> ToApo(Channel channel, SoundSettings s)
    {
        var voice = SoundSettings.IsVoice(channel);
        var list = new List<(ApoSetting, object)>();
        if (voice)
        {
            var ncOn = s.NoiseCancelingState.Enabled;
            list.Add((ApoSetting.CaptureState, s.GlobalEnableState));
            list.Add((ApoSetting.CaptureCompressorState, s.VolumeStabilizerState.Enabled));
            list.Add((ApoSetting.CaptureCompressorLevel, s.VolumeStabilizerState.Value));
            list.Add((ApoSetting.NoiseGateState, s.NoiseGateState.Enabled));
            list.Add((ApoSetting.NoiseGateThresholdDB, s.NoiseGateState.Value));
            list.Add((ApoSetting.NoiseGateAutoThreshold, s.AutomaticNoiseGateState.Enabled));
            list.Add((ApoSetting.NoiseGateOffsetDB, 3f));
            // AI noise cancelling replaces the classic noise reduction, as in Sonar.
            list.Add((ApoSetting.CaptureAmbientNoiseReductionState, !ncOn && s.NoiseReductionState.Enabled));
            list.Add((ApoSetting.CaptureAmbientNoiseReductionRate, s.NoiseReductionState.Value));
            list.Add((ApoSetting.ImpactNoiseReductionState, !ncOn && s.ImpactNoiseReductionState.Enabled));
            list.Add((ApoSetting.ImpactNoiseReductionAgressiveness, s.ImpactNoiseReductionState.Value));
            list.Add((ApoSetting.NoiseCancelingState, ncOn));
            list.Add((ApoSetting.NoiseCancelingMixFactor, s.NoiseCancelingState.Value));
            list.Add((ApoSetting.AECState, s.AcousticEchoCancelingState));
        }
        else
        {
            list.Add((ApoSetting.RenderState, s.GlobalEnableState));
            list.Add((ApoSetting.BassBoostState, s.BassBoostState.Enabled));
            list.Add((ApoSetting.BassBoostGainDB, s.BassBoostState.Value));
            list.Add((ApoSetting.MidBoostState, s.VoiceClarityState.Enabled));
            list.Add((ApoSetting.MidBoostGainDB, s.VoiceClarityState.Value));
            list.Add((ApoSetting.TrebleBoostState, s.TrebleBoostState.Enabled));
            list.Add((ApoSetting.TrebleBoostGainDB, s.TrebleBoostState.Value));
            list.Add((ApoSetting.CompressorState, s.SmartVolume.Enabled));
            list.Add((ApoSetting.CompressorLevel, s.SmartVolume.VolumeLevel));
            list.Add((ApoSetting.Compressor1ThresholdDB, LoudnessThreshold(s.SmartVolume.Loudness)));
            list.Add((ApoSetting.Compressor2ThresholdDB, LoudnessThreshold(s.SmartVolume.Loudness)));
            list.Add((ApoSetting.GeneralGainDB, s.GeneralGain));
            list.Add((ApoSetting.FormFactor, (int)s.FormFactor));
            list.Add((ApoSetting.ReverbGainDB, s.ReverbGainDB));
            list.Add((ApoSetting.VirtualSurroundState, s.VirtualSurroundState));
            list.Add((ApoSetting.AdvancedSurroundState, s.VirtualSurroundState));
            var speakers = s.VirtualSurroundChannels.All;
            for (var i = 0; i < speakers.Length; i++)
            {
                list.Add((SpeakerPositions[i], speakers[i].Position));
                list.Add((SpeakerGains[i], speakers[i].Gain));
            }
        }

        list.Add((voice ? ApoSetting.CaptureParametricEqState : ApoSetting.ParametricEqState, s.ParametricEQ.Enabled));
        for (var i = 0; i < ParametricEq.BandCount; i++)
        {
            var f = s.ParametricEQ.Filters[i];
            var b = Band(voice, i);
            list.Add((b.State, f.Enabled));
            list.Add((b.Freq, f.Frequency));
            list.Add((b.Q, f.QFactor));
            list.Add((b.Gain, f.Gain));
            list.Add((b.Type, (int)f.Type));
        }
        return list;
    }

    /// <summary>Reads a channel's current sound from the APO (used once, to carry over what Sonar had set).</summary>
    public static SoundSettings FromApo(SonarEngine sonar, Channel channel)
    {
        var voice = SoundSettings.IsVoice(channel);
        var s = SoundSettings.Flat(channel);
        bool B(ApoSetting x) => sonar.GetBool(channel, x);
        float F(ApoSetting x) => sonar.GetFloat(channel, x);

        if (voice)
        {
            s.GlobalEnableState = B(ApoSetting.CaptureState);
            s.VolumeStabilizerState = new(B(ApoSetting.CaptureCompressorState), F(ApoSetting.CaptureCompressorLevel));
            s.NoiseGateState = new(B(ApoSetting.NoiseGateState), F(ApoSetting.NoiseGateThresholdDB));
            s.AutomaticNoiseGateState = new(B(ApoSetting.NoiseGateAutoThreshold), 3);
            s.NoiseReductionState = new(B(ApoSetting.CaptureAmbientNoiseReductionState), F(ApoSetting.CaptureAmbientNoiseReductionRate));
            s.ImpactNoiseReductionState = new(B(ApoSetting.ImpactNoiseReductionState), F(ApoSetting.ImpactNoiseReductionAgressiveness));
            s.NoiseCancelingState = new(B(ApoSetting.NoiseCancelingState), F(ApoSetting.NoiseCancelingMixFactor));
            s.AcousticEchoCancelingState = B(ApoSetting.AECState);
        }
        else
        {
            s.GlobalEnableState = B(ApoSetting.RenderState);
            s.BassBoostState = new(B(ApoSetting.BassBoostState), F(ApoSetting.BassBoostGainDB));
            s.VoiceClarityState = new(B(ApoSetting.MidBoostState), F(ApoSetting.MidBoostGainDB));
            s.TrebleBoostState = new(B(ApoSetting.TrebleBoostState), F(ApoSetting.TrebleBoostGainDB));
            s.SmartVolume = new()
            {
                Enabled = B(ApoSetting.CompressorState),
                VolumeLevel = F(ApoSetting.CompressorLevel),
                Loudness = LoudnessFromThreshold(F(ApoSetting.Compressor1ThresholdDB)),
            };
            s.GeneralGain = F(ApoSetting.GeneralGainDB);
            s.FormFactor = sonar.GetLong(channel, ApoSetting.FormFactor) == 1 ? SpatialMode.Speakers : SpatialMode.Headphones;
            s.ReverbGainDB = F(ApoSetting.ReverbGainDB);
            s.VirtualSurroundState = B(ApoSetting.VirtualSurroundState);
            var speakers = s.VirtualSurroundChannels.All;
            for (var i = 0; i < speakers.Length; i++)
            {
                speakers[i].Position = F(SpeakerPositions[i]);
                speakers[i].Gain = F(SpeakerGains[i]);
            }
        }

        s.ParametricEQ.Enabled = B(voice ? ApoSetting.CaptureParametricEqState : ApoSetting.ParametricEqState);
        for (var i = 0; i < ParametricEq.BandCount; i++)
        {
            var b = Band(voice, i);
            var type = sonar.GetLong(channel, b.Type);
            s.ParametricEQ.Filters[i] = new EqFilter(B(b.State), F(b.Freq),
                Enum.IsDefined(typeof(FilterType), type) ? (FilterType)type : FilterType.Peaking, F(b.Gain), F(b.Q));
        }
        return s;
    }
}
