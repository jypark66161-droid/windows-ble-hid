using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

internal static class Program
{
    static readonly Guid Hid = U(0x1812);
    static Guid U(ushort id) => Guid.Parse($"0000{id:x4}-0000-1000-8000-00805f9b34fb");
    static readonly byte[] Mouse = {
        0x05,0x01,0x09,0x02,0xA1,0x01,0x09,0x01,0xA1,0x00,
        0x05,0x09,0x19,0x01,0x29,0x03,0x15,0,0x25,1,
        0x95,3,0x75,1,0x81,2,0x95,1,0x75,5,0x81,3,
        0x05,1,0x09,0x30,0x09,0x31,0x09,0x38,0x15,0x81,0x25,0x7f,
        0x75,8,0x95,3,0x81,6,0xC0,0xC0
    };
    static readonly byte[] Keyboard = {
        0x05,1,0x09,6,0xA1,1,0x05,7,0x19,0xe0,0x29,0xe7,
        0x15,0,0x25,1,0x75,1,0x95,8,0x81,2,
        0x95,1,0x75,8,0x81,1,0x95,6,0x75,8,
        0x15,0,0x25,0x65,0x05,7,0x19,0,0x29,0x65,0x81,0,0xC0
    };
    static byte[] WithId(byte[] value, byte id)
    {
        var result = new List<byte>(value);
        result.InsertRange(6, new byte[] { 0x85, id });
        return result.ToArray();
    }
    static async Task<GattLocalCharacteristic> Add(GattLocalService svc, ushort id,
        GattCharacteristicProperties prop, GattProtectionLevel protection,
        byte[]? value, bool explicitReads = false)
    {
        var p = new GattLocalCharacteristicParameters {
            CharacteristicProperties = prop,
            ReadProtectionLevel = protection,
            WriteProtectionLevel = protection
        };
        if (!explicitReads && value != null) p.StaticValue = value.AsBuffer();
        var created = await svc.CreateCharacteristicAsync(U(id), p);
        if (created.Error != BluetoothError.Success || created.Characteristic == null)
            throw new InvalidOperationException($"Required GATT characteristic 0x{id:X4}: {created.Error}");
        var c = created.Characteristic;
        if (explicitReads && value != null) {
            c.ReadRequested += async (_, args) => {
                var d = args.GetDeferral();
                try {
                    var request = await args.GetRequestAsync();
                    request?.RespondWithValue(value.AsBuffer());
                    Console.WriteLine($"[READ] 0x{id:X4}");
                } catch (Exception e) { Console.WriteLine($"[READ ERROR] 0x{id:X4}: {e.Message}"); }
                finally { d.Complete(); }
            };
        }
        return c;
    }
    static async Task<GattLocalCharacteristic> Input(GattLocalService svc, byte id,
        byte[] initial, GattProtectionLevel protection, bool dynamicReads)
    {
        var c = await Add(svc, 0x2A4D,
            GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            protection, initial, dynamicReads);
        var descriptor = await c.CreateDescriptorAsync(U(0x2908),
            new GattLocalDescriptorParameters {
                ReadProtectionLevel = protection,
                StaticValue = new byte[] { id, 1 }.AsBuffer()
            });
        if (descriptor.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Required Report Reference: {descriptor.Error}");
        c.SubscribedClientsChanged += (s, _) =>
            Console.WriteLine($"[HID SUBSCRIBERS] id={id} count={s.SubscribedClients.Count}");
        return c;
    }
    static async Task<int> Main(string[] args)
    {
        var mode = args.FirstOrDefault(x => x.StartsWith("--profile="))?.Split('=')[1] ?? "mouse";
        var dynamicReads = args.Contains("--read-handlers");
        var plain = args.Contains("--plain");
        if (mode != "mouse" && mode != "keyboard" && mode != "composite") {
            Console.WriteLine("Valid --profile=mouse | keyboard | composite");
            Console.ReadLine(); return 2;
        }
        var protection = plain ? GattProtectionLevel.Plain : GattProtectionLevel.EncryptionRequired;
        Console.WriteLine($"INDEPENDENT HOGP TEST: profile={mode}, reads={(dynamicReads?"handlers":"static")}, security={protection}");
        Console.WriteLine("Do not run the ordinary DeskBridge at the same time.");
        GattServiceProvider? provider = null;
        try {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null || !adapter.IsPeripheralRoleSupported)
                throw new InvalidOperationException("Windows Bluetooth peripheral role unavailable");
            var created = await GattServiceProvider.CreateAsync(Hid);
            if (created.Error != BluetoothError.Success || created.ServiceProvider == null)
                throw new InvalidOperationException($"HID service registration failed: {created.Error}");
            provider = created.ServiceProvider;
            provider.AdvertisementStatusChanged += (_, e) =>
                Console.WriteLine($"[ADVERTISEMENT] {e.Status} error={e.Error}");
            var svc = provider.Service;
            byte[] map = mode switch {
                "mouse" => Mouse,
                "keyboard" => Keyboard,
                _ => WithId(Mouse, 1).Concat(WithId(Keyboard, 2)).ToArray()
            };
            await Add(svc, 0x2A4A, GattCharacteristicProperties.Read, protection,
                new byte[] { 0x11, 0x01, 0, 0x02 }, dynamicReads);
            await Add(svc, 0x2A4B, GattCharacteristicProperties.Read, protection,
                map, dynamicReads);
            var protocolMode = (byte)1;
            var pm = await Add(svc, 0x2A4E,
                GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
                protection, new byte[] { protocolMode }, dynamicReads);
            pm.WriteRequested += async (_, e) => {
                var d = e.GetDeferral();
                try {
                    var req = await e.GetRequestAsync();
                    if (req != null && req.Value.Length > 0) {
                        var rd = DataReader.FromBuffer(req.Value);
                        protocolMode = rd.ReadByte();
                        Console.WriteLine($"[PROTOCOL MODE] {protocolMode}");
                    }
                } catch (Exception err) { Console.WriteLine(err.Message); }
                finally { d.Complete(); }
            };
            var cp = await Add(svc, 0x2A4C,
                GattCharacteristicProperties.WriteWithoutResponse, protection, null);
            cp.WriteRequested += async (_, e) => {
                var d = e.GetDeferral();
                try { await e.GetRequestAsync(); Console.WriteLine("[CONTROL POINT WRITE]"); }
                finally { d.Complete(); }
            };
            GattLocalCharacteristic? mouse = null, keyboard = null;
            if (mode != "keyboard")
                mouse = await Input(svc, (byte)(mode=="composite" ? 1 : 0),
                    new byte[4], protection, dynamicReads);
            if (mode != "mouse")
                keyboard = await Input(svc, (byte)(mode=="composite" ? 2 : 0),
                    new byte[8], protection, dynamicReads);
            Console.WriteLine("[GATT READY] All mandatory characteristics and descriptors created");
            provider.StartAdvertising(new GattServiceProviderAdvertisingParameters {
                IsDiscoverable = true, IsConnectable = true
            });
            Console.WriteLine("Commands: status, test, quit. Pair from Android while this console stays open.");
            while (true) {
                Console.Write("hid-test> ");
                var cmd = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (cmd == null || cmd == "quit") break;
                if (cmd == "status") {
                    Console.WriteLine($"Advertisement={provider.AdvertisementStatus}, MouseSubscribers={mouse?.SubscribedClients.Count ?? 0}, KeyboardSubscribers={keyboard?.SubscribedClients.Count ?? 0}");
                } else if (cmd == "test") {
                    if (mouse != null && mouse.SubscribedClients.Count > 0) {
                        var r = await mouse.NotifyValueAsync(new byte[] { 0, 20, 0, 0 }.AsBuffer());
                        Console.WriteLine($"Mouse notification recipients={r.Count}");
                    }
                    if (keyboard != null && keyboard.SubscribedClients.Count > 0) {
                        var r = await keyboard.NotifyValueAsync(new byte[] { 0, 0, 4, 0, 0, 0, 0, 0 }.AsBuffer());
                        await Task.Delay(50);
                        await keyboard.NotifyValueAsync(new byte[8].AsBuffer());
                        Console.WriteLine($"Keyboard notification recipients={r.Count}");
                    }
                }
            }
            return 0;
        } catch (Exception e) {
            Console.WriteLine("[FAILED] " + e);
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
            return 1;
        } finally {
            try { provider?.StopAdvertising(); } catch { }
        }
    }
}