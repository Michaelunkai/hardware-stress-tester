using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StressLab.Engines;

/// <summary>
/// GPU stress engine: submits a back-to-back stream of fullscreen-triangle draws whose
/// pixel shader runs a fixed 512-iteration math loop per pixel over an offscreen
/// 1024×1024 render target. No swapchain, no vsync, no display output — pure ALU burn-in.
/// Every burst ends with a Flush so work is truly pushed to the hardware.
/// </summary>
public sealed class GpuStressor : IStressEngine
{
    public const int RenderWidth = 1024;
    public const int RenderHeight = 1024;
    public const int ShaderLoopIterations = 512;

    /// <summary>Conservative ALU-op count per shader loop iteration (sin/cos counted at reduced rate).</summary>
    public const double FlopsPerIteration = 8.0;

    /// <summary>Sync interval slow-start cap: at most ~1024 draws (~sub-second of GPU work)
    /// may sit in the queue, keeping even fragile drivers far away from the TDR window.</summary>
    public const long MaxSyncInterval = 1024;

    private const string VertexShaderSource = @"
struct VsOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

VsOut vsMain(uint id : SV_VertexID)
{
    VsOut o;
    float2 p = float2((id << 1) & 2, id & 2);
    o.pos = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
    o.uv = p;
    return o;
}
";

    private const string PixelShaderSource = @"
struct VsOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

float4 psMain(VsOut i) : SV_Target
{
    float2 p = i.uv * 4.0 - 2.0;
    float3 z = float3(p.x, p.y, 0.0);
    float d = 1.0;
    [loop]
    for (int k = 0; k < 512; k++)
    {
        z = float3(p.x * d, p.y * d, z.z + 0.01);
        d = sin(dot(z, z) + (float)k) * 0.5 + 0.5;
        p += float2(sin(z.x + z.z), cos(z.y + z.z)) * 0.001;
    }
    return float4(d, frac(z.x), frac(z.y), 1.0);
}
";

    private readonly object _lock = new();
    private EngineStats _stats = new();
    private Thread? _thread;
    private volatile bool _stop;
    private int _generation;
    private long _draws;
    private bool _deviceLost;

    private static bool IsDeviceRemoved(Exception ex)
        => ex.Message.Contains("removed", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("suspended", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("0x887A0005", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("0x887A0006", StringComparison.OrdinalIgnoreCase);
    private long _startTimestamp = Stopwatch.GetTimestamp();

    public int LoadPercent { get; set; } = 100;

    public string Name => "GPU";

    public void Start()
    {
        Thread? previous;
        lock (_lock)
        {
            if (_stats.State == EngineState.Running) return;
            previous = _thread;
            _thread = null;
        }
        previous?.Join(2000);

        lock (_lock)
        {
            _stop = false;
            _draws = 0;
            _startTimestamp = Stopwatch.GetTimestamp();
            _stats = new EngineStats { State = EngineState.Running, StatusLine = "Warming up…" };
            var load = Math.Clamp(LoadPercent, 1, 100);
            int generation = ++_generation;
            _thread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "StressLab-GPU"
            };
            _thread.Start((load, generation));
        }
    }

    private void RenderLoop(object? state)
    {
        var (load, generation) = ((int, int))state!;
        ID3D11Device? device = null;
        try
        {
            device = D3D11.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_0 })
                ?? throw new InvalidOperationException("Direct3D 11 device creation returned no device.");

            ReadOnlyMemory<byte> vsCode = Compiler.Compile(VertexShaderSource, "vsMain", "StressLabVS", "vs_5_0");
            ReadOnlyMemory<byte> psCode = Compiler.Compile(PixelShaderSource, "psMain", "StressLabPS", "ps_5_0");
            if (vsCode.IsEmpty || psCode.IsEmpty)
                throw new InvalidOperationException("Shader compilation produced no code.");

            using ID3D11VertexShader vs = device.CreateVertexShader(vsCode.Span, null);
            using ID3D11PixelShader ps = device.CreatePixelShader(psCode.Span, null);

            var texDesc = new Texture2DDescription(
                Format.R8G8B8A8_UNorm, (uint)RenderWidth, (uint)RenderHeight, 1, 1,
                BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None,
                1, 0, ResourceOptionFlags.None);
            using ID3D11Texture2D target = device.CreateTexture2D(texDesc);
            using ID3D11RenderTargetView rtv = device.CreateRenderTargetView(target, null);

            // Staging copy used once per burst to fence the command queue: without it the
            // driver runs thousands of draws ahead and the counter would measure
            // submissions instead of executed work.
            var stagingDesc = new Texture2DDescription(
                Format.R8G8B8A8_UNorm, (uint)RenderWidth, (uint)RenderHeight, 1, 1,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read,
                1, 0, ResourceOptionFlags.None);
            using ID3D11Texture2D staging = device.CreateTexture2D(stagingDesc);

            using ID3D11DeviceContext context = device.ImmediateContext;
            context.OMSetRenderTargets(rtv, null);
            context.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, RenderWidth, RenderHeight));
            context.VSSetShader(vs);
            context.PSSetShader(ps);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

