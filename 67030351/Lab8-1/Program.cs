var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Welcome to IoT Edge Gateway by Siwarpat Auisui!");

app.MapGet("/api/status", () => new {
    gateway = "ESP32-EdgeGateway",
    status = "Online",
    uptimeSeconds = Environment.TickCount64 / 1000,
    isHealthy = true
});

app.MapGet("/api/led/{state}", (string state) => {
    string action = state.ToLower() == "on" ? "TURN ON 💡" : "TURN OFF 🌑";

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] LED Control: {state}");

    return Results.Ok(new { 
        device = "LED_D2", 
        requestedState = state, 
        actionResult = action,
        serverTime = DateTime.Now.ToString("HH:mm:ss")
    });
});

app.MapGet("/api/student", () => new {
    studentId = "67030351",
    studentName = "Siwarpat Auisui",
    faculty = "School of Industrial Education and Technology",
    targetSensor = "DHT22",
    timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
});

app.Run();

app.Run();
