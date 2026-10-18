using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioMixer.Core.Audio;

/// <summary>
/// Feeds a physical microphone into the Sonar virtual microphone: captures the mic and plays it into the
/// "Sonar - Microphone" render endpoint, which surfaces on the virtual mic that apps record from (and where
/// the APO applies mic processing). Sonar's app did this in-process; without it the virtual mic is silent.
/// </summary>
public sealed class MicBridge : IDisposable
{
    const string MmcssTask = "Pro Audio";
    const int RenderLatencyMs = 10;
    static readonly TimeSpan MaxQueued = TimeSpan.FromMilliseconds(40); // drop backlog beyond this to keep latency bounded
    static readonly TimeSpan TrimTo = TimeSpan.FromMilliseconds(10);

    readonly WasapiRecorder _recorder;
    readonly WasapiPlayer _player;
    readonly BufferedWaveProvider _buffer;
    int _stopped;

    public string SourceId { get; }
    public string TargetId { get; }

    /// <summary>Total latency the two WASAPI clients report (excludes the small jitter buffer).</summary>
    public int LatencyMs => _recorder.LatencyMilliseconds + _player.LatencyMilliseconds;

    /// <summary>Raised once if either side stops unexpectedly (device unplugged, disabled, format change).</summary>
    public event EventHandler<Exception?>? Stopped;

    public MicBridge(AudioEndpoints endpoints, string sourceMicId, string virtualMicRenderId)
    {
        SourceId = sourceMicId;
        TargetId = virtualMicRenderId;
        var source = endpoints.GetDevice(sourceMicId);
        var target = endpoints.GetDevice(virtualMicRenderId);

        _recorder = new WasapiRecorderBuilder()
            .WithDevice(source)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency(true)
            .WithMmcssThreadPriority(MmcssTask)
            .Build();
        _buffer = new BufferedWaveProvider(_recorder.WaveFormat, TimeSpan.FromSeconds(1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true, // underrun → silence instead of stopping
        };
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) => OnStopped(e.Exception);

        WaveFormat mix;
        using (var client = target.CreateAudioClient())
            mix = client.MixFormat;
        ISampleProvider samples = _buffer.ToSampleProvider();
        samples = AdaptChannels(samples, mix.Channels);
        if (samples.WaveFormat.SampleRate != mix.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);

        _player = new WasapiPlayerBuilder()
            .WithDevice(target)
            .WithSharedMode()
            .WithEventSync()
            .WithLowLatency(true)
            .WithLatency(RenderLatencyMs)
            .WithMmcssThreadPriority(MmcssTask)
            .Build();
        _player.Init(samples.ToWaveProvider());
        _player.PlaybackStopped += (_, e) => OnStopped(e.Exception);
    }

    public void Start()
    {
        _player.Play();
        _recorder.StartRecording();
    }

    void OnData(ReadOnlySpan<byte> data, NAudio.CoreAudioApi.AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (_buffer.BufferedDuration > MaxQueued)
            Trim();
        _buffer.AddSamples(data);
    }

    void Trim()
    {
        var excess = _buffer.BufferedBytes - (int)(TrimTo.TotalSeconds * _buffer.WaveFormat.AverageBytesPerSecond);
        excess -= excess % _buffer.WaveFormat.BlockAlign;
        if (excess > 0)
            _buffer.Read(new byte[excess]);
    }

    void OnStopped(Exception? error)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
            Stopped?.Invoke(this, error);
    }

    static ISampleProvider AdaptChannels(ISampleProvider input, int channels)
    {
        var inChannels = input.WaveFormat.Channels;
        if (inChannels == channels)
            return input;
        if (inChannels == 1 && channels == 2)
            return new MonoToStereoSampleProvider(input);
        // General case: output channel i takes input channel (i mod inChannels).
        var mux = new MultiplexingSampleProvider([input], channels);
        for (var i = 0; i < channels; i++)
            mux.ConnectInputToOutput(i % inChannels, i);
        return mux;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        try { _recorder.StopRecording(); } catch (Exception) { }
        try { _player.Stop(); } catch (Exception) { }
        _recorder.Dispose();
        _player.Dispose();
    }
}
