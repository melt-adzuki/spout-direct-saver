using System.Diagnostics;
using System.Runtime.InteropServices;
using Spout.Interop;
using SpoutDirectSaver.App.Services;

var options = SenderOptions.Parse(args);
WindowsScheduling.TryPromoteCurrentProcess(ProcessPriorityClass.High);
using var schedulingScope = WindowsScheduling.EnterGameProfile();
Console.WriteLine($"sender={options.Name}");
Console.WriteLine($"size={options.Width}x{options.Height}");
Console.WriteLine($"fps={options.FrameRate:0.###}");
Console.WriteLine($"duration={(options.Duration is null ? "infinite" : $"{options.Duration.Value.TotalSeconds:0.###}s")}");
Console.WriteLine($"mode={(options.SendTexture ? "send-texture" : "send-image")}");
Console.WriteLine($"scene={options.Scene}");

using var sender = new SpoutSender();
sender.SetFrameCount(true);

if (!sender.CreateOpenGL())
{
    throw new InvalidOperationException("CreateOpenGL failed.");
}

if (!sender.CreateSender(options.Name, options.Width, options.Height, 0))
{
    throw new InvalidOperationException("CreateSender failed.");
}

var pattern = CreatePattern(options.Scene, options.Width, options.Height);
var gpuRenderer = CreateGpuRenderer(options);
var frameBuffer = GC.AllocateUninitializedArray<byte>(checked((int)(options.Width * options.Height * 4)));
pattern.Initialize(frameBuffer);
var textureId = 0u;

if (options.SendTexture)
{
    textureId = gpuRenderer is null
        ? GlTexture.Create(options.Width, options.Height, frameBuffer)
        : GlTexture.CreateEmpty(options.Width, options.Height);
}

PrimeFirstFrame(sender, pattern, gpuRenderer, frameBuffer, textureId, options);

var nextFrameTicks = Stopwatch.GetTimestamp();
var endTicks = options.Duration is null
    ? long.MaxValue
    : Stopwatch.GetTimestamp() + (long)Math.Round(options.Duration.Value.TotalSeconds * Stopwatch.Frequency);
var framesSent = 0;
var totalStopwatch = Stopwatch.StartNew();
var lastReportElapsed = TimeSpan.Zero;
var lastReportedFrames = 0;

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    endTicks = 0;
};

try
{
    while (Stopwatch.GetTimestamp() < endTicks)
    {
        var success = false;
        if (gpuRenderer is not null)
        {
            gpuRenderer.Render(textureId, framesSent);
            success = sender.SendTexture(textureId, GlTexture.Texture2D, options.Width, options.Height, false, 0);
        }
        else
        {
            pattern.Advance(frameBuffer, framesSent);

            unsafe
            {
                fixed (byte* pixels = frameBuffer)
                {
                    success = options.SendTexture
                        ? GlTexture.Upload(textureId, options.Width, options.Height, pixels) &&
                          sender.SendTexture(textureId, GlTexture.Texture2D, options.Width, options.Height, false, 0)
                        : sender.SendImage(pixels, options.Width, options.Height, GlTexture.Bgra, false, 0);
                }
            }
        }

        if (!success)
        {
            throw new InvalidOperationException(options.SendTexture ? "SendTexture failed." : "SendImage failed.");
        }

        framesSent++;
        if ((totalStopwatch.Elapsed - lastReportElapsed).TotalSeconds >= 1.0)
        {
            var reportElapsed = totalStopwatch.Elapsed - lastReportElapsed;
            var reportFrames = framesSent - lastReportedFrames;
            Console.WriteLine(
                $"frames_sent={framesSent} elapsed={totalStopwatch.Elapsed.TotalSeconds:0.000} fps={reportFrames / reportElapsed.TotalSeconds:0.00}");
            lastReportElapsed = totalStopwatch.Elapsed;
            lastReportedFrames = framesSent;
        }

        WaitUntilNextFrame(ref nextFrameTicks, options.FrameRate);
    }
}
finally
{
    gpuRenderer?.Dispose();
    if (textureId != 0)
    {
        GlTexture.Delete(textureId);
    }

    sender.ReleaseSender();
    sender.CloseOpenGL();
}

static void WaitUntilNextFrame(ref long nextFrameTicks, double frameRate)
{
    nextFrameTicks += (long)Math.Round(Stopwatch.Frequency / Math.Max(frameRate, 1.0));

    while (true)
    {
        var remainingTicks = nextFrameTicks - Stopwatch.GetTimestamp();
        if (remainingTicks <= 0)
        {
            return;
        }

        var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
        if (remainingMs >= 2.0)
        {
            Thread.Sleep(1);
            continue;
        }

        Thread.SpinWait(128);
    }
}

static void PrimeFirstFrame(
    SpoutSender sender,
    IFramePattern pattern,
    IGpuSceneRenderer? gpuRenderer,
    byte[] frameBuffer,
    uint textureId,
    SenderOptions options)
{
    var success = false;
    if (gpuRenderer is not null)
    {
        gpuRenderer.Render(textureId, 0);
        success = sender.SendTexture(textureId, GlTexture.Texture2D, options.Width, options.Height, false, 0);
    }
    else
    {
        pattern.Advance(frameBuffer, 0);
        unsafe
        {
            fixed (byte* pixels = frameBuffer)
            {
                success = options.SendTexture
                    ? GlTexture.Upload(textureId, options.Width, options.Height, pixels) &&
                      sender.SendTexture(textureId, GlTexture.Texture2D, options.Width, options.Height, false, 0)
                    : sender.SendImage(pixels, options.Width, options.Height, GlTexture.Bgra, false, 0);
            }
        }
    }

    if (!success)
    {
        throw new InvalidOperationException("Failed to prime first sender frame.");
    }
}

static IFramePattern CreatePattern(ScenePatternKind scene, uint width, uint height)
{
    return scene switch
    {
        ScenePatternKind.Complex => new ComplexMovingPattern(width, height),
        _ => new SimpleMovingPattern(width, height)
    };
}

static IGpuSceneRenderer? CreateGpuRenderer(SenderOptions options)
{
    if (!options.SendTexture)
    {
        return null;
    }

    return options.Scene switch
    {
        ScenePatternKind.Complex => new ShaderGpuSceneRenderer(options.Width, options.Height),
        _ => null
    };
}

internal sealed record SenderOptions(
    string Name,
    uint Width,
    uint Height,
    double FrameRate,
    TimeSpan? Duration,
    bool SendTexture,
    ScenePatternKind Scene)
{
    public static SenderOptions Parse(string[] args)
    {
        var name = "SpoutDirectSaverTest";
        var width = 3840u;
        var height = 2160u;
        var frameRate = 60.0;
        TimeSpan? duration = TimeSpan.FromSeconds(10);
        var sendTexture = true;
        var scene = ScenePatternKind.Simple;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name":
                    name = args[++i];
                    break;
                case "--width":
                    width = uint.Parse(args[++i]);
                    break;
                case "--height":
                    height = uint.Parse(args[++i]);
                    break;
                case "--fps":
                    frameRate = double.Parse(args[++i]);
                    break;
                case "--seconds":
                    duration = TimeSpan.FromSeconds(double.Parse(args[++i]));
                    break;
                case "--infinite":
                    duration = null;
                    break;
                case "--send-image":
                    sendTexture = false;
                    break;
                case "--send-texture":
                    sendTexture = true;
                    break;
                case "--scene":
                    scene = Enum.Parse<ScenePatternKind>(args[++i], ignoreCase: true);
                    break;
                case "--complex-scene":
                    scene = ScenePatternKind.Complex;
                    break;
            }
        }

        return new SenderOptions(name, width, height, frameRate, duration, sendTexture, scene);
    }
}

