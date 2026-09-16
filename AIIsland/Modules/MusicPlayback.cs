using System;
using System.Threading;

namespace AIIsland.Modules;

public sealed class MusicPlaybackOrder
{
    private static long sequence;
    private readonly object gate = new();
    private bool? playing;
    public long LastStarted { get; private set; }
    public void Observe(bool value)
    {
        lock (gate)
        {
            // Initial discovery cannot reveal which player the user started last.
            if (playing == false && value) LastStarted = Interlocked.Increment(ref sequence);
            playing = value;
        }
    }
    public void Played() { lock (gate) LastStarted = Interlocked.Increment(ref sequence); }
}

public sealed record MusicProgress(double Position, double Duration)
{
    public static MusicProgress Calculate(TimeSpan start, TimeSpan end, TimeSpan position, DateTimeOffset updated, DateTimeOffset now, bool playing, double rate)
    {
        var duration = Math.Max(0, (end - start).TotalSeconds);
        var elapsed = playing && updated != default && updated <= now && double.IsFinite(rate) ? Math.Max(0, (now - updated).TotalSeconds) * Math.Max(0, rate) : 0;
        return new(Math.Clamp((position - start).TotalSeconds + elapsed, 0, duration), duration);
    }
    public static string Format(double seconds) => seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
}