            long draws = 0;
            long burstTicks = (long)(Stopwatch.Frequency * Governor.BurstMs / 1000.0);
            // Slow-start congestion control for the command queue: sync after every
            // draw at first, then double the interval (up to MaxSyncInterval) on each
            // successful fence. Submission therefore never outruns execution far
            // enough to trip driver TDR — and the counters measure EXECUTED draws.
            long syncInterval = 1;
            long nextSyncAt = 1;
            while (!_stop && !_deviceLost && generation == Volatile.Read(ref _generation))
            {
                long burstEnd = Stopwatch.GetTimestamp() + burstTicks;
                do
                {
                    context.Draw(3, 0);
                    draws++;
                    if (draws >= nextSyncAt)
                    {
                        try
                        {
                            context.CopyResource(staging, target);
                            context.Map((ID3D11Resource)staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                            context.Unmap((ID3D11Resource)staging, 0);
                            syncInterval = Math.Min(syncInterval * 2, MaxSyncInterval);
                        }
                        catch (Exception syncEx) when (IsDeviceRemoved(syncEx))
                        {
                            // The driver reset (TDR) under the draw stream. Report it
                            // clearly and stop submitting to a dead device.
                            _deviceLost = true;
                            SetError(new InvalidOperationException(
                                "GPU driver reset detected (device removed / TDR). " +
                                "The stress outran this driver — update the GPU driver or lower the GPU load slider."));
                            return;
                        }
                        catch
                        {
                            // Other sync failures are best-effort; keep going.
                        }
                        nextSyncAt = draws + syncInterval;
                    }
                }
                while (!_stop
                    && !_deviceLost
                    && generation == Volatile.Read(ref _generation)
                    && Stopwatch.GetTimestamp() < burstEnd);
                Interlocked.Exchange(ref _draws, draws);
                context.Flush();
                int sleep = Governor.ComputeSleepMs(load, Governor.BurstMs);
                if (sleep > 0) Thread.Sleep(sleep);
            }

            context.ClearState();
            context.Flush();
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            device?.Dispose();
        }
    }

    public void RequestStop()
    {
        _stop = true;
        Task.Run(Stop);
    }

    public void Stop()
    {
        _stop = true;
        Thread? thread;
        lock (_lock)
        {
            if (_stats.State != EngineState.Running) return;
            thread = _thread;
            _thread = null;
        }
        thread?.Join(5000);
        lock (_lock)
        {
            RefreshStats(EngineState.Stopped);
        }
    }

    public EngineStats Snapshot()
    {
        lock (_lock)
        {
            if (_stats.State == EngineState.Running) RefreshStats(EngineState.Running);
            return new EngineStats
            {
                State = _stats.State,
                WorkPerSecond = _stats.WorkPerSecond,
                TotalWork = _stats.TotalWork,
                ElapsedSeconds = _stats.ElapsedSeconds,
                StatusLine = _stats.StatusLine,
                ErrorText = _stats.ErrorText
            };
        }
    }

    public void Dispose() => RequestStop();

    private void SetError(Exception ex)
    {
        lock (_lock)
        {
            _stats.State = EngineState.Error;
            _stats.ErrorText = ex.Message;
            _stats.StatusLine = "Error: " + ex.Message;
        }
    }

    private void RefreshStats(EngineState state)
    {
        double elapsed = Math.Max(0.001, (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency);
        long draws = Interlocked.Read(ref _draws);
        _stats.TotalWork = draws;
        _stats.ElapsedSeconds = elapsed;
        _stats.WorkPerSecond = elapsed > 0.2 ? draws / elapsed : 0;
        _stats.State = state;
        _stats.StatusLine = $"{EstimateGflops(draws, elapsed):0} GFLOPS (est.) · {_stats.WorkPerSecond:0} draws/s";
    }

    public static double EstimateGflops(long draws, double seconds)
        => seconds <= 0
            ? 0
            : draws * (double)((long)RenderWidth * RenderHeight) * ShaderLoopIterations * FlopsPerIteration / seconds / 1e9;

    public static string TryGetAdapterName()
    {
        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            if (factory.EnumAdapters1(0, out IDXGIAdapter1? adapter).Failure || adapter is null)
                return "GPU";
            using (adapter)
                return adapter.Description1.Description ?? "GPU";
        }
        catch
        {
            return "GPU";
        }
    }
}