internal enum ScenePatternKind
{
    Simple,
    Complex
}

internal interface IFramePattern
{
    void Initialize(byte[] target);

    void Advance(byte[] target, int frameIndex);
}

internal interface IGpuSceneRenderer : IDisposable
{
    void Render(uint textureId, int frameIndex);
}

internal static class GlTexture
{
    public const uint Texture2D = 0x0DE1u;
    public const uint Bgra = 0x80E1u;
    private const uint UnsignedByte = 0x1401u;
    private const int TextureMinFilter = 0x2801;
    private const int TextureMagFilter = 0x2800;
    private const int TextureWrapS = 0x2802;
    private const int TextureWrapT = 0x2803;
    private const int Nearest = 0x2600;
    private const int ClampToEdge = 0x812F;
    private const int Rgba8 = 0x8058;

    public static uint Create(uint width, uint height, byte[] initialData)
    {
        unsafe
        {
            fixed (byte* pixels = initialData)
            {
                return Create(width, height, pixels);
            }
        }
    }

    public static unsafe uint Create(uint width, uint height, byte* pixels)
    {
        uint textureId = 0;
        glGenTextures(1, &textureId);
        glBindTexture(Texture2D, textureId);
        glTexParameteri(Texture2D, TextureMinFilter, Nearest);
        glTexParameteri(Texture2D, TextureMagFilter, Nearest);
        glTexParameteri(Texture2D, TextureWrapS, ClampToEdge);
        glTexParameteri(Texture2D, TextureWrapT, ClampToEdge);
        glTexImage2D(Texture2D, 0, Rgba8, (int)width, (int)height, 0, Bgra, UnsignedByte, pixels);
        return textureId;
    }

    public static uint CreateEmpty(uint width, uint height)
    {
        unsafe
        {
            return CreateEmpty(width, height, null);
        }
    }

    private static unsafe uint CreateEmpty(uint width, uint height, void* pixels)
    {
        uint textureId = 0;
        glGenTextures(1, &textureId);
        glBindTexture(Texture2D, textureId);
        glTexParameteri(Texture2D, TextureMinFilter, Nearest);
        glTexParameteri(Texture2D, TextureMagFilter, Nearest);
        glTexParameteri(Texture2D, TextureWrapS, ClampToEdge);
        glTexParameteri(Texture2D, TextureWrapT, ClampToEdge);
        glTexImage2D(Texture2D, 0, Rgba8, (int)width, (int)height, 0, Bgra, UnsignedByte, pixels);
        return textureId;
    }

    public static unsafe bool Upload(uint textureId, uint width, uint height, byte* pixels)
    {
        if (textureId == 0)
        {
            return false;
        }

        glBindTexture(Texture2D, textureId);
        glTexSubImage2D(Texture2D, 0, 0, 0, (int)width, (int)height, Bgra, UnsignedByte, pixels);
        return true;
    }

    public static void Delete(uint textureId)
    {
        unsafe
        {
            glDeleteTextures(1, &textureId);
        }
    }

    [DllImport("opengl32.dll")]
    private static extern unsafe void glGenTextures(int n, uint* textures);

    [DllImport("opengl32.dll")]
    private static extern void glBindTexture(uint target, uint texture);

    [DllImport("opengl32.dll")]
    private static extern void glTexParameteri(uint target, int pname, int param);

    [DllImport("opengl32.dll")]
    private static extern unsafe void glTexImage2D(uint target, int level, int internalformat, int width, int height, int border, uint format, uint type, void* pixels);

    [DllImport("opengl32.dll")]
    private static extern unsafe void glTexSubImage2D(uint target, int level, int xoffset, int yoffset, int width, int height, uint format, uint type, void* pixels);

    [DllImport("opengl32.dll")]
    private static extern unsafe void glDeleteTextures(int n, uint* textures);
}

internal sealed class ImmediateModeGpuSceneRenderer : IGpuSceneRenderer
{
    private const uint ColorBufferBit = 0x00004000u;
    private const uint Blend = 0x0BE2u;
    private const uint DepthTest = 0x0B71u;
    private const uint Texture2D = 0x0DE1u;
    private const uint SrcAlpha = 0x0302u;
    private const uint OneMinusSrcAlpha = 0x0303u;
    private const uint Quads = 0x0007u;
    private const uint Projection = 0x1701u;
    private const uint Modelview = 0x1700u;

    private readonly int _width;
    private readonly int _height;
    private readonly Sprite[] _sprites;
    private readonly int _matrixColumns;
    private readonly int _matrixRows;

