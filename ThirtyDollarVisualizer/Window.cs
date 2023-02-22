using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace ThirtyDollarVisualizer
{
    public class Window : GameWindow
    {
        public Window(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings) : base(gameWindowSettings, nativeWindowSettings)
        {
        }
        
        private VertexBuffer<float> _vbo = null!;
        private IndexBuffer _ibo = null!;
        private VertexArray<float> _vao = null!;
        private Shader _shader = null!;
        private readonly Stopwatch _stopwatch = new();

        private static void ClearAllErrors()
        {
            while (GL.GetError() != ErrorCode.NoError)
            {
                // Ignored
            }
            
        }

        private static void CheckErrors()
        {
            ErrorCode errorCode;
            while ((errorCode = GL.GetError()) != ErrorCode.NoError)
            {
                Console.WriteLine($"[OpenGL Error]: (0x{(int) errorCode:x8}) \'{errorCode}\'");
            }
        }
        
        protected override void OnLoad()
        {
            GL.ClearColor(.0f, .0f, .0f,1.0f);
            float[] positions =
            {
                -0.5f, -0.5f,
                0.5f, -0.5f,
                0.5f, 0.5f,
                -0.5f, 0.5f
            };

            uint[] indices = {
                0, 1, 2,
                2, 3, 0
            };
            
            _vao = new VertexArray<float>();
            _vbo = new VertexBuffer<float>(positions);
            _ibo = new IndexBuffer(indices);
            
            var layout = new VertexBufferLayout();
            layout.PushFloat(2);
            _vao.AddBuffer(_vbo, layout);

            _shader = Shader.FromFiles("./Assets/Shaders/shader.vert", "./Assets/Shaders/shader.frag");
            _stopwatch.Start();
            
            _shader.Unbind();
            _vao.Unbind();
            _vbo.Unbind();
            _ibo.Unbind();
            
            CheckErrors();
            
            base.OnLoad();
        }

        protected override void OnRenderFrame(FrameEventArgs args)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit);
            ClearAllErrors();
            _shader.Bind();
            
            // Oh my god! It's the LGBTQ lights.
            var r = (float) Math.Abs(Math.Cos(_stopwatch.ElapsedMilliseconds / 500f));
            var g = (float) Math.Abs(Math.Sin(_stopwatch.ElapsedMilliseconds / 500f));
            var b = (float) Math.Abs(Math.Sin(_stopwatch.ElapsedMilliseconds / 500f + 0.5));
            _shader.SetUniform4("u_Color", r, g, b, 1.0f);

            Renderer.Draw(_vao, _ibo, _shader);
            CheckErrors();
            
            SwapBuffers();
            //base.OnRenderFrame(args);
        }

        protected override void OnUpdateFrame(FrameEventArgs args)
        {
            var mouse = MouseState;
            
            base.OnUpdateFrame(args);
        }

        protected override void OnResize(ResizeEventArgs e)
        {
            GL.Viewport(0, 0, e.Width, e.Height);
            base.OnResize(e);
        }

        protected override void OnUnload()
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            
            base.OnUnload();
        }
    }
}