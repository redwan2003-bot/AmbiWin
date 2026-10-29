using AmbiWin.Core.Motion;
using AmbiWin.Core.Threading;

namespace AmbiWin.Core.Tests;

public class MotionAndThreadingTests
{
    [Fact]
    public void One_pole_rises_faster_than_it_falls()
    {
        var up = new OnePole(0f);
        var down = new OnePole(1f);
        for (int i = 0; i < 6; i++) // 100 ms at 60 fps
        {
            up.Step(1f, 1f / 60, attackSeconds: 0.030f, releaseSeconds: 0.250f);
            down.Step(0f, 1f / 60, attackSeconds: 0.030f, releaseSeconds: 0.250f);
        }
        Assert.True(up.Value > 0.95f, $"rise reached {up.Value:0.000}");
        Assert.True(down.Value > 0.6f, $"fall reached {down.Value:0.000}");
    }

    [Fact]
    public void One_pole_is_frame_rate_independent()
    {
        var at60 = new OnePole(0f);
        var at144 = new OnePole(0f);
        for (int i = 0; i < 60; i++) at60.Step(1f, 1f / 60, 0.25f, 0.25f);
        for (int i = 0; i < 144; i++) at144.Step(1f, 1f / 144, 0.25f, 0.25f);
        Assert.InRange(at60.Value - at144.Value, -1e-3f, 1e-3f);
    }

    [Fact]
    public void Ease_in_out_hits_its_end_points()
    {
        Assert.Equal(0f, Ease.InOutCubic(0f));
        Assert.Equal(1f, Ease.InOutCubic(1f));
        Assert.InRange(Ease.InOutCubic(0.5f), 0.499f, 0.501f);
    }

    [Fact]
    public async Task Latest_returns_the_newest_value_and_wakes_the_reader()
    {
        using var wake = new WakeSignal();
        var latest = new Latest<string>("first", wake);
        Assert.Equal("first", latest.Value);

        var reader = Task.Run(() => wake.Wait(TimeSpan.FromSeconds(5)));
        latest.Publish("second");

        Assert.True(await reader.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("second", latest.Value);
    }
}