    public ImmediateModeGpuSceneRenderer(uint width, uint height)
    {
        _width = checked((int)width);
        _height = checked((int)height);
        _sprites = BuildSprites();
        _matrixColumns = Math.Max(18, _width / 96);
        _matrixRows = Math.Max(10, _height / 96);
    }

    public void Render(uint textureId, int frameIndex)
    {
        GL.glViewport(0, 0, _width, _height);
        GL.glDisable(DepthTest);
        GL.glDisable(Texture2D);
        GL.glEnable(Blend);
        GL.glBlendFunc(SrcAlpha, OneMinusSrcAlpha);
        GL.glClearColor(0.06f, 0.07f, 0.09f, 1.0f);
        GL.glClear(ColorBufferBit);
        GL.glMatrixMode(Projection);
        GL.glLoadIdentity();
        GL.glOrtho(0.0, _width, _height, 0.0, -1.0, 1.0);
        GL.glMatrixMode(Modelview);
        GL.glLoadIdentity();

        DrawBackground(frameIndex);
        DrawAnimatedMatrix(frameIndex);
        DrawBands(frameIndex);
        DrawSprites(frameIndex);

        GL.glBindTexture(Texture2D, textureId);
        GL.glCopyTexSubImage2D(Texture2D, 0, 0, 0, 0, 0, _width, _height);
    }

    public void Dispose()
    {
    }

    private void DrawBackground(int frameIndex)
    {
        var stripeHeight = Math.Max(18, _height / 24);
        for (var stripe = 0; stripe < 18; stripe++)
        {
            var top = stripe * stripeHeight;
            var blue = (byte)(20 + ((stripe * 9) % 50));
            var green = (byte)(24 + ((stripe * 13) % 60));
            var red = (byte)(28 + ((stripe * 7) % 70));
            DrawRect(0, top, _width, stripeHeight + 1, blue, green, red, 255);
        }

        var pulse = (float)((Math.Sin(frameIndex * 0.09) + 1.0) * 0.5);
        DrawRect(0, 0, _width, _height, (byte)(16 + pulse * 20), (byte)(18 + pulse * 16), (byte)(22 + pulse * 28), 42);
    }

    private void DrawAnimatedMatrix(int frameIndex)
    {
        var left = _width / 6;
        var top = _height / 6;
        var matrixWidth = (_width * 2) / 3;
        var matrixHeight = (_height * 2) / 3;
        var cellWidth = Math.Max(16, matrixWidth / _matrixColumns);
        var cellHeight = Math.Max(16, matrixHeight / _matrixRows);
        var phase = frameIndex * 0.085;

        for (var row = 0; row < _matrixRows; row++)
        {
            for (var column = 0; column < _matrixColumns; column++)
            {
                var x = left + (column * cellWidth);
                var y = top + (row * cellHeight);
                var wave = Math.Sin((column * 0.73) + (row * 0.41) + phase);
                var twist = Math.Cos((column * 0.29) - (row * 0.63) - (phase * 1.7));
                var blue = (byte)(72 + ((wave + 1.0) * 68.0));
                var green = (byte)(56 + ((twist + 1.0) * 74.0));
                var red = (byte)(44 + (((wave - twist) + 2.0) * 42.0));
                DrawRect(x, y, cellWidth - 2, cellHeight - 2, blue, green, red, 210);
            }
        }
    }

    private void DrawBands(int frameIndex)
    {
        var bandHeight = Math.Max(18, _height / 28);
        for (var band = 0; band < 8; band++)
        {
            var y = Wrap((frameIndex * (band + 2) * 7) + (band * (_height / 9)), _height + bandHeight) - bandHeight;
            DrawRect(0, y, _width, bandHeight, (byte)(120 + band * 9), (byte)(44 + band * 11), (byte)(80 + band * 7), 96);
        }
    }

    private void DrawSprites(int frameIndex)
    {
        foreach (var sprite in _sprites)
        {
            var x = Wrap(sprite.StartX + (frameIndex * sprite.SpeedX), _width + sprite.Width) - sprite.Width;
            var y = Wrap(sprite.StartY + (frameIndex * sprite.SpeedY), _height + sprite.Height) - sprite.Height;
            var wave = (Math.Sin((frameIndex * sprite.PulseRate * 0.07) + sprite.Phase) + 1.0) * 0.5;
            var alpha = (byte)(110 + (wave * 120.0));
            DrawRect(
                x,
                y,
                sprite.Width,
                sprite.Height,
                (byte)Math.Clamp(sprite.Blue + (int)(wave * 70.0), 0, 255),
                (byte)Math.Clamp(sprite.Green + (int)(wave * 60.0), 0, 255),
                (byte)Math.Clamp(sprite.Red + (int)(wave * 90.0), 0, 255),
                alpha);
        }
    }

    private void DrawRect(int x, int y, int width, int height, byte blue, byte green, byte red, byte alpha)
    {
        var clippedLeft = Math.Max(0, x);
        var clippedTop = Math.Max(0, y);
        var clippedRight = Math.Min(_width, x + width);
        var clippedBottom = Math.Min(_height, y + height);
        if (clippedLeft >= clippedRight || clippedTop >= clippedBottom)
        {
            return;
        }

        GL.glColor4ub(red, green, blue, alpha);
        GL.glBegin(Quads);
        GL.glVertex2f(clippedLeft, clippedTop);
        GL.glVertex2f(clippedRight, clippedTop);
        GL.glVertex2f(clippedRight, clippedBottom);
        GL.glVertex2f(clippedLeft, clippedBottom);
        GL.glEnd();
    }

    private Sprite[] BuildSprites()
    {
        var sprites = new Sprite[180];
        for (var index = 0; index < sprites.Length; index++)
        {
            sprites[index] = new Sprite(
                StartX: (index * 131) % Math.Max(1, _width),
                StartY: (index * 71) % Math.Max(1, _height),
                Width: Math.Max(18, _width / (42 + (index % 15))),
                Height: Math.Max(18, _height / (36 + (index % 11))),
                SpeedX: 2 + (index % 7),
                SpeedY: 1 + ((index * 5) % 6),
                Blue: (byte)(45 + ((index * 19) % 170)),
                Green: (byte)(55 + ((index * 23) % 150)),
                Red: (byte)(60 + ((index * 29) % 145)),
                PulseRate: 1 + (index % 5),
                Phase: (index * 0.37) % (Math.PI * 2.0));
        }

        return sprites;
    }

