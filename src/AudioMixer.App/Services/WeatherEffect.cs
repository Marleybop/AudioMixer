using AudioMixer.Core.Headset;

namespace AudioMixer.App.Services;

/// <summary>
/// Moving weather drawn over the weather page: falling snow, rain (slanted by the wind), drifting fog, lightning,
/// twinkling stars on a clear night. Particles only light pixels the page leaves dark, with a pixel of space around
/// the page's text and icon, so the numbers stay readable.
/// </summary>
sealed class WeatherEffect
{
    enum Kind { None, Snow, Rain, Drizzle, Storm, Fog, Stars }

    record struct Particle(double X, double Y, double Speed, double Phase);

    readonly Random _random = new();
    readonly List<Particle> _particles = [];
    Kind _kind;
    double _wind;
    double _time;
    double _nextFlash;
    double _flashUntil;

    /// <summary>True if this weather has something to animate.</summary>
    public bool Animates => _kind != Kind.None;

    /// <summary>Sets up for a WMO weather code; <paramref name="wind"/> is in km/h or mph (only the slant uses it).</summary>
    public void Set(int code, bool day, double wind, bool heavy)
    {
        var kind = code switch
        {
            >= 71 and <= 77 or 85 or 86 => Kind.Snow,
            >= 95 => Kind.Storm,
            >= 51 and <= 57 => Kind.Drizzle,
            >= 61 and <= 67 or >= 80 and <= 82 => Kind.Rain,
            45 or 48 => Kind.Fog,
            0 or 1 when !day => Kind.Stars,
            _ => Kind.None,
        };
        _wind = Math.Clamp(wind / 40, 0, 1);
        if (kind == _kind)
            return;
        _kind = kind;
        _particles.Clear();
        var count = kind switch
        {
            Kind.Snow => heavy ? 80 : 50,
            Kind.Rain or Kind.Storm => heavy ? 34 : 24,
            Kind.Drizzle => 16,
            Kind.Fog => 9,
            Kind.Stars => 16,
            _ => 0,
        };
        for (var i = 0; i < count; i++)
            _particles.Add(New(spread: true));
        _nextFlash = _time + 3 + _random.NextDouble() * 5;
    }

    Particle New(bool spread)
    {
        double x = _random.NextDouble() * (OledFrame.Width + 20) - 10;
        double y = spread ? _random.NextDouble() * OledFrame.Height : -3;
        return _kind switch
        {
            Kind.Snow => new(x, y, 5 + _random.NextDouble() * 9, _random.NextDouble() * Math.Tau),
            Kind.Rain or Kind.Storm => new(x, y, 55 + _random.NextDouble() * 25, 0),
            Kind.Drizzle => new(x, y, 30 + _random.NextDouble() * 10, 0),
            Kind.Fog => new(_random.NextDouble() * OledFrame.Width, 4 + _random.NextDouble() * 54, 2 + _random.NextDouble() * 4, 8 + _random.NextDouble() * 14),
            Kind.Stars => new(_random.NextDouble() * OledFrame.Width, _random.NextDouble() * OledFrame.Height, 0, _random.NextDouble() * Math.Tau),
            _ => default,
        };
    }

    /// <summary>Moves everything on by <paramref name="seconds"/>.</summary>
    public void Step(double seconds)
    {
        _time += seconds;
        for (var i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            switch (_kind)
            {
                case Kind.Snow:
                    p = p with { Y = p.Y + p.Speed * seconds, X = p.X + (Math.Sin(_time * 1.3 + p.Phase) * 4 + _wind * 10) * seconds };
                    break;
                case Kind.Rain or Kind.Storm or Kind.Drizzle:
                    p = p with { Y = p.Y + p.Speed * seconds, X = p.X - p.Speed * (0.15 + _wind * 0.35) * seconds };
                    break;
                case Kind.Fog:
                    p = p with { X = p.X + p.Speed * seconds };
                    if (p.X - p.Phase > OledFrame.Width)
                        p = p with { X = -p.Phase };
                    break;
            }
            if (p.Y > OledFrame.Height + 2 || p.X < -12 || p.X > OledFrame.Width + 12)
                p = New(spread: false);
            _particles[i] = p;
        }
        if (_kind == Kind.Storm && _time >= _nextFlash)
        {
            _flashUntil = _time + 0.12;
            _nextFlash = _time + 4 + _random.NextDouble() * 7;
        }
    }

    /// <summary>The page with the weather drawn into its empty space.</summary>
    public OledFrame Apply(OledFrame page)
    {
        var frame = new OledFrame();
        var keepOut = new bool[OledFrame.Width, OledFrame.Height];
        for (var y = 0; y < OledFrame.Height; y++)
            for (var x = 0; x < OledFrame.Width; x++)
            {
                if (!page[x, y])
                    continue;
                frame[x, y] = true;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        if (x + dx is >= 0 and < OledFrame.Width && y + dy is >= 0 and < OledFrame.Height)
                            keepOut[x + dx, y + dy] = true;
            }

        void Dot(double fx, double fy)
        {
            int x = (int)Math.Round(fx), y = (int)Math.Round(fy);
            if (x is >= 0 and < OledFrame.Width && y is >= 0 and < OledFrame.Height && !keepOut[x, y])
                frame[x, y] = true;
        }

        foreach (var p in _particles)
        {
            switch (_kind)
            {
                case Kind.Snow:
                    Dot(p.X, p.Y);
                    if (p.Speed > 9) // the nearer, faster flakes are bigger
                    {
                        Dot(p.X + 1, p.Y);
                        Dot(p.X, p.Y + 1);
                        Dot(p.X + 1, p.Y + 1);
                    }
                    break;
                case Kind.Rain or Kind.Storm or Kind.Drizzle:
                    var length = _kind == Kind.Drizzle ? 2 : 4;
                    var slant = 0.15 + _wind * 0.35;
                    for (var k = 0; k < length; k++)
                        Dot(p.X + k * slant, p.Y - k);
                    break;
                case Kind.Fog:
                    for (var k = 0; k < p.Phase; k += 2) // dashed wisps
                        Dot(p.X - k, p.Y + Math.Sin((p.X - k) * 0.15) * 1.2);
                    break;
                case Kind.Stars:
                    var twinkle = Math.Sin(_time * 2.1 + p.Phase * 3);
                    if (twinkle > -0.3)
                        Dot(p.X, p.Y);
                    if (twinkle > 0.85)
                    {
                        Dot(p.X - 1, p.Y);
                        Dot(p.X + 1, p.Y);
                        Dot(p.X, p.Y - 1);
                        Dot(p.X, p.Y + 1);
                    }
                    break;
            }
        }

        if (_time < _flashUntil) // lightning: the whole screen flashes
        {
            var flash = new OledFrame();
            for (var y = 0; y < OledFrame.Height; y++)
                for (var x = 0; x < OledFrame.Width; x++)
                    flash[x, y] = !frame[x, y];
            return flash;
        }
        return frame;
    }
}
