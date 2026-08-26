using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Modbus_Debug_Tool
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;

        private readonly object _clientsLock = new();
        private readonly List<ClientInfo> _clients = new();

        // awaiting response state for QuickRead quick actions (stores requested register address). -1 = none
        private volatile int _awaitingP1Address = -1;
        private DateTime _p1RequestTime = DateTime.MinValue;
        private static readonly TimeSpan P1ResponseWindow = TimeSpan.FromSeconds(5);

        private class ClientInfo
        {
            public TcpClient Client { get; }
            public string Endpoint { get; }
            public HashSet<byte> SlaveIdsSeen { get; } = new();
            public DateTime LastSeenUtc { get; set; }

            public ClientInfo(TcpClient c)
            {
                Client = c;
                Endpoint = c.Client.RemoteEndPoint?.ToString() ?? "unknown";
                LastSeenUtc = DateTime.UtcNow;
            }

            public override string ToString()
            {
                if (SlaveIdsSeen.Count == 0) return Endpoint + " (unknown slave)";
                return Endpoint + " (slaves: " + string.Join(",", SlaveIdsSeen) + ")";
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            // Initialize UI defaults
            PortTextBox.Text = "1286";
            FunctionComboBox.ItemsSource = new[] { "3 - Read Holding Registers", "6 - Write Single Register" };
            FunctionComboBox.SelectedIndex = 0;
            SlaveIdTextBox.Text = "1";
            AddressTextBox.Text = "1016";
            QuantityTextBox.Text = "1";

            ClientsListBox.SelectionChanged += ClientsListBox_SelectionChanged;

            // Quick slave id textbox default and sync
            QuickSlaveIdTextBox.Text = SlaveIdTextBox.Text;
        }

        private void ClientsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ClientsListBox.SelectedItem is ClientInfo ci)
            {
                // if we've seen exactly one slave id for this client, prefill slave id textbox
                if (ci.SlaveIdsSeen.Count == 1)
                {
                    SlaveIdTextBox.Text = ci.SlaveIdsSeen.First().ToString();
                }
            }
        }

        private void Log(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        private async void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out var port))
            {
                MessageBox.Show("Invalid port");
                return;
            }

            _cts = new CancellationTokenSource();
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Log($"Server listening on port {port}");
                StartServerButton.IsEnabled = false;
                StopServerButton.IsEnabled = true;

                // start background cleanup task to remove zombie clients periodically
                _ = Task.Run(() => CleanupLoopAsync(_cts.Token));

                await AcceptLoopAsync(_listener, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Server error: {ex.Message}");
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    var ci = new ClientInfo(client);
                    lock (_clientsLock)
                    {
                        _clients.Add(ci);
                    }
                    Dispatcher.Invoke(() => ClientsListBox.Items.Add(ci));
                    _ = Task.Run(() => HandleClientAsync(ci, token));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"AcceptLoop error: {ex.Message}");
            }
        }

        private async Task HandleClientAsync(ClientInfo ci, CancellationToken token)
        {
            var client = ci.Client;
            var ep = ci.Endpoint;
            Log($"Client connected: {ep}");
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        try
                        {
                            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                            if (bytesRead == 0) break; // closed

                            // update last seen timestamp for heartbeat
                            ci.LastSeenUtc = DateTime.UtcNow;

                            var data = new byte[bytesRead];
                            Array.Copy(buffer, data, bytesRead);
                            Log($"Received raw from {ep}: {ToHex(data)}");

                            var frames = ExtractFrames(data);
                            foreach (var frame in frames)
                            {
                                ParseAndLogFrame(frame, isIncoming: true, ci);

                                // If user requested a QuickRead recently, attempt to process this incoming frame as response
                                try
                                {
                                    if (_awaitingP1Address >= 0 && DateTime.UtcNow - _p1RequestTime <= P1ResponseWindow)
                                    {
                                        if (frame.Length >= 3 && frame[1] == 3)
                                        {
                                            var addr = _awaitingP1Address;
                                            ProcessP1Response(frame, addr);
                                            _awaitingP1Address = -1;
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Log($"Client {ep} handling error: {ex.Message}");
                            break;
                        }
                    }
                }
            }
            finally
            {
                Log($"Client disconnected: {ep}");
                lock (_clientsLock)
                {
                    _clients.Remove(ci);
                }
                Dispatcher.Invoke(() => ClientsListBox.Items.Remove(ci));
            }
        }

        private List<byte[]> ExtractFrames(byte[] data)
        {
            var result = new List<byte[]>();
            int i = 0;
            while (i + 4 <= data.Length) // at least addr(1)+func(1)+crc(2)
            {
                bool found = false;
                for (int len = 4; i + len <= data.Length; len++)
                {
                    var slice = new byte[len];
                    Array.Copy(data, i, slice, 0, len);
                    if (VerifyCrc(slice))
                    {
                        result.Add(slice);
                        i += len;
                        found = true;
                        break;
                    }
                }
                if (!found) i++;
            }
            return result;
        }

        private void ParseAndLogFrame(byte[] frame, bool isIncoming, ClientInfo? ci = null)
        {
            if (frame.Length < 4)
            {
                Log($"Frame too short: {ToHex(frame)}");
                return;
            }

            if (!VerifyCrc(frame))
            {
                Log($"CRC invalid: {ToHex(frame)}");
                return;
            }

            var slave = frame[0];
            var function = frame[1];
            var pdu = new byte[frame.Length - 4];
            if (pdu.Length > 0) Array.Copy(frame, 2, pdu, 0, pdu.Length);

            var direction = isIncoming ? "<-" : "->";
            Log($"{direction} Slave={slave}, Function={function}, Frame={ToHex(frame)}");

            // record seen slave id for client
            if (ci != null)
            {
                if (!ci.SlaveIdsSeen.Contains(slave))
                {
                    ci.SlaveIdsSeen.Add(slave);
                    Dispatcher.Invoke(() =>
                    {
                        // refresh item display
                        var idx = ClientsListBox.Items.IndexOf(ci);
                        if (idx >= 0)
                        {
                            ClientsListBox.Items[idx] = null; // force refresh
                            ClientsListBox.Items[idx] = ci;
                        }
                    });
                }
            }

            // Do not parse responses in the log. Values will be interpreted later.
            if (isIncoming)
            {
                return;
            }

            try
            {
                switch (function)
                {
                    case 3: // Read Holding Registers
                        // outgoing request parsing: log address/quantity
                        if (pdu.Length >= 4)
                        {
                            var addr = (ushort)((pdu[0] << 8) | pdu[1]);
                            var qty = (ushort)((pdu[2] << 8) | pdu[3]);
                            Log($"Function 3 request: Address={addr}, Quantity={qty}");
                        }
                        break;
                    case 6: // Write Single Register
                        // outgoing request parsing: log address/value
                        if (pdu.Length >= 4)
                        {
                            var addr = (ushort)((pdu[0] << 8) | pdu[1]);
                            var value = (ushort)((pdu[2] << 8) | pdu[3]);
                            Log($"Function 6 request: Address={addr}, Value={value}");
                        }
                        break;
                    default:
                        Log($"Unhandled function {function}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"Error parsing PDU: {ex.Message}");
            }
        }

        // Periodic cleanup loop that uses application-layer heartbeat to detect dead clients
        private async Task CleanupLoopAsync(CancellationToken token)
        {
            try
            {
                var interval = TimeSpan.FromSeconds(60);
                var inactivityThreshold = TimeSpan.FromSeconds(180);
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);

                    List<ClientInfo> toRemove = new List<ClientInfo>();
                    List<Task> probeTasks = new List<Task>();

                    lock (_clientsLock)
                    {
                        foreach (var ci in _clients.ToArray())
                        {
                            // remove immediately if socket reports disconnected
                            try
                            {
                                if (ci.Client == null || ci.Client.Client == null || !ci.Client.Client.Connected)
                                {
                                    toRemove.Add(ci);
                                    continue;
                                }
                            }
                            catch { toRemove.Add(ci); continue; }

                            // if client has been inactive for longer than threshold, probe with heartbeat
                            if (DateTime.UtcNow - ci.LastSeenUtc > inactivityThreshold)
                            {
                                // probe asynchronously; capture ci in closure
                                var task = Task.Run(async () =>
                                {
                                    var ok = await SendHeartbeatAsync(ci, 10000).ConfigureAwait(false);
                                    if (!ok)
                                    {
                                        lock (_clientsLock) { toRemove.Add(ci); }
                                    }
                                });
                                probeTasks.Add(task);
                            }
                        }

                        // remove those marked
                        foreach (var ci in toRemove)
                        {
                            _clients.Remove(ci);
                        }
                    }

                    if (probeTasks.Count > 0)
                    {
                        // wait for probes to finish (short timeout overall)
                        try { await Task.WhenAll(probeTasks).ConfigureAwait(false); } catch { }
                    }

                    // finalize removal of probed items (they may have been added while probes ran)
                    List<ClientInfo> finalRemove = new List<ClientInfo>();
                    lock (_clientsLock)
                    {
                        foreach (var ci in _clients.ToArray())
                        {
                            if (DateTime.UtcNow - ci.LastSeenUtc > inactivityThreshold)
                            {
                                // if still inactive, remove
                                finalRemove.Add(ci);
                                _clients.Remove(ci);
                            }
                        }
                    }

                    foreach (var ci in toRemove.Concat(finalRemove).Distinct())
                    {
                        try
                        {
                            try { ci.Client.Client.Shutdown(SocketShutdown.Both); } catch { }
                            ci.Client.Close();
                        }
                        catch { }

                        Dispatcher.Invoke(() => ClientsListBox.Items.Remove(ci));
                        Log($"Removed zombie client: {ci.Endpoint}");
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"CleanupLoop error: {ex.Message}");
            }
        }

        // Send an application-layer heartbeat (small one-byte probe). Returns true if write succeeded.
        private async Task<bool> SendHeartbeatAsync(ClientInfo ci, int timeoutMs)
        {
            try
            {
                var sock = ci.Client.Client;
                if (sock == null || !sock.Connected) return false;

                if (sock.Poll(0, SelectMode.SelectError)) return false;

                var stream = ci.Client.GetStream();
                var ping = new byte[] { 0x00 };

                using var cts = new CancellationTokenSource(timeoutMs);
                await stream.WriteAsync(ping, 0, ping.Length, cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(cts.Token).ConfigureAwait(false);

                // successful write; do not wait for response here (HandleClientAsync will update LastSeen when data arrives)
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Determine whether a TcpClient socket is still alive (best-effort)
        private static bool IsClientAlive(TcpClient client)
        {
            try
            {
                if (client == null) return false;
                var sock = client.Client;
                if (sock == null) return false;

                if (!sock.Connected) return false;

                bool disconnect = sock.Poll(0, SelectMode.SelectRead) && sock.Available == 0;
                if (disconnect) return false;

                if (sock.Poll(0, SelectMode.SelectError)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void StopServerButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch { }
            _listener = null;
            _cts = null;
            StartServerButton.IsEnabled = true;
            StopServerButton.IsEnabled = false;
            Log("Server stopped");

            lock (_clientsLock)
            {
                foreach (var ci in _clients.ToArray())
                {
                    try { ci.Client.Close(); } catch { }
                }
                _clients.Clear();
            }
            Dispatcher.Invoke(() => ClientsListBox.Items.Clear());
        }

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ");
        }

        private static ushort Crc16(byte[] data, int length)
        {
            ushort crc = 0xFFFF;
            for (int pos = 0; pos < length; pos++)
            {
                crc ^= data[pos];
                for (int i = 0; i < 8; i++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }

        private static bool VerifyCrc(byte[] frame)
        {
            if (frame.Length < 3) return false;
            var len = frame.Length;
            var crc = Crc16(frame, len - 2);
            var lo = (byte)(crc & 0xFF);
            var hi = (byte)((crc >> 8) & 0xFF);
            return frame[len - 2] == lo && frame[len - 1] == hi;
        }

        private static byte[] AppendCrc(byte[] payload)
        {
            var crc = Crc16(payload, payload.Length);
            var lo = (byte)(crc & 0xFF);
            var hi = (byte)((crc >> 8) & 0xFF);
            var result = new byte[payload.Length + 2];
            Array.Copy(payload, result, payload.Length);
            result[result.Length - 2] = lo;
            result[result.Length - 1] = hi;
            return result;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortTextBox.Text, out _))
            {
                MessageBox.Show("Invalid server port");
                return;
            }

            if (!int.TryParse(SlaveIdTextBox.Text, out var slaveId) || slaveId < 0 || slaveId > 247)
            {
                MessageBox.Show("Invalid slave id (0-247)");
                return;
            }
            if (!int.TryParse(AddressTextBox.Text, out var address) || address < 0 || address > 0xFFFF)
            {
                MessageBox.Show("Invalid address");
                return;
            }
            if (!int.TryParse(QuantityTextBox.Text, out var quantity) || quantity < 1 || quantity > 125)
            {
                MessageBox.Show("Invalid quantity (1-125)");
                return;
            }

            var function = FunctionComboBox.SelectedIndex == 0 ? 3 : 6;

            // prepare frame
            byte[] frame;
            if (function == 3)
            {
                var payload = new byte[6];
                payload[0] = (byte)slaveId;
                payload[1] = 3;
                payload[2] = (byte)((address >> 8) & 0xFF);
                payload[3] = (byte)(address & 0xFF);
                payload[4] = (byte)((quantity >> 8) & 0xFF);
                payload[5] = (byte)(quantity & 0xFF);
                frame = AppendCrc(payload);
            }
            else
            {
                // function 6: single register value expected
                var text = DataTextBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (text == null)
                {
                    MessageBox.Show("Provide single register value for function 6 in Data field.");
                    return;
                }
                if (!ushort.TryParse(text, out var value))
                {
                    MessageBox.Show($"Invalid register value: {text}");
                    return;
                }

                var payload = new byte[6];
                payload[0] = (byte)slaveId;
                payload[1] = 6;
                payload[2] = (byte)((address >> 8) & 0xFF);
                payload[3] = (byte)(address & 0xFF);
                payload[4] = (byte)((value >> 8) & 0xFF);
                payload[5] = (byte)(value & 0xFF);
                frame = AppendCrc(payload);
            }

            // snapshot clients
            List<ClientInfo> clientsSnapshot;
            lock (_clientsLock)
            {
                clientsSnapshot = _clients.ToList();
            }

            if (clientsSnapshot.Count == 0)
            {
                MessageBox.Show("No connected clients to send to.");
                return;
            }

            Log($"Broadcasting -> {ToHex(frame)} to {clientsSnapshot.Count} clients");

            // capture request context for response handling
            var requestFunction = function;
            var requestAddress = address;
            var requestQuantity = quantity;

            var tasks = clientsSnapshot.Select(async ci =>
            {
                try
                {
                    var stream = ci.Client.GetStream();
                    await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    Log($"Sent to {ci.Endpoint} -> {ToHex(frame)}");

                    // attempt to read short response
                    var resp = await ReadResponseAsync(ci, stream, 300).ConfigureAwait(false);
                    if (resp != null && resp.Length > 0)
                    {
                        // try to extract one or more full frames from the raw response
                        var frames = ExtractFrames(resp);
                        if (frames.Count == 0)
                        {
                            // fallback: treat the entire buffer as a single frame
                            Dispatcher.Invoke(() => ParseAndLogFrame(resp, isIncoming: true, ci));

                            // If this request was the QuickRead1219 (function 3 address 1219), extract pdu and show in UI
                            if (requestFunction == 3 && requestAddress == 1219)
                            {
                                Dispatcher.Invoke(() => ProcessP1Response(resp));
                            }
                        }
                        else
                        {
                            foreach (var f in frames)
                            {
                                Dispatcher.Invoke(() => ParseAndLogFrame(f, isIncoming: true, ci));

                                if (requestFunction == 3 && requestAddress == 1219)
                                {
                                    Dispatcher.Invoke(() => ProcessP1Response(f));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Send error to {ci.Endpoint}: {ex.Message}");
                    // if send fails, remove client asynchronously
                    lock (_clientsLock)
                    {
                        try { _clients.Remove(ci); } catch { }
                    }
                    Dispatcher.Invoke(() => ClientsListBox.Items.Remove(ci));
                }
            }).ToArray();

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch { }
        }

        // Read small response from client with a short timeout; returns collected bytes or empty array
        private async Task<byte[]> ReadResponseAsync(ClientInfo ci, NetworkStream stream, int timeoutMs)
        {
            try
            {
                var buffer = new byte[512];
                using var cts = new CancellationTokenSource(timeoutMs);
                var ms = new MemoryStream();

                // wait briefly to allow data to arrive
                await Task.Delay(50, cts.Token).ConfigureAwait(false);

                while (!cts.IsCancellationRequested && ci.Client.Connected)
                {
                    if (stream.DataAvailable)
                    {
                        var cnt = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
                        if (cnt <= 0) break;
                        ms.Write(buffer, 0, cnt);
                        // small pause to gather more
                        await Task.Delay(20, cts.Token).ConfigureAwait(false);
                        continue;
                    }
                    // no data available; break
                    break;
                }

                return ms.ToArray();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private void QuickSlaveIdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var txt = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(txt, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }
        }

        private void QuickRead1016Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1016 qty 1 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1016";
            QuantityTextBox.Text = "1";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1038Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1038 qty 1 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1038";
            QuantityTextBox.Text = "1";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1200Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1200 qty 1 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1200";
            QuantityTextBox.Text = "1";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1199Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 6 setting at address 1199 value 1 and send
            FunctionComboBox.SelectedIndex = 1; // function 6
            AddressTextBox.Text = "1199";
            DataTextBox.Text = "1";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1055Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1055 qty 8 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1055";
            QuantityTextBox.Text = "8";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1127Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1127 qty 8 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1127";
            QuantityTextBox.Text = "8";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1071Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1071 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1071";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1073Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1073 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1073";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1143Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1143 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1143";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1145Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1145 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1145";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1219Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1219 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1219";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark that we are awaiting this register's response
            _awaitingP1Address = 1219;
            _p1RequestTime = DateTime.UtcNow;

            // Trigger send; requires a client selected
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1225Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1225 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1225";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1225;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1231Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1231 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1231";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            _awaitingP1Address = 1231;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1233Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1233 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1233";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            _awaitingP1Address = 1233;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1235Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1235 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1235";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            _awaitingP1Address = 1235;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1239Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1239 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1239";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            _awaitingP1Address = 1239;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1245Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1245 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1245";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            _awaitingP1Address = 1245;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1263Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1263 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1263";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1263;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1269Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1269 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1269";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1269;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1275Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1275 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1275";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1275;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1277Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1277 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1277";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1277;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1279Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1279 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1279";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1279;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1283Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1283 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1283";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1283;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        private void QuickRead1289Button_Click(object sender, RoutedEventArgs e)
        {
            // Prepare a function 3 read at address 1289 qty 2 and send
            FunctionComboBox.SelectedIndex = 0; // function 3
            AddressTextBox.Text = "1289";
            QuantityTextBox.Text = "2";

            // ensure slave id comes from quick textbox if valid
            var q = QuickSlaveIdTextBox.Text.Trim();
            if (byte.TryParse(q, out var id) && id <= 247)
            {
                SlaveIdTextBox.Text = id.ToString();
            }

            // mark awaiting and send
            _awaitingP1Address = 1289;
            _p1RequestTime = DateTime.UtcNow;
            SendButton_Click(SendButton, new RoutedEventArgs());
        }

        // Process response frame for QuickRead1219 and display PDU data in P1ThicknessTextBox
        private void ProcessP1Response(byte[] frame, int? expectedAddress = null)
        {

            try
            {
                if (frame == null || frame.Length == 0) return;

                byte[] dataBytes = Array.Empty<byte>();

                // Case 1: RTU frame with CRC
                if (frame.Length >= 5 && VerifyCrc(frame))
                {
                    // RTU: [slave][func][bytecount][data...][crc_lo][crc_hi]
                    var function = frame[1];
                    if (function != 3)
                    {
                        Log($"ProcessP1Response: unexpected RTU function {function}");
                        return;
                    }

                    var byteCount = frame[2];
                    if (byteCount <= 0 || frame.Length < 3 + byteCount + 2) // +2 for CRC
                    {
                        Log("ProcessP1Response: RTU frame byteCount mismatch");
                        return;
                    }

                    dataBytes = new byte[byteCount];
                    Array.Copy(frame, 3, dataBytes, 0, byteCount);
                }
                else if (frame.Length >= 9)
                {
                    // Case 2: Modbus TCP (MBAP) frame without CRC
                    // MBAP header is 7 bytes: Trans(2), Proto(2), Length(2), UnitId(1) -> PDU starts at index 7
                    var funcIndex = 7;
                    var function = frame[funcIndex];
                    if (function != 3)
                    {
                        Log($"ProcessP1Response: unexpected TCP function {function}");
                        return;
                    }

                    // byte count should be at index 8
                    var byteCount = frame[8];
                    if (byteCount <= 0 || frame.Length < funcIndex + 2 + byteCount)
                    {
                        Log("ProcessP1Response: MBAP frame byteCount mismatch");
                        return;
                    }

                    dataBytes = new byte[byteCount];
                    Array.Copy(frame, funcIndex + 2, dataBytes, 0, byteCount);
                }
                else if (frame.Length >= 3 && frame[1] == 3)
                {
                    // Case 3: Plain RTU-like frame without CRC (e.g., [slave][func][bytecount][data...])
                    var byteCount = frame[2];
                    if (byteCount <= 0 || frame.Length < 3 + byteCount)
                    {
                        Log("ProcessP1Response: plain frame byteCount mismatch");
                        return;
                    }

                    dataBytes = new byte[byteCount];
                    Array.Copy(frame, 3, dataBytes, 0, byteCount);
                }

                if (dataBytes == null || dataBytes.Length == 0)
                {
                    Log("ProcessP1Response: no data bytes extracted");
                    return;
                }

                var hex = ToHex(dataBytes);

                // choose target textbox based on expectedAddress
                TextBox? target = null;
                if (expectedAddress.HasValue)
                {
                    switch (expectedAddress.Value)
                    {
                        case 1219: target = P1ThicknessTextBox; break;
                        case 1225: target = P1JACTextBox; break;
                        case 1231: target = P1JDCTextBox; break;
                        case 1233: target = P1EonTextBox; break;
                        case 1235: target = P1EoffCouponTextBox; break;
                        case 1239: target = P1TemperatureTextBox; break;
                        case 1245: target = P1LossTextBox; break;
                        // P2 mappings
                        case 1263: target = P2ThicknessTextBox; break;
                        case 1269: target = P2JACTextBox; break;
                        case 1275: target = P2JDCTextBox; break;
                        case 1277: target = P2EonTextBox; break;
                        case 1279: target = P2EoffCouponTextBox; break;
                        case 1283: target = P2TemperatureTextBox; break;
                        case 1289: target = P2LossTextBox; break;
                    }
                }

                if (dataBytes.Length == 4)
                {
                    var tmp = (byte[])dataBytes.Clone();
                    Array.Reverse(tmp);
                    var f = BitConverter.ToSingle(tmp, 0);
                    if (target != null)
                        Dispatcher.Invoke(() => target.Text = $"{hex} / {f}");
                    else
                        Dispatcher.Invoke(() => P1ThicknessTextBox.Text = $"{hex} / {f}");
                }
                else
                {
                    if (target != null)
                        Dispatcher.Invoke(() => target.Text = hex);
                    else
                        Dispatcher.Invoke(() => P1ThicknessTextBox.Text = hex);
                }

                Log($"ProcessP1Response: extracted {dataBytes.Length} bytes -> {hex} (addr={expectedAddress})");
            }
            catch (Exception ex)
            {
                Log($"ProcessP1Response error: {ex.Message}");
            }
        }

    }
}