    private static int Wrap(int value, int modulo)
    {
        var remainder = value % modulo;
        return remainder < 0 ? remainder + modulo : remainder;
    }

    private readonly record struct Sprite(
        int StartX,
        int StartY,
        int Width,
        int Height,
        int SpeedX,
        int SpeedY,
        byte Blue,
        byte Green,
        byte Red,
        int PulseRate,
        double Phase);
}

internal static class GL
{
    [DllImport("opengl32.dll")]
    public static extern void glFinish();

    [DllImport("opengl32.dll")]
    public static extern void glFlush();

    [DllImport("opengl32.dll")]
    public static extern void glViewport(int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    public static extern void glDisable(uint cap);

    [DllImport("opengl32.dll")]
    public static extern void glEnable(uint cap);

    [DllImport("opengl32.dll")]
    public static extern void glBlendFunc(uint sfactor, uint dfactor);

    [DllImport("opengl32.dll")]
    public static extern void glClearColor(float red, float green, float blue, float alpha);

    [DllImport("opengl32.dll")]
    public static extern void glClear(uint mask);

    [DllImport("opengl32.dll")]
    public static extern void glMatrixMode(uint mode);

    [DllImport("opengl32.dll")]
    public static extern void glLoadIdentity();

    [DllImport("opengl32.dll")]
    public static extern void glOrtho(double left, double right, double bottom, double top, double zNear, double zFar);

    [DllImport("opengl32.dll")]
    public static extern void glColor4ub(byte red, byte green, byte blue, byte alpha);

    [DllImport("opengl32.dll")]
    public static extern void glBegin(uint mode);

    [DllImport("opengl32.dll")]
    public static extern void glVertex2f(float x, float y);

    [DllImport("opengl32.dll")]
    public static extern void glEnd();

    [DllImport("opengl32.dll")]
    public static extern void glCopyTexSubImage2D(uint target, int level, int xoffset, int yoffset, int x, int y, int width, int height);

    [DllImport("opengl32.dll")]
    public static extern void glBindTexture(uint target, uint texture);

    [DllImport("opengl32.dll")]
    public static extern void glDrawBuffer(uint mode);

    [DllImport("opengl32.dll")]
    public static extern unsafe void glReadPixels(int x, int y, int width, int height, uint format, uint type, void* pixels);
}

internal sealed class ShaderGpuSceneRenderer : IGpuSceneRenderer
{
    private const uint VertexShader = 0x8B31u;
    private const uint FragmentShader = 0x8B30u;
    private const uint Quads = 0x0007u;
    private const uint Projection = 0x1701u;
    private const uint Modelview = 0x1700u;
    private const uint ColorBufferBit = 0x00004000u;
    private const uint Framebuffer = 0x8D40u;
    private const uint ColorAttachment0 = 0x8CE0u;
    private const uint FramebufferComplete = 0x8CD5u;
    private const uint Rgba = 0x1908u;
    private const uint UnsignedByte = 0x1401u;

    private readonly int _width;
    private readonly int _height;
    private readonly uint _program;
    private readonly uint _framebuffer;
    private readonly int _timeLocation;
    private readonly int _resolutionLocation;

    public ShaderGpuSceneRenderer(uint width, uint height)
    {
        _width = checked((int)width);
        _height = checked((int)height);

        var vertexShader = GLShader.CreateShader(VertexShader, VertexShaderSource);
        var fragmentShader = GLShader.CreateShader(FragmentShader, FragmentShaderSource);
        try
        {
            _program = GLShader.CreateProgram(vertexShader, fragmentShader);
        }
        finally
        {
            GLShader.DeleteShader(vertexShader);
            GLShader.DeleteShader(fragmentShader);
        }

        _timeLocation = GLShader.GetUniformLocation(_program, "uTime");
        _resolutionLocation = GLShader.GetUniformLocation(_program, "uResolution");
        _framebuffer = GLFramebuffer.Create();
    }

    public void Render(uint textureId, int frameIndex)
    {
        GLFramebuffer.Bind(Framebuffer, _framebuffer);
        GLFramebuffer.AttachTexture2D(Framebuffer, ColorAttachment0, GlTexture.Texture2D, textureId);
        GL.glDrawBuffer(ColorAttachment0);
        if (GLFramebuffer.CheckStatus(Framebuffer) != FramebufferComplete)
        {
            throw new InvalidOperationException("GL framebuffer is incomplete.");
        }

        GL.glViewport(0, 0, _width, _height);
        GL.glClearColor(0.04f, 0.05f, 0.07f, 1.0f);
        GL.glClear(ColorBufferBit);
        GL.glMatrixMode(Projection);
        GL.glLoadIdentity();
        GL.glOrtho(-1.0, 1.0, -1.0, 1.0, -1.0, 1.0);
        GL.glMatrixMode(Modelview);
        GL.glLoadIdentity();

        GLShader.UseProgram(_program);
        GLShader.Uniform1f(_timeLocation, frameIndex / 60.0f);
        GLShader.Uniform2f(_resolutionLocation, _width, _height);

        GL.glBegin(Quads);
        GL.glVertex2f(-1.0f, -1.0f);
        GL.glVertex2f(1.0f, -1.0f);
        GL.glVertex2f(1.0f, 1.0f);
        GL.glVertex2f(-1.0f, 1.0f);
        GL.glEnd();

        GLShader.UseProgram(0);
        if (frameIndex == 0)
        {
            VerifyFirstFrameIsNotBlack();
        }

        GL.glFinish();
        GLFramebuffer.Bind(Framebuffer, 0);
    }

    public void Dispose()
    {
        GLFramebuffer.Delete(_framebuffer);
        GLShader.DeleteProgram(_program);
    }

