using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Client
{
    class Client
    {
        private readonly string _serverIp;
        private readonly int _serverPort;
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private StreamReader _reader;
        private StreamWriter _writer;
        private string _code;
        private int _sessionDuration;
        private int _inactivityTimeout;
        private DateTime _connectedAt;
        private IPEndPoint _localEndPoint;
        private CancellationTokenSource _cts = new CancellationTokenSource();

        public Client(string serverIp, int serverPort)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
        }

        public async Task StartAsync()
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_serverIp, _serverPort);
                _stream = _tcpClient.GetStream();
                _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
                _writer = new StreamWriter(_stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                _localEndPoint = _tcpClient.Client.LocalEndPoint as IPEndPoint;

                // Отправка CONNECT
                await _writer.WriteLineAsync("CONNECT");

                // Чтение ответа
                string response = await _reader.ReadLineAsync();
                if (response == null)
                {
                    Console.WriteLine("Сервер закрыл соединение.");
                    return;
                }

                if (response.StartsWith("REJECTED"))
                {
                    Console.WriteLine($"Сервер отклонил подключение: {response}");
                    return;
                }

                if (response.StartsWith("CONNECTED"))
                {
                    string[] parts = response.Split(' ');
                    if (parts.Length >= 4)
                    {
                        _code = parts[1];
                        _sessionDuration = int.Parse(parts[2]);
                        _inactivityTimeout = int.Parse(parts[3]);
                        _connectedAt = DateTime.Now;

                        Console.WriteLine($"Подключено к серверу. Код: {_code}, Длительность сессии: {_sessionDuration}с, Таймаут неактивности: {_inactivityTimeout}с");
                        Console.WriteLine($"Локальный адрес: {_localEndPoint}");

                        // Интервалы для периодических задач
                        int statusIntervalMs = Math.Max(1000, _inactivityTimeout * 1000 / 2);
                        int renewIntervalMs = Math.Max(1000, _sessionDuration * 1000 / 2);

                        // Запуск периодической отправки STATUS
                        var statusTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(statusIntervalMs));
                        _ = Task.Run(async () =>
                        {
                            while (await statusTimer.WaitForNextTickAsync(_cts.Token))
                            {
                                await SendStatusAsync();
                            }
                        });

                        // Запуск периодической отправки RENEW
                        var renewTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(renewIntervalMs));
                        _ = Task.Run(async () =>
                        {
                            while (await renewTimer.WaitForNextTickAsync(_cts.Token))
                            {
                                await RenewCodeAsync();
                            }
                        });

                        // Запуск чтения сообщений
                        await ReadServerMessagesAsync();
                    }
                    else
                        Console.WriteLine("Неверный ответ сервера.");
                }
                else
                    Console.WriteLine($"Неожиданный ответ: {response}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
            finally
            {
                Stop();
            }
        }

        private async Task ReadServerMessagesAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    string? line = await _reader.ReadLineAsync();
                    if (line == null) break;

                    if (line.StartsWith("DISCONNECT"))
                    {
                        Console.WriteLine($"\nСервер отключил: {line}");
                        _cts.Cancel();
                        break;
                    }
                    else if (line.StartsWith("RENEWED"))
                    {
                        string[] parts = line.Split(' ');
                        if (parts.Length == 2)
                        {
                            _code = parts[1];
                            Console.WriteLine($"\nКод обновлён: {_code}");
                        }
                    }
                    else if (line.StartsWith("ACTIVE"))
                    {
                        // Подтверждение статуса
                    }
                    else if (line.StartsWith("ERROR"))
                    {
                        Console.WriteLine($"\nОшибка сервера: {line}");
                        _cts.Cancel();
                        break;
                    }
                    else
                    {
                        Console.WriteLine($"\nСообщение сервера: {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nОшибка чтения: {ex.Message}");
            }
            finally
            {
                _cts.Cancel();
            }
        }

        private async Task SendStatusAsync()
        {
            if (_cts.IsCancellationRequested) return;
            try
            {
                await _writer.WriteLineAsync($"STATUS {_code}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nОшибка отправки статуса: {ex.Message}");
                _cts.Cancel();
            }
        }

        private async Task RenewCodeAsync()
        {
            if (_cts.IsCancellationRequested) return;
            try
            {
                await _writer.WriteLineAsync($"RENEW {_code}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nОшибка обновления кода: {ex.Message}");
                _cts.Cancel();
            }
        }

        private void Stop()
        {
            _writer?.Close();
            _reader?.Close();
            _stream?.Close();
            _tcpClient?.Close();
            Console.WriteLine("\nКлиент остановлен.");
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Настройка клиента:");
            Console.Write("IP-адрес сервера (по умолчанию 127.0.0.1): ");
            string ipInput = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(ipInput)) ipInput = "127.0.0.1";

            Console.Write("Порт сервера (по умолчанию 8888): ");
            string portInput = Console.ReadLine();
            int port = string.IsNullOrWhiteSpace(portInput) ? 8888 : int.Parse(portInput);

            var client = new Client(ipInput, port);
            await client.StartAsync();

            Console.WriteLine("\nНажмите любую клавишу для выхода...");
            Console.ReadKey();
        }
    }
}