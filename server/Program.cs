using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Server
{
    class Server
    {
        private readonly int _port;
        private readonly IPAddress _ip;
        private readonly int _maxClients;
        private readonly int _sessionDuration;
        private readonly int _inactivityTimeout;

        private TcpListener _listener;
        private readonly List<ClientInfo> _clients = new List<ClientInfo>();
        private readonly object _lock = new object();
        private CancellationTokenSource _serverCts = new CancellationTokenSource();

        public Server(IPAddress ip, int port, int maxClients, int sessionDuration, int inactivityTimeout)
        {
            _ip = ip;
            _port = port;
            _maxClients = maxClients;
            _sessionDuration = sessionDuration;
            _inactivityTimeout = inactivityTimeout;
        }

        public async Task StartAsync()
        {
            _listener = new TcpListener(_ip, _port);
            _listener.Start();
            Console.WriteLine($"Сервер запущен на {_ip}:{_port}. Максимум клиентов: {_maxClients}, длительность сессии: {_sessionDuration}с, таймаут неактивности: {_inactivityTimeout}с");

            // Запуск обработки команд
            _ = Task.Run(AdminLoopAsync);

            try
            {
                while (!_serverCts.IsCancellationRequested)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    _ = HandleClientAsync(tcpClient, _serverCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Сервер остановлен.");
            }
            finally
            {
                _listener.Stop();
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            var clientEndpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint;
            Console.WriteLine($"Входящее подключение от {clientEndpoint}");

            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
            {
                // Проверка лимита клиентов
                lock (_lock)
                {
                    if (_clients.Count >= _maxClients)
                    {
                        writer.WriteLine("REJECTED No free slots");
                        Console.WriteLine($"Отклонён клиент {clientEndpoint}: достигнут лимит подключений");
                        return;
                    }
                }

                // Ожидаем CONNECT
                string? firstLine = await reader.ReadLineAsync();
                if (firstLine == null || !firstLine.StartsWith("CONNECT"))
                {
                    writer.WriteLine("ERROR Expected CONNECT command");
                    return;
                }

                // Генерация уникального кода
                string code = Guid.NewGuid().ToString();

                // Создание записи о клиенте
                var clientInfo = new ClientInfo
                {
                    TcpClient = tcpClient,
                    Stream = stream,
                    Reader = reader,
                    Writer = writer,
                    Code = code,
                    RemoteEndPoint = clientEndpoint,
                    ConnectedAt = DateTime.Now,
                    LastActivity = DateTime.Now,
                    Cts = new CancellationTokenSource()
                };

                lock (_lock)
                {
                    _clients.Add(clientInfo);
                }

                // Отправка подтверждения с параметрами
                writer.WriteLine($"CONNECTED {code} {_sessionDuration} {_inactivityTimeout}");
                Console.WriteLine($"Клиент {clientEndpoint} подключён с кодом {code}");

                // Запуск таймеров отключения
                StartDisconnectTimers(clientInfo);

                try
                {
                    // Цикл обработки команд от клиента
                    while (!token.IsCancellationRequested && !clientInfo.Cts.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break;

                        // Обновление времени последней активности
                        clientInfo.LastActivity = DateTime.Now;

                        // Сброс таймеров
                        RestartDisconnectTimers(clientInfo);

                        // Обработка команд
                        if (line.StartsWith("RENEW"))
                        {
                            string[] parts = line.Split(' ');
                            if (parts.Length == 2 && parts[1] == clientInfo.Code)
                            {
                                string newCode = Guid.NewGuid().ToString();
                                clientInfo.Code = newCode;
                                writer.WriteLine($"RENEWED {newCode}");
                                Console.WriteLine($"Клиент {clientInfo.Code} обновил код на {newCode}");
                            }
                            else
                                writer.WriteLine("ERROR Invalid code");
                        }
                        else if (line.StartsWith("STATUS"))
                        {
                            string[] parts = line.Split(' ');
                            if (parts.Length == 2 && parts[1] == clientInfo.Code)
                                writer.WriteLine("ACTIVE");
                            else
                                writer.WriteLine("ERROR Invalid code");
                        }
                        else
                            writer.WriteLine("ERROR Unknown command");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при обработке клиента {clientInfo.Code}: {ex.Message}");
                }
                finally
                {
                    // Удаление клиента из списка при отключении
                    lock (_lock)
                    {
                        _clients.Remove(clientInfo);
                    }
                    clientInfo.Cts.Cancel();
                    Console.WriteLine($"Клиент {clientInfo.Code} отключён");
                }
            }
        }

        private void StartDisconnectTimers(ClientInfo client)
        {
            var cts = client.Cts;
            var token = cts.Token;

            // Таймер
            Task.Delay(_sessionDuration * 1000, token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !token.IsCancellationRequested)
                {
                    DisconnectClient(client, "Истекло время сессии");
                }
            }, token);

            Task.Delay(_inactivityTimeout * 1000, token).ContinueWith(t =>
            {
                if (!t.IsCanceled && !token.IsCancellationRequested)
                    DisconnectClient(client, "Таймаут");
            }, token);
        }

        private void RestartDisconnectTimers(ClientInfo client)
        {
            client.Cts.Cancel();
            client.Cts = new CancellationTokenSource();
            StartDisconnectTimers(client);
        }

        private void DisconnectClient(ClientInfo client, string reason)
        {
            try
            {
                client.Writer.WriteLine($"DISCONNECT {reason}");
            }
            catch { }
            finally
            {
                client.TcpClient.Close();
            }
        }

        private async Task AdminLoopAsync()
        {
            while (!_serverCts.IsCancellationRequested)
            {
                string? cmd = await Task.Run(() => Console.ReadLine());
                if (cmd == null)
                	break;

                if (cmd.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    lock (_lock)
                    {
                        if (_clients.Count == 0)
                            Console.WriteLine("Нет подключённых клиентов.");
                        else
                        {
                            Console.WriteLine($"Подключённые клиенты ({_clients.Count}):");
                            foreach (var c in _clients)
                            {
                                TimeSpan duration = DateTime.Now - c.ConnectedAt;
                                Console.WriteLine($"Код: {c.Code}, IP: {c.RemoteEndPoint}, Подключён: {c.ConnectedAt}, Длительность: {duration:hh\\:mm\\:ss}, Последняя активность: {c.LastActivity}");
                            }
                        }
                    }
                }
                else if (cmd.StartsWith("kick ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                    {
                        string code = parts[1];
                        ClientInfo? client = null;
                        lock (_lock)
                        {
                            client = _clients.Find(c => c.Code == code);
                        }
                        if (client != null)
                        {
                            DisconnectClient(client, "Отключён администратором");
                            Console.WriteLine($"Клиент {code} отключён.");
                        }
                        else
                            Console.WriteLine($"Клиент с кодом {code} не найден.");
                    }
                    else
                        Console.WriteLine("Использование: kick <код>");
                }
                else if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    _serverCts.Cancel();
                    List<ClientInfo> clientsCopy;
                    lock (_lock)
                    {
                        clientsCopy = new List<ClientInfo>(_clients);
                    }
                    foreach (var c in clientsCopy)
                    {
                        DisconnectClient(c, "Сервер завершает работу");
                    }
                    break;
                }
                else
                    Console.WriteLine("Доступные команды: list, kick <код>, exit");
            }
        }

        private class ClientInfo
        {
            public TcpClient TcpClient { get; set; }
            public NetworkStream Stream { get; set; }
            public StreamReader Reader { get; set; }
            public StreamWriter Writer { get; set; }
            public string Code { get; set; }
            public IPEndPoint RemoteEndPoint { get; set; }
            public DateTime ConnectedAt { get; set; }
            public DateTime LastActivity { get; set; }
            public CancellationTokenSource Cts { get; set; }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Настройка сервера:");
            Console.Write("IP-адрес (по умолчанию 127.0.0.1): ");
            string ipInput = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(ipInput)) ipInput = "127.0.0.1";
            IPAddress ip = IPAddress.Parse(ipInput);

            Console.Write("Порт (по умолчанию 8888): ");
            string portInput = Console.ReadLine();
            int port = string.IsNullOrWhiteSpace(portInput) ? 8888 : int.Parse(portInput);

            Console.Write("Максимальное количество клиентов (по умолчанию 5): ");
            string maxInput = Console.ReadLine();
            int maxClients = string.IsNullOrWhiteSpace(maxInput) ? 5 : int.Parse(maxInput);

            Console.Write("Длительность сессии (по умолчанию 60): ");
            string sessionInput = Console.ReadLine();
            int sessionDuration = string.IsNullOrWhiteSpace(sessionInput) ? 60 : int.Parse(sessionInput);

            Console.Write("Таймаут (по умолчанию 30): ");
            string inactivityInput = Console.ReadLine();
            int inactivityTimeout = string.IsNullOrWhiteSpace(inactivityInput) ? 30 : int.Parse(inactivityInput);

            var server = new Server(ip, port, maxClients, sessionDuration, inactivityTimeout);
            await server.StartAsync();
        }
    }
}