    private unsafe void VerifyFirstFrameIsNotBlack()
    {
        Span<byte> pixels = stackalloc byte[16];
        fixed (byte* pixelPtr = pixels)
        {
            GL.glReadPixels(_width / 2, _height / 2, 1, 1, Rgba, UnsignedByte, pixelPtr);
        }

        var hasColor = false;
        for (var index = 0; index < pixels.Length; index++)
        {
            if (pixels[index] != 0)
            {
                hasColor = true;
                break;
            }
        }

        if (!hasColor)
        {
            throw new InvalidOperationException("ShaderGpuSceneRenderer produced a black first pixel.");
        }
    }

    private const string VertexShaderSource = """
        #version 120
        void main()
        {
            gl_Position = gl_Vertex;
        }
        """;

    private const string FragmentShaderSource = """
        #version 120

        uniform float uTime;
        uniform vec2 uResolution;

        float hash(vec2 p)
        {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
        }

        vec3 palette(float t)
        {
            return 0.5 + 0.5 * cos(vec3(0.2, 1.1, 2.2) + (t * 6.28318));
        }

        void main()
        {
            vec2 uv = gl_FragCoord.xy / uResolution.xy;
            vec2 centered = uv * 2.0 - 1.0;
            centered.x *= uResolution.x / uResolution.y;

            vec2 grid = floor(uv * vec2(240.0, 135.0));
            float t = uTime * 0.9;

            float n0 = hash(grid + floor(t * 9.0));
            float n1 = hash(grid.yx + floor(t * 13.0) + 37.0);
            float n2 = hash(grid + floor(t * 17.0) + 91.0);

            vec3 color = palette(n0 + (t * 0.07)) * 0.45;
            color += palette(n1 + (t * 0.11)) * 0.25;
            color += vec3(n2 * 0.22, n0 * 0.18, n1 * 0.14);

            float stripes = 0.5 + 0.5 * sin((centered.x * 18.0) + (centered.y * 9.0) + (t * 7.5));
            color += vec3(0.18, 0.12, 0.08) * stripes;

            for (int i = 0; i < 18; i++)
            {
                float fi = float(i);
                vec2 center = vec2(
                    sin((fi * 1.73) + (t * (0.8 + fi * 0.05))),
                    cos((fi * 1.21) - (t * (0.6 + fi * 0.04))));
                center *= vec2(0.92, 0.66);
                float radius = 0.06 + 0.025 * sin(t * 1.7 + fi);
                float dist = length(centered - center);
                float blob = smoothstep(radius, radius - 0.025, dist);
                vec3 tint = palette(fi * 0.071 + t * 0.05);
                color = mix(color, tint, blob * 0.6);
            }

            color = clamp(color, 0.0, 1.0);
            gl_FragColor = vec4(color, 1.0);
        }
        """;
}

internal static class GLFramebuffer
{
    private static readonly GlGenFramebuffers s_genFramebuffers = Load<GlGenFramebuffers>("glGenFramebuffers");
    private static readonly GlBindFramebuffer s_bindFramebuffer = Load<GlBindFramebuffer>("glBindFramebuffer");
    private static readonly GlFramebufferTexture2D s_framebufferTexture2D = Load<GlFramebufferTexture2D>("glFramebufferTexture2D");
    private static readonly GlCheckFramebufferStatus s_checkFramebufferStatus = Load<GlCheckFramebufferStatus>("glCheckFramebufferStatus");
    private static readonly GlDeleteFramebuffers s_deleteFramebuffers = Load<GlDeleteFramebuffers>("glDeleteFramebuffers");

    public static uint Create()
    {
        unsafe
        {
            uint framebuffer = 0;
            s_genFramebuffers(1, &framebuffer);
            return framebuffer;
        }
    }

    public static void Bind(uint target, uint framebuffer) => s_bindFramebuffer(target, framebuffer);

    public static void AttachTexture2D(uint target, uint attachment, uint textureTarget, uint texture)
        => s_framebufferTexture2D(target, attachment, textureTarget, texture, 0);

    public static uint CheckStatus(uint target) => s_checkFramebufferStatus(target);

    public static void Delete(uint framebuffer)
    {
        if (framebuffer == 0)
        {
            return;
        }

        unsafe
        {
            var framebufferCopy = framebuffer;
            s_deleteFramebuffers(1, &framebufferCopy);
        }
    }

