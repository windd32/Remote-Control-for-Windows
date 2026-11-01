using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WpfMouse = System.Windows.Input.Mouse;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfKey = System.Windows.Input.Key;
using MessageBox = System.Windows.MessageBox;

namespace RemoteControl;

public partial class MainWindow : Window
{
    private TcpListener? listener;
    private TcpClient? peer;
    private NetworkStream? stream;
    private DispatcherTimer? timer;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private volatile bool approved;
    private volatile bool closing;
    private int screenWidth;
    private int screenHeight;

    public MainWindow() { InitializeComponent(); ControllerPanel.Visibility = Visibility.Collapsed; ClientPanel.Visibility = Visibility.Visible; StatusText.Text = "Введите IP и нажмите Connect"; Closed += (_, _) => Stop(); }

    private void RoleBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ControllerPanel is null) return;
        bool host = RoleBox.SelectedIndex == 0;
        ControllerPanel.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        ClientPanel.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = host ? "Ожидание" : "Готов к подключению";
    }

    private async void Listen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            closing = false;
            listener = new TcpListener(IPAddress.Any, Port(PortBox.Text)); listener.Start(); ListenButton.IsEnabled = false;
            StatusText.Text = "Ожидание входящего подключения";
            peer = await listener.AcceptTcpClientAsync(); stream = peer.GetStream();
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            if (reader.ReadString() != "HELLO") throw new IOException("Неверный запрос клиента");
            writer.Write("REQUEST"); writer.Flush();
            if (reader.ReadString() != "CONSENT") { StatusText.Text = "Пользователь клиента отказал"; return; }
            approved = true;
            writer.Write("APPROVED"); writer.Flush();
            StatusText.Text = "Сеанс активен — Esc или закрытие окна отключает";
            RemoteImage.Focus();
            await Task.Run(() => ReceiveLoop(reader));
        }
        catch (Exception ex) { if (!closing) StatusText.Text = "Отключено: " + ex.Message; }
        finally { ListenButton.IsEnabled = true; Stop(); }
    }

    private void ReceiveLoop(BinaryReader reader)
    {
        try
        {
            while (!closing && peer?.Connected == true)
            {
                string type = reader.ReadString();
                if (type == "FRAME")
                {
                    int length = reader.ReadInt32();
                    if (length is < 1 or > 12_000_000) throw new IOException("Размер кадра вне диапазона");
                    byte[] data = reader.ReadBytes(length);
                    if (data.Length != length) break;
                    Dispatcher.Invoke(() => ShowFrame(data));
                }
                else if (type == "M")
                {
                    int x = reader.ReadInt32(), y = reader.ReadInt32(); byte button = reader.ReadByte(); int action = reader.ReadInt32();
                    SetCursorPos(x, y); if (button != 0) MouseInput(button, action);
                }
                else if (type == "K") KeyInput(reader.ReadInt32(), reader.ReadBoolean());
                else if (type == "STOP") break;
            }
        }
        catch { }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            closing = false; ConnectButton.IsEnabled = false;
            peer = new TcpClient(); await peer.ConnectAsync(HostBox.Text.Trim(), Port(ClientPortBox.Text)); stream = peer.GetStream();
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write("HELLO"); writer.Flush();
            if (reader.ReadString() != "REQUEST") throw new IOException("Управляющий отклонил подключение");
            bool consent = MessageBox.Show("Разрешить показ экрана и управление мышью/клавиатурой?", "Разрешение на удалённое управление", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!consent) { StatusText.Text = "Вы отказали в подключении"; return; }
            writer.Write("CONSENT"); writer.Flush();
            if (reader.ReadString() != "APPROVED") { StatusText.Text = "Управляющий не начал сеанс"; return; }
            approved = true; StatusText.Text = "Экран передаётся — закройте окно для остановки";
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += async (_, _) => await SendFrameAsync(); timer.Start();
            await Task.Run(() => ReadControlLoop(reader));
        }
        catch (Exception ex) { if (!closing) StatusText.Text = "Ошибка: " + ex.Message; }
        finally { ConnectButton.IsEnabled = true; Stop(); }
    }

    private void ReadControlLoop(BinaryReader reader)
    {
        try
        {
            while (!closing && peer?.Connected == true)
            {
                string type = reader.ReadString();
                if (type == "M") { int x = reader.ReadInt32(), y = reader.ReadInt32(); byte b = reader.ReadByte(); int a = reader.ReadInt32(); SetCursorPos(x, y); if (b != 0) MouseInput(b, a); }
                else if (type == "K") KeyInput(reader.ReadInt32(), reader.ReadBoolean());
                else if (type == "STOP") break;
            }
        }
        catch { }
    }

    private async Task SendFrameAsync()
    {
        try
        {
            if (stream is null || peer?.Connected != true || !approved) return;
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            screenWidth = bounds.Width; screenHeight = bounds.Height;
            int w = Math.Max(1, bounds.Width / 2), h = Math.Max(1, bounds.Height / 2);
            using var captured = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(captured)) g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            using (var scaled = new Bitmap(w, h))
            {
                using (var sg = Graphics.FromImage(scaled)) sg.DrawImage(captured, 0, 0, w, h);
                using var ms = new MemoryStream(); scaled.Save(ms, ImageFormat.Jpeg);
                await sendLock.WaitAsync();
                try { using var bw = new BinaryWriter(stream, Encoding.UTF8, true); bw.Write("FRAME"); bw.Write((int)ms.Length); bw.Write(ms.ToArray()); bw.Flush(); }
                finally { sendLock.Release(); }
            }
        }
        catch (Exception ex) { Dispatcher.Invoke(() => StatusText.Text = "Ошибка передачи экрана: " + ex.Message); Stop(); }
    }

    private void ShowFrame(byte[] data)
    {
        using var ms = new MemoryStream(data);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = ms; bitmap.EndInit(); bitmap.Freeze(); RemoteImage.Source = bitmap;
    }

    private async Task SendMouse(byte button, int action)
    {
        if (stream is null || !approved) return;
        try
        {
            var p = WpfMouse.GetPosition(RemoteImage);
            int x = (int)(p.X / Math.Max(1, RemoteImage.ActualWidth) * screenWidth);
            int y = (int)(p.Y / Math.Max(1, RemoteImage.ActualHeight) * screenHeight);
            await sendLock.WaitAsync();
            try { using var bw = new BinaryWriter(stream, Encoding.UTF8, true); bw.Write("M"); bw.Write(x); bw.Write(y); bw.Write(button); bw.Write(action); bw.Flush(); }
            finally { sendLock.Release(); }
        }
        catch { }
    }

    private async void RemoteImage_MouseMove(object s, System.Windows.Input.MouseEventArgs e) { await SendMouse(0, 0); }
    private async void RemoteImage_MouseDown(object s, System.Windows.Input.MouseButtonEventArgs e) { RemoteImage.Focus(); await SendMouse((byte)(e.ChangedButton == System.Windows.Input.MouseButton.Right ? 2 : 1), 1); }
    private async void RemoteImage_MouseUp(object s, System.Windows.Input.MouseButtonEventArgs e) { await SendMouse((byte)(e.ChangedButton == System.Windows.Input.MouseButton.Right ? 2 : 1), 0); }
    private async void RemoteImage_KeyDown(object s, WpfKeyEventArgs e) { if (approved) await SendKey(System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key), true); if (e.Key == WpfKey.Escape) Stop(); }
    private async void RemoteImage_KeyUp(object s, WpfKeyEventArgs e) { if (approved) await SendKey(System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key), false); }
    private async Task SendKey(int vk, bool down) { try { await sendLock.WaitAsync(); try { using var bw = new BinaryWriter(stream!, Encoding.UTF8, true); bw.Write("K"); bw.Write(vk); bw.Write(down); bw.Flush(); } finally { sendLock.Release(); } } catch { } }

    private void Stop()
    {
        closing = true; approved = false; timer?.Stop(); timer = null;
        try { stream?.Dispose(); peer?.Close(); listener?.Stop(); } catch { }
        stream = null; peer = null; listener = null;
        if (StatusText is not null && !IsClosing) StatusText.Text = "Отключено";
    }
    private bool IsClosing => !IsVisible;
    private static int Port(string s) => int.TryParse(s, out int p) && p is > 0 and < 65536 ? p : throw new ArgumentException("Порт должен быть от 1 до 65535");

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public U u; }
    [StructLayout(LayoutKind.Explicit)] private struct U { [FieldOffset(0)] public M m; [FieldOffset(0)] public K k; }
    [StructLayout(LayoutKind.Sequential)] private struct M { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct K { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    private static void MouseInput(byte b, int a) { uint f = b == 2 ? (a == 1 ? 8u : 16u) : (a == 1 ? 2u : 4u); SendInput(1, new[] { new INPUT { type = 0, u = new U { m = new M { flags = f } } } }, Marshal.SizeOf<INPUT>()); }
    private static void KeyInput(int vk, bool down) => SendInput(1, new[] { new INPUT { type = 1, u = new U { k = new K { vk = (ushort)vk, flags = down ? 0u : 2u } } } }, Marshal.SizeOf<INPUT>());
}
