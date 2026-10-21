using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace SkyPass.App;

internal static class Program
{
    private static void Main()
    {
        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 800),
            Title = "SkyPass 過境預報台",
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        };

        using var window = Window.Create(options);
        GL? gl = null;
        IInputContext? input = null;
        ImGuiController? imgui = null;

        window.Load += () =>
        {
            gl = window.CreateOpenGL();
            input = window.CreateInput();
            imgui = new ImGuiController(gl, window, input);
        };
        window.FramebufferResize += size => gl?.Viewport(size);
        window.Render += delta =>
        {
            imgui!.Update((float)delta);
            gl!.ClearColor(0.1f, 0.1f, 0.12f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            ImGuiNET.ImGui.ShowDemoWindow();
            imgui.Render();
        };
        window.Closing += () =>
        {
            imgui?.Dispose();
            input?.Dispose();
            gl?.Dispose();
        };

        window.Run();
    }
}
