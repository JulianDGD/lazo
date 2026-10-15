using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lazo;

// Prueba de extremo a extremo del canal de retorno de la pantalla compartida, sin mover el mouse real:
// saludo, permiso para dibujar y para controlar, trazos en ambos sentidos, quitar permisos,
// y que un visor de una versión anterior (que no saluda) solo reciba los paquetes de siempre.
internal static class ControlSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "control-smoke";
        Directory.CreateDirectory(output);
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int code = 1;
        app.Startup += async (s, e) =>
        {
            try { code = await Run(output) ? 0 : 1; }
            catch (Exception ex) { Console.WriteLine("ERROR: " + ex); code = 1; }
            app.Shutdown();
        };
        app.Run();
        return code;
    }

    private static bool Check(bool condition, string what)
    {
        Console.WriteLine((condition ? "OK    " : "FALLO ") + what);
        return condition;
    }

    private static async Task<bool> Until(Func<bool> condition, int milliseconds)
    {
        for (int waited = 0; waited < milliseconds; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static async Task<bool> Run(string output)
    {
        Theme.Load();
        IPAddress local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .First(a => { byte[] b = a.GetAddressBytes(); return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31; });

        Window shared = new Window { Title = "Lazo prueba control", Width = 520, Height = 360, Content = new Grid { Background = Brushes.Beige } };
        shared.Show();
        await Task.Delay(300);
        ShareSource source = new ShareSource { IsWindow = true, Handle = new System.Windows.Interop.WindowInteropHelper(shared).Handle, Label = "Lazo prueba control" };

        bool ok = true;
        int port = FreePort();
        using (NetworkEngine viewerSide = new NetworkEngine(Path.Combine(output, "inbox"), FreeUdp(), port))
        using (NetworkEngine sharer = new NetworkEngine(Path.Combine(output, "inbox2"), FreeUdp(), FreePort()))
        {
            ScreenViewerWindow window = null;
            viewerSide.ScreenOffered += invite =>
            {
                TaskCompletionSource<IScreenSink> result = new TaskCompletionSource<IScreenSink>();
                Application.Current.Dispatcher.BeginInvoke((Action)(() =>
                {
                    window = new ScreenViewerWindow(invite.Sender, invite.Source) { Width = 640, Height = 420, Left = 40, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual };
                    window.Show();
                    result.TrySetResult(new ScreenViewer(window));
                }));
                return result.Task;
            };
            viewerSide.Start();

            ScreenShareSession session = new ScreenShareSession(sharer, source);
            AnnotationLayer hostLayer = new AnnotationLayer { Width = 1280, Height = 720 };
            hostLayer.Measure(new Size(1280, 720));
            hostLayer.Arrange(new Rect(0, 0, 1280, 720));
            hostLayer.Local += op => session.Annotate(op);
            int fromViewer = 0;
            session.Annotated += op => Application.Current.Dispatcher.BeginInvoke((Action)(() => { fromViewer++; hostLayer.Apply(op); }));
            byte asked = 0;
            session.PermissionRequested += (id, name, kind) => { asked |= kind; session.Answer(id, kind, true); };
            bool ended = false;
            session.Ended += () => ended = true;
            session.Invite(new Peer { Id = Guid.NewGuid(), Name = "Visor", Address = local, Port = port });
            session.Start();

            ok &= Check(await Until(() => window != null && window.CanAsk, 5000), "el visor nuevo y quien comparte se saludan");

            // Un trazo de quien comparte antes de dar permisos llega al visor.
            hostLayer.Commit(Stroke(11, AnnotationTool.Arrow, 0.1, 0.1, 0.5, 0.5));
            ok &= Check(await Until(() => window.Layer.Children.Count == 1, 3000), "el trazo de quien comparte aparece en el visor");

            window.Request(SharePermission.Draw);
            ok &= Check(await Until(() => (window.Granted & SharePermission.Draw) != 0, 3000) && (asked & SharePermission.Draw) != 0,
                "pedir dibujar llega como petición y el permiso vuelve al visor");

            window.Layer.Commit(Stroke(22, AnnotationTool.Pen, 0.2, 0.2, 0.3, 0.4));
            ok &= Check(await Until(() => fromViewer == 1 && hostLayer.Children.Count == 2, 3000), "el trazo del visor llega a quien comparte");

            window.Layer.EraseMine();
            ok &= Check(await Until(() => hostLayer.Children.Count == 1, 3000), "borrar los trazos propios se refleja en quien comparte");

            window.Request(SharePermission.Control);
            ok &= Check(await Until(() => (window.Granted & SharePermission.Control) != 0 && session.Controller == "Visor", 3000),
                "pedir control se concede y quien comparte sabe quién controla");

            session.RevokeAll();
            ok &= Check(await Until(() => window.Granted == 0 && session.Controller == null && !session.AnyGranted, 3000), "quitar permisos llega al visor");

            // Sin permiso, un trazo del visor se ignora.
            window.Layer.Commit(Stroke(33, AnnotationTool.Box, 0.6, 0.6, 0.8, 0.8));
            await Task.Delay(600);
            ok &= Check(hostLayer.Children.Count == 1 && fromViewer == 1, "sin permiso, los trazos del visor no se aceptan");

            hostLayer.Commit(new AnnotationOp { Kind = AnnotationOp.Clear });
            ok &= Check(await Until(() => window.Layer.Children.Count == 0, 3000), "borrar todo llega al visor");

            ok &= await OldViewer(session, hostLayer, local);

            session.Stop();
            ok &= Check(await Until(() => ended, 4000), "la transmisión termina");
            if (window != null) window.Close();
        }
        shared.Close();
        Console.WriteLine(ok ? "OK: canal de retorno de la pantalla compartida" : "FALLO en el canal de retorno");
        return ok;
    }

    /// <summary>Visor de una versión anterior: no saluda y rechaza cualquier paquete que no sea S, F, K o E.</summary>
    private static async Task<bool> OldViewer(ScreenShareSession session, AnnotationLayer hostLayer, IPAddress local)
    {
        TcpListener listener = new TcpListener(local, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        session.Invite(new Peer { Id = Guid.NewGuid(), Name = "Antiguo", Address = local, Port = port });
        bool clean = true;
        int frames = 0;
        using (TcpClient client = await listener.AcceptTcpClientAsync())
        {
            listener.Stop();
            client.ReceiveTimeout = 5000;
            NetworkStream stream = client.GetStream();
            BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true);
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(5));
            for (int i = 0; i < 3; i++) reader.ReadBytes(reader.ReadUInt16());
            stream.WriteByte(1);
            Task reading = Task.Run(() =>
            {
                try
                {
                    DateTime until = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < until)
                    {
                        byte type = reader.ReadByte();
                        if (type == (byte)'K') continue;
                        if (type == (byte)'E') break;
                        if (type == (byte)'S') { reader.ReadInt32(); reader.ReadInt32(); continue; }
                        if (type != (byte)'F') { clean = false; break; }
                        reader.ReadInt32(); reader.ReadInt32();
                        int count = reader.ReadInt32();
                        for (int i = 0; i < count; i++)
                        {
                            reader.ReadBytes(8);
                            reader.ReadBytes(reader.ReadInt32());
                        }
                        frames++;
                    }
                }
                catch (IOException) { }
            });
            // Un trazo mientras mira: al visor antiguo no debe llegarle.
            await Task.Delay(400);
            hostLayer.Commit(Stroke(44, AnnotationTool.Pen, 0.4, 0.4, 0.6, 0.6));
            await reading;
            bool ok = Check(magic == "LAZOS", "un visor antiguo recibe la invitación de siempre");
            ok &= Check(clean && frames > 0, "un visor antiguo solo recibe fotogramas conocidos (" + frames + ")");
            return ok;
        }
    }

    private static AnnotationOp Stroke(int id, AnnotationTool tool, double x0, double y0, double x1, double y1)
    {
        return new AnnotationOp
        {
            Kind = AnnotationOp.Begin,
            Id = id,
            Tool = tool,
            Color = 0xFFF25C45,
            Xs = new[] { AnnotationOp.Norm(x0), AnnotationOp.Norm(x1) },
            Ys = new[] { AnnotationOp.Norm(y0), AnnotationOp.Norm(y1) }
        };
    }

    private static int FreePort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static int FreeUdp()
    {
        using (UdpClient probe = new UdpClient(0)) return ((IPEndPoint)probe.Client.LocalEndPoint).Port;
    }
}