    private static T Load<T>(string name) where T : Delegate
    {
        var address = Wgl.wglGetProcAddress(name);
        if (address == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenGL function {name} is not available.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void GlGenFramebuffers(int count, uint* framebuffers);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindFramebuffer(uint target, uint framebuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlFramebufferTexture2D(uint target, uint attachment, uint textureTarget, uint texture, int level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GlCheckFramebufferStatus(uint target);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void GlDeleteFramebuffers(int count, uint* framebuffers);
}

internal static class GLShader
{
    private static readonly GlCreateShader s_createShader = Load<GlCreateShader>("glCreateShader");
    private static readonly GlShaderSource s_shaderSource = Load<GlShaderSource>("glShaderSource");
    private static readonly GlCompileShader s_compileShader = Load<GlCompileShader>("glCompileShader");
    private static readonly GlGetShaderIv s_getShaderIv = Load<GlGetShaderIv>("glGetShaderiv");
    private static readonly GlGetShaderInfoLog s_getShaderInfoLog = Load<GlGetShaderInfoLog>("glGetShaderInfoLog");
    private static readonly GlDeleteShader s_deleteShader = Load<GlDeleteShader>("glDeleteShader");
    private static readonly GlCreateProgram s_createProgram = Load<GlCreateProgram>("glCreateProgram");
    private static readonly GlAttachShader s_attachShader = Load<GlAttachShader>("glAttachShader");
    private static readonly GlLinkProgram s_linkProgram = Load<GlLinkProgram>("glLinkProgram");
    private static readonly GlGetProgramIv s_getProgramIv = Load<GlGetProgramIv>("glGetProgramiv");
    private static readonly GlGetProgramInfoLog s_getProgramInfoLog = Load<GlGetProgramInfoLog>("glGetProgramInfoLog");
    private static readonly GlDeleteProgram s_deleteProgram = Load<GlDeleteProgram>("glDeleteProgram");
    private static readonly GlUseProgram s_useProgram = Load<GlUseProgram>("glUseProgram");
    private static readonly GlGetUniformLocation s_getUniformLocation = Load<GlGetUniformLocation>("glGetUniformLocation");
    private static readonly GlUniform1f s_uniform1f = Load<GlUniform1f>("glUniform1f");
    private static readonly GlUniform2f s_uniform2f = Load<GlUniform2f>("glUniform2f");

    public static uint CreateShader(uint type, string source)
    {
        var shader = s_createShader(type);
        unsafe
        {
            var sourceBytes = System.Text.Encoding.ASCII.GetBytes(source);
            fixed (byte* sourcePtr = sourceBytes)
            {
                var sourcePointer = (IntPtr)sourcePtr;
                var length = sourceBytes.Length;
                s_shaderSource(shader, 1, &sourcePointer, &length);
            }
        }

        s_compileShader(shader);
        s_getShaderIv(shader, 0x8B81u, out var status);
        if (status != 0)
        {
            return shader;
        }

        throw new InvalidOperationException($"GL shader compile failed: {GetShaderInfoLog(shader)}");
    }

    public static uint CreateProgram(uint vertexShader, uint fragmentShader)
    {
        var program = s_createProgram();
        s_attachShader(program, vertexShader);
        s_attachShader(program, fragmentShader);
        s_linkProgram(program);
        s_getProgramIv(program, 0x8B82u, out var status);
        if (status != 0)
        {
            return program;
        }

        throw new InvalidOperationException($"GL program link failed: {GetProgramInfoLog(program)}");
    }

    public static int GetUniformLocation(uint program, string name)
    {
        return s_getUniformLocation(program, name);
    }

    public static void Uniform1f(int location, float value) => s_uniform1f(location, value);

    public static void Uniform2f(int location, float x, float y) => s_uniform2f(location, x, y);

    public static void UseProgram(uint program) => s_useProgram(program);

    public static void DeleteShader(uint shader) => s_deleteShader(shader);

    public static void DeleteProgram(uint program)
    {
        if (program != 0)
        {
            s_deleteProgram(program);
        }
    }

    private static string GetShaderInfoLog(uint shader)
    {
        s_getShaderIv(shader, 0x8B84u, out var length);
        if (length <= 1)
        {
            return "(no log)";
        }

        var buffer = new byte[length];
        s_getShaderInfoLog(shader, length, out var written, buffer);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Max(0, written));
    }

    private static string GetProgramInfoLog(uint program)
    {
        s_getProgramIv(program, 0x8B84u, out var length);
        if (length <= 1)
        {
            return "(no log)";
        }

        var buffer = new byte[length];
        s_getProgramInfoLog(program, length, out var written, buffer);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Max(0, written));
    }

    private static T Load<T>(string name) where T : Delegate
    {
        var address = Wgl.wglGetProcAddress(name);
        if (address == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenGL function {name} is not available.");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GlCreateShader(uint shaderType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void GlShaderSource(uint shader, int count, IntPtr* source, int* length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlCompileShader(uint shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGetShaderIv(uint shader, uint pname, out int parameters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGetShaderInfoLog(uint shader, int maxLength, out int length, byte[] infoLog);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteShader(uint shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GlCreateProgram();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlAttachShader(uint program, uint shader);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlLinkProgram(uint program);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGetProgramIv(uint program, uint pname, out int parameters);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlGetProgramInfoLog(uint program, int maxLength, out int length, byte[] infoLog);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlDeleteProgram(uint program);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlUseProgram(uint program);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GlGetUniformLocation(uint program, [MarshalAs(UnmanagedType.LPStr)] string name);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlUniform1f(int location, float v0);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlUniform2f(int location, float v0, float v1);
}

internal static class Wgl
{
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)]
    public static extern IntPtr wglGetProcAddress(string lpszProc);
}

internal sealed class SimpleMovingPattern : IFramePattern
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _background;
    private readonly int _barWidth;
    private readonly int _barHeight;
    private readonly int _blockSize;
    private int _previousVerticalX = -1;
    private int _previousHorizontalY = -1;
    private int _previousBlockX = -1;
    private int _previousBlockY = -1;

    public SimpleMovingPattern(uint width, uint height)
    {
        _width = checked((int)width);
        _height = checked((int)height);
        _background = new byte[checked(_width * _height * 4)];
        _barWidth = Math.Max(32, _width / 24);
        _barHeight = Math.Max(32, _height / 18);
        _blockSize = Math.Max(48, Math.Min(_width, _height) / 10);
        BuildBackground();
    }

    public void Initialize(byte[] target)
    {
        Buffer.BlockCopy(_background, 0, target, 0, _background.Length);
    }

    public void Advance(byte[] target, int frameIndex)
    {
        var verticalX = Wrap(frameIndex * Math.Max(4, _width / 320), _width + _barWidth) - _barWidth;
        var horizontalY = Wrap(frameIndex * Math.Max(3, _height / 360), _height + _barHeight) - _barHeight;
        var blockX = Wrap(frameIndex * Math.Max(5, _width / 280), _width + _blockSize) - _blockSize;
        var blockY = Wrap(frameIndex * Math.Max(4, _height / 300), _height + _blockSize) - _blockSize;

        RestoreVerticalBar(target, _previousVerticalX);
        RestoreHorizontalBar(target, _previousHorizontalY);
        RestoreBlock(target, _previousBlockX, _previousBlockY);

        DrawVerticalBar(target, verticalX);
        DrawHorizontalBar(target, horizontalY);
        DrawBlock(target, blockX, blockY, frameIndex);

        _previousVerticalX = verticalX;
        _previousHorizontalY = horizontalY;
        _previousBlockX = blockX;
        _previousBlockY = blockY;
    }

    private void BuildBackground()
    {
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var offset = (y * _width + x) * 4;
                var grid = (((x / 32) + (y / 32)) & 1) == 0 ? 22 : 34;
                _background[offset] = (byte)(grid + ((x * 31) / Math.Max(1, _width - 1)));       // B
                _background[offset + 1] = (byte)(grid + ((y * 47) / Math.Max(1, _height - 1))); // G
                _background[offset + 2] = (byte)(grid + (((x + y) * 19) / Math.Max(1, _width + _height - 2))); // R
                _background[offset + 3] = 255;
            }
        }
    }

