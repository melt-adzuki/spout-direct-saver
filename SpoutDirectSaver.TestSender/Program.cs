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

var pattern = new MovingPattern(options.Width, options.Height);
var frameBuffer = GC.AllocateUninitializedArray<byte>(checked((int)(options.Width * options.Height * 4)));
pattern.Initialize(frameBuffer);
var textureId = 0u;

if (options.SendTexture)
{
    textureId = GlTexture.Create(options.Width, options.Height, frameBuffer);
}

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
        pattern.Advance(frameBuffer, framesSent);

        unsafe
        {
            fixed (byte* pixels = frameBuffer)
            {
                var success = options.SendTexture
                    ? GlTexture.Upload(textureId, options.Width, options.Height, pixels) &&
                      sender.SendTexture(textureId, GlTexture.Texture2D, options.Width, options.Height, false, 0)
                    : sender.SendImage(pixels, options.Width, options.Height, GlTexture.Bgra, false, 0);

                if (!success)
                {
                    throw new InvalidOperationException(options.SendTexture ? "SendTexture failed." : "SendImage failed.");
                }
            }
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

internal sealed record SenderOptions(string Name, uint Width, uint Height, double FrameRate, TimeSpan? Duration, bool SendTexture)
{
    public static SenderOptions Parse(string[] args)
    {
        var name = "SpoutDirectSaverTest";
        var width = 3840u;
        var height = 2160u;
        var frameRate = 60.0;
        TimeSpan? duration = TimeSpan.FromSeconds(10);
        var sendTexture = true;

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
            }
        }

        return new SenderOptions(name, width, height, frameRate, duration, sendTexture);
    }
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

internal sealed class MovingPattern
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

    public MovingPattern(uint width, uint height)
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
