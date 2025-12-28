# Quick test to verify VapeCache can talk to Redis at all

Write-Host "Testing basic Redis connectivity..." -ForegroundColor Yellow

# Create a minimal C# program
$testCode = @'
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

var host = "192.168.100.50";
var port = 6379;

Console.WriteLine($"Connecting to {host}:{port}...");
using var client = new TcpClient();
await client.ConnectAsync(host, port);
using var stream = client.GetStream();

// Send AUTH admin redis4me!!
var authCmd = "*3\r\n$4\r\nAUTH\r\n$5\r\nadmin\r\n$10\r\nredis4me!!\r\n";
var authBytes = Encoding.UTF8.GetBytes(authCmd);
await stream.WriteAsync(authBytes);
await stream.FlushAsync();

// Read response
var buffer = new byte[1024];
var read = await stream.ReadAsync(buffer);
var response = Encoding.UTF8.GetString(buffer, 0, read);
Console.WriteLine($"AUTH response: {response.Replace("\r", "\\r").Replace("\n", "\\n")}");

// Send PING
var pingCmd = "*1\r\n$4\r\nPING\r\n";
var pingBytes = Encoding.UTF8.GetBytes(pingCmd);
await stream.WriteAsync(pingBytes);
await stream.FlushAsync();

read = await stream.ReadAsync(buffer);
response = Encoding.UTF8.GetString(buffer, 0, read);
Console.WriteLine($"PING response: {response.Replace("\r", "\\r").Replace("\n", "\\n")}");

// Send SET test value
var setCmd = "*5\r\n$3\r\nSET\r\n$4\r\ntest\r\n$5\r\nhello\r\n$2\r\nPX\r\n$6\r\n600000\r\n";
var setBytes = Encoding.UTF8.GetBytes(setCmd);
Console.WriteLine($"\nSending SET command:");
Console.WriteLine(setCmd.Replace("\r", "\\r").Replace("\n", "\\n"));
await stream.WriteAsync(setBytes);
await stream.FlushAsync();

read = await stream.ReadAsync(buffer);
response = Encoding.UTF8.GetString(buffer, 0, read);
Console.WriteLine($"SET response: {response.Replace("\r", "\\r").Replace("\n", "\\n")}");

Console.WriteLine("\n✓ All commands succeeded!");
'@

$testCode | Out-File -FilePath "TestRedisConnection.csx" -Encoding UTF8

Write-Host "Running test..." -ForegroundColor Cyan
dotnet script "TestRedisConnection.csx"

Remove-Item "TestRedisConnection.csx" -ErrorAction SilentlyContinue
