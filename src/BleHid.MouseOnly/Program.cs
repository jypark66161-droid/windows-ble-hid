using System.Diagnostics;
using System.Threading.Channels;
using BleHid.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine($"MouseOnly launch OK | process={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
    return 0;
}

using var singleInstance = new Mutex(true, @"Local\BleHid.Peripheral", out bool firstInstance);
if (!firstInstance)
{
    Console.Error.WriteLine("Another BLE HID instance is running. Stop it before launching MouseOnly.");
    return 2;
}

var path = AppPaths.InLogs("mouse-only-trace.log");
var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
var writer = Task.Run(async () =>
{
    using var file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
    await foreach (var line in lines.Reader.ReadAllAsync())
    {
        await file.WriteLineAsync(line);
        await file.FlushAsync();
    }
});
var clock = Stopwatch.StartNew();
void Trace(string message)
{
    var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] +{clock.ElapsedMilliseconds}ms {message}";
    lines.Writer.TryWrite(line);
    Console.WriteLine(line);
}

GattServiceProvider? provider = null;
try
{
    Trace("START isolated mouse-only HOGP experiment; no keyboard report, no hooks, no Android app");
    var adapter = await BluetoothAdapter.GetDefaultAsync();
    if (adapter is null || !adapter.IsPeripheralRoleSupported)
        throw new InvalidOperationException("Windows Bluetooth adapter lacks BLE peripheral role.");

    var serviceResult = await GattServiceProvider.CreateAsync(HidDescriptors.HidService);
    if (serviceResult.Error != BluetoothError.Success)
        throw new InvalidOperationException($"HID service creation: {serviceResult.Error}");
    provider = serviceResult.ServiceProvider;
    var service = provider.Service;
    var protection = GattProtectionLevel.EncryptionRequired;

    async Task<GattLocalCharacteristic> Required(Guid uuid, string name, GattLocalCharacteristicParameters parameters)
    {
        var result = await service.CreateCharacteristicAsync(uuid, parameters);
        Trace($"CREATE {name}: {result.Error}");
        if (result.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Required {name} cannot be created: {result.Error}");
        return result.Characteristic;
    }

    static IBuffer Bytes(byte[] bytes) => CryptographicBuffer.CreateFromByteArray(bytes);
    var info = await Required(HidDescriptors.HidInformation, "HID Information", new()
    {
        CharacteristicProperties = GattCharacteristicProperties.Read,
        ReadProtectionLevel = protection,
        StaticValue = Bytes(HidDescriptors.HidInformationValue)
    });

    // No report IDs in a single-report device; just six mouse input bytes.
    var reportMap = new byte[]
    {
        0x05,0x01, 0x09,0x02, 0xA1,0x01, 0x09,0x01, 0xA1,0x00,
        0x05,0x09, 0x19,0x01, 0x29,0x03, 0x15,0x00, 0x25,0x01,
        0x95,0x03, 0x75,0x01, 0x81,0x02, 0x95,0x01, 0x75,0x05, 0x81,0x01,
        0x05,0x01, 0x09,0x30, 0x09,0x31, 0x16,0x01,0x80,
        0x26,0xFF,0x7F, 0x75,0x10, 0x95,0x02, 0x81,0x06,
        0x09,0x38, 0x15,0x81, 0x25,0x7F, 0x75,0x08, 0x95,0x01, 0x81,0x06,
        0xC0,0xC0
    };
    await Required(HidDescriptors.ReportMap, "Mouse-only Report Map", new()
    {
        CharacteristicProperties = GattCharacteristicProperties.Read,
        ReadProtectionLevel = protection,
        StaticValue = Bytes(reportMap)
    });
    Trace($"REPORT_MAP bytes={reportMap.Length}, static Windows-managed read, single mouse, no Report ID");

    await Required(HidDescriptors.HidControlPoint, "HID Control Point", new()
    {
        CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
        WriteProtectionLevel = protection
    });

    var protocolMode = await Required(HidDescriptors.ProtocolMode, "Protocol Mode", new()
    {
        CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
        ReadProtectionLevel = protection,
        WriteProtectionLevel = protection,
        StaticValue = Bytes([0x01])
    });
    protocolMode.WriteRequested += async (_, e) =>
    {
        using var d = e.GetDeferral();
        var request = await e.GetRequestAsync();
        Trace($"PROTOCOL write bytes={request?.Value?.Length}");
    };

    var current = new byte[6];
    var mouse = await Required(HidDescriptors.Report, "Mouse input Report", new()
    {
        CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
        ReadProtectionLevel = protection
    });
    mouse.ReadRequested += async (_, e) =>
    {
        using var d = e.GetDeferral();
        var request = await e.GetRequestAsync();
        if (request is not null) request.RespondWithValue(Bytes(current));
        Trace("MOUSE_READ responded (peer receipt unconfirmed)");
    };
    mouse.SubscribedClientsChanged += (sender, _) =>
    {
        Trace($"MOUSE_SUBSCRIPTIONS count={sender.SubscribedClients.Count} peers={string.Join(",", sender.SubscribedClients.Select(c => c.Session.DeviceId.Id))}");
    };
    var descriptorResult = await mouse.CreateDescriptorAsync(HidDescriptors.ReportReference, new()
    {
        ReadProtectionLevel = protection,
        StaticValue = Bytes([0x00, HidDescriptors.ReportTypeInput])
    });
    Trace($"MOUSE Report Reference id=0 input: {descriptorResult.Error}");
    if (descriptorResult.Error != BluetoothError.Success)
        throw new InvalidOperationException($"Mouse Report Reference failed: {descriptorResult.Error}");

    var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    provider.AdvertisementStatusChanged += (_, e) =>
    {
        Trace($"ADVERTISEMENT status={e.Status} error={e.Error}");
        if (e.Status == GattServiceProviderAdvertisementStatus.Started) started.TrySetResult(true);
    };
    provider.StartAdvertising(new() { IsConnectable = true, IsDiscoverable = true });
    if (await Task.WhenAny(started.Task, Task.Delay(10000)) != started.Task)
        throw new InvalidOperationException($"Mouse-only BLE advertising failed: {provider.AdvertisementStatus}");
    Trace("READY. Pair from Android. Commands: status | move X Y | click | scroll N | quit");

    async Task Send(byte[] payload)
    {
        if (mouse.SubscribedClients.Count == 0) { Trace("SEND-DROPPED no mouse subscription"); return; }
        Array.Copy(payload, current, 6);
        var results = await mouse.NotifyValueAsync(Bytes(payload));
        Trace($"MOUSE_NOTIFY payload={Convert.ToHexString(payload)} statuses={string.Join(",", results.Select(x => x.Status.ToString()))}; Android input not confirmed");
    }

    while (true)
    {
        Console.Write("> ");
        var command = Console.ReadLine();
        if (command is null || command.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) continue;
        try
        {
            switch (words[0].ToLowerInvariant())
            {
                case "status":
                    Trace($"STATUS advertising={provider.AdvertisementStatus}, mouseSubscribers={mouse.SubscribedClients.Count}");
                    break;
                case "move" when words.Length == 3:
                    await Send(HidReports.Mouse(MouseButtons.None, int.Parse(words[1]), int.Parse(words[2]), 0));
                    break;
                case "scroll" when words.Length == 2:
                    await Send(HidReports.Mouse(MouseButtons.None, 0, 0, int.Parse(words[1])));
                    break;
                case "click":
                    await Send(HidReports.Mouse(MouseButtons.Left, 0, 0, 0));
                    await Task.Delay(35);
                    await Send(HidReports.Mouse(MouseButtons.None, 0, 0, 0));
                    break;
                default:
                    Console.WriteLine("Commands: status | move 50 0 | click | scroll 1 | quit");
                    break;
            }
        }
        catch (Exception ex) { Trace("COMMAND_ERROR " + ex); }
    }
}
catch (Exception ex)
{
    Trace("FATAL " + ex);
    return 1;
}
finally
{
    if (provider is not null) { provider.StopAdvertising(); provider.Dispose(); }
    lines.Writer.TryComplete();
    await writer;
}
return 0;
