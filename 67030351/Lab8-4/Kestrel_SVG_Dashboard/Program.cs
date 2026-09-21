using System.Globalization;
using System.IO.Ports;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TelemetryStateStore>();
builder.Services.AddHostedService<SerialBridgeWorker>();

var app = builder.Build();

app.MapGet("/api/telemetry", (TelemetryStateStore state) =>
{
    var (raw, voltage, percent, source, updated) = state.GetSnapshot();
    string alertLevel = percent > 85.0
        ? "DANGER (HIGH)"
        : percent >= 70.0 ? "WARNING" : "NORMAL";

    return Results.Ok(new
    {
        sensor = "ESP32-Potentiometer",
        rawValue = raw,
        voltage,
        percentage = percent,
        dataSource = source,
        timestamp = updated.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        alertLevel
    });
});

// เสิร์ฟ wwwroot/index.html เป็นหน้าแรก พร้อมไฟล์สถิตอื่น ๆ
app.UseFileServer();

app.Run();

public class TelemetryStateStore
{
    private readonly object _lock = new();
    private int _rawValue;
    private DateTime _lastUpdated = DateTime.UtcNow;
    private string _source = "Initializing";

    public void Update(int rawValue, string source)
    {
        lock (_lock)
        {
            _rawValue = Math.Clamp(rawValue, 0, 4095);
            _source = source;
            _lastUpdated = DateTime.UtcNow;
        }
    }

    public (int raw, double voltage, double percent, string source, DateTime updated) GetSnapshot()
    {
        lock (_lock)
        {
            double voltage = Math.Round((_rawValue / 4095.0) * 3.3, 2);
            double percent = Math.Round((_rawValue / 4095.0) * 100.0, 1);
            return (_rawValue, voltage, percent, _source, _lastUpdated);
        }
    }
}

public class SerialBridgeWorker : BackgroundService
{
    private readonly TelemetryStateStore _stateStore;
    private readonly ILogger<SerialBridgeWorker> _logger;

    public SerialBridgeWorker(TelemetryStateStore stateStore, ILogger<SerialBridgeWorker> logger)
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
                        _logger.LogInformation("Live Hardware connected on {Port}", selectedPort);

                        while (!stoppingToken.IsCancellationRequested && serial.IsOpen)
                        {
                            try
                            {
                                if (serial.BytesToRead > 0)
                                {
                                    string line = serial.ReadLine().Trim();
                                    if (int.TryParse(line, out int value) && value is >= 0 and <= 4095)
                                    {
                                        _stateStore.Update(value, $"Live Hardware ({selectedPort})");
                                    }
                                }
                                else
                                {
                                    await Task.Delay(50, stoppingToken);
                                }
                            }
                            catch (TimeoutException)
                            {
                                await Task.Delay(50, stoppingToken);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or ArgumentException)
                    {
                        _logger.LogWarning("Cannot open {Port}: {Message}. Switching to simulation.",
                            selectedPort, ex.Message);
                    }
                }
                else
                {
                    _logger.LogInformation("No USB serial port found. Running Simulation Mode.");
                }

                // จำลองข้อมูล 2 วินาที ก่อนวนกลับไปตรวจหาบอร์ดอีกครั้ง
                for (int i = 0; i < 20 && !stoppingToken.IsCancellationRequested; i++)
                {
                    double t = Environment.TickCount64 / 1000.0;
                    int simulatedAdc = (int)((Math.Sin(t * 1.5) + 1.0) / 2.0 * 4095);
                    _stateStore.Update(simulatedAdc, "Simulation Mode (Sine Wave)");
                    await Task.Delay(100, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // ปิด Worker อย่างเรียบร้อยเมื่อหยุด Kestrel
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
