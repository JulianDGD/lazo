using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>Permisos que quien comparte concede a cada persona que mira.</summary>
    internal static class SharePermission
    {
        public const byte Draw = 1;
        public const byte Control = 2;
        /// <summary>Se suma al tipo de una petición para renunciar a ese permiso.</summary>
        public const byte Release = 0x80;
    }

    /// <summary>
    /// Envío de quien mira hacia quien comparte (peticiones, trazos, mouse y teclado) en su propio hilo,
    /// para que una red lenta no congele la ventana. Los paquetes salen en el orden en que se piden.
    /// </summary>
    internal sealed class ScreenBackChannel
    {
        private readonly Stream _stream;
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _queue = new System.Collections.Concurrent.BlockingCollection<byte[]>(4096);

        public ScreenBackChannel(Stream stream)
        {
            _stream = stream;
            new System.Threading.Thread(Pump) { IsBackground = true, Name = "Lazo screen back-channel" }.Start();
        }

        public void Send(byte[] packet)
        {
            try { _queue.TryAdd(packet); }
            catch (InvalidOperationException) { }
        }

        public void Close()
        {
            try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        }

        private void Pump()
        {
            try
            {
                foreach (byte[] packet in _queue.GetConsumingEnumerable())
                {
                    _stream.Write(packet, 0, packet.Length);
                    _stream.Flush();
                }
            }
            catch { Close(); }
        }
    }

    /// <summary>
    /// Evento de mouse o teclado enviado por quien controla. Las coordenadas van normalizadas (0–65535)
    /// sobre la fuente compartida. Tamaño fijo por tipo: 1 mover · 2/3 botón · 4 rueda · 5/6 tecla.
    /// </summary>
    internal struct RemoteInputEvent
    {
        public const byte Move = 1, Down = 2, Up = 3, Wheel = 4, KeyDown = 5, KeyUp = 6;
        public byte Type;
        public byte Button;
        public ushort X, Y;
        public short Delta;
        public ushort Key;

        public byte[] ToBytes()
        {
            using (MemoryStream buffer = new MemoryStream(8))
            using (BinaryWriter writer = new BinaryWriter(buffer))
            {
                writer.Write((byte)'I');
                writer.Write(Type);
                switch (Type)
                {
                    case Move: writer.Write(X); writer.Write(Y); break;
                    case Down:
                    case Up: writer.Write(Button); writer.Write(X); writer.Write(Y); break;
                    case Wheel: writer.Write(Delta); writer.Write(X); writer.Write(Y); break;
                    default: writer.Write(Key); break;
                }
                writer.Flush();
                return buffer.ToArray();
            }
        }

        /// <summary>Lee el cuerpo tras el tipo 'I'. Devuelve false si el tipo no se conoce.</summary>
        public static bool Read(BinaryReader reader, out RemoteInputEvent input)
        {
            input = new RemoteInputEvent { Type = reader.ReadByte() };
            switch (input.Type)
            {
                case Move: input.X = reader.ReadUInt16(); input.Y = reader.ReadUInt16(); return true;
                case Down:
                case Up: input.Button = reader.ReadByte(); input.X = reader.ReadUInt16(); input.Y = reader.ReadUInt16(); return true;
                case Wheel: input.Delta = reader.ReadInt16(); input.X = reader.ReadUInt16(); input.Y = reader.ReadUInt16(); return true;
                case KeyDown:
                case KeyUp: input.Key = reader.ReadUInt16(); return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reproduce en este equipo el mouse y el teclado de quien tiene el control, limitado al área compartida.
    /// Recuerda botones y teclas pulsados para soltarlos al quitar el control y no dejar nada atascado.
    /// Debe usarse desde un hilo en píxeles físicos (<see cref="Native.EnterPerMonitorDpi"/>).
    /// </summary>
    internal sealed class RemoteInputInjector
    {
        private readonly ShareSource _source;
        private readonly object _lock = new object();
        private readonly HashSet<ushort> _keys = new HashSet<ushort>();
        private readonly HashSet<byte> _buttons = new HashSet<byte>();

        public RemoteInputInjector(ShareSource source)
        {
            _source = source;
        }

        public void Apply(RemoteInputEvent input)
        {
            lock (_lock)
            {
                switch (input.Type)
                {
                    case RemoteInputEvent.Move:
                        MoveTo(input.X, input.Y, 0, 0);
                        break;
                    case RemoteInputEvent.Down:
                    case RemoteInputEvent.Up:
                        uint flag = ButtonFlag(input.Button, input.Type == RemoteInputEvent.Down);
                        if (flag == 0) return;
                        if (input.Type == RemoteInputEvent.Down) _buttons.Add(input.Button);
                        else if (!_buttons.Remove(input.Button)) return;
                        MoveTo(input.X, input.Y, flag, 0);
                        break;
                    case RemoteInputEvent.Wheel:
                        MoveTo(input.X, input.Y, MouseWheel, input.Delta);
                        break;
                    case RemoteInputEvent.KeyDown:
                        if (input.Key == 0 || input.Key > 0xFE) return;
                        _keys.Add(input.Key);
                        Key(input.Key, false);
                        break;
                    case RemoteInputEvent.KeyUp:
                        if (!_keys.Remove(input.Key)) return;
                        Key(input.Key, true);
                        break;
                }
            }
        }

        /// <summary>Suelta todo lo que quien controlaba dejó pulsado.</summary>
        public void ReleaseAll()
        {
            lock (_lock)
            {
                foreach (ushort key in _keys) Key(key, true);
                _keys.Clear();
                foreach (byte button in _buttons)
                {
                    uint flag = ButtonFlag(button, false);
                    if (flag != 0) Send(MouseInput(0, 0, flag, 0));
                }
                _buttons.Clear();
            }
        }

        private void MoveTo(ushort nx, ushort ny, uint extra, int data)
        {
            Native.RECT area;
            if (!Area(out area)) return;
            int width = Math.Max(1, area.Right - area.Left), height = Math.Max(1, area.Bottom - area.Top);
            double x = area.Left + nx / 65535.0 * (width - 1);
            double y = area.Top + ny / 65535.0 * (height - 1);
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
            int vw = Math.Max(2, GetSystemMetrics(78)), vh = Math.Max(2, GetSystemMetrics(79));
            int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            Send(MouseInput(ax, ay, MouseMove | MouseAbsolute | MouseVirtualDesk | extra, data));
        }

        /// <summary>Rectángulo físico de la fuente: el monitor o el marco visible de la ventana.</summary>
        private bool Area(out Native.RECT area)
        {
            if (!_source.IsWindow)
            {
                area = new Native.RECT { Left = _source.Left, Top = _source.Top, Right = _source.Left + _source.Width, Bottom = _source.Top + _source.Height };
                return true;
            }
            area = new Native.RECT();
            if (!Native.IsWindow(_source.Handle) || Native.IsIconic(_source.Handle)) return false;
            if (Native.DwmGetWindowAttribute(_source.Handle, 9, out area, Marshal.SizeOf(typeof(Native.RECT))) == 0) return true;
            return Native.GetWindowRect(_source.Handle, out area);
        }

        private static uint ButtonFlag(byte button, bool down)
        {
            switch (button)
            {
                case 1: return down ? 0x0002u : 0x0004u;
                case 2: return down ? 0x0008u : 0x0010u;
                case 3: return down ? 0x0020u : 0x0040u;
            }
            return 0;
        }

        private static void Key(ushort vk, bool up)
        {
            uint flags = up ? 0x0002u : 0u;
            if (IsExtended(vk)) flags |= 0x0001u;
            INPUT input = new INPUT { type = 1 };
            input.u.ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags, dwExtraInfo = Marker };
            Send(input);
        }

        private static bool IsExtended(ushort vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24: case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2C: case 0x2D: case 0x2E: case 0x5B: case 0x5C: case 0x5D: case 0x6F: case 0x90:
                case 0xA3: case 0xA5:
                    return true;
            }
            return false;
        }

        private static INPUT MouseInput(int x, int y, uint flags, int data)
        {
            INPUT input = new INPUT { type = 0 };
            input.u.mi = new MOUSEINPUT { dx = x, dy = y, mouseData = data, dwFlags = flags, dwExtraInfo = Marker };
            return input;
        }

        private static void Send(INPUT input)
        {
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        /// <summary>Marca la entrada inyectada por Lazo, por si alguna ventana necesita distinguirla.</summary>
        private static readonly IntPtr Marker = new IntPtr(0x4C415A4F);
        private const uint MouseMove = 0x0001, MouseWheel = 0x0800, MouseVirtualDesk = 0x4000, MouseAbsolute = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT { public int dx; public int dy; public int mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public int type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    }

    /// <summary>
    /// Capa transparente sobre lo que se comparte, donde quien comparte dibuja y ve lo que dibujan los demás.
    /// No aparece en la transmisión (los trazos viajan aparte, como vectores) y deja pasar el mouse
    /// mientras no se está dibujando.
    /// </summary>
    internal sealed class ScreenOverlayWindow : Window
    {
        private readonly ShareSource _source;
        private readonly DispatcherTimer _follow;
        private IntPtr _hwnd;
        private Native.RECT _placed;
        private bool _placedOnce;

        public AnnotationLayer Layer { get; private set; }
        public event Action DrawingEnded;

        public ScreenOverlayWindow(ShareSource source)
        {
            _source = source;
            Title = "Lazo · anotaciones";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            Layer = new AnnotationLayer();
            Content = Layer;
            _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(source.IsWindow ? 120 : 1000) };
            _follow.Tick += (s, e) => Follow();
            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                SetClickThrough(true);
                try { Native.SetWindowDisplayAffinity(_hwnd, 0x11); } catch { }
                Follow();
                _follow.Start();
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape && Layer.Drawing) { SetDrawing(false); e.Handled = true; } };
            Closed += (s, e) => _follow.Stop();
        }

        public bool Drawing { get { return Layer.Drawing; } }

        public void SetDrawing(bool on)
        {
            if (Layer.Drawing == on) return;
            Layer.Drawing = on;
            SetClickThrough(!on);
            if (on) { Activate(); Focus(); }
            else
            {
                Action handler = DrawingEnded;
                if (handler != null) handler();
            }
        }

        private void SetClickThrough(bool through)
        {
            if (_hwnd == IntPtr.Zero) return;
            int style = Native.GetWindowLong(_hwnd, -20);
            // WS_EX_TOOLWINDOW siempre; WS_EX_TRANSPARENT y WS_EX_NOACTIVATE mientras no se dibuja.
            style |= 0x80;
            if (through) style |= 0x20 | 0x08000000;
            else style &= ~(0x20 | 0x08000000);
            SetWindowLong(_hwnd, -20, style);
        }

        /// <summary>Se coloca exactamente sobre la fuente, en píxeles físicos. Las ventanas se siguen al moverse.</summary>
        private void Follow()
        {
            if (_hwnd == IntPtr.Zero) return;
            IntPtr previous = Native.EnterPerMonitorDpi();
            try
            {
                Native.RECT area = new Native.RECT();
                bool visible;
                if (_source.IsWindow)
                {
                    visible = Native.IsWindow(_source.Handle) && !Native.IsIconic(_source.Handle) &&
                        Native.DwmGetWindowAttribute(_source.Handle, 9, out area, Marshal.SizeOf(typeof(Native.RECT))) == 0;
                }
                else
                {
                    visible = true;
                    area = new Native.RECT { Left = _source.Left, Top = _source.Top, Right = _source.Left + _source.Width, Bottom = _source.Top + _source.Height };
                }
                Opacity = visible ? 1 : 0;
                if (!visible) { if (Layer.Drawing) SetDrawing(false); return; }
                if (area.Left == _placed.Left && area.Top == _placed.Top && area.Right == _placed.Right && area.Bottom == _placed.Bottom) return;
                // Solo la primera vez se coloca en la capa «siempre visible»; después no se reordena,
                // para no tapar la barra ni los avisos que se abran luego.
                uint flags = _placedOnce ? 0x0010u | 0x0004u : 0x0010u;
                _placedOnce = true;
                _placed = area;
                SetWindowPos(_hwnd, new IntPtr(-1), area.Left, area.Top, area.Right - area.Left, area.Bottom - area.Top, flags);
            }
            finally { Native.LeaveDpi(previous); }
        }

        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }

    internal static class WindowOrder
    {
        /// <summary>Pone la ventana encima de las demás «siempre visibles» sin activarla.</summary>
        public static void RaiseTopmost(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    }

    /// <summary>Aviso abajo a la derecha: alguien que mira pide dibujar o controlar. No aparece en la transmisión.</summary>
    internal sealed class PermissionPromptWindow : Window
    {
        private readonly Action<bool> _respond;
        private bool _answered;

        public PermissionPromptWindow(string viewer, byte kind, int stack, Action<bool> respond)
        {
            _respond = respond;
            bool control = kind == SharePermission.Control;
            Title = "Lazo · permiso";
            Width = 380;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            FontFamily = Theme.Font;

            StackPanel body = new StackPanel();
            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.Children.Add(new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                Background = control ? Theme.Danger : Theme.Primary,
                Child = Icons.Make(control ? "pointer" : "pen", 20, control ? Brushes.White : Theme.PrimaryText, 2)
            });
            StackPanel words = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Theme.Text(viewer + (control ? " quiere controlar tu equipo" : " quiere dibujar en tu pantalla"), 13, Theme.Ink, FontWeights.SemiBold));
            words.Children.Add(Theme.Text(control
                ? "Podrá mover el mouse, hacer clic y escribir dentro de lo que compartes. Quítale el control cuando quieras desde la barra superior."
                : "Sus trazos aparecerán sobre tu pantalla y los verán todos los que miran.", 11.5, Theme.Muted));
            Grid.SetColumn(words, 1);
            head.Children.Add(words);
            body.Children.Add(head);
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button deny = Theme.Button("Rechazar", false);
            deny.Click += (s, e) => Answer(false);
            Button allow = Theme.Button("Permitir", true);
            allow.Margin = new Thickness(8, 0, 0, 0);
            allow.Click += (s, e) => Answer(true);
            buttons.Children.Add(deny);
            buttons.Children.Add(allow);
            body.Children.Add(buttons);
            Content = new Border
            {
                Margin = new Thickness(10),
                Background = Theme.ShellSurface(),
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 14, 14, 12),
                Child = body
            };
            DispatcherTimer timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            timeout.Tick += (s, e) => { timeout.Stop(); Answer(false); };
            timeout.Start();
            SourceInitialized += (s, e) =>
            {
                try { Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, 0x11); } catch { }
            };
            Loaded += (s, e) => WindowPlacement.PlaceBottomRight(this, stack);
            Closed += (s, e) => { timeout.Stop(); Answer(false); };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Answer(false); };
        }

        public void Dismiss()
        {
            Answer(false);
        }

        private void Answer(bool accept)
        {
            if (_answered) return;
            _answered = true;
            _respond(accept);
            if (IsVisible) Close();
        }
    }
}