    private void RestoreVerticalBar(byte[] target, int x)
    {
        if (x >= _width || x + _barWidth <= 0)
        {
            return;
        }

        RestoreRect(target, x, 0, _barWidth, _height);
    }

    private void RestoreHorizontalBar(byte[] target, int y)
    {
        if (y >= _height || y + _barHeight <= 0)
        {
            return;
        }

        RestoreRect(target, 0, y, _width, _barHeight);
    }

    private void RestoreBlock(byte[] target, int x, int y)
    {
        if (x >= _width || y >= _height || x + _blockSize <= 0 || y + _blockSize <= 0)
        {
            return;
        }

        RestoreRect(target, x, y, _blockSize, _blockSize);
    }

    private void RestoreRect(byte[] target, int x, int y, int rectWidth, int rectHeight)
    {
        var clippedLeft = Math.Max(0, x);
        var clippedTop = Math.Max(0, y);
        var clippedRight = Math.Min(_width, x + rectWidth);
        var clippedBottom = Math.Min(_height, y + rectHeight);
        if (clippedLeft >= clippedRight || clippedTop >= clippedBottom)
        {
            return;
        }

        var copyBytes = (clippedRight - clippedLeft) * 4;
        for (var row = clippedTop; row < clippedBottom; row++)
        {
            var offset = (row * _width + clippedLeft) * 4;
            Buffer.BlockCopy(_background, offset, target, offset, copyBytes);
        }
    }

    private void DrawVerticalBar(byte[] target, int x)
    {
        DrawRect(target, x, 0, _barWidth, _height, 0x20, 0xF0, 0x40);
    }

    private void DrawHorizontalBar(byte[] target, int y)
    {
        DrawRect(target, 0, y, _width, _barHeight, 0xE0, 0x40, 0x20);
    }

    private void DrawBlock(byte[] target, int x, int y, int frameIndex)
    {
        var blue = (byte)(60 + (frameIndex * 7) % 160);
        var green = (byte)(90 + (frameIndex * 11) % 120);
        var red = (byte)(120 + (frameIndex * 13) % 100);
        DrawRect(target, x, y, _blockSize, _blockSize, blue, green, red);
    }

    private void DrawRect(byte[] target, int x, int y, int rectWidth, int rectHeight, byte blue, byte green, byte red)
    {
        var clippedLeft = Math.Max(0, x);
        var clippedTop = Math.Max(0, y);
        var clippedRight = Math.Min(_width, x + rectWidth);
        var clippedBottom = Math.Min(_height, y + rectHeight);
        if (clippedLeft >= clippedRight || clippedTop >= clippedBottom)
        {
            return;
        }

        for (var row = clippedTop; row < clippedBottom; row++)
        {
            var offset = (row * _width + clippedLeft) * 4;
            for (var column = clippedLeft; column < clippedRight; column++)
            {
                target[offset] = blue;
                target[offset + 1] = green;
                target[offset + 2] = red;
                target[offset + 3] = 255;
                offset += 4;
            }
        }
    }

    private static int Wrap(int value, int modulo)
    {
        var remainder = value % modulo;
        return remainder < 0 ? remainder + modulo : remainder;
    }
}

