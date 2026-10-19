using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

static class App
{
    static Guid Id(ushort n) => Guid.Parse($"0000{n:x4}-0000-1000-8000-00805f9b34fb");
    static byte[] ReportMap = {
        // Mouse: Report ID 1, buttons / X / Y / wheel
        0x05,0x01,0x09,0x02,0xA1,0x01,0x85,0x01,0x09,0x01,0xA1,0x00,
        0x05,0x09,0x19,0x01,0x29,0x03,0x15,0x00,0x25,0x01,
        0x95,0x03,0x75,0x01,0x81,0x02,0x95,0x01,0x75,0x05,0x81,0x03,
        0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x38,0x15,0x81,0x25,0x7F,
        0x75,0x08,0x95,0x03,0x81,0x06,0xC0,0xC0,
        // Keyboard: Report ID 2, 8 modifiers, reserved byte, 6 key usages
        0x05,0x01,0x09,0x06,0xA1,0x01,0x85,0x02,
        0x05,0x07,0x19,0xE0,0x29,0xE7,0x15,0x00,0x25,0x01,
        0x75,0x01,0x95,0x08,0x81,0x02,
        0x95,0x01,0x75,0x08,0x81,0x01,
        0x95,0x06,0x75,0x08,0x15,0x00,0x25,0x65,
        0x05,0x07,0x19,0x00,0x29,0x65,0x81,0x00,0xC0
    };
    static GattServiceProvider? hid,bat,dis;
    static GattLocalCharacteristic? mouse, keyboard;
    static byte mode=1;
    static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "DeskBridge-HOGP-startup.log");
    static void L(string text) {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
    static void PauseOnError() {
        Console.WriteLine();
        Console.WriteLine("STARTUP FAILED. This window will remain open until you press Enter.");
        Console.WriteLine("Log: " + LogPath);
        try { Console.ReadLine(); } catch { }
    }
    static async Task<GattServiceProvider> Service(ushort id) {
        var r=await GattServiceProvider.CreateAsync(Id(id));
        L($"SERVICE {id:X4}: {r.Error}");
        if(r.ServiceProvider==null || r.Error!=BluetoothError.Success) throw new Exception($"Service {id:X4}: {r.Error}");
        r.ServiceProvider.AdvertisementStatusChanged += (_,e)=>L($"ADV {id:X4}: {e.Status}");
        return r.ServiceProvider;
    }
    static async Task<GattLocalCharacteristic> Characteristic(GattServiceProvider svc,ushort id,GattLocalCharacteristicParameters par) {
        var r=await svc.Service.CreateCharacteristicAsync(Id(id),par);
        L($"CHAR {id:X4}: {r.Error}");
        if(r.Characteristic==null || r.Error!=BluetoothError.Success) throw new Exception($"Char {id:X4}: {r.Error}");
        return r.Characteristic;
    }
    static async Task Readonly(GattServiceProvider s,ushort id,byte[] data,GattProtectionLevel sec=GattProtectionLevel.Plain) =>
       await Characteristic(s,id,new GattLocalCharacteristicParameters{
           CharacteristicProperties=GattCharacteristicProperties.Read,ReadProtectionLevel=sec,StaticValue=data.AsBuffer()
       });
    static async Task<int> Main(string[] args) {
        try {
            L("DeskBridge Native HOGP v0.2 - independent Windows BLE mouse and keyboard");
            var adapter=await BluetoothAdapter.GetDefaultAsync();
            if(adapter==null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported) {
                L("ERROR: BLE peripheral mode unavailable"); PauseOnError(); return 2;
            }
            var sec=args.Contains("--plain") ? GattProtectionLevel.Plain : GattProtectionLevel.EncryptionRequired;
            bat=await Service(0x180F);
            await Readonly(bat,0x2A19,new byte[]{100});
            dis=await Service(0x180A);
            await Readonly(dis,0x2A29,Encoding.UTF8.GetBytes("DeskBridge Experimental"));
            await Readonly(dis,0x2A24,Encoding.UTF8.GetBytes("Native Mouse Keyboard v0.2"));
            await Readonly(dis,0x2A50,new byte[]{2,0xFF,0xFF,1,0,1,0});
            hid=await Service(0x1812);
            await Readonly(hid,0x2A4A,new byte[]{0x11,0x01,0,0x02},sec);
            await Readonly(hid,0x2A4B,ReportMap,sec);
            mouse=await Characteristic(hid,0x2A4D,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[5].AsBuffer()
            });
            mouse.SubscribedClientsChanged += (_,_)=>L($"SUBSCRIBERS={mouse.SubscribedClients.Count}");
            var d=await mouse.CreateDescriptorAsync(Id(0x2908),new GattLocalDescriptorParameters{
                ReadProtectionLevel=sec, StaticValue=new byte[]{1,1}.AsBuffer()
            });
            L($"MOUSE REPORT REF 2908: {d.Error}");
            keyboard=await Characteristic(hid,0x2A4D,new GattLocalCharacteristicParameters {
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.Notify,
                ReadProtectionLevel=sec, StaticValue=new byte[9].AsBuffer()
            });
            keyboard.SubscribedClientsChanged += (_,_)=>L($"KEYBOARD SUBSCRIBERS={keyboard.SubscribedClients.Count}");
            var kd=await keyboard.CreateDescriptorAsync(Id(0x2908),new GattLocalDescriptorParameters{
                ReadProtectionLevel=sec,StaticValue=new byte[]{2,1}.AsBuffer()
            });
            L($"KEYBOARD REPORT REF 2908: {kd.Error}");
            var pm=await Characteristic(hid,0x2A4E,new GattLocalCharacteristicParameters{
                CharacteristicProperties=GattCharacteristicProperties.Read|GattCharacteristicProperties.WriteWithoutResponse,
                ReadProtectionLevel=sec,WriteProtectionLevel=sec
            });
            pm.ReadRequested+=async (_,e)=> {
                var def=e.GetDeferral(); try { var req=await e.GetRequestAsync();req.RespondWithValue(new byte[]{mode}.AsBuffer());} catch(Exception ex){L(ex.Message);} finally{def.Complete();}
            };
            pm.WriteRequested+=async (_,e)=> {
                var def=e.GetDeferral();try {var req=await e.GetRequestAsync(); var rd=DataReader.FromBuffer(req.Value); if(rd.UnconsumedBufferLength>0)mode=rd.ReadByte();L($"MODE={mode}");}catch(Exception ex){L(ex.Message);}finally{def.Complete();}
            };
            var ctrl=await Characteristic(hid,0x2A4C,new GattLocalCharacteristicParameters{
                CharacteristicProperties=GattCharacteristicProperties.WriteWithoutResponse,WriteProtectionLevel=sec
            });
            ctrl.WriteRequested+=async (_,e)=> {
                var def=e.GetDeferral();try {var req=await e.GetRequestAsync(); L("CONTROL POINT WRITE");}catch(Exception ex){L(ex.Message);}finally{def.Complete();}
            };
            bat.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=false,IsConnectable=true});
            dis.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=false,IsConnectable=true});
            hid.StartAdvertising(new GattServiceProviderAdvertisingParameters{IsDiscoverable=true,IsConnectable=true});
            L($"ADVERTISING HID={hid.AdvertisementStatus}");
            L("Pair phone, then commands: status, right, left, up, down, click, a, enter, space, quit");
            while(true) {
                Console.Write("hogp> ");
                var cmd=Console.ReadLine()?.Trim().ToLowerInvariant();
                if(cmd==null || cmd=="quit")break;
                if(cmd=="status"){L($"HID={hid.AdvertisementStatus}, mouse={mouse.SubscribedClients.Count}, keyboard={keyboard.SubscribedClients.Count}, protocol={mode}");continue;}
                if(cmd=="a" || cmd=="enter" || cmd=="space"){
                    if(keyboard.SubscribedClients.Count==0){L("NO KEYBOARD SUBSCRIBER");continue;}
                    byte usage=cmd=="a"?(byte)0x04:cmd=="enter"?(byte)0x28:(byte)0x2C;
                    await keyboard.NotifyValueAsync(new byte[]{2,0,0,usage,0,0,0,0,0}.AsBuffer());
                    await Task.Delay(40);
                    await keyboard.NotifyValueAsync(new byte[]{2,0,0,0,0,0,0,0,0}.AsBuffer());
                    L($"KEY SENT {cmd}");continue;
                }
                var (buttons,dx,dy,wheel) = cmd switch {
                    "right"=>(0,12,0,0),"left"=>(0,-12,0,0),"up"=>(0,0,-12,0),
                    "down"=>(0,0,12,0),"click"=>(1,0,0,0),_=>(0,0,0,0)
                };
                if(mouse.SubscribedClients.Count==0){L("NO INPUT SUBSCRIBER. Device may not be recognized as HID.");continue;}
                var result=await mouse.NotifyValueAsync(new byte[]{1,(byte)buttons,unchecked((byte)dx),unchecked((byte)dy),unchecked((byte)wheel)}.AsBuffer());
                L($"NOTIFY recipients={result.Count}, status={string.Join(",",result.Select(x=>x.Status))}");
                if(cmd=="click") {await Task.Delay(35);await mouse.NotifyValueAsync(new byte[]{1,0,0,0,0}.AsBuffer());}
            }
            hid.StopAdvertising();bat.StopAdvertising();dis.StopAdvertising();return 0;
        }catch(Exception ex){L("FATAL ERROR: " + ex); PauseOnError(); return 1;}
    }
}
