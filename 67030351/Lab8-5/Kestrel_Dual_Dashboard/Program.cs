using System.Globalization;
using System.IO.Ports;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DualChannelStateStore>();
builder.Services.AddHostedService<DualSerialBridgeWorker>();

var app = builder.Build();

// Endpoint สถานะระบบ
app.MapGet("/api/status", () => Results.Ok(new
{
    gateway = "Kestrel Dual-Channel IoT Gateway",
    studentId = "67030351",
    uptimeSeconds = Environment.TickCount64 / 1000,
    serverTime = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
}));

// Endpoint ส่งข้อมูล Telemetry 2 ช่องสัญญาณ
app.MapGet("/api/telemetry", (DualChannelStateStore state) => Results.Ok(state.GetSnapshot()));

// ให้บริการ static files และ default files จากโฟลเดอร์ wwwroot
app.UseFileServer();

app.Run();

// คลังข้อมูลส่วนกลาง 2 ช่องสัญญาณ (Thread-Safe)
public class DualChannelStateStore
{
    private readonly object _lock = new();
    private int _rawA = 0;
    private int _rawB = 0;
    private string _source = "Initializing";
    private DateTime _lastUpdated = DateTime.UtcNow;

    public void Update(int rawA, int rawB, string source)
    {
        lock (_lock)
        {
            _rawA = Math.Clamp(rawA, 0, 4095);
            _rawB = Math.Clamp(rawB, 0, 4095);
            _source = source;
            _lastUpdated = DateTime.UtcNow;
        }
    }

    public object GetSnapshot()
    {
        lock (_lock)
        {
            return new
            {
                channelA = new
                {
                    name = "Potentiometer (Hardware)",
                    rawValue = _rawA,
                    voltage = Math.Round((_rawA / 4095.0) * 3.3, 2),
                    percentage = Math.Round((_rawA / 4095.0) * 100.0, 1)
                },
                channelB = new
                {
                    name = "LDR / Room Sensor (Simulated)",
                    rawValue = _rawB,
                    voltage = Math.Round((_rawB / 4095.0) * 3.3, 2),
                    percentage = Math.Round((_rawB / 4095.0) * 100.0, 1)
                },
                dataSource = _source,
                lastUpdated = _lastUpdated.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)
            };
        }
    }
}

// Background Worker ดักฟัง Serial และสร้างสัญญาณจำลอง
public class DualSerialBridgeWorker : BackgroundService
{
    private readonly DualChannelStateStore _stateStore;
    private readonly ILogger<DualSerialBridgeWorker> _logger;

    public DualSerialBridgeWorker(DualChannelStateStore stateStore, ILogger<DualSerialBridgeWorker> logger)
    {
        _stateStore = stateStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                string? selectedPort = SelectPort(SerialPort.GetPortNames());

                if (selectedPort is not null)
                {
                    _logger.LogInformation("Serial port selected: {Port}", selectedPort);

                    try
                    {
                        using var serial = new SerialPort(selectedPort, 115200)
                        {
                            ReadTimeout = 2000,
                            NewLine = "\n"
                        };

                        serial.Open();
                        serial.DiscardInBuffer();
                        _logger.LogInformation("✅ เชื่อมต่อฮาร์ดแวร์พอร์ต {Port} สำเร็จ (Live Mode)", selectedPort);

                        while (!stoppingToken.IsCancellationRequested && serial.IsOpen)
                        {
                            try
                            {
                                if (serial.BytesToRead > 0)
                                {
                                    string line = serial.ReadLine().Trim();

                                    if (line.Contains(','))
                                    {
                                        var parts = line.Split(',');
                                        if (parts.Length >= 2 &&
                                            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vA) &&
                                            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int vB))
                                        {
                                            _stateStore.Update(vA, vB, $"Live Hardware ({selectedPort})");
                                        }
                                    }
                                    else if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vA) && vA is >= 0 and <= 4095)
                                    {
                                        // สำหรับฮาร์ดแวร์เดิมที่ส่ง ADC ค่าเดียว: Channel A มาจากบอร์ดจริง, Channel B จำลอง Sine Wave
                                        double t = Environment.TickCount64 / 1000.0;
                                        int simB = (int)((Math.Sin(t * 1.2) + 1.0) / 2.0 * 4095);
                                        _stateStore.Update(vA, simB, $"Live ({selectedPort}) + Sim B");
                                    }
                                }
                                else
                                {
                                    await Task.Delay(40, stoppingToken);
                                }
                            }
                            catch (TimeoutException)
                            {
                                await Task.Delay(40, stoppingToken);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                    {
                        _logger.LogWarning("⚠️ ไม่สามารถเปิดพอร์ต {Port}: {Message} (อาจมีโปรแกรมอื่นเปิดพอร์ตนี้ค้างอยู่)", selectedPort, ex.Message);
                    }
                }
                else
                {
                    _logger.LogInformation("No USB serial port found. Running Simulation Mode (2 Channels).");
                }

                // จำลองข้อมูล 2 วินาที ก่อนวนกลับไปตรวจหาพอร์ตอีกครั้ง
                for (int i = 0; i < 20 && !stoppingToken.IsCancellationRequested; i++)
                {
                    double time = Environment.TickCount64 / 1000.0;
                    int sA = (int)((Math.Sin(time * 0.8) + 1.0) / 2.0 * 4095);
                    int sB = (int)((Math.Cos(time * 1.4) + 1.0) / 2.0 * 4095);
                    _stateStore.Update(sA, sB, "Simulation Mode (2 Channels)");
                    await Task.Delay(100, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // หยุดการทำงานอย่างสง่างาม
        }
    }

    private static string? SelectPort(IEnumerable<string> ports)
    {
        const string preferredPort = "/dev/cu.usbserial-0001";
        string[] availablePorts = ports.ToArray();

        if (availablePorts.Contains(preferredPort, StringComparer.OrdinalIgnoreCase))
        {
            return preferredPort;
        }

        return availablePorts.FirstOrDefault(port =>
            port.Contains("usbserial", StringComparison.OrdinalIgnoreCase) ||
            port.Contains("usbmodem", StringComparison.OrdinalIgnoreCase) ||
            port.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase) ||
            port.Contains("ttyACM", StringComparison.OrdinalIgnoreCase) ||
            (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
             !port.Equals("COM1", StringComparison.OrdinalIgnoreCase)));
    }
}