internal sealed class ComplexMovingPattern : IFramePattern
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _tileSize;
    private readonly byte[] _background;
    private readonly Sprite[] _sprites;
    private readonly int[] _previousSpriteX;
    private readonly int[] _previousSpriteY;
    private readonly int[] _previousBandY;

    public ComplexMovingPattern(uint width, uint height)
    {
        _width = checked((int)width);
        _height = checked((int)height);
        _tileSize = Math.Max(16, Math.Min(_width, _height) / 48);
        _background = new byte[checked(_width * _height * 4)];
        _sprites = BuildSprites();
        _previousSpriteX = new int[_sprites.Length];
        _previousSpriteY = new int[_sprites.Length];
        _previousBandY = new int[6];
        Array.Fill(_previousSpriteX, int.MinValue);
        Array.Fill(_previousSpriteY, int.MinValue);
        Array.Fill(_previousBandY, int.MinValue);
        BuildBackground();
    }

    public void Initialize(byte[] target)
    {
        Buffer.BlockCopy(_background, 0, target, 0, _background.Length);
        DrawDynamicElements(target, 0);
    }

    public void Advance(byte[] target, int frameIndex)
    {
        RestoreDynamicElements(target);
        DrawDynamicElements(target, frameIndex);
    }

    private void BuildBackground()
    {
        for (var top = 0; top < _height; top += _tileSize)
        {
            var tileY = top / _tileSize;
            var rectHeight = Math.Min(_tileSize, _height - top);
            for (var left = 0; left < _width; left += _tileSize)
            {
                var tileX = left / _tileSize;
                var rectWidth = Math.Min(_tileSize, _width - left);
                var blue = (byte)(30 + (((tileX * 17) + (tileY * 29)) & 0x9F));
                var green = (byte)(24 + (((tileX * 11) ^ (tileY * 37)) & 0xAF));
                var red = (byte)(20 + (((tileX * 23) + (tileY * 13)) & 0xBF));
                FillRect(_background, left, top, rectWidth, rectHeight, blue, green, red);
            }
        }
    }

    private void RestoreDynamicElements(byte[] target)
    {
        for (var bandIndex = 0; bandIndex < _previousBandY.Length; bandIndex++)
        {
            if (_previousBandY[bandIndex] != int.MinValue)
            {
                RestoreRect(target, 0, _previousBandY[bandIndex], _width, Math.Max(20, _height / 24));
            }
        }

        for (var index = 0; index < _sprites.Length; index++)
        {
            if (_previousSpriteX[index] != int.MinValue && _previousSpriteY[index] != int.MinValue)
            {
                var sprite = _sprites[index];
                RestoreRect(target, _previousSpriteX[index], _previousSpriteY[index], sprite.Width, sprite.Height);
            }
        }
    }

    private void DrawDynamicElements(byte[] target, int frameIndex)
    {
        DrawAnimatedMatrix(target, frameIndex);
        DrawBands(target, frameIndex);

        for (var index = 0; index < _sprites.Length; index++)
        {
            DrawSprite(target, index, frameIndex);
        }
    }

    private void DrawAnimatedMatrix(byte[] target, int frameIndex)
    {
        var matrixWidth = (_width * 3) / 5;
        var matrixHeight = (_height * 3) / 5;
        var left = (_width - matrixWidth) / 2;
        var top = (_height - matrixHeight) / 2;
        var tileWidth = Math.Max(24, _tileSize);
        var tileHeight = Math.Max(24, _tileSize);
        var phase = frameIndex * 5;

        for (var y = top; y < top + matrixHeight; y += tileHeight)
        {
            var tileY = (y - top) / tileHeight;
            var rectHeight = Math.Min(tileHeight, (top + matrixHeight) - y);
            for (var x = left; x < left + matrixWidth; x += tileWidth)
            {
                var tileX = (x - left) / tileWidth;
                var rectWidth = Math.Min(tileWidth, (left + matrixWidth) - x);
                var blue = (byte)(40 + (((tileX * 29) + (tileY * 17) + phase) & 0xB7));
                var green = (byte)(30 + ((((tileX * 11) ^ (tileY * 37)) + (phase * 3)) & 0xBF));
                var red = (byte)(20 + ((((tileX * 19) + (tileY * 23) + (phase * 7))) & 0xCF));
                FillRect(target, x, y, rectWidth, rectHeight, blue, green, red);
            }
        }
    }

    private void DrawBands(byte[] target, int frameIndex)
    {
        var bandHeight = Math.Max(20, _height / 24);
        for (var band = 0; band < 6; band++)
        {
            var y = Wrap((frameIndex * (band + 3) * 5) + (band * (_height / 7)), _height + bandHeight) - bandHeight;
            var blue = (byte)(70 + (band * 19) % 120);
            var green = (byte)(100 + (band * 23) % 90);
            var red = (byte)(120 + (band * 29) % 80);
            FillRect(target, 0, y, _width, bandHeight, blue, green, red);
            _previousBandY[band] = y;
        }
    }

    private void DrawSprite(byte[] target, int spriteIndex, int frameIndex)
    {
        var sprite = _sprites[spriteIndex];
        var x = Wrap(sprite.StartX + (frameIndex * sprite.SpeedX), _width + sprite.Width) - sprite.Width;
        var y = Wrap(sprite.StartY + (frameIndex * sprite.SpeedY), _height + sprite.Height) - sprite.Height;
        var pulse = (frameIndex * sprite.PulseRate) & 0x3F;
        FillRect(
            target,
            x,
            y,
            sprite.Width,
            sprite.Height,
            (byte)Math.Clamp(sprite.Blue + pulse, 0, 255),
            (byte)Math.Clamp(sprite.Green + (pulse / 2), 0, 255),
            (byte)Math.Clamp(sprite.Red + ((pulse * 3) / 4), 0, 255));
        _previousSpriteX[spriteIndex] = x;
        _previousSpriteY[spriteIndex] = y;
    }

    private Sprite[] BuildSprites()
    {
        var sprites = new Sprite[96];
        for (var index = 0; index < sprites.Length; index++)
        {
            sprites[index] = new Sprite(
                StartX: (index * 97) % Math.Max(1, _width),
                StartY: (index * 57) % Math.Max(1, _height),
                Width: Math.Max(18, _width / (28 + (index % 19))),
                Height: Math.Max(18, _height / (24 + (index % 13))),
                SpeedX: 3 + (index % 7),
                SpeedY: 2 + ((index * 3) % 5),
                Blue: (byte)(50 + ((index * 31) % 160)),
                Green: (byte)(40 + ((index * 17) % 180)),
                Red: (byte)(30 + ((index * 23) % 190)),
                PulseRate: 3 + (index % 5));
        }

        return sprites;
    }

    private void RestoreRect(byte[] target, int x, int y, int rectWidth, int rectHeight)
    {
        var clippedLeft = Math.Max(0, x);
        var clippedTop = Math.Max(0, y);
        var clippedRight = Math.Min(_width, x + rectWidth);
        var clippedBottom = Math.Min(_height, y + rectHeight);
        if (clippedLeft >= clippedRight || clippedTop >= clippedBottom)
        {
            return;
        }

        var copyBytes = (clippedRight - clippedLeft) * 4;
        for (var row = clippedTop; row < clippedBottom; row++)
        {
            var offset = (row * _width + clippedLeft) * 4;
            Buffer.BlockCopy(_background, offset, target, offset, copyBytes);
        }
    }

    private void FillRect(byte[] target, int x, int y, int rectWidth, int rectHeight, byte blue, byte green, byte red)
    {
        var clippedLeft = Math.Max(0, x);
        var clippedTop = Math.Max(0, y);
        var clippedRight = Math.Min(_width, x + rectWidth);
        var clippedBottom = Math.Min(_height, y + rectHeight);
        if (clippedLeft >= clippedRight || clippedTop >= clippedBottom)
        {
            return;
        }

        for (var row = clippedTop; row < clippedBottom; row++)
        {
            var offset = (row * _width + clippedLeft) * 4;
            for (var column = clippedLeft; column < clippedRight; column++)
            {
                target[offset] = blue;
                target[offset + 1] = green;
                target[offset + 2] = red;
                target[offset + 3] = 255;
                offset += 4;
            }
        }
    }

    private static int Wrap(int value, int modulo)
    {
        var remainder = value % modulo;
        return remainder < 0 ? remainder + modulo : remainder;
    }

    private readonly record struct Sprite(
        int StartX,
        int StartY,
        int Width,
        int Height,
        int SpeedX,
        int SpeedY,
        byte Blue,
        byte Green,
        byte Red,
        int PulseRate);
}
